import { randomUUID } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { MiniConnect } from './mini-connect.ts';
import {
  type NativeProjectMeta,
  type NativeProjectSnapshot,
} from '../shared/native-desktop-contract.ts';
import { CURRENT_PROJECT_VERSION } from '../shared/project-version.ts';
import { projectReduce, type AnyAction } from '../src/editor/reduce.ts';
import type { ProjectDoc, Timeline } from '../src/editor/types.ts';
import { revisionOf } from '../src/agent/external-edit-session.ts';
import { runProjectMigrations } from '../src/persist/migrations/index.ts';
import {
  deleteStoredEntry,
  getStoredEntry,
  readStore,
  withSerializedProjectStore,
} from '../server/plugins/project-store.ts';
import { nativeDesktopAuthorized } from '../server/native-auth.ts';
import { canonicalServerRunToolCatalog } from '../server/agent-runs/tool-policy.ts';

const MAX_BODY_BYTES = 16 * 1024 * 1024;
const PROJECT_ID = /^[a-zA-Z0-9_-]{1,160}$/;

function nativeTokenMatches(req: IncomingMessage): boolean {
  return nativeDesktopAuthorized(req);
}

function json(res: ServerResponse, status: number, body: unknown): void {
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Cache-Control', 'no-store');
  res.end(JSON.stringify(body));
}

async function readJson(req: IncomingMessage): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const chunk of req) {
    const value = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    total += value.length;
    if (total > MAX_BODY_BYTES) throw new Error('native request body is too large');
    chunks.push(value);
  }
  const parsed: unknown = JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('native request body must be an object');
  }
  return parsed as Record<string, unknown>;
}

function pathParts(req: IncomingMessage): string[] {
  const pathname = new URL(req.url ?? '/', 'http://localhost').pathname;
  return pathname.split('/').filter(Boolean);
}

function requestUrl(req: IncomingMessage): URL {
  return new URL(req.url ?? '/', 'http://localhost');
}

function projectMeta(value: unknown): NativeProjectMeta | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const record = value as Record<string, unknown>;
  if (typeof record.id !== 'string' || !PROJECT_ID.test(record.id)) return null;
  if (typeof record.name !== 'string' || typeof record.updatedAt !== 'number') return null;
  return {
    id: record.id,
    name: record.name,
    updatedAt: record.updatedAt,
    ...(typeof record.description === 'string' ? { description: record.description } : {}),
  };
}

function migratedProject(value: unknown): ProjectDoc | null {
  return runProjectMigrations(value)?.doc ?? null;
}

function emptyProject(): ProjectDoc {
  const timelineId = `tl_${randomUUID()}`;
  const timeline: Timeline = {
    id: timelineId,
    name: '序列 1',
    order: 0,
    fps: 30,
    width: 1920,
    height: 1080,
    items: [],
    selectedId: null,
    trackOrder: ['track_v1'],
    tracks: { track_v1: { kind: 'video' } },
  };
  return {
    version: CURRENT_PROJECT_VERSION,
    assets: [],
    mediaFolders: [],
    timelines: [timeline],
    activeTimelineId: timelineId,
  };
}

async function readProjects(): Promise<NativeProjectMeta[]> {
  const store = await readStore();
  const value = store.entries.projects;
  return Array.isArray(value)
    ? value.map(projectMeta).filter((item): item is NativeProjectMeta => item !== null)
    : [];
}

async function readProject(id: string): Promise<NativeProjectSnapshot | null> {
  const entry = await getStoredEntry(`project:${id}`);
  if (!entry.found) return null;
  const doc = migratedProject(entry.value);
  if (!doc) throw new Error('stored project document is corrupt or unsupported');
  const meta = (await readProjects()).find((item) => item.id === id);
  if (!meta) return null;
  return { meta, doc, revision: revisionOf(doc) };
}

async function writeProject(
  id: string,
  doc: ProjectDoc,
  patch: Partial<NativeProjectMeta> = {},
): Promise<NativeProjectSnapshot> {
  const normalized = migratedProject(doc);
  if (!normalized) throw new Error('native project document is invalid');
  return withSerializedProjectStore(async (store) => {
    const currentIndex = await store.readEntry('projects');
    const existing = Array.isArray(currentIndex.value)
      ? currentIndex.value.map(projectMeta).find((item) => item?.id === id) ?? null
      : null;
    const meta: NativeProjectMeta = {
      id,
      name: typeof patch.name === 'string' && patch.name.trim()
        ? patch.name.trim().slice(0, 120)
        : existing?.name ?? '未命名工程',
      updatedAt: Date.now(),
      ...(typeof patch.description === 'string'
        ? { description: patch.description }
        : existing?.description ? { description: existing.description } : {}),
    };
    await store.writeEntryExact(`project:${id}`, normalized);
    const entries = Array.isArray(currentIndex.value)
      ? currentIndex.value.filter((item) => projectMeta(item)?.id !== id)
      : [];
    await store.writeEntry('projects', [...entries, meta]);
    return { meta, doc: normalized, revision: revisionOf(normalized) };
  });
}

async function createProject(name: string, description?: string): Promise<NativeProjectSnapshot> {
  const id = `project_${randomUUID()}`;
  return writeProject(id, emptyProject(), {
    name: name.trim().slice(0, 120) || '未命名工程',
    ...(description ? { description } : {}),
  });
}

