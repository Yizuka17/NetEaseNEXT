using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunFontChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow();
        check("Icon font ships inside the application", File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NetEaseNEXTIcons.ttf")), null);
        check("Windows symbol resource uses the bundled font", IconFonts.Symbols.Source == IconFonts.Source, IconFonts.Symbols.Source);
        check("Playback and heart icons use the bundled font", PlayPauseIcon.FontFamily.Source == IconFonts.Source && HeartIcon.FontFamily.Source == IconFonts.Source, PlayPauseIcon.FontFamily.Source);
        SetNoticeIcon(Notice);
        check("Native popup icon source uses the bundled font", Notice.IconSource is FontIconSource source && source.FontFamily.Source == IconFonts.Source, null);
        Nav.SelectedItem = Nav.MenuItems[0]; Root.UpdateLayout(); await Task.Delay(100);
        check("Filled sidebar icon uses the bundled font", ((NavigationViewItem)Nav.MenuItems[0]).Icon is FontIcon icon && icon.FontFamily.Source == IconFonts.Source, null);
        var originalFont = _settings.FontFamily;
        try
        {
            _settings.FontFamily = "Microsoft YaHei UI"; ApplyFont();
            check("Changing content font preserves bundled playback icons", PlayPauseIcon.FontFamily.Source == IconFonts.Source, null);
        }
        finally { _settings.FontFamily = originalFont; ApplyFont(); }
        var board = new StackPanel { Spacing = 20, Background = new SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(24) };
        foreach (var font in new[] { IconFonts.Symbols, new FontFamily("Segoe MDL2 Assets") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (var glyph in new[] { "\uE768", "\uE769", "\uE892", "\uE893", "\uE8B1", "\uEB51", "\uE713", "\uE90A" })
                row.Children.Add(new FontIcon { FontFamily = font, Glyph = glyph, FontSize = 32, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) });
            board.Children.Add(row);
        }
        PageBody.Children.Add(board); Root.UpdateLayout(); await Task.Delay(150);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(board);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(SettingsStore.DataDirectory);
        var file = await folder.CreateFileAsync("font-preview.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var bytes = new byte[pixels.Length]; using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        PageBody.Children.Remove(board);
        check("Bundled glyphs render in WinUI", bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, new { bitmap.PixelWidth, bitmap.PixelHeight });
    }
}
