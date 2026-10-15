using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace NetEaseNEXT.Services;

public static class Artwork
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly ConditionalWeakTable<Image, State> States = new();
    private sealed class Fetch { public readonly CancellationTokenSource Cancellation = new(); public required Task<string> Task; public int Readers; }
    private static readonly Dictionary<string, Fetch> Fetches = new();
    private static readonly object FetchLock = new();
    internal static int Downloads;
    private static readonly Dictionary<string, (BitmapImage Bitmap, string Path, long Bytes, long Used)> Decoded = new();
    private static long _decodedBytes, _used;
    private static bool _suspended;
    public static void SuspendTree(DependencyObject root) { _suspended = true; Decoded.Clear(); _decodedBytes = 0; Visit(root, image => { if (States.TryGetValue(image, out var state)) state.Load?.Cancel(); image.Source = null; }); }
    public static void ResumeTree(DependencyObject root) { _suspended = false; Visit(root, image => { if (States.TryGetValue(image, out _)) Load(image); }); }
    private static void Visit(DependencyObject root, Action<Image> action)
    {
        if (root is Image image) action(image);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Visit(VisualTreeHelper.GetChild(root, i), action);
    }
    public static string CacheDirectory { get; set; } = SettingsStore.Load().CacheDirectory;
    public static readonly DependencyProperty UriProperty = DependencyProperty.RegisterAttached("Uri", typeof(string), typeof(Artwork), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached("Size", typeof(double), typeof(Artwork), new PropertyMetadata(96d));
    public static string GetUri(DependencyObject obj) => (string)obj.GetValue(UriProperty);
    public static void SetUri(DependencyObject obj, string value) => obj.SetValue(UriProperty, value);
    public static double GetSize(DependencyObject obj) => (double)obj.GetValue(SizeProperty);
    public static void SetSize(DependencyObject obj, double value) => obj.SetValue(SizeProperty, value);
    public static readonly DependencyProperty PreserveSourceProperty = DependencyProperty.RegisterAttached("PreserveSource", typeof(bool), typeof(Artwork), new PropertyMetadata(false));
    public static bool GetPreserveSource(DependencyObject obj) => (bool)obj.GetValue(PreserveSourceProperty);
    public static void SetPreserveSource(DependencyObject obj, bool value) => obj.SetValue(PreserveSourceProperty, value);
    private sealed class State { public CancellationTokenSource? Load; public string? Error; public bool Queued; }
    internal static object Inspect(Image image) => new { uri = GetUri(image), image.IsLoaded, suspended = _suspended, loading = States.TryGetValue(image, out var state) && state.Load != null, error = state?.Error, source = image.Source?.GetType().Name };
    public static void Refresh(Image image) => QueueLoad(image);
    private static void QueueLoad(Image image)
    {
        var state = States.GetOrCreateValue(image);
        if (state.Queued) return;
        state.Queued = true;
        if (!image.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { state.Queued = false; Load(image); })) state.Queued = false;
    }
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not Image image) return;
        if (!States.TryGetValue(image, out _))
        {
            States.Add(image, new());
            image.Loaded += (_, _) => QueueLoad(image);
            image.Unloaded += (_, _) =>
            {
                States.GetOrCreateValue(image).Load?.Cancel();
                // A recycled row may already be attached again when the old
                // Unloaded notification arrives. Do not erase its new bitmap.
                image.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!_suspended && (image.IsLoaded || image.XamlRoot != null)) QueueLoad(image);
                    else image.Source = null;
                });
            };
        }
        QueueLoad(image);
    }
    private static async void Load(Image image)
    {
        var state = States.GetOrCreateValue(image); state.Load?.Cancel();
        state.Error = null;
        using var load = new CancellationTokenSource(); state.Load = load;
        if (!GetPreserveSource(image) || _suspended || string.IsNullOrWhiteSpace(GetUri(image))) image.Source = null;
        // Empty named Images can still report IsLoaded=false while attached to
        // the window. Use the visual root, otherwise no Source is ever assigned.
        if (_suspended || !image.IsLoaded && image.XamlRoot == null || string.IsNullOrWhiteSpace(GetUri(image))) { state.Load = null; return; }
        try
        {
            var size = (int)Math.Ceiling(GetSize(image) * (image.XamlRoot?.RasterizationScale ?? 1));
            var key = CacheDirectory + ":" + CacheIdentity(GetUri(image), size) + ":" + size;
            if (Decoded.TryGetValue(key, out var cached))
            {
                Decoded[key] = cached with { Used = ++_used }; Touch(cached.Path); image.Source = cached.Bitmap; return;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var path = await GetFileAsync(GetUri(image), Math.Clamp(size, 64, 1600), load.Token);
                load.Token.ThrowIfCancellationRequested();
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = size, DecodePixelType = DecodePixelType.Physical };
                    using var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
                    await bitmap.SetSourceAsync(stream).AsTask(load.Token);
                    load.Token.ThrowIfCancellationRequested();
                    if (_suspended || !image.IsLoaded && image.XamlRoot == null) return;
                    image.Source = bitmap;
                    Remember(key, bitmap, path); break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt == 0) { try { File.Delete(path); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { state.Error = ex.Message; }
        finally { if (ReferenceEquals(state.Load, load)) state.Load = null; }
    }
    private static void Remember(string key, BitmapImage bitmap, string path)
    {
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        if (bytes > 32 * 1024 * 1024) return;
        if (Decoded.Remove(key, out var previous)) _decodedBytes -= previous.Bytes;
        while (Decoded.Count > 0 && _decodedBytes + bytes > 32 * 1024 * 1024)
        {
            var oldest = Decoded.MinBy(p => p.Value.Used); _decodedBytes -= oldest.Value.Bytes; Decoded.Remove(oldest.Key);
        }
        Decoded[key] = (bitmap, path, bytes, ++_used); _decodedBytes += bytes;
    }
    public static Task<string> GetFileAsync(string url, int size, CancellationToken ct) => GetFileInDirectoryAsync(url, size, CacheDirectory, ct);
    internal static async Task<string> GetFileInDirectoryAsync(string url, int size, string cacheDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = CacheRequest(url, size);
        var directory = Path.Combine(cacheDirectory, "Artwork"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheIdentity(url, size)))) + ".img");
        if (ValidImage(path)) { Touch(path); return path; }
        Fetch fetch;
        lock (FetchLock)
        {
            if (!Fetches.TryGetValue(path, out fetch!) || fetch.Cancellation.IsCancellationRequested)
            {
                fetch = new() { Task = null! };
                fetch.Task = DownloadAsync(request, path, fetch.Cancellation.Token); Fetches[path] = fetch;
            }
            fetch.Readers++;
        }
        try { return await fetch.Task.WaitAsync(ct); }
        finally
        {
            lock (FetchLock)
            {
                if (--fetch.Readers == 0)
                {
                    if (Fetches.GetValueOrDefault(path) == fetch) Fetches.Remove(path);
                    fetch.Cancellation.Cancel();
                    _ = fetch.Task.ContinueWith(t => { _ = t.Exception; fetch.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }
    internal static string CacheRequest(string url, int size)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidDataException("无效图片");
        // Normalize NetEase's thumbnail parameter; preserve other hosts' signed URLs.
        if (!uri.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase)) return url;
        var bucket = new[] { 64, 96, 192, 320, 512, 768, 1024, 1600 }.First(s => s >= Math.Clamp(size, 64, 1600));
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("param=", StringComparison.OrdinalIgnoreCase));
        return new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "", Query = string.Join('&', query.Append($"param={bucket}y{bucket}")) }.Uri.AbsoluteUri;
    }
    internal static string CacheIdentity(string url, int size)
    {
        var request = new Uri(CacheRequest(url, size));
        // The API rotates these CDN hosts for the same immutable image path.
        return request.Host is "p1.music.126.net" or "p2.music.126.net" or "p3.music.126.net" or "p4.music.126.net"
            ? new UriBuilder(request) { Host = "p1.music.126.net" }.Uri.AbsoluteUri : request.AbsoluteUri;
    }
    private static bool ValidImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) < 12) return false;
            return header[0] == 0xff && header[1] == 0xd8 || header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                header[..3].SequenceEqual("GIF"u8) || header[..2].SequenceEqual("BM"u8) || header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
        }
        catch { return false; }
    }
    private static void Touch(string path) { try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { } }
    private static async Task<string> DownloadAsync(string request, string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (ValidImage(path)) { Touch(path); return path; }
            Interlocked.Increment(ref Downloads);
            using var response = await Client.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[32768]; int read; long total = 0;
                while ((read = await stream.ReadAsync(buffer, ct)) != 0)
                { if ((total += read) > 12 * 1024 * 1024) throw new InvalidDataException("图片过大"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            }
            if (!ValidImage(temp)) throw new InvalidDataException("图片内容无效");
            ct.ThrowIfCancellationRequested(); File.Move(temp, path, true); return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); Gate.Release(); }
    }
    public static async Task TrimAsync()
    {
        var directory = Path.Combine(CacheDirectory, "Artwork");
        if (!Directory.Exists(directory)) return;
        await Task.Run(() =>
        {
            var files = new DirectoryInfo(directory).GetFiles("*.img").OrderBy(f => f.LastWriteTimeUtc).ToArray();
            var total = files.Sum(f => f.Length);
            foreach (var file in files) { if (total <= 256 * 1024 * 1024) break; try { total -= file.Length; file.Delete(); } catch { } }
        });
    }
}