async function duplicateProject(id: string): Promise<NativeProjectSnapshot> {
  const source = await readProject(id);
  if (!source) throw new Error('project not found');
  const duplicateName = `${source.meta.name} 副本`.slice(0, 120);
  return writeProject(`project_${randomUUID()}`, source.doc, {
    name: duplicateName,
    ...(source.meta.description ? { description: source.meta.description } : {}),
  });
}

function validAction(value: unknown): value is AnyAction {
  return !!value
    && typeof value === 'object'
    && !Array.isArray(value)
    && typeof (value as { type?: unknown }).type === 'string';
}

function sendError(res: ServerResponse, error: unknown): void {
  json(res, 400, { error: error instanceof Error ? error.message : String(error) });
}

/** Mount the native-only API below /api/native. No browser origin is trusted. */
export function mountNativeDesktopApi(app: Pick<MiniConnect, 'use'>): void {
  const undoStacks = new Map<string, ProjectDoc[]>();
  const redoStacks = new Map<string, ProjectDoc[]>();

  app.use('/api/native', async (req, res) => {
    if (!nativeTokenMatches(req)) {
      req.resume();
      json(res, 401, { error: 'invalid native client token' });
      return;
    }
    try {
      const parts = pathParts(req);
      if (req.method === 'GET' && parts[0] === 'health') {
        json(res, 200, { ok: true, protocol: 1, runtime: 'native-service' });
        return;
      }
      if (req.method === 'GET' && parts.length === 2
        && parts[0] === 'agent' && parts[1] === 'tools') {
        const askOnly = requestUrl(req).searchParams.get('askOnly') === '1';
        json(res, 200, {
          version: 1,
          tools: canonicalServerRunToolCatalog(askOnly),
        });
        return;
      }
      if (req.method === 'GET' && parts.length === 1 && parts[0] === 'projects') {
        json(res, 200, { projects: await readProjects() });
        return;
      }
      if (req.method === 'POST' && parts.length === 1 && parts[0] === 'projects') {
        const body = await readJson(req);
        const name = typeof body.name === 'string' ? body.name : '';
        const description = typeof body.description === 'string' ? body.description : undefined;
        json(res, 201, await createProject(name, description));
        return;
      }
      if (parts[0] !== 'projects' || !parts[1] || !PROJECT_ID.test(parts[1])) {
        json(res, 404, { error: 'native route not found' });
        return;
      }
      const id = parts[1];
      if (req.method === 'GET' && parts.length === 2) {
        const snapshot = await readProject(id);
        if (!snapshot) { json(res, 404, { error: 'project not found' }); return; }
        json(res, 200, snapshot);
        return;
      }
      if (req.method === 'PATCH' && parts.length === 2) {
        const body = await readJson(req);
        const snapshot = await readProject(id);
        if (!snapshot) { json(res, 404, { error: 'project not found' }); return; }
        json(res, 200, await writeProject(id, snapshot.doc, {
          ...(typeof body.name === 'string' ? { name: body.name } : {}),
          ...(typeof body.description === 'string' ? { description: body.description } : {}),
        }));
        return;
      }
      if (req.method === 'DELETE' && parts.length === 2) {
        if (!(await readProject(id))) { json(res, 404, { error: 'project not found' }); return; }
        await deleteStoredEntry(`project:${id}`);
        undoStacks.delete(id);
        redoStacks.delete(id);
        json(res, 200, { ok: true });
        return;
      }
      if (req.method === 'POST' && parts.length === 3 && parts[2] === 'duplicate') {
        json(res, 201, await duplicateProject(id));
        return;
      }
      if (req.method === 'GET' && parts.length === 3 && parts[2] === 'history') {
        json(res, 200, {
          canUndo: (undoStacks.get(id)?.length ?? 0) > 0,
          canRedo: (redoStacks.get(id)?.length ?? 0) > 0,
        });
        return;
      }
      if (req.method === 'POST' && parts.length === 3
        && (parts[2] === 'undo' || parts[2] === 'redo')) {
        const source = parts[2] === 'undo' ? undoStacks : redoStacks;
        const destination = parts[2] === 'undo' ? redoStacks : undoStacks;
        const stack = source.get(id) ?? [];
        const snapshot = await readProject(id);
        if (!snapshot) { json(res, 404, { error: 'project not found' }); return; }
        const target = stack.at(-1);
        if (!target) {
          json(res, 409, { error: parts[2] === 'undo' ? 'nothing to undo' : 'nothing to redo' });
          return;
        }
        const next = await writeProject(id, target);
        stack.pop();
        source.set(id, stack);
        const destinationStack = destination.get(id) ?? [];
        destinationStack.push(snapshot.doc);
        while (destinationStack.length > 50) destinationStack.shift();
        destination.set(id, destinationStack);
        json(res, 200, next);
        return;
      }
      if (req.method === 'POST' && parts.length === 3 && parts[2] === 'actions') {
        const body = await readJson(req);
        if (!validAction(body.action)) throw new Error('native action is invalid');
        const snapshot = await readProject(id);
        if (!snapshot) { json(res, 404, { error: 'project not found' }); return; }
        const next = projectReduce(snapshot.doc, body.action);
        if (next !== snapshot.doc) {
          const stack = undoStacks.get(id) ?? [];
          stack.push(snapshot.doc);
          while (stack.length > 50) stack.shift();
          undoStacks.set(id, stack);
          redoStacks.delete(id);
        }
        json(res, 200, next === snapshot.doc ? snapshot : await writeProject(id, next));
        return;
      }
      json(res, 405, { error: 'native method not allowed' });
    } catch (error) {
      sendError(res, error);
    }
  });
}
