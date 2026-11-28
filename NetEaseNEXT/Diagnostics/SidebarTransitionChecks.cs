using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSidebarTransitionChecksAsync(Action<string, bool, object?> check)
    {
        var oldAnimations = _settings.Animations;
        var oldPreview = _interactiveMotionPreview;
        var oldSize = AppWindow.Size;
        var oldMode = Nav.PaneDisplayMode;
        _sessionReady = true; _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        _settings.Animations = true; ConfigureInterfaceMotion();
        _interactiveMotionPreview = true;
        try
        {
            RestoreWindow();
            foreach (var height in new[] { 1600, 1100 })
            foreach (var mode in new[] { NavigationViewPaneDisplayMode.LeftCompact, NavigationViewPaneDisplayMode.Left })
            {
                AppWindow.Resize(new(1280, height));
                Nav.PaneDisplayMode = mode; Nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
                Nav.SelectedItem = Nav.MenuItems[5]; await Task.Delay(700);
                var samples = new List<object>(); var failures = 0;
                foreach (var index in new[] { 6, 7, 6, 0, 6, 1, 7, 5 })
                {
                    var previous = _sidebarSelectionMotion.Y;
                    Nav.SelectedItem = Nav.MenuItems[index];
                    await Task.Delay(80);
                    var visual = ElementCompositionPreview.GetElementVisual(SidebarSelectionIndicator);
                    var scale = visual.TryGetAnimationController("Scale") != null;
                    var move = visual.TryGetAnimationController("Translation") != null;
                    samples.Add(new { index, _view, previous, target = _sidebarSelectionMotion.Y, scale, move, _sidebarSelectionMotion.ScrollBound, visible = SidebarSelectionIndicator.Visibility.ToString() });
                    if (!scale || !move) failures++;
                    await Task.Delay(FluentMotion.Duration + TimeSpan.FromMilliseconds(60));
                }
                check($"{mode} {height}px downloads and queue retain their selection transition", failures == 0, samples);
            }
        }
        finally
        {
            _interactiveMotionPreview = oldPreview; _settings.Animations = oldAnimations;
            ConfigureInterfaceMotion(); Nav.PaneDisplayMode = oldMode; AppWindow.Resize(oldSize);
        }
    }
}
