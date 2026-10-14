using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

public sealed class ApiClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _id;
    private bool _disposed;
    public string CacheDirectory { get; private set; } = SettingsStore.Load().CacheDirectory;
    public string Cookie { get; private set; } = SettingsStore.ReadCookie();
    public bool HasSession => Cookie.Length > 0;
    internal bool IsWorkerRunning => _process is { HasExited: false };
    public void SetCookie(string cookie) { SettingsStore.SaveCookie(cookie); Cookie = cookie; }

    private void Start()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        var directory = Path.Combine(AppContext.BaseDirectory, "api");
        // Development builds use the locked dependencies in the source tree.
        if (!Directory.Exists(Path.Combine(directory, "node_modules")))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "api", "node_modules"))) root = root.Parent;
            if (root != null) directory = Path.Combine(root.FullName, "api");
        }
        if (!Directory.Exists(Path.Combine(directory, "node_modules"))) throw new InvalidOperationException("API 依赖未安装，请先运行 scripts\\build.ps1。");
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        var info = new ProcessStartInfo(File.Exists(bundledNode) ? bundledNode : "node")
        {
            WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--max-old-space-size=128");
        info.ArgumentList.Add(Path.Combine(directory, "bridge.mjs"));
        info.Environment["ENABLE_GENERAL_UNBLOCK"] = "false";
        var stateDirectory = Path.Combine(CacheDirectory, "Api");
        Directory.CreateDirectory(stateDirectory);
        info.Environment["TEMP"] = stateDirectory;
        info.Environment["TMP"] = stateDirectory;
        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 API。");
        _process.ErrorDataReceived += (_, _) => { }; // Never persist upstream cookie diagnostics.
        _process.BeginErrorReadLine();
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Start();
            var values = parameters == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(parameters))!;
            if (HasSession) values["cookie"] = Cookie;
            values["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = ++_id;
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = values }).AsMemory(), timeout.Token);
            await _process.StandardInput.FlushAsync(timeout.Token);
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line == null) throw new IOException("API 进程已结束，请重试。");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("id").GetInt32() != id) throw new IOException("API 响应不匹配。");
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            var data = root.GetProperty("data");
            if (data.TryGetProperty("code", out var code) && code.GetInt32() is not (200 or 800 or 801 or 802 or 803))
                throw new InvalidOperationException(data.Text("msg") is { Length: > 0 } msg ? msg : $"接口返回 {code}，请稍后重试或登录。");
            return data.Clone();
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (IOException) { Stop(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<List<Track>> SearchAsync(string query, int offset, CancellationToken ct)
    {
        var json = await CallAsync("cloudsearch", new { keywords = query, type = 1, limit = 50, offset }, ct);
        if (!json.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) return [];
        return songs.EnumerateArray().Select(Track.FromJson).ToList();
    }
    public async Task<List<Playlist>> DiscoverAsync(CancellationToken ct)
    {
        var json = await CallAsync("personalized", new { limit = 24 }, ct);
        return json.GetProperty("result").EnumerateArray().Select(Playlist.FromJson).ToList();
    }
    public async Task<List<Track>> PlaylistTracksAsync(long id, int offset, CancellationToken ct)
    {
        var json = await CallAsync("playlist_track_all", new { id, limit = 100, offset }, ct);
        return json.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToList();
    }
    private void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process?.Dispose(); _process = null;
    }
    public async Task SetCacheDirectoryAsync(string directory)
    {
        await _gate.WaitAsync();
        try { Stop(); CacheDirectory = directory; }
        finally { _gate.Release(); }
    }
    public void Dispose() { _disposed = true; Stop(); }
}
