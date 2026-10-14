import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { methods, normalizeResult } from './bridge.mjs';

function bridge() {
  const child = spawn(process.execPath, ['bridge.mjs'], { cwd: import.meta.dirname, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
  return { child, async request(value) {
    child.stdin.write((typeof value === 'string' ? value : JSON.stringify(value)) + '\n');
    return JSON.parse((await lines.next()).value);
  } };
}
test('protocol recovers from malformed input; arbitrary upstream methods are rejected', { timeout: 15000 }, async () => {
  const { child, request } = bridge();
  try {
    assert.ok((await request('{')).error);
    assert.equal((await request({ id: 12, method: 'logout' })).id, 12);
    const rejected = await request({ id: 13, method: 'server' });
    assert.ok(rejected.error);
    const qr = await request({ id: 14, method: 'login_qr_create', params: { key: 'local-test', qrimg: true } });
    assert.equal(qr.id, 14);
    assert.match(qr.data.data.qrimg, /^data:image\/png;base64,/);
    assert.match(qr.data.data.qrurl, /local-test/);
  } finally { child.stdin.end(); await once(child, 'exit'); }
});
test('closing the parent pipe terminates the bridge without an orphan process', { timeout: 5000 }, async () => {
  const { child } = bridge();
  child.stdin.end();
  const [code] = await once(child, 'exit');
  assert.equal(code, 0);
});

test('social writes use the intended recipient, thread and operation without network access', async () => {
  const require = createRequire(import.meta.url);
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  const calls = [];
  const request = async (url, data) => { calls.push({ url, data }); return { status: 200, body: { code: 200 } }; };
  async function invoke(name, params) {
    assert.ok(methods.has(name));
    return require(join(root, 'module', `${name}.js`))(params, request);
  }
  await invoke('send_text', { user_ids: '200', msg: 'draft' });
  assert.deepEqual(calls.pop(), { url: '/api/msg/private/send', data: { type: 'text', msg: 'draft', userIds: '[200]' } });
  await invoke('comment', { t: 2, type: 6, threadId: 'A_EV_2_12_1', commentId: 22, content: 'reply' });
  assert.deepEqual(calls.pop(), { url: '/api/resource/comments/reply', data: { threadId: 'A_EV_2_12_1', commentId: 22, content: 'reply' } });
  await invoke('resource_like', { t: 0, type: 6, threadId: 'A_EV_2_12_1' });
  assert.deepEqual(calls.pop(), { url: '/api/resource/unlike', data: { threadId: 'A_EV_2_12_1' } });
  await invoke('comment_like', { t: 1, type: 0, id: 123, cid: 22 });
  assert.deepEqual(calls.pop(), { url: '/api/v1/comment/like', data: { threadId: 'R_SO_4_123', commentId: 22 } });
  await invoke('playlist_tracks', { op: 'add', pid: 9, tracks: '123' });
  const saved = calls.pop();
  assert.equal(saved.data.op, 'add'); assert.equal(saved.data.pid, 9); assert.equal(saved.data.trackIds, '["123"]');
  await invoke('like', { id: 123, like: 'false' });
  assert.equal(calls.pop().data.like, false);
  await invoke('playlist_subscribe', { id: 9, t: 2 });
  assert.equal(calls.pop().url, '/api/playlist/unsubscribe');
  await invoke('playlist_create', { name: 'draft', privacy: 10, type: 'NORMAL' });
  assert.deepEqual(calls.pop(), { url: '/api/playlist/create', data: { name: 'draft', privacy: 10, type: 'NORMAL' } });
});
test('rejected playlist saves and recipient restrictions cannot report success', () => {
  assert.equal(normalizeResult('playlist_tracks', { body: { status: 200, body: { code: 502, msg: 'rejected' } } }).code, 502);
  assert.throws(() => normalizeResult('send_text', { body: { code: 200, blacklist: [200] } }));
  assert.throws(() => normalizeResult('send_song', { body: { code: 200, data: { success: false } } }));
  assert.equal(normalizeResult('send_text', { body: { code: 200, msgs: [] } }).code, 200);
});
