using NetEaseNEXT.Models;
using System.Text.Json;

namespace NetEaseNEXT.Services;

// Short-lived authorization leases, scoped by session and requested quality.
// Playback URLs stay in memory and are never written to the metadata cache.
internal sealed class TrackUrls(ApiClient api) : IDisposable
{
    private sealed record Lease(JsonElement Info, DateTimeOffset Expires);
    private sealed class Batch { public required string Scope; public required long[] Ids; public readonly CancellationTokenSource Cancel = new(TimeSpan.FromSeconds(25)); public Task Task = Task.CompletedTask; public int Readers; }
    private readonly Dictionary<string, Lease> _leases = new();
    private readonly Dictionary<string, Batch> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;
    private string Scope(string level) => MetadataCache.Key(api.Cookie, "song_url_v1", level);
    private static string Key(string scope, long id) => scope + ":" + id;
    private Batch? Start(IEnumerable<long> ids, string level)
    {
        var scope = Scope(level);
        lock (_gate)
        {
            if (_disposed) return null;
            var needed = ids.Where(id => id > 0).Distinct().Take(12).Where(id => !_pending.ContainsKey(Key(scope, id)) && (! _leases.TryGetValue(Key(scope, id), out var lease) || lease.Expires <= DateTimeOffset.UtcNow)).ToArray();
            if (needed.Length == 0) return null;
            var batch = new Batch { Scope = scope, Ids = needed };
            foreach (var id in needed) _pending[Key(scope, id)] = batch;
            batch.Task = FetchAsync(batch, level); return batch;
        }
    }
    private async Task FetchAsync(Batch batch, string level)
    {
        try
        {
            var result = await api.CallAsync("song_url_v1", new { id = string.Join(',', batch.Ids), level }, batch.Cancel.Token);
            if (batch.Scope != Scope(level)) return;
            lock (_gate)
            {
                if (batch.Cancel.IsCancellationRequested) return;
                foreach (var info in result.GetProperty("data").EnumerateArray())
                {
                    var id = info.Number("id"); if (id == 0 && batch.Ids.Length == 1) id = batch.Ids[0];
                    if (id <= 0) continue;
                    var life = info.Text("url").Length == 0 ? 15 : Math.Max(0, Math.Min(600, info.Number("expi") is > 0 and var expiry ? expiry - 30 : 270));
                    _leases[Key(batch.Scope, id)] = new(info.Clone(), DateTimeOffset.UtcNow.AddSeconds(life));
                }
                while (_leases.Count > 256) _leases.Remove(_leases.MinBy(p => p.Value.Expires).Key);
            }
        }
        finally
        {
            lock (_gate) foreach (var id in batch.Ids) if (_pending.GetValueOrDefault(Key(batch.Scope, id)) == batch) _pending.Remove(Key(batch.Scope, id));
            batch.Cancel.Dispose();
        }
    }
    internal async Task<(JsonElement Info, bool Cached)> GetAsync(Track track, string level, IEnumerable<Track> neighbors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = Scope(level); var key = Key(scope, track.Id);
        Batch? batch;
        lock (_gate)
        {
            if (_leases.TryGetValue(key, out var lease) && lease.Expires > DateTimeOffset.UtcNow) return (lease.Info, true);
            Start(new[] { track.Id }.Concat(neighbors.Select(t => t.Id)), level);
            batch = _pending.GetValueOrDefault(key); if (batch != null) batch.Readers++;
        }
        try { if (batch != null) await batch.Task.WaitAsync(ct); }
        finally { if (batch != null) lock (_gate) batch.Readers--; }
        ct.ThrowIfCancellationRequested();
        lock (_gate) if (_leases.TryGetValue(key, out var ready) && ready.Expires > DateTimeOffset.UtcNow) return (ready.Info, false);
        throw new InvalidOperationException("播放地址未就绪，请重试。");
    }
    internal async Task WarmAsync(IEnumerable<Track> tracks, string level)
    {
        var batch = Start(tracks.Select(t => t.Id), level);
        if (batch != null) try { await batch.Task; } catch { }
    }
    internal void SuspendWarm()
    {
        lock (_gate)
        {
            foreach (var batch in _pending.Values.Distinct().Where(b => b.Readers == 0).ToArray())
            {
                // A foreground play immediately after hiding must start a new
                // batch instead of joining a canceled warm-up still unwinding.
                foreach (var id in batch.Ids) if (_pending.GetValueOrDefault(Key(batch.Scope, id)) == batch) _pending.Remove(Key(batch.Scope, id));
                batch.Cancel.Cancel();
            }
        }
    }
    internal void Invalidate(long id, string level) { lock (_gate) _leases.Remove(Key(Scope(level), id)); }
    public void Dispose() { lock (_gate) { _disposed = true; foreach (var batch in _pending.Values.Distinct()) batch.Cancel.Cancel(); _leases.Clear(); } }
}
