import { jsonSchema, tool } from 'ai';
import type { AgentContext } from '../../src/agent/context';
import { policyForTool } from '../../src/agent/execution-policy';
import { makeDraft } from '../../src/editor/store';
import { ToolActivation } from '../../src/agent/tool-activation';
import type { AgentToolSchema } from '../../src/agent/tool-schema';
import { toolResultModelOutput } from '../../src/agent/tool-result-output';
import { executeCodexTool } from '../../src/agent/runtime';
import { loadAgentSettings } from '../../src/agent/settings/agentSettings';
import {
  isFailedToolResult,
  toolFailureReason,
  ToolFailureTracker,
} from '../../src/agent/toolFailure';
import { toolExecutionMode } from '../../src/agent/tools/execution-modes';
import { recordAcceptedTool, type AcceptanceLoopState } from './acceptance-loop';
import { assertCanonicalToolInvocation } from './tool-policy';
import {
  digestToolArgs,
  pushRunEvent,
  waitForToolResult,
  type ServerRun,
} from './store';
import {
  getStoredEntry,
  withSerializedProjectStore,
} from '../plugins/project-store';
import { runProjectMigrations } from '../../src/persist/migrations/index';
import { revisionOf } from '../../src/agent/external-edit-session';
import type { ProjectDoc } from '../../src/editor/types';

export interface ActivationState {
  current: ToolActivation;
  tail: Promise<void>;
  followupText: string | null;
  toolFailures: ToolFailureTracker;
  acceptance: AcceptanceLoopState;
  repeatGuardNote?: string;
  lastSuccessfulPureTool?: { name: string; argsDigest: string; result: unknown };
}

function cacheablePureTool(name: string): boolean {
  return name === 'analyze_music';
}

function recordTool(activation: ActivationState, schema: AgentToolSchema, args: Record<string, unknown>): void {
  activation.acceptance = recordAcceptedTool(
    activation.acceptance,
    policyForTool(schema.name, args).effect,
  );
}

function migratedDocument(value: unknown): ProjectDoc {
  const migrated = runProjectMigrations(value);
  if (!migrated) throw new Error('native Agent could not read the current project document');
  return migrated.doc;
}

async function commitNativeDocument(
  projectId: string,
  base: ProjectDoc,
  next: ProjectDoc,
): Promise<void> {
  const baseRevision = revisionOf(base);
  await withSerializedProjectStore(async (store) => {
    const currentEntry = await store.readEntry(`project:${projectId}`);
    if (!currentEntry.found) throw new Error('native Agent project no longer exists');
    const current = migratedDocument(currentEntry.value);
    if (revisionOf(current) !== baseRevision) {
      throw new Error('project changed while the native Agent was editing; refresh and retry');
    }
    await store.writeEntryExact(`project:${projectId}`, next);

    const index = await store.readEntry('projects');
    if (!Array.isArray(index.value)) return;
    const entries = index.value.map((item) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) return item;
      const record = item as Record<string, unknown>;
      return record.id === projectId ? { ...record, updatedAt: Date.now() } : item;
    });
    await store.writeEntry('projects', entries);
  });
}

async function executeNativeTool(
  run: ServerRun,
  schema: AgentToolSchema,
  args: Record<string, unknown>,
  toolCallId: string,
  activation: ActivationState,
): Promise<{ result: unknown; activation: ToolActivation }> {
  const stored = await getStoredEntry(`project:${run.projectId}`);
  if (!stored.found) throw new Error('native Agent project no longer exists');
  const base = migratedDocument(stored.value);
  const draft = makeDraft(base);
  const context: AgentContext = {
    commands: draft.commands,
    getState: draft.getState,
    getDoc: draft.getDoc,
    getCreativeMode: () => null,
    templates: [],
    audio: [],
    getProjectId: () => run.projectId,
    onToolProgress: (note) => pushRunEvent(run, 'tool-progress', { toolCallId, note }),
  };
  const update = await executeCodexTool({
    name: schema.name,
    args,
    activation: activation.current,
    ctx: context,
    settings: loadAgentSettings(),
    toolCallId,
    signal: run.abort?.signal,
    onEvent: () => undefined,
  });
  if (!update.execution.success) {
    throw new Error(typeof update.execution.result === 'string'
      ? update.execution.result
      : JSON.stringify(update.execution.result));
  }
  await commitNativeDocument(run.projectId, base, draft.getDoc());
  return { result: update.execution.result, activation: update.activation };
}

