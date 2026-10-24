using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly UISettings _themeSettings = new();
    private ElementTheme PreferredAppTheme => _settings.Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => SystemAppTheme
    };
    private ElementTheme SystemAppTheme
    {
        get
        {
            if (!new AccessibilitySettings().HighContrast && Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null) is int light)
                return light == 0 ? ElementTheme.Dark : ElementTheme.Light;
            var background = _themeSettings.GetColorValue(UIColorType.Background);
            return background.R * 299 + background.G * 587 + background.B * 114 < 128000 ? ElementTheme.Dark : ElementTheme.Light;
        }
    }
    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_exiting) return;
        Root.RequestedTheme = PreferredAppTheme;
        RefreshThemeSurfaces();
    });
    private void ApplyPopupTheme(FrameworkElement element) => element.RequestedTheme = Root.ActualTheme;
    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog)
    {
        ApplyPopupTheme(dialog);
        try { return await dialog.ShowAsync(); }
        finally { dialog.ClearValue(FrameworkElement.RequestedThemeProperty); }
    }
    private void Popup_Opened(object? sender, object args)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is FrameworkElement child) ApplyPopupTheme(child);
    }
    private void ShowThemedFlyout(FlyoutBase flyout, FrameworkElement anchor)
    {
        flyout.Opened += Popup_Opened;
        flyout.Closed += Closed;
        void Closed(object? sender, object args) { flyout.Opened -= Popup_Opened; flyout.Closed -= Closed; }
        flyout.ShowAt(anchor);
    }
    private void RefreshThemeSurfaces()
    {
        if (_exiting) return;
        if (_dialog != null) ApplyPopupTheme(_dialog);
        if (Root.XamlRoot != null) Popup_Opened(null, EventArgs.Empty);
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        UpdateAppIcon();
        if (_hidden) return;
        RefreshLyricAppearance();
        _coverBackgroundKey = null;
        if (_view == "playing" && _playback.Current is { } track) _ = UpdateCoverBackgroundAsync(track);
    }
}
