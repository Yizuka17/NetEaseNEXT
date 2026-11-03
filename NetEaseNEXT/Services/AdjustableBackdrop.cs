using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NetEaseNEXT.Services;

public sealed class AdjustableBackdrop(string kind, double transparency, bool systemDefaults, ElementTheme theme) : SystemBackdrop
{
    internal const double DefaultTransparency = 65;
    internal string Kind => kind;
    internal bool UsesSystemDefaults => systemDefaults;
    internal (float Tint, float Luminosity)? AppliedProfile => _mica != null ? (_mica.TintOpacity, _mica.LuminosityOpacity) : _acrylic != null ? (_acrylic.TintOpacity, _acrylic.LuminosityOpacity) : null;
    internal (Color Tint, Color Fallback)? AppliedColors => _mica != null ? (_mica.TintColor, _mica.FallbackColor) : _acrylic != null ? (_acrylic.TintColor, _acrylic.FallbackColor) : null;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _configuration;
    private SystemBackdropTheme BackdropTheme => theme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _configuration = GetDefaultSystemBackdropConfiguration(target, root);
        _configuration.Theme = BackdropTheme;
        if (kind == "Acrylic")
        {
            _acrylic = new DesktopAcrylicController();
            _acrylic.SetSystemBackdropConfiguration(_configuration);
            if (!systemDefaults) ApplyCustom();
            _acrylic.AddSystemBackdropTarget(target);
            if (systemDefaults) _acrylic.ResetProperties();
        }
        else
        {
            _mica = new MicaController { Kind = kind == "MicaAlt" ? MicaKind.BaseAlt : MicaKind.Base };
            _mica.SetSystemBackdropConfiguration(_configuration);
            if (!systemDefaults) ApplyCustom();
            _mica.AddSystemBackdropTarget(target);
            if (systemDefaults) _mica.ResetProperties();
        }
    }

    internal static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : DefaultTransparency;
    internal static (float Tint, float Luminosity) CustomProfile(string kind, double transparency)
    {
        // Both layers change throughout the slider range. Keep a theme-colored
        // base even at maximum transparency for readable foreground content.
        var amount = (float)(Normalize(transparency) / 100);
        return kind switch
        {
            "Acrylic" => (1f - .90f * amount, .98f - .58f * amount),
            "MicaAlt" => (1f - .72f * amount, .99f - .39f * amount),
            _ => (1f - .82f * amount, .98f - .48f * amount)
        };
    }

    internal void Update(double value, bool useSystem, ElementTheme newTheme)
    {
        value = Normalize(value);
        if (transparency == value && systemDefaults == useSystem && theme == newTheme) return;
        var wasCustom = !systemDefaults;
        var themeChanged = theme != newTheme;
        transparency = value; systemDefaults = useSystem; theme = newTheme;
        if (themeChanged && _configuration != null) _configuration.Theme = BackdropTheme;
        if (!useSystem) ApplyCustom();
        else if (wasCustom || themeChanged) { _mica?.ResetProperties(); _acrylic?.ResetProperties(); }
    }

    private void ApplyCustom()
    {
        // A custom controller stops automatically following theme defaults.
        // Set all four properties so no light palette can leak into dark mode.
        var dark = theme == ElementTheme.Dark;
        var shade = (byte)(dark ? 32 : 244);
        var color = Color.FromArgb(255, shade, shade, shade);
        var (tint, luminosity) = CustomProfile(kind, transparency);
        if (_mica != null)
        {
            _mica.TintColor = color; _mica.FallbackColor = color;
            _mica.TintOpacity = tint; _mica.LuminosityOpacity = luminosity;
        }
        if (_acrylic != null)
        {
            _acrylic.TintColor = color; _acrylic.FallbackColor = color;
            _acrylic.TintOpacity = tint; _acrylic.LuminosityOpacity = luminosity;
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // XAML updates the retained configuration. Theme changes are applied
        // explicitly by Update; asking the base implementation to look up a
        // target here can fail if the callback follows a native detach.
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _configuration = null;
        _mica?.RemoveSystemBackdropTarget(target); _mica?.Dispose(); _mica = null;
        _acrylic?.RemoveSystemBackdropTarget(target); _acrylic?.Dispose(); _acrylic = null;
    }
}
