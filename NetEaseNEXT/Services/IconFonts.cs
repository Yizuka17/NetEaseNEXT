using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace NetEaseNEXT.Services;

internal static class IconFonts
{
    internal const string Source = "ms-appx:///Assets/Fonts/NetEaseNEXTIcons.ttf#NetEaseNEXT Icons";
    internal static FontFamily Symbols => (FontFamily)Application.Current.Resources["AppIconFontFamily"];
    internal static FontFamily FrameworkSymbols => (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
    internal static FontIcon Create(string glyph) => new()
    {
        Glyph = glyph, FontFamily = Symbols, FontSize = 20, Width = 20, Height = 20,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsTextScaleFactorEnabled = false
    };
}
