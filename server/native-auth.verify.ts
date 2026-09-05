import assert from 'node:assert/strict';
import type { IncomingMessage } from 'node:http';
import { nativeDesktopAuthorized } from './native-auth.ts';

const previous = process.env.OPENCHATCUT_NATIVE_TOKEN;
process.env.OPENCHATCUT_NATIVE_TOKEN = 'native-test-token';

function request(remoteAddress: string, token?: string): IncomingMessage {
  return {
    socket: { remoteAddress } as IncomingMessage['socket'],
    headers: token ? { 'x-openchatcut-native-token': token } : {},
  } as IncomingMessage;
}

try {
  assert.equal(nativeDesktopAuthorized(request('127.0.0.1', 'native-test-token')), true);
  assert.equal(nativeDesktopAuthorized(request('::1', 'native-test-token')), true);
  assert.equal(nativeDesktopAuthorized(request('192.0.2.10', 'native-test-token')), false);
  assert.equal(nativeDesktopAuthorized(request('127.0.0.1', 'wrong-token')), false);
  assert.equal(nativeDesktopAuthorized(request('127.0.0.1')), false);
  console.log('native-auth.verify: loopback token boundary OK');
} finally {
  if (previous === undefined) delete process.env.OPENCHATCUT_NATIVE_TOKEN;
  else process.env.OPENCHATCUT_NATIVE_TOKEN = previous;
}
