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
        FontHost.RequestedTheme = Root.RequestedTheme = PreferredAppTheme;
        RefreshThemeSurfaces();
    });
    private void ApplyPopupTheme(FrameworkElement element)
    {
        element.RequestedTheme = Root.ActualTheme;
        ApplyPopupMaterial(element);
        ApplyTextFont(element, new FontFamily(_settings.FontFamily));
    }
    private Brush FloatingBackground()
    {
        var solid = (SolidColorBrush)SolidLayer.Background;
        if (_settings.Backdrop == "Solid" || SystemBackdrop == null) return solid;
        var color = solid.Color;
        var blend = 1 - (1 - ((SystemBackdrop as Services.AdjustableBackdrop)?.SolidBlend ?? 0)) * .88;
        color.A = (byte)Math.Clamp(Math.Round(blend * 255), 0, 255);
        return new SolidColorBrush(color);
    }
    private SystemBackdrop? FloatingBackdrop(SystemBackdrop? current)
    {
        if (_settings.Backdrop == "Solid" || SystemBackdrop == null) return null;
        var value = _settings.Backdrop == "Acrylic" ? _settings.AcrylicTransparency : _settings.MicaTransparency;
        var defaults = _settings.UsesSystemMaterial(_settings.Backdrop);
        if (current is Services.AdjustableBackdrop backdrop && backdrop.Kind == _settings.Backdrop)
        { backdrop.Update(value, defaults, Root.ActualTheme); return backdrop; }
        return new Services.AdjustableBackdrop(_settings.Backdrop, value, defaults, Root.ActualTheme);
    }
    private void ApplyPopupMaterial(DependencyObject element, Brush? brush = null)
    {
        brush ??= FloatingBackground();
        if (element is ContentDialog dialog) dialog.Background = brush;
        else if (element is MenuFlyoutPresenter menu) { menu.SystemBackdrop = FloatingBackdrop(menu.SystemBackdrop); menu.Background = brush; }
        else if (element is FlyoutPresenter flyout) flyout.Background = brush;
        else if (element is Border { Name: "PopupBorder" } border) border.Background = brush;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPopupMaterial(VisualTreeHelper.GetChild(element, i), brush);
    }
    private void ApplyFloatingMaterials()
    {
        QueuePane.Background = FloatingBackground();
        if (_dialog != null) ApplyPopupMaterial(_dialog);
        if (Root.XamlRoot != null)
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {
                if (popup.Child is FrameworkElement and not MenuFlyoutPresenter and not Microsoft.UI.Xaml.Shapes.Shape) popup.SystemBackdrop = FloatingBackdrop(popup.SystemBackdrop);
                if (popup.Child != null) ApplyPopupMaterial(popup.Child);
            }
    }
    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog)
    {
        dialog.Resources["ContentDialogButtonHeight"] = 44d;
        dialog.Resources["ContentDialogButtonMinHeight"] = 44d;
        Style TouchStyle(Style? original)
        {
            var style = new Style(typeof(Button)) { BasedOn = original ?? (Style)Application.Current.Resources["DefaultButtonStyle"] };
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44d));
            return style;
        }
        dialog.PrimaryButtonStyle = TouchStyle(dialog.PrimaryButtonStyle ?? (dialog.DefaultButton == ContentDialogButton.Primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null));
        dialog.SecondaryButtonStyle = TouchStyle(dialog.SecondaryButtonStyle);
        dialog.CloseButtonStyle = TouchStyle(dialog.CloseButtonStyle);
        ApplyPopupTheme(dialog);
        dialog.Opened += DialogMaterialOpened;
        try { return await dialog.ShowAsync(); }
        finally { dialog.Opened -= DialogMaterialOpened; dialog.ClearValue(FrameworkElement.RequestedThemeProperty); }
    }
    private void DialogMaterialOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => ApplyFloatingMaterials();
    private void Popup_Opened(object? sender, object args)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is FrameworkElement child) ApplyPopupTheme(child);
        ApplyFloatingMaterials();
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
        if (SystemBackdrop is Services.AdjustableBackdrop) ApplyMaterial(Root.ActualTheme);
        RefreshLyricAppearance();
        _coverBackgroundKey = null;
        if (_view == "playing" && _playback.Current is { } track) _ = UpdateCoverBackgroundAsync(track);
    }
}
