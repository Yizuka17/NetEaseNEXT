import { createInterface } from 'node:readline';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';

// A private stdin/stdout pipe: no HTTP listener, port, remote callers or cookie in argv.
export const methods = new Set([
  'cloudsearch', 'personalized', 'playlist_detail', 'playlist_track_all',
  'song_url_v1', 'lyric', 'login_qr_key', 'login_qr_create', 'login_qr_check',
  'login_status', 'user_playlist', 'recommend_songs', 'likelist', 'like',
  'top_artists', 'artist_album', 'album', 'song_detail', 'song_music_detail',
  'vip_info', 'event', 'msg_notices', 'msg_comments', 'msg_private', 'msg_private_history', 'song_download_url_v1',
  'dj_recommend', 'dj_program',
  'playlist_tracks', 'resource_like', 'comment_event', 'comment_music', 'comment_playlist', 'comment_album', 'comment_dj', 'comment_mv', 'comment_video', 'comment', 'comment_like', 'send_text', 'send_song', 'send_album', 'send_playlist', 'msg_forwards',
  'song_red_count', 'song_like_check',
  'playlist_subscribe', 'playlist_create',
]);
const require = createRequire(import.meta.url);
export function normalizeResult(method, result) {
  let body = result.body;
  // This upstream method wraps the request response one extra time. Preserve
  // the business status so a rejected save cannot look like a successful one.
  if (method === 'playlist_tracks' && body?.body && typeof body.body === 'object') body = body.body;
  if (method.startsWith('send_') && (body?.blacklist?.length || body?.data === false || body?.data?.success === false))
    throw { body: { msg: '对方当前无法接收这条私信' } };
  return body;
}
let api;
let playbackKeys;
async function initializePlaybackKeys() {
  const root = dirname(require.resolve('@neteasecloudmusicapienhanced/api'));
  const { getXeapiPublicKey } = require(join(root, 'util', 'xeapiKey.js'));
  const file = join(tmpdir(), 'xeapi_public_key');
  let current = {};
  try { current = JSON.parse(await readFile(file, 'utf8')); } catch { }
  const key = await getXeapiPublicKey(current, global.deviceId);
  await writeFile(file, JSON.stringify(key));
}
export async function dispatch(message) {
  if (!Number.isSafeInteger(message?.id) || !methods.has(message?.method))
    throw new Error('不支持的 API 方法');
  api ??= require('@neteasecloudmusicapienhanced/api');
  if (message.method === 'song_url_v1') {
    // Some upstream key responses omit `sk`. Use the package's supported EAPI
    // transport in that case; account and copyright checks still apply.
    playbackKeys ??= initializePlaybackKeys().then(() => true).catch(() => false);
  }
  const parameters = { ...message.params, timeout: 20000, unblock: 'false' };
  if (message.method === 'song_url_v1' && !(await playbackKeys)) parameters.crypto = 'eapi';
  const result = await api[message.method](parameters);
  return normalizeResult(message.method, result);
}

export function serve() {
  process.env.ENABLE_GENERAL_UNBLOCK = 'false';
  // Upstream diagnostics can contain cookies. Keep them off the protocol and logs.
  for (const key of ['log', 'info', 'debug', 'warn', 'error']) console[key] = () => {};
  let idle;
  let chain = Promise.resolve();
  const arm = () => { clearTimeout(idle); idle = setTimeout(() => process.exit(0), 45000); idle.unref(); };
  const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
  input.on('line', line => {
    clearTimeout(idle);
    chain = chain.then(async () => {
      let request;
      try {
        if (line.length > 131072) throw new Error('请求过大');
        request = JSON.parse(line);
        const data = await dispatch(request);
        process.stdout.write(JSON.stringify({ id: request.id, data }) + '\n');
      } catch (error) {
        process.stdout.write(JSON.stringify({ id: request?.id ?? null, error: typeof error?.body?.msg === 'string' ? error.body.msg : 'API 请求失败，请检查网络或稍后重试。' }) + '\n');
      } finally { arm(); }
    });
  });
  input.on('close', () => chain.finally(() => process.exit(0)));
  arm();
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) serve();
