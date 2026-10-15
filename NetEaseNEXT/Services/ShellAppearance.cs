using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace NetEaseNEXT.Services;

internal static class ShellAppearance
{
    // Windows can use different themes for apps and the taskbar.
    internal static bool IsDark
    {
        get
        {
            if (new AccessibilitySettings().HighContrast)
            {
                var background = new UISettings().GetColorValue(UIColorType.Background);
                return background.R * 299 + background.G * 587 + background.B * 114 < 128000;
            }
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) is int value && value == 0;
        }
    }
    internal static ElementTheme Theme => IsDark ? ElementTheme.Dark : ElementTheme.Light;
    internal static string IconPath(bool dark) => Path.Combine(AppContext.BaseDirectory, "Assets", dark ? "AppIconDark.ico" : "AppIcon.ico");
    internal static string IconPath() => IconPath(IsDark);
}
