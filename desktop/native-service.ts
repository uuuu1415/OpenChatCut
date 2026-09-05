import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { startEmbeddedServer } from './embedded-server.ts';

/**
 * Headless local service host for the native desktop client.
 *
 * It intentionally has no renderer, no Electron imports and no browser
 * lifecycle. The WPF process owns the window; this process owns the existing
 * storage, MCP, AI, media and export services.
 */
const distDir = join(process.cwd(), 'dist');
const portFile = process.env.OPENCHATCUT_NATIVE_PORT_FILE?.trim();

const { server, port } = await startEmbeddedServer(distDir);
const line = `OPENCHATCUT_NATIVE_PORT=${port}`;
console.log(line);
if (portFile) await writeFile(portFile, `${port}\n`, { encoding: 'utf8' });

const shutdown = (): void => {
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3_000).unref();
};
process.once('SIGINT', shutdown);
process.once('SIGTERM', shutdown);
