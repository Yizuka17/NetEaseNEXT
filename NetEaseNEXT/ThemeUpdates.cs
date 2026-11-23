using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
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
    private void NativeContextFlyoutOpening(object? sender, object args)
    {
        if (sender is FlyoutBase flyout)
        {
            PrepareFlyoutTheme(flyout);
            flyout.SystemBackdrop = FloatingWindowBackdrop(flyout.SystemBackdrop);
        }
    }
    private void ApplyPopupTheme(FrameworkElement element)
    {
        element.RequestedTheme = Root.ActualTheme;
        ApplyPopupMaterial(element);
        ApplyTextFont(element, new FontFamily(_settings.FontFamily));
    }
    private FlyoutBase? _activeFlyout;
    private bool _dialogSurfaceReady;
    private int _dialogPickerDepth;
    private double _floatingRasterScale;
    private void FloatingRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_exiting || _hidden || _floatingRasterScale == sender.RasterizationScale) return;
        _floatingRasterScale = sender.RasterizationScale;
        ApplyFloatingMaterials(); PositionQueuePopup();
    }
    private double FloatingRasterScale => Root.XamlRoot?.RasterizationScale ?? 1;
    private Thickness FloatingStroke => new(new AccessibilitySettings().HighContrast ? 2 : 1 / FloatingRasterScale);
    private double SnapFloatingPosition(double value) => Math.Round(value * FloatingRasterScale) / FloatingRasterScale;
    private async Task<T> PreserveDialogForPickerAsync<T>(Func<Task<T>> pick)
    {
        _dialogPickerDepth++;
        try { return await pick(); }
        finally { _dialogPickerDepth--; }
    }
    private Brush FloatingBackground()
    {
        if (new AccessibilitySettings().HighContrast) return (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"];
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var color = dark ? Windows.UI.Color.FromArgb(255, 44, 44, 44) : Windows.UI.Color.FromArgb(255, 252, 252, 252);
        var value = _settings.UsesSystemMaterial("Acrylic") ? Services.AdjustableBackdrop.DefaultTransparency : Services.AdjustableBackdrop.Normalize(_settings.AcrylicTransparency);
        // Keep floating surfaces slightly denser than the page, using the WinUI
        // in-app acrylic palette rather than a semi-transparent solid fill.
        var preset = Services.AdjustableBackdrop.DefaultTransparency;
        var tint = dark ? .15 : .08;
        var opacity = value <= preset ? tint + (1 - tint) * (1 - value / preset) : tint * (1 - .55 * (value - preset) / (100 - preset));
        return new AcrylicBrush
        {
            TintColor = color,
            FallbackColor = dark ? color : Windows.UI.Color.FromArgb(255, 249, 249, 249),
            TintOpacity = opacity
        };
    }
    private Brush FloatingWindowTint()
    {
        if (new AccessibilitySettings().HighContrast) return (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"];
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var value = _settings.UsesSystemMaterial("Acrylic") ? Services.AdjustableBackdrop.DefaultTransparency : Services.AdjustableBackdrop.Normalize(_settings.AcrylicTransparency);
        var blend = Math.Max(0, 1 - value / Services.AdjustableBackdrop.DefaultTransparency);
        var alpha = (byte)Math.Round(24 + 231 * blend);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, (byte)(dark ? 44 : 249), (byte)(dark ? 44 : 249), (byte)(dark ? 44 : 249)));
    }
    private SystemBackdrop FloatingWindowBackdrop(SystemBackdrop? current)
    {
        if (current is Services.AdjustableBackdrop backdrop)
        { backdrop.UpdateMaterial("Acrylic", _settings.AcrylicTransparency, _settings.UsesSystemMaterial("Acrylic"), Root.ActualTheme); return backdrop; }
        return new Services.AdjustableBackdrop("Acrylic", _settings.AcrylicTransparency, _settings.UsesSystemMaterial("Acrylic"), Root.ActualTheme, transient: true);
    }
    private void ApplySelectorMaterial(ComboBox combo, Brush? brush = null)
    {
        combo.Resources["ComboBoxDropDownBackground"] = brush ?? FloatingBackground();
        combo.Resources["ComboBoxDropdownBorderThickness"] = FloatingStroke;
    }
    private void ApplyPopupMaterial(DependencyObject element, Brush? brush = null)
    {
        brush ??= FloatingBackground();
        if (element is ContentDialog dialog) { dialog.Background = brush; dialog.BorderThickness = FloatingStroke; }
        else if (element is MenuFlyoutPresenter menu) { menu.Background = FloatingWindowTint(); menu.BorderThickness = FloatingStroke; }
        else if (element is FlyoutPresenter flyout)
        {
            var wrapper = ReferenceEquals(flyout.Content, QueuePane);
            flyout.Background = wrapper ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : FloatingWindowTint();
            flyout.BorderThickness = wrapper ? new Thickness(0) : FloatingStroke;
        }
        else if (element is CommandBar commands) { commands.Background = FloatingWindowTint(); commands.BorderThickness = FloatingStroke; }
        else if (element is ComboBox combo) ApplySelectorMaterial(combo, brush);
        else if (element is Border { Name: "PopupBorder" } border) { border.Background = brush; border.BorderThickness = FloatingStroke; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPopupMaterial(VisualTreeHelper.GetChild(element, i), brush);
    }
    private void ApplyFloatingMaterials()
    {
        var brush = FloatingBackground();
        QueuePane.Background = FloatingWindowTint();
        if (_queueSurfaceOpen) QueueSurface.SystemBackdrop = FloatingWindowBackdrop(QueueSurface.SystemBackdrop);
        QueuePane.BorderThickness = FloatingStroke;
        ApplyPopupMaterial(FontHost, brush);
        if (_dialog != null) ApplyPopupMaterial(_dialog, brush);
        if (Root.XamlRoot != null)
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if (popup.Child != null) ApplyPopupMaterial(popup.Child, brush);
    }
    private bool DismissDialogFromOutside(ContentDialog dialog, bool lightDismiss)
    {
        if (!lightDismiss || !_dialogSurfaceReady || _dialogPickerDepth != 0) return false;
        dialog.Hide();
        return true;
    }
    private async Task<ContentDialogResult> ShowThemedDialogAsync(ContentDialog dialog, bool lightDismiss = true)
    {
        dialog.Resources["ContentDialogButtonHeight"] = 44d;
        dialog.Resources["ContentDialogButtonMinHeight"] = 44d;
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
        dialog.XamlRoot = Root.XamlRoot;
        var inputSurfaces = new HashSet<UIElement>();
        Border? background = null;
        void PointerPressed(object sender, PointerRoutedEventArgs args)
        {
            if (background == null) return;
            var position = args.GetCurrentPoint(background).Position;
            if ((position.X < 0 || position.Y < 0 || position.X >= background.ActualWidth || position.Y >= background.ActualHeight)
                && DismissDialogFromOutside(dialog, lightDismiss)) args.Handled = true;
        }
        PointerEventHandler pressed = PointerPressed;
        void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            background = FindDescendant<Border>(dialog, b => b.Name == "BackgroundElement");
            if (FindDescendant<Grid>(dialog, g => g.Name == "LayoutRoot") is { } layout) inputSurfaces.Add(layout);
            // ContentDialog reparents its smoke layer into a separate native
            // popup. It does not bubble pointer events through LayoutRoot.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if (popup.Child is FrameworkElement { Name: "SmokeLayerBackground" } smoke) inputSurfaces.Add(smoke);
            foreach (var surface in inputSurfaces) surface.AddHandler(UIElement.PointerPressedEvent, pressed, true);
            _dialogSurfaceReady = true;
            ApplyPopupTheme(dialog);
        }
        dialog.Opened += Opened;
        _dialogSurfaceReady = false;
        try { return await dialog.ShowAsync(); }
        finally
        {
            _dialogSurfaceReady = false;
            foreach (var surface in inputSurfaces) surface.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            dialog.Opened -= Opened;
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
        _activeFlyout?.Hide(); _activeFlyout = flyout;
        PrepareFlyoutTheme(flyout);
        flyout.SystemBackdrop = FloatingWindowBackdrop(flyout.SystemBackdrop);
        flyout.Opened += Popup_Opened;
        flyout.Closed += Closed;
        void Closed(object? sender, object args) { flyout.Opened -= Popup_Opened; flyout.Closed -= Closed; if (ReferenceEquals(_activeFlyout, flyout)) _activeFlyout = null; }
        flyout.ShowAt(anchor);
    }
    private void PrepareFlyoutTheme(FlyoutBase flyout)
    {
        flyout.ShouldConstrainToRootBounds = false;
        if (flyout is MenuFlyout menu)
        {
            var style = new Style(typeof(MenuFlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["DefaultMenuFlyoutPresenterStyle"] };
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, Root.ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, FloatingWindowTint()));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, FloatingStroke));
            menu.MenuFlyoutPresenterStyle = style;
        }
        else if (flyout is Flyout plain)
        {
            var style = new Style(typeof(FlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["DefaultFlyoutPresenterStyle"] };
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, Root.ActualTheme));
            style.Setters.Add(new Setter(Control.BackgroundProperty, FloatingWindowTint()));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, FloatingStroke));
            plain.FlyoutPresenterStyle = style;
        }
        flyout.AreOpenCloseAnimationsEnabled = PlayerAnimationEnabled;
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
