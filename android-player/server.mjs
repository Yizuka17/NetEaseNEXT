import { createServer } from 'node:http';
import { dispatch } from '../api/bridge.mjs';

// For USB debugging only. Never expose this bridge to LAN or the Internet.
const allowed = new Set(['song_detail', 'song_url_v1']);
const port = 32157;

const reply = (res, status, body) => {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff'
  });
  res.end(JSON.stringify(body));
};

createServer(async (req, res) => {
  if (req.url === '/health' && req.method === 'GET') {
    reply(res, 200, { ok: true });
    return;
  }
  if (req.url !== '/api' || req.method !== 'POST') {
    reply(res, 404, { error: 'Not found' });
    return;
  }
  try {
    let body = '';
    for await (const chunk of req) {
      body += chunk.toString('utf8');
      if (body.length > 8192) throw new Error('Request too large');
    }
    const call = JSON.parse(body);
    if (!allowed.has(call.method) || !call.params
        || typeof call.params !== 'object' || Array.isArray(call.params)
        || Object.prototype.hasOwnProperty.call(call.params, 'cookie'))
      throw new Error('Unsupported request');
    const data = await dispatch({ id: 1, method: call.method, params: call.params });
    reply(res, 200, { data });
  } catch {
    // Do not leak upstream exceptions into network responses.
    reply(res, 502, { error: '网易云 API 请求失败；请检查歌曲 ID 与网络连接。' });
  }
}).listen(port, '127.0.0.1', () => {
  console.log('Read-only preview bridge on http://127.0.0.1:32157');
  console.log('On Android via USB: adb reverse tcp:32157 tcp:32157');
});
