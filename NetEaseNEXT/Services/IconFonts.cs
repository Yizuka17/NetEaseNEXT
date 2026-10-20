using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NetEaseNEXT.Services;

internal static class IconFonts
{
    internal const string Source = "ms-appx:///Assets/Fonts/NetEaseNEXTIcons.ttf#NetEaseNEXT Icons, Segoe Fluent Icons, Segoe MDL2 Assets";
    internal static FontFamily Symbols => (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
}
