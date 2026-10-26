import { createServer } from 'node:http';
import { dispatch } from '../api/bridge.mjs';

// USB-only developer bridge. It binds to PC loopback and exposes a fixed set
// of account/player actions. Login cookies stay in memory on this Node process.
const port = 32157;
let qrKey = null;
let sessionCookie = null;

function result(response) {
  if (response?.code !== undefined && ![200, 800, 801, 802, 803].includes(response.code))
    throw new Error('网易云 API 请求失败');
  return response;
}

async function upstream(method, params = {}) {
  return result(await dispatch({
    id: 1, method, params: sessionCookie ? { ...params, cookie: sessionCookie } : params
  }));
}

function profileInfo(data) {
  const p = data?.data?.profile;
  if (!p || !Number.isSafeInteger(p.userId)) return null;
  return {
    userId: p.userId,
    nickname: typeof p.nickname === 'string' ? p.nickname : '网易云用户',
    avatarUrl: typeof p.avatarUrl === 'string' ? p.avatarUrl : ''
  };
}

function safeTrack(s) {
  const al = s?.al ?? s?.album ?? {};
  const ar = s?.ar ?? s?.artists ?? [];
  return {
    id: Number(s?.id) || 0,
    name: typeof s?.name === 'string' ? s.name : '',
    dt: Number(s?.dt ?? s?.duration) || 0,
    al: {
      id: Number(al.id) || 0,
      name: typeof al.name === 'string' ? al.name : '',
      picUrl: typeof al.picUrl === 'string' ? al.picUrl : ''
    },
    ar: Array.isArray(ar) ? ar.map(a => ({
      id: Number(a.id) || 0,
      name: typeof a.name === 'string' ? a.name : ''
    })) : []
  };
}

async function handle(action, p) {
  switch (action) {
    case 'login_start': {
      qrKey = null;
      const keyResponse = await upstream('login_qr_key');
      const key = keyResponse?.data?.unikey;
      if (typeof key !== 'string' || !key) throw new Error('获取登录二维码失败');
      const qrResponse = await upstream('login_qr_create', { key, qrimg: true });
      const qrimg = qrResponse?.data?.qrimg;
      if (typeof qrimg !== 'string' || !qrimg.startsWith('data:image/'))
        throw new Error('登录二维码内容无效');
      qrKey = key;
      return { qrimg };
    }
    case 'login_poll': {
      if (!qrKey) throw new Error('请刷新登录二维码');
      const check = await upstream('login_qr_check', { key: qrKey });
      const code = check?.code;
      if (code === 800) qrKey = null;
      if (code === 803) {
        if (typeof check.cookie !== 'string' || !check.cookie)
          throw new Error('登录没有返回有效会话');
        sessionCookie = check.cookie;
        qrKey = null;
        const status = await upstream('login_status');
        return { code, profile: profileInfo(status) };
      }
      if (![800, 801, 802].includes(code)) throw new Error('扫码状态无法确认');
      return { code };
    }
    case 'login_status': {
      if (!sessionCookie) return { authenticated: false };
      const status = await upstream('login_status');
      const profile = profileInfo(status);
      if (!profile) {
        sessionCookie = null;
        return { authenticated: false };
      }
      return { authenticated: true, profile };
    }
    case 'my_playlists': {
      if (!sessionCookie) throw new Error('请先扫码登录');
      const profile = profileInfo(await upstream('login_status'));
      if (!profile) throw new Error('登录已失效，请重新登录');
      const list = await upstream('user_playlist', { uid: profile.userId, limit: 50, offset: 0 });
      return { playlists: (Array.isArray(list?.playlist) ? list.playlist : []).map(item => ({
        id: Number(item.id) || 0,
        name: typeof item.name === 'string' ? item.name : '',
        cover: typeof item.coverImgUrl === 'string' ? item.coverImgUrl : ''
      })) };
    }
    case 'playlist_tracks': {
      if (!sessionCookie) throw new Error('请先扫码登录');
      const id = Number(p.id);
      if (!Number.isSafeInteger(id) || id <= 0) throw new Error('无效歌单');
      const tracks = await upstream('playlist_track_all', { id, limit: 100, offset: 0 });
      return { songs: (Array.isArray(tracks?.songs) ? tracks.songs : []).map(safeTrack) };
    }
    case 'play_url': {
      if (!sessionCookie) throw new Error('请先扫码登录');
      const id = Number(p.id);
      if (!Number.isSafeInteger(id) || id <= 0) throw new Error('无效歌曲');
      const response = await upstream('song_url_v1', { id: String(id), level: 'standard' });
      const url = response?.data?.find?.(x => Number(x.id) === id)?.url
        ?? response?.data?.[0]?.url;
      if (typeof url !== 'string' || !/^https?:\/\//i.test(url))
        throw new Error('当前账号无法获取歌曲播放地址，可能受版权或会员权限限制');
      return { url };
    }
    case 'logout':
      sessionCookie = null;
      qrKey = null;
      return { authenticated: false };
    default:
      throw new Error('不支持的操作');
  }
}

function reply(res, code, payload) {
  res.writeHead(code, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'pragma': 'no-cache',
    'x-content-type-options': 'nosniff'
  });
  res.end(JSON.stringify(payload));
}

const server = createServer(async (req, res) => {
  if (req.url === '/health' && req.method === 'GET') {
    reply(res, 200, { ok: true });
    return;
  }
  // Deny browser-originated calls; no CORS and no arbitrary upstream methods.
  if (req.url !== '/api' || req.method !== 'POST' || req.headers.origin) {
    reply(res, 404, { error: 'Not found' });
    return;
  }
  try {
    if (!String(req.headers['content-type'] || '').startsWith('application/json'))
      throw new Error('Invalid content type');
    let body = '';
    for await (const chunk of req) {
      body += chunk.toString('utf8');
      if (body.length > 8192) throw new Error('Request too large');
    }
    const call = JSON.parse(body);
    if (!call || typeof call !== 'object' ||
        !call.params || typeof call.params !== 'object' || Array.isArray(call.params) ||
        Object.hasOwn(call.params, 'cookie'))
      throw new Error('Invalid request');
    reply(res, 200, { data: await handle(call.action, call.params) });
  } catch (error) {
    // No cookies, private API bodies or raw upstream errors in network replies.
    const safe = [
      '请刷新登录二维码', '请先扫码登录', '登录已失效，请重新登录',
      '无效歌单', '无效歌曲', '当前账号无法获取歌曲播放地址，可能受版权或会员权限限制'
    ];
    reply(res, 400, { error: safe.includes(error?.message)
      ? error.message : '请求失败，请检查电脑端 Node 服务及网络连接。' });
  }
});
server.listen(port, '127.0.0.1', () => {
  console.log('Android QR-login bridge (USB only) on 127.0.0.1:32157');
  console.log('Connect device and run: adb reverse tcp:32157 tcp:32157');
});
