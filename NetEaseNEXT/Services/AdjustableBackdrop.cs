using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NetEaseNEXT.Services;

public sealed class AdjustableBackdrop(string kind, double transparency, bool systemDefaults, ElementTheme theme) : SystemBackdrop
{
    internal const double DefaultTransparency = 65;
    internal string Kind => kind;
    internal bool UsesSystemDefaults => systemDefaults || Normalize(transparency) == DefaultTransparency;
    internal double SolidBlend => systemDefaults ? 0 : Math.Max(0, 1 - Normalize(transparency) / DefaultTransparency);
    private bool UsesNativeController => systemDefaults || Normalize(transparency) <= DefaultTransparency;
    internal (float Tint, float Luminosity)? AppliedProfile => _mica != null ? (_mica.TintOpacity, _mica.LuminosityOpacity) : _acrylic != null ? (_acrylic.TintOpacity, _acrylic.LuminosityOpacity) : null;
    internal (Color Tint, Color Fallback)? AppliedColors => _mica != null ? (_mica.TintColor, _mica.FallbackColor) : _acrylic != null ? (_acrylic.TintColor, _acrylic.FallbackColor) : null;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _xamlConfiguration;
    private SystemBackdropConfiguration? _configuration;
    private (Color Tint, Color Fallback, float TintOpacity, float LuminosityOpacity)? _native;
    private DispatcherQueueTimer? _presetTimer;
    internal bool NativeProfileReady => _native.HasValue;
    private SystemBackdropTheme BackdropTheme => theme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _xamlConfiguration = GetDefaultSystemBackdropConfiguration(target, root);
        _configuration = new SystemBackdropConfiguration { Theme = BackdropTheme };
        CopyWindowState();
        if (kind == "Acrylic")
        {
            _acrylic = new DesktopAcrylicController();
            _acrylic.SetSystemBackdropConfiguration(_configuration);
            _acrylic.AddSystemBackdropTarget(target);
        }
        else
        {
            _mica = new MicaController { Kind = kind == "MicaAlt" ? MicaKind.BaseAlt : MicaKind.Base };
            _mica.SetSystemBackdropConfiguration(_configuration);
            _mica.AddSystemBackdropTarget(target);
        }
        RefreshNativeProfile();
    }

    internal static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : DefaultTransparency;
    private void ResetNativeProperties()
    {
        _mica?.ResetProperties();
        _acrylic?.ResetProperties();
    }

    private void RefreshNativeProfile()
    {
        _native = null;
        ResetNativeProperties();
        if (_configuration == null) return;
        // The native controller updates its getters after the theme reaches the
        // composition tree. Wait once rather than capturing the previous palette.
        if (_presetTimer == null)
        {
            _presetTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _presetTimer.Interval = TimeSpan.FromMilliseconds(100);
            _presetTimer.IsRepeating = false;
            _presetTimer.Tick += CaptureNativeProfile;
        }
        _presetTimer.Stop();
        _presetTimer.Start();
    }

    private void CaptureNativeProfile(DispatcherQueueTimer sender, object args)
    {
        if (AppliedColors is not { } colors || AppliedProfile is not { } profile) return;
        _native = (colors.Tint, colors.Fallback, profile.Tint, profile.Luminosity);
        if (!UsesNativeController) ApplyCustom();
    }

    internal void Update(double value, bool useSystem, ElementTheme newTheme)
    {
        value = Normalize(value);
        if (transparency == value && systemDefaults == useSystem && theme == newTheme) return;
        var wasNative = UsesNativeController;
        var themeChanged = theme != newTheme;
        transparency = value; systemDefaults = useSystem; theme = newTheme;
        if (themeChanged)
        {
            if (_configuration != null) _configuration.Theme = BackdropTheme;
            RefreshNativeProfile();
        }
        else if (UsesNativeController && !wasNative) ResetNativeProperties();
        if (!UsesNativeController) ApplyCustom();
    }

    private void ApplyCustom()
    {
        if (_native is not { } native) return;
        // Above the native point, gradually reveal more of the backdrop.
        var amount = (float)((Normalize(transparency) - DefaultTransparency) / (100 - DefaultTransparency));
        var tint = native.TintOpacity * (1 - .55f * amount);
        var luminosity = native.LuminosityOpacity * (1 - .35f * amount);
        if (_mica != null)
        {
            _mica.TintColor = native.Tint; _mica.FallbackColor = native.Fallback;
            _mica.TintOpacity = tint; _mica.LuminosityOpacity = luminosity;
        }
        if (_acrylic != null)
        {
            _acrylic.TintColor = native.Tint; _acrylic.FallbackColor = native.Fallback;
            _acrylic.TintOpacity = tint; _acrylic.LuminosityOpacity = luminosity;
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // XAML can update its default theme after ApplyAppearance. Follow its window
        // state, but keep the app's chosen theme as the source for material colors.
        CopyWindowState();
    }

    private void CopyWindowState()
    {
        if (_configuration is not { } active || _xamlConfiguration is not { } xaml) return;
        active.IsInputActive = xaml.IsInputActive;
        active.HighContrastBackgroundColor = xaml.HighContrastBackgroundColor;
        active.IsHighContrast = xaml.IsHighContrast;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_presetTimer != null)
        {
            _presetTimer.Stop();
            _presetTimer.Tick -= CaptureNativeProfile;
            _presetTimer = null;
        }
        _native = null;
        _xamlConfiguration = null;
        _configuration = null;
        _mica?.RemoveSystemBackdropTarget(target); _mica?.Dispose(); _mica = null;
        _acrylic?.RemoveSystemBackdropTarget(target); _acrylic?.Dispose(); _acrylic = null;
    }
}
