// Optional real-network contract check; no account or cookie is required.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
const child = spawn(process.execPath, ['bridge.mjs'], { cwd: import.meta.dirname, stdio: ['pipe', 'pipe', 'ignore'] });
const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
let id = 0;
async function call(method, params = {}) {
  child.stdin.write(JSON.stringify({ id: ++id, method, params }) + '\n');
  const response = JSON.parse((await lines.next()).value);
  if (response.error) throw new Error(`${method}: ${response.error}`);
  return response.data;
}
const watchdog = setTimeout(() => { child.kill(); process.exitCode = 1; }, 55000);
try {
  const discover = await call('personalized', { limit: 6 });
  console.log(`Discover: ${discover.result?.length ?? 0} playlists (code ${discover.code})`);
  const search = await call('cloudsearch', { keywords: '陈奕迅', type: 1, limit: 5 });
  const songs = search.result?.songs ?? [];
  if (!songs.length) throw new Error(`Search returned no songs (code ${search.code})`);
  console.log(`Search: ${songs.length} tracks`);
  const song = songs[0];
  const lyric = await call('lyric', { id: song.id });
  console.log(`Lyrics: ${lyric.lrc?.lyric?.length ?? 0} characters`);
  const url = await call('song_url_v1', { id: song.id, level: 'standard' });
  console.log(`Stream: ${url.data?.[0]?.url ? 'URL available' : 'account/rights required'}`);
  if (discover.result?.length) {
    const playlist = await call('playlist_track_all', { id: discover.result[0].id, limit: 5, offset: 0 });
    console.log(`Playlist: ${playlist.songs?.length ?? 0} tracks`);
  }
  const qr = await call('login_qr_key');
  console.log(`QR login key: ${qr.data?.unikey ? 'available' : 'unavailable'}`);
} catch (error) { console.error(error.message); process.exitCode = 1; }
finally { clearTimeout(watchdog); child.stdin.end(); }
