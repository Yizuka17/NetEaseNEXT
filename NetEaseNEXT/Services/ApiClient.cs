using System.Text.Json;
using NetEaseNEXT.Api;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

public sealed class ApiClient : IDisposable
{
    private readonly ApiClient? _sessionSource;
    private readonly NativeApi _native = new();
    private readonly ApiScheduler _scheduler = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>>? _fixture;
    public ApiClient(SettingsStore? settings = null) { _cacheDirectory = (settings ?? SettingsStore.Load()).CacheDirectory; _cookie = SettingsStore.ReadCookie(); }
    public ApiClient(ApiClient sessionSource) { _sessionSource = sessionSource; }
    internal ApiClient(Func<string, JsonElement, CancellationToken, Task<JsonElement>> fixture, string cacheDirectory, string cookie = "") { _fixture = fixture; _cacheDirectory = cacheDirectory; _cookie = cookie; }
    private readonly MetadataCache _cache = new();
    private MetadataCache Cache => _sessionSource?.Cache ?? _cache;
    private int _requestCount;
    internal int RequestCount => Volatile.Read(ref _requestCount);
    private int _profileRequestCount;
    internal int ProfileRequestCount => Volatile.Read(ref _profileRequestCount);
    private bool _disposed;
    private string _cacheDirectory = "", _cookie = "";
    public string CacheDirectory => _sessionSource?.CacheDirectory ?? _cacheDirectory;
    public string Cookie => _sessionSource?.Cookie ?? _cookie;
    public bool HasSession => Cookie.Length > 0;
    internal bool IsWorkerRunning => false; // Native HTTP starts no child processes.
    public void SetCookie(string cookie) { SettingsStore.SaveCookie(cookie); _cookie = cookie; Cache.Clear(CacheDirectory); }
    public async Task<JsonElement> CallCachedAsync(string method, object? parameters = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var policy = MetadataCache.Policy(method);
        if (policy.Lifetime == TimeSpan.Zero) return await CallAsync(method, parameters, cancellation);
        var cookie = Cookie; var key = MetadataCache.Key(cookie, method, parameters); var directory = CacheDirectory;
        var version = Cache.Version;
        var cached = await Task.Run(() => Cache.TryGet(directory, key, policy.Disk, out var value) ? (JsonElement?)value : null, cancellation);
        if (cached is JsonElement hit) return hit;
        var result = await CallAsync(method, parameters, cancellation);
        if (Cookie == cookie && !cancellation.IsCancellationRequested) await Task.Run(() => Cache.Put(directory, key, result, policy.Lifetime, policy.Disk, version), cancellation);
        return result;
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cookie = Cookie;
        var data = await _scheduler.Schedule(ApiEndpoints.Writes.Contains(method), async () =>
        {
            Interlocked.Increment(ref _requestCount);
            if (method == "login_status") Interlocked.Increment(ref _profileRequestCount);
            Diagnostics.StartupTrace.Mark("Native request: " + method);
            return _fixture != null
                ? await _fixture(method, JsonSerializer.SerializeToElement(parameters ?? new { }), timeout.Token).ConfigureAwait(false)
                : await _native.CallAsync(method, parameters, cookie, timeout.Token).ConfigureAwait(false);
        }, timeout.Token);
        if (data.TryGetProperty("code", out var code) && int.TryParse(code.ToString(), out var status) && status is not (200 or 800 or 801 or 802 or 803))
            throw new MusicApiException(status, data.Text("msg") is { Length: > 0 } msg ? msg : $"接口返回 {status}，请稍后重试或登录。");
        if (method is "like" or "playlist_tracks" or "song_order_update" or "playlist_delete" or "playlist_subscribe" or "playlist_create") await Task.Run(() => Cache.Clear(CacheDirectory));
        return data.Clone();
    }
    internal Task<JsonElement> RemovePlaylistTrackAsync(long playlistId, long ownerId, long accountId, long trackId)
        => RemovePlaylistTracksAsync(playlistId, ownerId, accountId, [trackId]);
    internal Task<JsonElement> RemovePlaylistTracksAsync(long playlistId, long ownerId, long accountId, IReadOnlyList<long> trackIds)
    {
        if (playlistId <= 0 || trackIds.Count == 0 || trackIds.Any(id => id <= 0) || accountId <= 0 || ownerId != accountId)
            throw new InvalidOperationException("只能从自己创建的歌单中移除歌曲");
        return CallAsync("playlist_tracks", new { op = "del", pid = playlistId, tracks = string.Join(',', trackIds.Distinct()) });
    }
    public async Task<List<Track>> SearchAsync(string query, int offset, CancellationToken ct)
    {
        var json = await CallAsync("cloudsearch", new { keywords = query, type = 1, limit = 50, offset }, ct);
        if (!json.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) return [];
        return songs.EnumerateArray().Select(Track.FromJson).ToList();
    }
    public async Task<List<Playlist>> DiscoverAsync(CancellationToken ct)
    {
        var json = await CallCachedAsync("personalized", new { limit = 24 }, ct);
        return json.GetProperty("result").EnumerateArray().Select(Playlist.FromJson).ToList();
    }
    public async Task<List<Track>> PlaylistTracksAsync(long id, int offset, CancellationToken ct)
    {
        var json = await CallCachedAsync("playlist_track_all", new { id, limit = 100, offset }, ct);
        return json.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToList();
    }
    internal async Task WarmDetailsAsync(IEnumerable<Track> tracks, CancellationToken ct)
    {
        var ids = tracks.Where(t => !t.IsLocal).Take(8).Select(t => t.Id).Distinct().ToArray(); if (ids.Length == 0) return;
        var cookie = Cookie;
        var version = Cache.Version;
        try
        {
            var result = await CallCachedAsync("song_detail", new { ids = string.Join(',', ids) }, ct);
            if (Cookie != cookie || ct.IsCancellationRequested) return;
            var privileges = result.TryGetProperty("privileges", out var p) ? p.EnumerateArray().ToArray() : [];
            foreach (var song in result.GetProperty("songs").EnumerateArray())
            {
                var id = song.Number("id");
                using var single = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, songs = new[] { song }, privileges = privileges.Where(item => item.Number("id") == id).ToArray() }));
                await Task.Run(() => Cache.Put(CacheDirectory, MetadataCache.Key(cookie, "song_detail", new { ids = id.ToString() }), single.RootElement, TimeSpan.FromMinutes(5), true, version), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }
    public Task SetCacheDirectoryAsync(string directory) { ObjectDisposedException.ThrowIf(_disposed, this); _cacheDirectory = directory; return Task.CompletedTask; }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _native.Dispose(); _lifetime.Dispose(); }
}
