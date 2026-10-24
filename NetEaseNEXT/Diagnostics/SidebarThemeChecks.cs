using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSidebarThemeChecksAsync(Action<string, bool, object?> check, List<object> results)
    {
        RestoreWindow(); _sessionReady = true; _settings.Backdrop = "Solid";
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(720 * scale)));
        await NavigateAsync("queue");
        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left; Nav.IsPaneOpen = true;
        var items = Nav.MenuItems.OfType<NavigationViewItem>().Append((NavigationViewItem)Nav.SettingsItem).ToArray();
        var originalIcons = items.ToDictionary(item => item, item => item.Icon);
        foreach (var (tag, regular, filled) in new[] { ("library", "\uE8D6", "\uF8B3"), ("radio", "\uF8BA", "\uF8BB"), ("saved-albums", "\uE93C", "\uF8B1") })
        {
            var item = items.First(i => (string?)i.Tag == tag);
            item.IsSelected = false; var outline = (item.Icon as FontIcon)?.Glyph;
            item.IsSelected = true; var selected = (item.Icon as FontIcon)?.Glyph; item.IsSelected = false;
            check(tag + " uses the approved bundled outline and filled glyphs", outline == regular && selected == filled, null);
        }
        var failures = new List<string>();
        foreach (var theme in new[] { "Dark", "Light", "Dark" })
        {
            _settings.Theme = theme; ApplyAppearance(); Root.UpdateLayout();
            foreach (var selected in new[] { false, true }) foreach (var item in items)
            {
                foreach (var other in items) other.IsSelected = false;
                Nav.SelectedItem = selected ? item : null;
                item.IsSelected = selected; Root.UpdateLayout();
                // Read the actual rendered glyph, not only the template slot.
                var icon = item.Icon;
                var image = new RenderTargetBitmap(); await image.RenderAsync(icon);
                var buffer = await image.GetPixelsAsync(); var pixels = new byte[buffer.Length];
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                double light = 0, weight = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var alpha = pixels[i + 3]; if (alpha < 200) continue;
                    light += alpha * (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3d; weight += alpha;
                }
                var brightness = weight == 0 ? -1 : light / weight;
                var themeMatches = theme == "Dark" ? brightness >= 200 : brightness is >= 0 and <= 80;
                var fontMatches = icon is FontIcon font && font.FontFamily.Source == IconFonts.Source && !font.FontFamily.Source.Contains(',');
                var name = $"{theme} {item.Tag ?? "settings"} {(selected ? "selected" : "normal")}";
                results.Add(new { name, type = icon.GetType().Name, font = (icon as FontIcon)?.FontFamily.Source ?? FindDescendant<TextBlock>(icon)?.FontFamily.Source,
                    glyph = (icon as FontIcon)?.Glyph, brightness, themeMatches, fontMatches,
                    iconColor = (icon.Foreground as SolidColorBrush)?.Color.ToString(), itemColor = (item.Foreground as SolidColorBrush)?.Color.ToString() });
                if (!themeMatches || !fontMatches) failures.Add(name);
                check(name + " keeps its bundled icon and theme binding", themeMatches && fontMatches && ReferenceEquals(item.Icon, originalIcons[item]) && icon.GetBindingExpression(IconElement.ForegroundProperty) != null, null);
            }
            var radio = items.First(i => (string?)i.Tag == "radio");
            Nav.SelectedItem = radio; radio.IsSelected = true;
            Nav.IsPaneOpen = true; UpdateCompactAccount(); Root.UpdateLayout();
            await SaveLibraryImageAsync(Root, "sidebar-icons-" + theme.ToLowerInvariant() + ".png");
        }
        check("All sidebar states use bundled glyphs and follow light/dark foregrounds", failures.Count == 0, failures);
    }
}
