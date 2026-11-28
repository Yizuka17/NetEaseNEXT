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
    private CornerRadius? _defaultFloatingCorner, _defaultTooltipCorner;
    private CornerRadius _floatingCorner, _tooltipCorner;
    private bool _floatingCornersReady;
    private bool _hasCustomFloatingCorner;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolTip, object> _floatingTooltips = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, object> _dialogShadows = new();
    private void PrepareFloatingToolTip(ToolTip tip)
    {
        if (_floatingTooltips.TryAdd(tip, new object())) tip.Opened += (_, _) => { RefreshFloatingCorners(); ApplyPopupTheme(tip); };
    }
    private void RefreshFloatingCorners()
    {
        _defaultFloatingCorner ??= (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
        _defaultTooltipCorner ??= Application.Current.Resources.TryGetValue("ToolTipCornerRadius", out var tooltip) && tooltip is CornerRadius radius ? radius : new CornerRadius(4);
        ApplyFloatingCorners(Services.FloatingCornerPolicy.Read(_defaultFloatingCorner.Value, _defaultTooltipCorner.Value));
    }
    private void ApplyFloatingCorners(Services.FloatingCornerPolicy.Corners selected)
    {
        _hasCustomFloatingCorner = selected.HasCustomOverlay;
        if (!_floatingCornersReady || _floatingCorner != selected.Overlay) { _floatingCorner = selected.Overlay; Application.Current.Resources["OverlayCornerRadius"] = _floatingCorner; }
        if (!_floatingCornersReady || _tooltipCorner != selected.ToolTip) { _tooltipCorner = selected.ToolTip; Application.Current.Resources["ToolTipCornerRadius"] = _tooltipCorner; }
        _floatingCornersReady = true;
    }
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
    private sealed class SelectorSurfaceState { public bool Applying; public Popup? Popup; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, SelectorSurfaceState> _selectorSurfaces = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Popup, object> _selectorPopupHooks = new();
    private void Selector_Loaded(object sender, RoutedEventArgs args) => ApplySelectorMaterial((ComboBox)sender);
    private void ApplySelectorMaterial(ComboBox combo, Brush? brush = null)
    {
        var state = _selectorSurfaces.GetValue(combo, c => { c.Loaded += Selector_Loaded; return new SelectorSurfaceState(); });
        if (state.Applying) return;
        state.Applying = true;
        try
        {
            combo.Resources["ComboBoxDropDownBackground"] = brush ?? FloatingBackground();
            if (!combo.Resources.TryGetValue("ComboBoxDropdownBorderThickness", out var stroke) || stroke is not Thickness thickness || thickness != FloatingStroke) combo.Resources["ComboBoxDropdownBorderThickness"] = FloatingStroke;
            if (!combo.IsLoaded) return;
            if (VisualTreeHelper.GetChildrenCount(combo) == 0) combo.ApplyTemplate();
            var popup = FindDescendant<Popup>(combo, p => p.Name == "Popup") ?? state.Popup;
            if (popup == null) return;
            state.Popup = popup;
            if (!popup.IsOpen) popup.ShouldConstrainToRootBounds = false;
            popup.SystemBackdrop = FloatingWindowBackdrop(popup.SystemBackdrop);
            if (_selectorPopupHooks.TryAdd(popup, new object())) popup.Opened += SelectorPopupOpened;
        }
        finally { state.Applying = false; }
    }
    private Popup? SelectorPopup(ComboBox combo) => _selectorSurfaces.TryGetValue(combo, out var state) ? state.Popup : null;
    private void SelectorPopupOpened(object? sender, object args)
    {
        if (sender is Popup { Child: FrameworkElement surface }) ApplyPopupTheme(surface);
        Popup_Opened(sender, args);
        if (sender is Popup { Child: { } child } && FindDescendant<ScrollViewer>(child) is { } scroll && _popupTypographyScrollers.TryAdd(scroll, new object()))
            scroll.ViewChanged += PopupTypographyScrolled;
    }
    private void ApplyPopupMaterial(DependencyObject element, Brush? brush = null)
    {
        brush ??= FloatingBackground();
        if (element is ContentDialog dialog) { dialog.Background = brush; dialog.BorderThickness = FloatingStroke; dialog.CornerRadius = _floatingCorner; }
        else if (element is MenuFlyoutPresenter menu) { menu.Background = FloatingWindowTint(); menu.BorderThickness = FloatingStroke; menu.CornerRadius = _floatingCorner; }
        else if (element is FlyoutPresenter flyout)
        {
            var wrapper = ReferenceEquals(flyout.Content, QueuePane);
            flyout.Background = wrapper ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : FloatingWindowTint();
            flyout.BorderThickness = wrapper ? new Thickness(0) : FloatingStroke;
            flyout.CornerRadius = _floatingCorner;
        }
        else if (element is CommandBar commands) { commands.Background = FloatingWindowTint(); commands.BorderThickness = FloatingStroke; }
        else if (element is ComboBox combo) ApplySelectorMaterial(combo, brush);
        else if (element is Border { Name: "PopupBorder" } border)
        {
            border.Background = brush; border.BorderThickness = FloatingStroke; border.CornerRadius = _floatingCorner;
            border.BackgroundSizing = BackgroundSizing.OuterBorderEdge; border.UseLayoutRounding = true;
        }
        else if (element is Border { Name: "BackgroundElement" } frame && _dialog != null && ReferenceEquals(FindDescendant<Border>(_dialog, b => b.Name == "BackgroundElement"), frame))
        {
            // Paint acrylic underneath the translucent stroke. Insetting the
            // fill exposes a fringe at fractional-DPI rounded edges.
            frame.BackgroundSizing = BackgroundSizing.OuterBorderEdge; frame.UseLayoutRounding = true;
            // Keep the native shadow at flyout elevation. ContentDialog's
            // default 128-DIP elevation creates a wide halo in in-app acrylic.
            frame.Shadow ??= new ThemeShadow();
            if (_dialogShadows.TryAdd(frame, new object()))
                frame.SizeChanged += static (sender, _) =>
                {
                    // Native content remeasurement can restore its default
                    // elevation after Opened (for example, a loaded ComboBox).
                    var border = (Border)sender;
                    if (border.Translation.Z != 32) border.Translation = new(border.Translation.X, border.Translation.Y, 32);
                };
            frame.Translation = new(frame.Translation.X, frame.Translation.Y, 32);
        }
        else if (element is ToolTip tip) { tip.Background = brush; tip.BorderThickness = FloatingStroke; tip.CornerRadius = _tooltipCorner; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ApplyPopupMaterial(VisualTreeHelper.GetChild(element, i), brush);
    }
    private void ApplyFloatingMaterials()
    {
        RefreshFloatingCorners();
        var brush = FloatingBackground();
        Notice.Background = PlayerNotice.Background = FloatingBackground();
        if (_eventPictureOverlay != null) _eventPictureOverlay.Background = FloatingBackground();
        Notice.BorderThickness = PlayerNotice.BorderThickness = FloatingStroke;
        foreach (var notice in new[] { Notice, PlayerNotice })
            if (_hasCustomFloatingCorner) notice.CornerRadius = _floatingCorner; else notice.ClearValue(Control.CornerRadiusProperty);
        QueuePane.Background = FloatingWindowTint();
        if (_queueSurfaceOpen) QueueSurface.SystemBackdrop = FloatingWindowBackdrop(QueueSurface.SystemBackdrop);
        QueuePane.BorderThickness = FloatingStroke;
        ApplyPopupMaterial(FontHost, brush);
        if (_dialog != null) ApplyPopupMaterial(_dialog, brush);
        foreach (var root in FloatingXamlRoots())
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
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
        RefreshFloatingCorners();
        dialog.Resources["ContentDialogButtonHeight"] = 44d;
        dialog.Resources["ContentDialogButtonMinHeight"] = 44d;
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // The stock template keeps its content/command separator even when
        // CommandSpace is collapsed. Custom-heading dialogs have no button row.
        if (dialog.PrimaryButtonText.Length == 0 && dialog.SecondaryButtonText.Length == 0 && dialog.CloseButtonText.Length == 0)
            dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
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
        var outsideInput = new Microsoft.UI.Xaml.Shapes.Rectangle { Name = "DialogOutsideInput", Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Border? background = null;
        var opened = false;
        void PointerPressed(object sender, PointerRoutedEventArgs args)
        {
            background = FindDescendant<Border>(dialog, b => b.Name == "BackgroundElement");
            if (background == null) return;
            // Popup coordinates can differ from the owner at fractional/high
            // DPI. Use the hit-tested visual ancestry so content clicks always
            // remain inside, including blank content and template children.
            for (var source = args.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
                if (ReferenceEquals(source, background)) return;
            if (DismissDialogFromOutside(dialog, lightDismiss)) args.Handled = true;
        }
        PointerEventHandler pressed = PointerPressed;
        void AttachInput(UIElement surface)
        {
            if (inputSurfaces.Add(surface)) surface.AddHandler(UIElement.PointerPressedEvent, pressed, true);
        }
        void PrepareSurface()
        {
            background = FindDescendant<Border>(dialog, b => b.Name == "BackgroundElement");
            AttachInput(dialog);
            if (FindDescendant<Grid>(dialog, g => g.Name == "LayoutRoot") is { } layout)
            {
                // The native sizing state forces LayoutRoot.Background to null.
                // An explicit input child keeps empty popup space hit-testable
                // without replacing the native template or its visual states.
                if (!ReferenceEquals(outsideInput.Parent, layout))
                {
                    if (outsideInput.Parent is Panel previous) previous.Children.Remove(outsideInput);
                    layout.Children.Insert(0, outsideInput);
                }
                AttachInput(layout);
            }
            // ContentDialog reparents its smoke layer into a separate native
            // popup. It does not bubble pointer events through LayoutRoot.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if (popup.Child is { } child && FindDescendant<FrameworkElement>(child, e => e.Name == "SmokeLayerBackground") is { } smoke)
                {
                    if (smoke is Microsoft.UI.Xaml.Shapes.Shape shape) shape.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    AttachInput(smoke);
                }
            ApplyPopupTheme(dialog);
            _dialogSurfaceReady = opened && background != null;
        }
        void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            opened = true;
            PrepareSurface();
        }
        void Loaded(object sender, RoutedEventArgs args) => PrepareSurface();
        dialog.Loaded += Loaded;
        dialog.Opened += Opened;
        _dialogSurfaceReady = false;
        try { return await dialog.ShowAsync(); }
        finally
        {
            _dialogSurfaceReady = false;
            foreach (var surface in inputSurfaces) surface.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            if (outsideInput.Parent is Panel parent) parent.Children.Remove(outsideInput);
            dialog.Opened -= Opened;
            dialog.Loaded -= Loaded;
            dialog.ClearValue(FrameworkElement.RequestedThemeProperty);
        }
    }
    private void Popup_Opened(object? sender, object args)
    {
        foreach (var root in FloatingXamlRoots())
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is FrameworkElement child) ApplyPopupTheme(child);
        ApplyFloatingMaterials();
    }
    private IEnumerable<XamlRoot> FloatingXamlRoots() => new[] { Root.XamlRoot, _dialog?.XamlRoot, _tray?.Menu?.Presenter?.XamlRoot }.OfType<XamlRoot>().Distinct();
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
        RefreshFloatingCorners();
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
