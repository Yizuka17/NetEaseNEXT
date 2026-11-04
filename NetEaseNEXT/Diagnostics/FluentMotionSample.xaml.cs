using System.Numerics;
using NetEaseNEXT.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace NetEaseNEXT.Diagnostics;

public sealed partial class FluentMotionSample : Window
{
    private IconElement? _active;
    private static readonly TimeSpan Duration = FluentMotion.Duration;
    public FluentMotionSample()
    {
        InitializeComponent();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new(work.X + (work.Width - 720) / 2, work.Y + (work.Height - 400) / 2, 720, 400));
        Dock.Loaded += (_, _) => MoveMarker(Dock.MenuItems.OfType<NavigationViewItem>().First(i => i.IsSelected), false);
        DockHost.SizeChanged += (_, _) => { if (Dock.IsLoaded && Dock.SelectedItem is NavigationViewItem item) MoveMarker(item, false); };
    }
    private void MoveMarker(NavigationViewItem item, bool animate)
    {
        var point = item.TransformToVisual(DockHost).TransformPoint(new(item.ActualWidth / 2 - 8, item.ActualHeight - 10));
        Marker.TranslationTransition = animate ? new Vector3Transition { Duration = Duration } : null;
        Marker.Translation = new((float)point.X, (float)point.Y, 0); Marker.Visibility = Visibility.Visible;
    }
    private void Invoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Icon: { } icon } item) return;
        var enabled = new UISettings().AnimationsEnabled;
        if (_active != null && (_active != icon || !enabled)) { var old = ElementCompositionPreview.GetElementVisual(_active); old.StopAnimation("Translation"); old.Properties.InsertVector3("Translation", Vector3.Zero); }
        MoveMarker(item, enabled);
        if (!enabled) return;
        _active = icon;
        FluentMotion.Bounce(icon, new(0, -6, 0));
    }
}
