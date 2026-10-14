using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NetEaseNEXT.Services;

public static class Artwork
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly ConditionalWeakTable<Image, State> States = new();
    public static string CacheDirectory { get; set; } = SettingsStore.Load().CacheDirectory;
    public static readonly DependencyProperty UriProperty = DependencyProperty.RegisterAttached("Uri", typeof(string), typeof(Artwork), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached("Size", typeof(double), typeof(Artwork), new PropertyMetadata(96d));
    public static string GetUri(DependencyObject obj) => (string)obj.GetValue(UriProperty);
    public static void SetUri(DependencyObject obj, string value) => obj.SetValue(UriProperty, value);
    public static double GetSize(DependencyObject obj) => (double)obj.GetValue(SizeProperty);
    public static void SetSize(DependencyObject obj, double value) => obj.SetValue(SizeProperty, value);
    private sealed class State { public CancellationTokenSource? Load; }
    public static void Refresh(Image image) => Load(image);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not Image image) return;
        if (!States.TryGetValue(image, out _))
        {
            States.Add(image, new());
            image.Loaded += (_, _) => Load(image);
            image.Unloaded += (_, _) => { States.GetOrCreateValue(image).Load?.Cancel(); image.Source = null; };
        }
        Load(image);
    }
    private static async void Load(Image image)
    {
        var state = States.GetOrCreateValue(image); state.Load?.Cancel();
        using var load = new CancellationTokenSource(); state.Load = load;
        image.Source = null;
        if (!image.IsLoaded || string.IsNullOrWhiteSpace(GetUri(image))) { state.Load = null; return; }
        try
        {
            var size = (int)Math.Ceiling(GetSize(image) * (image.XamlRoot?.RasterizationScale ?? 1));
            var path = await GetFileAsync(GetUri(image), Math.Clamp(size, 64, 1600), load.Token);
            load.Token.ThrowIfCancellationRequested();
            image.Source = new BitmapImage(new System.Uri(path)) { DecodePixelWidth = size, DecodePixelType = DecodePixelType.Physical };
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (ReferenceEquals(state.Load, load)) state.Load = null; }
    }
    public static async Task<string> GetFileAsync(string url, int size, CancellationToken ct)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidDataException("无效图片");
        var request = url + (url.Contains('?') ? "&" : "?") + $"param={size}y{size}";
        var directory = Path.Combine(CacheDirectory, "Artwork"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request))) + ".img");
        if (File.Exists(path)) return path;
        await Gate.WaitAsync(ct);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path)) return path;
            using var response = await Client.GetAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[32768]; int read; long total = 0;
                while ((read = await stream.ReadAsync(buffer, ct)) != 0)
                { if ((total += read) > 12 * 1024 * 1024) throw new InvalidDataException("图片过大"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            }
            File.Move(temp, path, true); return path;
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
