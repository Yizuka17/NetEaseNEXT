import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { methods, normalizeResult, readAllMessagesRequest, createScheduler, dispatch } from './bridge.mjs';

test('a local request loads only its endpoint without the upstream entry point or server', async () => {
  const require = createRequire(import.meta.url);
  const entry = require.resolve('@neteasecloudmusicapienhanced/api');
  const data = await dispatch({ id: 1, method: 'login_qr_create', params: { key: 'lazy-test', qrimg: true } });
  assert.match(data.data.qrimg, /^data:image\/png;base64,/);
  assert.equal(require.cache[entry], undefined);
  assert.equal(require.cache[join(dirname(entry), 'server.js')], undefined);
  assert.equal(require.cache[join(dirname(entry), 'util', 'request.js')], undefined);
});

test('independent reads overlap; writes remain ordered barriers with bounded concurrency', async () => {
  let active = 0, max = 0;
  const events = [];
  let finish;
  const done = new Promise(resolve => { finish = resolve; });
  const scheduler = createScheduler(async request => {
    active++; max = Math.max(max, active); events.push('start:' + request.id);
    await new Promise(resolve => setTimeout(resolve, request.delay));
    events.push('end:' + request.id); active--;
  }, finish, 2);
  scheduler.enqueue({ id: 'slow', method: 'login_status', delay: 100 });
  scheduler.enqueue({ id: 'fast', method: 'personalized', delay: 5 });
  scheduler.enqueue({ id: 'save', method: 'playlist_tracks', delay: 5 });
  scheduler.enqueue({ id: 'after', method: 'playlist_detail', delay: 5 });
  await done;
  assert.equal(max, 2);
  assert.ok(events.indexOf('end:fast') < events.indexOf('end:slow'));
  assert.ok(events.indexOf('start:save') > events.indexOf('end:slow'));
  assert.ok(events.indexOf('start:after') > events.indexOf('end:save'));
});

function bridge() {
  const child = spawn(process.execPath, ['bridge.mjs'], { cwd: import.meta.dirname, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
  return { child, async request(value) {
    child.stdin.write((typeof value === 'string' ? value : JSON.stringify(value)) + '\n');
    return JSON.parse((await lines.next()).value);
  } };
}

test('saved albums use authenticated library paging and playlist removal preserves server errors', async () => {
  const require = createRequire(import.meta.url);
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  assert.ok(methods.has('album_sublist'));
  let call;
  await require(join(root, 'module', 'album_sublist.js'))({ limit: 48, offset: 48 }, async (url, data) => {
    call = { url, data }; return { status: 200, body: { code: 200, data: [], hasMore: false } };
  });
  assert.deepEqual(call, { url: '/api/album/sublist', data: { limit: 48, offset: 48, total: true } });
  const result = await require(join(root, 'module', 'playlist_tracks.js'))({ op: 'del', pid: 900, tracks: '123' }, async (url, data) => {
    call = { url, data }; return { status: 200, body: { code: 403, msg: 'denied' } };
  });
  assert.deepEqual(call, { url: '/api/playlist/manipulate/tracks', data: { op: 'del', pid: 900, trackIds: '["123"]', imme: 'true' } });
  assert.equal(normalizeResult('playlist_tracks', result).code, 403);
});
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
  await invoke('playlist_delete', { id: 9 });
  assert.deepEqual(calls.pop(), { url: '/api/playlist/remove', data: { ids: '[9]' } });
  await invoke('song_order_update', { pid: 9, ids: '[123,456]' });
  assert.deepEqual(calls.pop(), { url: '/api/playlist/manipulate/tracks', data: { pid: 9, trackIds: '[123,456]', op: 'update' } });
  await invoke('playlist_create', { name: 'draft', privacy: 10, type: 'NORMAL' });
  assert.deepEqual(calls.pop(), { url: '/api/playlist/create', data: { name: 'draft', privacy: 10, type: 'NORMAL' } });
});
test('rejected playlist saves and recipient restrictions cannot report success', () => {
  assert.equal(normalizeResult('playlist_tracks', { body: { status: 200, body: { code: 502, msg: 'rejected' } } }).code, 502);
  assert.throws(() => normalizeResult('send_text', { body: { code: 200, blacklist: [200] } }));
  assert.throws(() => normalizeResult('send_song', { body: { code: 200, data: { success: false } } }));
  assert.equal(normalizeResult('send_text', { body: { code: 200, msgs: [] } }).code, 200);
  assert.throws(() => normalizeResult('share_resource', { body: { code: 200, data: { success: false } } }));
});
test('my podcasts requests subscribed content with pagination', async () => {
  const require = createRequire(import.meta.url);
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  assert.ok(methods.has('dj_sublist'));
  let requested;
  await require(join(root, 'module', 'dj_sublist.js'))({ limit: 48, offset: 48 }, async (url, data) => {
    requested = { url, data }; return { status: 200, body: { code: 200, djRadios: [] } };
  });
  assert.deepEqual(requested, { url: '/api/djradio/get/subed', data: { limit: 48, offset: 48, total: true } });
});
test('server read acknowledgement uses the fixed official count-clear route', async () => {
  const require = createRequire(import.meta.url);
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  assert.ok(methods.has('msg_read_all'));
  assert.ok(!methods.has('api'));
  const params = readAllMessagesRequest({ cookie: 'unit-test', timestamp: 123, uri: '/api/msg/private/delete', data: { userId: 99 }, crypto: 'eapi' });
  let requested;
  await require(join(root, 'module', 'api.js'))(params, async (uri, data, options) => {
    requested = { uri, data, crypto: options.crypto }; return { status: 200, body: { code: 200 } };
  });
  assert.deepEqual(requested, { uri: '/api/msg/count/clear', data: {}, crypto: 'weapi' });
  assert.equal(params.cookie, 'unit-test');
  assert.equal(params.timestamp, 123);
});

test('own events paginate and publishing sends only the selected draft resource', async () => {
  const require = createRequire(import.meta.url);
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  const calls = [];
  const request = async (url, data) => { calls.push({ url, data }); return { status: 200, body: { code: 200 } }; };
  assert.ok(methods.has('user_event')); assert.ok(methods.has('share_resource'));
  await require(join(root, 'module', 'user_event.js'))({ uid: 7, lasttime: 123, limit: 20 }, request);
  assert.deepEqual(calls.pop(), { url: '/api/event/get/7', data: { getcounts: true, time: 123, limit: 20, total: false, fromRN: 'true' } });
  await require(join(root, 'module', 'share_resource.js'))({ type: 'noresource', msg: 'draft', id: '' }, request);
  assert.deepEqual(calls.pop(), { url: '/api/share/friends/resource', data: { type: 'noresource', msg: 'draft', id: '' } });
  await require(join(root, 'module', 'share_resource.js'))({ type: 'song', msg: 'draft', id: '123' }, request);
  assert.deepEqual(calls.pop(), { url: '/api/share/friends/resource', data: { type: 'song', msg: 'draft', id: '123' } });
});
