using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task ShowNavigationIconPreviewAsync(Action<string, bool, object?> check)
    {
        RestoreWindow(); _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance();
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1120 * scale), (int)(900 * scale)));
        await NavigateAsync("queue");
        var board = new StackPanel { Width = 868, Height = 360, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(16), Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
        foreach (var dark in new[] { false, true })
        {
            var color = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
            var panel = new StackPanel { Spacing = 20, Width = 410, Padding = new Thickness(20), Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 28, 28, 28) : Windows.UI.Color.FromArgb(255, 250, 250, 250)) };
            panel.Children.Add(new TextBlock { Text = dark ? "深色" : "浅色", FontSize = 24, Foreground = color });
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            header.Children.Add(new TextBlock { Width = 210 });
            foreach (var label in new[] { "未选中", "选中" }) header.Children.Add(new TextBlock { Text = label, Width = 48, FontSize = 13, TextAlignment = TextAlignment.Center, Foreground = color });
            panel.Children.Add(header);
            foreach (var (label, regular, filled) in new[] {
                ("歌单：原播客音符", "\uE8D6", "\uF8B3"),
                ("播客：耳机", "\uF8BA", "\uF8BB"),
                ("专辑：Fluent Album", "\uE93C", "\uF8B1") })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                row.Children.Add(new TextBlock { Text = label, Width = 210, VerticalAlignment = VerticalAlignment.Center, Foreground = color });
                foreach (var glyph in new[] { regular, filled }) row.Children.Add(new FontIcon { Glyph = glyph, FontFamily = IconFonts.Symbols, FontSize = 30, Width = 48, Height = 48, Foreground = color });
                panel.Children.Add(row);
            }
            board.Children.Add(panel);
        }
        PageBody.Children.Add(board);
        try { Root.UpdateLayout(); await Task.Delay(200); Root.UpdateLayout(); await SaveLibraryImageAsync(board, "navigation-icons-preview.png"); }
        finally { PageBody.Children.Remove(board); }
        check("Navigation icon candidates rendered from real WinUI fonts", true, null);
    }
}
