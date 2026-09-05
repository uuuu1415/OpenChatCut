/**
 * Contract shared by the native desktop client and the local service.
 *
 * The native client never renders a web document. It talks to the service over
 * loopback using this one explicit header and the JSON shapes below. Keeping
 * this boundary small prevents UI code from depending on Node modules or the
 * historical Electron IPC surface.
 */

import type { ProjectDoc } from '../src/editor/types.ts';

export const NATIVE_DESKTOP_HEADER = 'x-openchatcut-native-token';
export const NATIVE_DESKTOP_API_PREFIX = '/api/native';

export interface NativeProjectMeta {
  id: string;
  name: string;
  updatedAt: number;
  description?: string;
}

export interface NativeProjectSnapshot {
  meta: NativeProjectMeta;
  doc: ProjectDoc;
  revision: string;
}

export interface NativeProjectsResponse {
  projects: NativeProjectMeta[];
}

export interface NativeProjectActionRequest {
  action: { type: string; [key: string]: unknown };
}

export interface NativeHealthResponse {
  ok: true;
  protocol: 1;
  runtime: 'native-service';
}
