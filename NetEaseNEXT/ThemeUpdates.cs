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
    private Flyout? _dialogSurface;
    private bool _dialogSurfaceReady;
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
        var kind = _settings.Backdrop == "Solid" || SystemBackdrop == null ? "Solid" : _settings.Backdrop;
        var value = _settings.Backdrop == "Acrylic" ? _settings.AcrylicTransparency : _settings.MicaTransparency;
        var defaults = _settings.UsesSystemMaterial(_settings.Backdrop);
        if (current is Services.AdjustableBackdrop backdrop)
        { backdrop.UpdateMaterial(kind, value, defaults, Root.ActualTheme); return backdrop; }
        return new Services.AdjustableBackdrop(kind, value, defaults, Root.ActualTheme);
    }
    private void ApplyPopupMaterial(DependencyObject element, Brush? brush = null)
    {
        brush ??= FloatingBackground();
        if (element is ContentDialog dialog) { dialog.Background = brush; dialog.BorderThickness = new Thickness(new AccessibilitySettings().HighContrast ? 2 : .5); }
        else if (element is MenuFlyoutPresenter menu) { menu.SystemBackdrop = FloatingBackdrop(menu.SystemBackdrop); menu.Background = brush; menu.BorderThickness = new Thickness(new AccessibilitySettings().HighContrast ? 2 : .5); }
        else if (element is FlyoutPresenter flyout) flyout.Background = ReferenceEquals(flyout.Content, _dialogSurface?.Content) || ReferenceEquals(flyout.Content, QueuePane) ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : brush;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPopupMaterial(VisualTreeHelper.GetChild(element, i), brush);
    }
    private void ApplyFloatingMaterials()
    {
        QueuePane.Background = FloatingBackground();
        QueuePane.BorderThickness = new Thickness(new AccessibilitySettings().HighContrast ? 2 : .5);
        if (_queueSurfaceOpen) QueueSurface.SystemBackdrop = FloatingBackdrop(QueueSurface.SystemBackdrop);
        if (_dialogSurfaceReady && _dialogSurface != null) _dialogSurface.SystemBackdrop = FloatingBackdrop(_dialogSurface.SystemBackdrop);
        if (_dialog != null) ApplyPopupMaterial(_dialog);
        if (Root.XamlRoot != null)
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {

                if (popup.Child != null) ApplyPopupMaterial(popup.Child);
            }
    }
    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog)
    {
        dialog.Resources["ContentDialogButtonHeight"] = 44d;
        dialog.Resources["ContentDialogButtonMinHeight"] = 44d;
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
        var width = dialog.Resources.TryGetValue("ContentDialogMaxWidth", out var customWidth) && customWidth is double number ? number : 548;
        var host = new Grid { MaxWidth = Math.Min(width, Root.ActualWidth - 32), MaxHeight = Root.ActualHeight - 32 };
        host.Children.Add(dialog);
        var surface = new Flyout { Content = host, SystemBackdrop = FloatingBackdrop(null), Placement = FlyoutPlacementMode.Full, ShouldConstrainToRootBounds = false, LightDismissOverlayMode = LightDismissOverlayMode.On, FlyoutPresenterStyle = FloatingPresenterStyle(host.MaxWidth, host.MaxHeight) };
        _dialogSurface = surface; _dialogSurfaceReady = false; var active = true; var sizing = false;
        var surfaceClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var surfaceOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.Opened += (_, _) => surfaceOpened.TrySetResult();
        surface.Closed += (_, _) => surfaceClosed.TrySetResult();
        var wasShown = false;
        void FitSurface()
        {
            if (!active || sizing || !dialog.IsLoaded) return;
            var border = FindDescendant<Border>(dialog, b => b.Name == "BackgroundElement");
            if (border == null) return;
            sizing = true;
            try
            {
                host.Width = host.Height = double.NaN;
                border.Measure(new Windows.Foundation.Size(host.MaxWidth, host.MaxHeight));
                host.Width = Math.Min(host.MaxWidth, border.DesiredSize.Width);
                host.Height = Math.Min(host.MaxHeight, border.DesiredSize.Height);
                dialog.Width = host.Width; dialog.Height = host.Height;
                surface.FlyoutPresenterStyle = FloatingPresenterStyle(host.Width, host.Height);
                host.UpdateLayout();
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).FirstOrDefault(p => p.Child is FlyoutPresenter presenter && ReferenceEquals(presenter.Content, host));
                if (popup != null)
                {
                    popup.HorizontalOffset = Math.Max(16, (Root.ActualWidth - host.Width) / 2);
                    popup.VerticalOffset = Math.Max(16, (Root.ActualHeight - host.Height) / 2);
                }
            }
            finally { sizing = false; }
        }
        void ContentSizeChanged(object sender, SizeChangedEventArgs args) { if (_dialogSurfaceReady) FitSurface(); }
        void RootSizeChanged(object sender, SizeChangedEventArgs args)
        {
            host.MaxWidth = Math.Max(0, Math.Min(width, Root.ActualWidth - 32)); host.MaxHeight = Math.Max(0, Root.ActualHeight - 32);
            FitSurface();
        }
        Root.SizeChanged += RootSizeChanged;
        if (dialog.Content is FrameworkElement content) content.SizeChanged += ContentSizeChanged;
        surface.Closing += (_, args) => args.Cancel = active;
        surface.Opened += Popup_Opened;
        try
        {
            surface.ShowAt(Root); wasShown = true;
            await surfaceOpened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            host.UpdateLayout();
            dialog.XamlRoot = host.XamlRoot;
            var showing = dialog.ShowAsync(ContentDialogPlacement.InPlace);
            host.UpdateLayout(); FitSurface(); _dialogSurfaceReady = true; ApplyFloatingMaterials();
            return await showing;
        }
        finally
        {
            active = false; surface.Opened -= Popup_Opened; surface.Hide();
            // The next dialog must wait for the native flyout's exit transition.
            // Removing its child early can unload the next surface as it opens.
            if (wasShown)
            {
                try { await surfaceClosed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException ex) { Services.Log.Write(ex); }
            }
            host.Children.Remove(dialog);
            if (dialog.Content is FrameworkElement oldContent) oldContent.SizeChanged -= ContentSizeChanged;
            Root.SizeChanged -= RootSizeChanged;
            if (ReferenceEquals(_dialogSurface, surface)) { _dialogSurface = null; _dialogSurfaceReady = false; }
            dialog.ClearValue(FrameworkElement.RequestedThemeProperty);
        }
    }
    private void Popup_Opened(object? sender, object args)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            if (popup.Child is FrameworkElement child) ApplyPopupTheme(child);
        ApplyFloatingMaterials();
    }
    private void ShowThemedFlyout(FlyoutBase flyout, FrameworkElement anchor)
    {
        flyout.SystemBackdrop = FloatingBackdrop(flyout.SystemBackdrop);
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
