using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSidebarChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1600));
        var items = Nav.MenuItems.OfType<NavigationViewItem>().Append((NavigationViewItem)Nav.SettingsItem).ToArray();
        foreach (var mode in new[] { NavigationViewPaneDisplayMode.LeftCompact, NavigationViewPaneDisplayMode.Left })
        {
            Nav.PaneDisplayMode = mode; Nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
            foreach (var item in items)
            {
                Nav.SelectedItem = item; Root.UpdateLayout(); await Task.Delay(50);
                var icon = item.Icon;
                check($"{mode} {item.Content} selected icon keeps the standard slot", Math.Abs(icon.ActualWidth - 20) < .1 && Math.Abs(icon.ActualHeight - 20) < .1, new { icon.ActualWidth, icon.ActualHeight });
                if (icon is PathIcon path)
                {
                    var bounds = path.Data.Bounds;
                    check($"{mode} {item.Content} entire vector fits with padding", bounds.X >= .9 && bounds.Y >= .9 && bounds.Right <= 19.1 && bounds.Bottom <= 19.1,
                        new { bounds.X, bounds.Y, bounds.Width, bounds.Height });
                }
            }
        }
        var settings = (NavigationViewItem)Nav.SettingsItem;
        Nav.SelectedItem = settings; Root.UpdateLayout();
        if (PlayerAnimationEnabled)
        {
            RotateSettingsIcon();
            var first = _settingsIconRotation;
            check("Settings rotation targets the visible selected icon", first?.GetCurrentState() == ClockState.Active && ReferenceEquals(settings.Icon.RenderTransform, _settingsIconTransform), null);
            await Task.Delay(100); RotateSettingsIcon();
            check("Repeated settings activation restarts rotation", _settingsIconRotation?.GetCurrentState() == ClockState.Active && !ReferenceEquals(first, _settingsIconRotation), null);
            await Task.Delay(500);
            check("Settings rotation finishes without a persistent animation", _settingsIconRotation == null && _settingsIconTransform == null, null);
            RotateSettingsIcon(); HideWindow();
            check("Hiding the app stops settings rotation", _settingsIconRotation == null && _settingsIconTransform == null, null);
            RestoreWindow();
        }
        var enabled = _settings.Animations;
        try
        {
            _settings.Animations = false; RotateSettingsIcon();
            check("Disabled animations keep the settings icon still", _settingsIconRotation == null, null);
        }
        finally { _settings.Animations = enabled; }
        if (Environment.GetCommandLineArgs().Contains("--inspect-layout"))
        {
            AppWindow.Title = "NetEaseNEXT 侧栏检查";
            Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact; Nav.IsPaneOpen = false;
            foreach (var item in items) item.IsSelected = false;
            Nav.SelectedItem = Nav.MenuItems[1];
            ((NavigationViewItem)Nav.MenuItems[1]).IsSelected = true;
            await Task.Delay(60000);
        }
    }
}
