using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _coverLoad;
    private async Task UpdateCoverBackgroundAsync(Track track)
    {
        _coverLoad?.Cancel(); using var load = new CancellationTokenSource(); _coverLoad = load;
        try
        {
            if (track.Cover.Length == 0) { BlurredCover.Source = null; CoverTint.Background = null; return; }
            var path = await Artwork.GetFileAsync(track.Cover, 256, load.Token); load.Token.ThrowIfCancellationRequested();
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path);
            var bytes = bitmap.GetPixelBytes();
            long r = 0, g = 0, b = 0, weight = 0;
            for (var i = 0; i + 3 < bytes.Length; i += 32)
            {
                var saturation = Math.Max(bytes[i], Math.Max(bytes[i + 1], bytes[i + 2])) - Math.Min(bytes[i], Math.Min(bytes[i + 1], bytes[i + 2]));
                var w = saturation + 1; b += bytes[i] * w; g += bytes[i + 1] * w; r += bytes[i + 2] * w; weight += w;
            }
            var dark = Root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
            byte Shade(long value) => (byte)Math.Clamp(dark ? value / (double)Math.Max(1, weight) * .35 : value / (double)Math.Max(1, weight) * .2 + 195, 0, 255);
            var tint = Color.FromArgb(255, Shade(r), Shade(g), Shade(b));
            using var effect = new GaussianBlurEffect { Source = bitmap, BlurAmount = 32, BorderMode = EffectBorderMode.Hard };
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 256, 256, 96);
            using (var draw = target.CreateDrawingSession()) draw.DrawImage(effect, new Rect(0, 0, 256, 256), bitmap.Bounds);
            using var stream = new InMemoryRandomAccessStream(); await target.SaveAsync(stream, CanvasBitmapFileFormat.Png); stream.Seek(0);
            var source = new BitmapImage(); await source.SetSourceAsync(stream); load.Token.ThrowIfCancellationRequested();
            if (_hidden || _view != "playing") return;
            BlurredCover.Source = source;
            CoverTint.Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Color = tint }, new GradientStop { Color = dark ? Color.FromArgb(255, 20, 20, 24) : Color.FromArgb(255, 250, 250, 250), Offset = 1 } } };
        }
        catch (OperationCanceledException) { }
        catch { if (!load.IsCancellationRequested) { BlurredCover.Source = null; CoverTint.Background = null; } }
        finally { if (ReferenceEquals(_coverLoad, load)) _coverLoad = null; }
    }
}
