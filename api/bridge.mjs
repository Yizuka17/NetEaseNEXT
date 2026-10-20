import { createInterface } from 'node:readline';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { readFile } from 'node:fs/promises';
import { closeSync, openSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';

// A private stdin/stdout pipe: no HTTP listener, port, remote callers or cookie in argv.
export const methods = new Set([
  'cloudsearch', 'personalized', 'playlist_detail', 'playlist_track_all',
  'song_url_v1', 'lyric', 'login_qr_key', 'login_qr_create', 'login_qr_check',
  'login_status', 'user_playlist', 'recommend_songs', 'likelist', 'like',
  'top_artists', 'artist_album', 'album', 'album_sublist', 'song_detail', 'song_music_detail',
  'vip_info', 'event', 'msg_notices', 'msg_comments', 'msg_private', 'msg_private_history', 'song_download_url_v1',
  'dj_recommend', 'dj_program', 'dj_sublist',
  'playlist_tracks', 'resource_like', 'comment_event', 'comment_music', 'comment_playlist', 'comment_album', 'comment_dj', 'comment_mv', 'comment_video', 'comment', 'comment_like', 'send_text', 'send_song', 'send_album', 'send_playlist', 'msg_forwards',
  'song_red_count', 'song_like_check',
  'playlist_subscribe', 'playlist_create', 'playlist_delete', 'song_order_update',
  'artists', 'artist_detail', 'artist_desc', 'artist_mv', 'mv_url', 'pl_count',
  'msg_read_all', 'user_event', 'share_resource',
]);
// The official web client's "一键已读" operation. Keep the route and payload
// fixed; the pipe must never expose the upstream generic API dispatcher.
export function readAllMessagesRequest(params = {}) {
  return { cookie: params.cookie, timestamp: params.timestamp, uri: '/api/msg/count/clear', data: {}, crypto: 'weapi' };
}
const require = createRequire(import.meta.url);
export function normalizeResult(method, result) {
  let body = result.body;
  // This upstream method wraps the request response one extra time. Preserve
  // the business status so a rejected save cannot look like a successful one.
  if (method === 'playlist_tracks' && body?.body && typeof body.body === 'object') body = body.body;
  if (method.startsWith('send_') && (body?.blacklist?.length || body?.data === false || body?.data?.success === false))
    throw { body: { msg: '对方当前无法接收这条私信' } };
  if (method === 'share_resource' && (body?.data === false || body?.success === false || body?.data?.success === false))
    throw { body: { msg: body?.msg || '发布失败，请重试' } };
  return body;
}
let upstreamRoot;
let requestModule;
function upstreamMethod(method) {
  if (!upstreamRoot) {
    upstreamRoot = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
    // request.js expects this file even when anonymous login has not run.
    closeSync(openSync(join(tmpdir(), 'anonymous_token'), 'a'));
  }
  return require(join(upstreamRoot, 'module', `${method}.js`));
}
function upstreamRequest(...args) {
  requestModule ??= require(join(upstreamRoot, 'util', 'request.js'));
  return requestModule(...args);
}
let playbackKeys;
async function hasPlaybackKeys() {
  try { return !!JSON.parse(await readFile(join(tmpdir(), 'xeapi_public_key'), 'utf8')).sk; }
  catch { return false; }
}
export async function dispatch(message) {
  if (!Number.isSafeInteger(message?.id) || !methods.has(message?.method))
    throw new Error('不支持的 API 方法');
  // Loading the package entry point eagerly imports every endpoint and the HTTP
  // server. The private pipe needs only the allowlisted module being called.
  const method = upstreamMethod(message.method === 'msg_read_all' ? 'api' : message.method);
  const parameters = { ...message.params, timeout: 20000, unblock: 'false' };
  if (typeof parameters.cookie === 'string') parameters.cookie = require(join(upstreamRoot, 'util', 'index.js')).cookieToJson(parameters.cookie);
  else parameters.cookie ??= {};
  // Key registration in this pinned dependency has no timeout. Do not repeat
  // that network round trip after every idle worker exit; EAPI is supported by
  // the same endpoint and applies the same account/rights checks.
  if (message.method === 'song_url_v1' && !(await (playbackKeys ??= hasPlaybackKeys()))) parameters.crypto = 'eapi';
  const result = await method(message.method === 'msg_read_all' ? readAllMessagesRequest(parameters) : parameters, upstreamRequest);
  return normalizeResult(message.method, result);
}

const writes = new Set(['like', 'playlist_tracks', 'song_order_update', 'playlist_delete', 'playlist_subscribe', 'playlist_create', 'resource_like', 'comment', 'comment_like', 'send_text', 'send_song', 'send_album', 'send_playlist', 'msg_read_all', 'share_resource']);

// Reads can finish out of order. Writes are FIFO barriers, preserving the
// ordering of saves, removals and acknowledgments relative to queued reads.
export function createScheduler(handle, onIdle = () => {}, limit = 4) {
  const queue = [];
  let active = 0, writing = false;
  function pump() {
    while (!writing && active < limit && queue.length) {
      const next = queue[0], write = writes.has(next.method);
      if (write && active) break;
      queue.shift(); active++; writing = write;
      Promise.resolve().then(() => handle(next)).catch(() => {}).finally(() => {
        active--; if (write) writing = false;
        pump(); if (!active && !queue.length) onIdle();
      });
      if (write) break;
    }
  }
  return { get isIdle() { return !active && !queue.length; }, enqueue(request) { queue.push(request); pump(); } };
}

export function serve() {
  process.env.ENABLE_GENERAL_UNBLOCK = 'false';
  // Upstream diagnostics can contain cookies. Keep them off the protocol and logs.
  for (const key of ['log', 'info', 'debug', 'warn', 'error']) console[key] = () => {};
  let idle;
  const arm = () => { clearTimeout(idle); idle = setTimeout(() => process.exit(0), 15000); idle.unref(); };
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  const scheduler = createScheduler(async request => {
      try {
        const data = await dispatch(request);
        process.stdout.write(JSON.stringify({ id: request.id, data }) + '\n');
      } catch (error) {
        process.stdout.write(JSON.stringify({ id: request?.id ?? null, error: typeof error?.body?.msg === 'string' ? error.body.msg : 'API 请求失败，请检查网络或稍后重试。' }) + '\n');
      }
  }, arm);
  input.on('line', line => {
    clearTimeout(idle);
    try {
      if (line.length > 131072) throw new Error('请求过大');
      scheduler.enqueue(JSON.parse(line));
    } catch { process.stdout.write(JSON.stringify({ id: null, error: 'API 请求无效' }) + '\n'); if (scheduler.isIdle) arm(); }
  });
  input.on('close', () => process.exit(0));
  arm();
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) serve();
