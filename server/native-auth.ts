import { timingSafeEqual } from 'node:crypto';
import type { IncomingMessage } from 'node:http';
import { isLoopbackAddress } from './loopback-address.ts';
import { NATIVE_DESKTOP_HEADER } from '../shared/native-desktop-contract.ts';

/**
 * Native desktop authentication is process-scoped: the WPF client generates a
 * high-entropy token and passes it only to its private child service. The
 * service still requires a loopback socket, so a token can never turn the
 * API into a public listener.
 */
export function nativeDesktopAuthorized(req: IncomingMessage): boolean {
  if (!isLoopbackAddress(req.socket.remoteAddress)) return false;
  const expected = process.env.OPENCHATCUT_NATIVE_TOKEN?.trim();
  const actual = req.headers[NATIVE_DESKTOP_HEADER];
  if (!expected || typeof actual !== 'string') return false;
  const left = Buffer.from(actual);
  const right = Buffer.from(expected);
  return left.length === right.length && timingSafeEqual(left, right);
}
