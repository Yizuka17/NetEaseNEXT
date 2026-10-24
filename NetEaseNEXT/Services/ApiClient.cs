using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

public sealed class ApiClient : IDisposable
{
    private readonly ApiClient? _sessionSource;
    public ApiClient(SettingsStore? settings = null) { _cacheDirectory = (settings ?? SettingsStore.Load()).CacheDirectory; _cookie = SettingsStore.ReadCookie(); }
    public ApiClient(ApiClient sessionSource) { _sessionSource = sessionSource; }
    internal ApiClient(string bridgeScript, string cacheDirectory) { _bridgeScript = bridgeScript; _cacheDirectory = cacheDirectory; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MetadataCache _cache = new();
    private MetadataCache Cache => _sessionSource?.Cache ?? _cache;
    private int _requestCount;
    internal int RequestCount => Volatile.Read(ref _requestCount);
    private Process? _process;
    private readonly object _workerLock = new();
    private sealed record Pending(Process Process, TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly string? _bridgeScript;
    private int _id;
    private bool _disposed;
    private string _cacheDirectory = "";
    private string _cookie = "";
    public string CacheDirectory => _sessionSource?.CacheDirectory ?? _cacheDirectory;
    public string Cookie => _sessionSource?.Cookie ?? _cookie;
    public bool HasSession => Cookie.Length > 0;
    internal bool IsWorkerRunning { get { lock (_workerLock) return _process is { HasExited: false }; } }
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

    private Process Start()
    {
        lock (_workerLock)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }) return _process;
        _process?.Dispose();
        var directory = Path.Combine(AppContext.BaseDirectory, "api");
        // Development builds use the locked dependencies in the source tree.
        if (_bridgeScript != null) directory = Path.GetDirectoryName(_bridgeScript)!;
        if (_bridgeScript == null && !Directory.Exists(Path.Combine(directory, "node_modules")))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "api", "node_modules"))) root = root.Parent;
            if (root != null) directory = Path.Combine(root.FullName, "api");
        }
        if (_bridgeScript == null && !Directory.Exists(Path.Combine(directory, "node_modules"))) throw new InvalidOperationException("API 依赖未安装，请先运行 scripts\\build.ps1。");
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        var info = new ProcessStartInfo(File.Exists(bundledNode) ? bundledNode : "node")
        {
            WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--max-old-space-size=128");
        info.ArgumentList.Add(_bridgeScript ?? Path.Combine(directory, "bridge.mjs"));
        info.Environment["ENABLE_GENERAL_UNBLOCK"] = "false";
        var stateDirectory = Path.Combine(CacheDirectory, "Api");
        Directory.CreateDirectory(stateDirectory);
        info.Environment["TEMP"] = stateDirectory;
        info.Environment["TMP"] = stateDirectory;
        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 API。");
        _process.ErrorDataReceived += (_, _) => { }; // Never persist upstream cookie diagnostics.
        _process.BeginErrorReadLine();
        _ = ReadResponsesAsync(_process);
        return _process;
        }
    }

    private async Task ReadResponsesAsync(Process process)
    {
        Exception failure = new IOException("API 进程已结束，请重试。");
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                using var document = JsonDocument.Parse(line);
                var response = document.RootElement;
                if (!response.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id)) continue;
                if (_pending.TryRemove(id, out var pending) && ReferenceEquals(pending.Process, process)) pending.Completion.TrySetResult(response.Clone());
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { Stop(process, failure); }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Process? process = null; Pending? pending = null; var id = 0; var sent = false;
        try
        {
            await _gate.WaitAsync(timeout.Token);
            try
            {
            ObjectDisposedException.ThrowIf(_disposed, this);
            process = await Task.Run(Start, timeout.Token);
            Interlocked.Increment(ref _requestCount);
            var values = parameters == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(parameters))!;
            if (HasSession) values["cookie"] = Cookie;
            values["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            id = ++_id;
            pending = new(process, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[id] = pending;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = values }).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            sent = true;
            }
            catch (OperationCanceledException)
            {
                // Reset a potentially partial line while still owning the
                // writer gate. A new request must not reuse this worker before
                // the canceled writer has finished tearing it down.
                if (process != null && !sent) Stop(process);
                throw;
            }
            finally { _gate.Release(); }
            var root = await pending.Completion.Task.WaitAsync(timeout.Token);
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            var data = root.GetProperty("data");
            if (data.TryGetProperty("code", out var code) && code.GetInt32() is not (200 or 800 or 801 or 802 or 803))
                throw new InvalidOperationException(data.Text("msg") is { Length: > 0 } msg ? msg : $"接口返回 {code}，请稍后重试或登录。");
            if (method is "like" or "playlist_tracks" or "song_order_update" or "playlist_delete" or "playlist_subscribe" or "playlist_create") await Task.Run(() => Cache.Clear(CacheDirectory));
            return data.Clone();
        }
        catch (OperationCanceledException)
        {
            if (id != 0) _pending.TryRemove(id, out _);
            if (process != null)
            {
                await _gate.WaitAsync();
                try { if (!_pending.Values.Any(p => ReferenceEquals(p.Process, process))) Stop(process); }
                finally { _gate.Release(); }
            }
            throw;
        }
        catch (IOException) { if (process != null) Stop(process); throw; }
        finally { if (id != 0) _pending.TryRemove(id, out _); }
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
        var ids = tracks.Take(8).Select(t => t.Id).Distinct().ToArray(); if (ids.Length == 0) return;
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
    private void Stop(Process? expected = null, Exception? failure = null)
    {
        lock (_workerLock)
        {
            var process = expected ?? _process;
            if (process == null) return;
            foreach (var entry in _pending.Where(p => ReferenceEquals(p.Value.Process, process)).ToArray())
                if (_pending.TryRemove(entry.Key, out var pending)) pending.Completion.TrySetException(failure ?? new IOException("API 请求已停止"));
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
        }
    }
    public async Task SetCacheDirectoryAsync(string directory)
    {
        await _gate.WaitAsync();
        try { Stop(); _cacheDirectory = directory; }
        finally { _gate.Release(); }
    }
    public void Dispose() { _disposed = true; Stop(); }
}
