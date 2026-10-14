using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NetEaseNEXT.Services;

public sealed class AdjustableBackdrop(string kind, double transparency) : SystemBackdrop
{
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        var config = GetDefaultSystemBackdropConfiguration(target, root);
        if (kind == "Acrylic")
        {
            _acrylic = new DesktopAcrylicController();
            _acrylic.SetSystemBackdropConfiguration(config); _acrylic.AddSystemBackdropTarget(target);
        }
        else
        {
            _mica = new MicaController { Kind = kind == "MicaAlt" ? MicaKind.BaseAlt : MicaKind.Base };
            _mica.SetSystemBackdropConfiguration(config); _mica.AddSystemBackdropTarget(target);
        }
        Apply(config.Theme == SystemBackdropTheme.Dark);
    }
    private void Apply(bool dark)
    {
        var tint = dark ? Color.FromArgb(255, 30, 30, 30) : Color.FromArgb(255, 245, 245, 245);
        var opacity = (float)(1 - Math.Clamp(transparency, 0, 100) / 100);
        if (_mica != null) { _mica.TintColor = tint; _mica.FallbackColor = tint; _mica.TintOpacity = opacity; _mica.LuminosityOpacity = opacity; }
        if (_acrylic != null) { _acrylic.TintColor = tint; _acrylic.FallbackColor = tint; _acrylic.TintOpacity = opacity; _acrylic.LuminosityOpacity = opacity; }
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        var configuration = GetDefaultSystemBackdropConfiguration(target, root);
        if (configuration != null) Apply(configuration.Theme == SystemBackdropTheme.Dark);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _mica?.RemoveSystemBackdropTarget(target); _mica?.Dispose(); _mica = null;
        _acrylic?.RemoveSystemBackdropTarget(target); _acrylic?.Dispose(); _acrylic = null;
    }
}
