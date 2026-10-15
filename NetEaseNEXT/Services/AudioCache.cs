using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

// Cache only the authorized, currently playing file. No queue-wide prefetch.
internal sealed class AudioCache : IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private CancellationTokenSource? _download;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal bool IsDownloading => !Completion.IsCompleted && _download != null;
    internal string? ActivePath { get; set; }
    internal static string? PathFor(string directory, long id, string quality, JsonElement info, Uri uri)
    {
        if (info.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == JsonValueKind.Object || info.Number("size") <= 0) return null;
        var version = info.Text("md5") is { Length: > 0 } md5 ? md5 : uri.AbsolutePath;
        var key = $"{id}:{quality}:{info.Number("br")}:{info.Text("type")}:{info.Number("size")}:{version}";
        return Path.Combine(directory, "Audio", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".audio");
    }
    internal static string? Find(string? path, long size)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            if (new FileInfo(path).Length != size) { File.Delete(path); return null; }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return path;
        }
        catch { return null; }
    }
    internal void Start(Uri uri, string? path, long size, string md5)
    {
        Stop();
        if (path == null || size > 512L * 1024 * 1024 || Find(path, size) != null) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3)); _download = cancellation;
        Completion = DownloadAsync(uri, path, size, md5, cancellation.Token);
        _ = Completion.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }
    internal void Stop()
    {
        var cancellation = _download; _download = null;
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task DownloadAsync(Uri uri, string path, long expected, string md5, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if ((total += read) > expected) throw new InvalidDataException("音频长度不匹配");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (total != expected) throw new InvalidDataException("音频未下载完整");
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (md5.Length == 32 && !digest.Equals(md5, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("音频校验失败");
            ct.ThrowIfCancellationRequested(); File.Move(temp, path, true);
            Trim(Path.GetDirectoryName(path)!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(new IOException("歌曲缓存未保存", ex)); }
        finally { try { File.Delete(temp); } catch { } }
    }
    private void Trim(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.audio").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= 1024L * 1024 * 1024) break;
            if (file.FullName == ActivePath) continue;
            try { var size = file.Length; file.Delete(); total -= size; } catch { }
        }
    }
    public void Dispose() => Stop();
}
