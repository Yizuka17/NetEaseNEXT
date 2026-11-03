using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;
using Windows.Graphics;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _windowPlacementTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private RectInt32 _normalWindowBounds;
    private bool _normalWindowMaximized, _restoringWindowPlacement, _windowPlacementDirty, _checkingWindowPreferences;
    private readonly bool _diagnosticWindow = Environment.GetCommandLineArgs().Contains("--smoke-test");
    private void InitializeWindowPreferences()
    {
        RememberWindowToggle.IsOn = _settings.RememberWindowBounds;
        _normalWindowBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _windowPlacementTimer.Tick += (_, _) => FlushWindowPlacement();
        if (!_diagnosticWindow) RestoreWindowPlacement();
    }
    private bool RestoreWindowPlacement()
    {
        if (!_settings.RememberWindowBounds || _settings.WindowPlacement is not { IsValid: true } saved) return false;
        var display = DisplayArea.GetFromRect(saved.Bounds, DisplayAreaFallback.Nearest);
        if (display == null || display.WorkArea.Width <= 0 || display.WorkArea.Height <= 0) return false;
        var bounds = saved.ClampTo(display.WorkArea);
        _restoringWindowPlacement = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Restore(); presenter.PreferredMinimumWidth = Math.Min(900, display.WorkArea.Width);
                AppWindow.MoveAndResize(bounds); _normalWindowBounds = bounds; _normalWindowMaximized = saved.Maximized;
                if (saved.Maximized) presenter.Maximize();
            }
            return true;
        }
        finally { _restoringWindowPlacement = false; }
    }
    private void CaptureWindowPlacement()
    {
        if (_restoringWindowPlacement || _fullScreen || _exiting || _diagnosticWindow && !_checkingWindowPreferences || AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        if (presenter.State == OverlappedPresenterState.Restored)
        {
            var size = AppWindow.Size;
            if (size.Width < 200 || size.Height < 150) return;
            _normalWindowBounds = new(AppWindow.Position.X, AppWindow.Position.Y, size.Width, size.Height);
        }
        _normalWindowMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (!_settings.RememberWindowBounds) return;
        var next = new WindowPlacement(_normalWindowBounds.X, _normalWindowBounds.Y, _normalWindowBounds.Width, _normalWindowBounds.Height, _normalWindowMaximized);
        if (!next.IsValid || next == _settings.WindowPlacement) return;
        _settings.WindowPlacement = next; _windowPlacementDirty = true; _windowPlacementTimer.Stop(); _windowPlacementTimer.Start();
    }
    private void FlushWindowPlacement()
    {
        _windowPlacementTimer.Stop();
        if (!_windowPlacementDirty) return;
        _windowPlacementDirty = false; SaveSettings();
    }
    private void RememberWindow_Changed(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _settings.RememberWindowBounds = RememberWindowToggle.IsOn;
        _windowPlacementTimer.Stop(); _windowPlacementDirty = false;
        if (_settings.RememberWindowBounds) CaptureWindowPlacement(); else _settings.WindowPlacement = null;
        SaveSettings();
    }
    private int RestoreWindowCommand => !_fullScreen && _normalWindowMaximized ? 3 : 9;
}