export async function executeBrowserTool(
  run: ServerRun,
  schema: AgentToolSchema,
  args: Record<string, unknown>,
  toolCallId: string,
  activation: ActivationState,
): Promise<unknown> {
  const parallel = toolExecutionMode(schema.name) === 'parallel';
  let release: (() => void) | undefined;
  if (!parallel) {
    const previous = activation.tail;
    const { promise: next, resolve } = Promise.withResolvers<void>();
    activation.tail = next;
    release = resolve;
    await previous;
  }
  try {
    activation.current = activation.current.admit(schema.name);
    assertCanonicalToolInvocation(schema, args, activation.current.schemas());
    const argsDigest = digestToolArgs(args);
    const cached = activation.lastSuccessfulPureTool;
    if (cacheablePureTool(schema.name)
      && cached?.name === schema.name
      && cached.argsDigest === argsDigest) {
      activation.repeatGuardNote = `Reused the adjacent successful ${schema.name} result; skipped duplicate browser execution.`;
      const shaped = activation.current.withToolResult(schema.name, cached.result);
      activation.current = shaped.activation;
      recordTool(activation, schema, args);
      return shaped.result;
    }
    activation.repeatGuardNote = undefined;
    activation.lastSuccessfulPureTool = undefined;
    pushRunEvent(run, 'tool-request', { toolCallId, name: schema.name, args, argsDigest });
    if (run.nativeClient) {
      const native = await executeNativeTool(run, schema, args, toolCallId, activation);
      const shaped = native.activation.withToolResult(schema.name, native.result);
      activation.current = shaped.activation;
      activation.toolFailures.record(schema.name, { success: true, result: shaped.result });
      recordTool(activation, schema, args);
      return shaped.result;
    }
    const delivered = await waitForToolResult(run, toolCallId, schema.name, argsDigest);
    const followup = delivered && typeof delivered === 'object'
      && '__followup' in delivered && typeof delivered.__followup === 'string'
      ? delivered.__followup
      : null;
    if (followup) activation.followupText = followup;
    const shaped = activation.current.withToolResult(schema.name, delivered);
    activation.current = shaped.activation;
    if (isFailedToolResult(shaped.result)) throw new Error(toolFailureReason(shaped.result));
    activation.toolFailures.record(schema.name, { success: true, result: shaped.result });
    if (!followup) recordTool(activation, schema, args);
    if (cacheablePureTool(schema.name)) {
      activation.lastSuccessfulPureTool = { name: schema.name, argsDigest, result: shaped.result };
    }
    return shaped.result;
  } catch (error) {
    activation.toolFailures.record(schema.name, { success: false, result: error });
    throw error;
  } finally {
    release?.();
  }
}

export function createServerTools(
  run: ServerRun,
  schemas: readonly AgentToolSchema[],
  activation: ActivationState,
) {
  return Object.fromEntries(schemas.map((schema) => [schema.name, tool({
    description: schema.description,
    inputSchema: jsonSchema<Record<string, unknown>>(
      schema.input_schema as Parameters<typeof jsonSchema<Record<string, unknown>>>[0],
    ),
    execute: (args: Record<string, unknown>, options: { toolCallId: string }) => (
      executeBrowserTool(run, schema, args, options.toolCallId, activation)
    ),
    toModelOutput: ({ output }) => toolResultModelOutput(output, schema.name === 'load_skill'),
  })]));
}
