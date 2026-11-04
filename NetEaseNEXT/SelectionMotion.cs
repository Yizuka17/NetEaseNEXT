using System.Numerics;
using NetEaseNEXT.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private sealed class SelectionMotionState
    {
        public object? Item;
        public object? PendingItem;
        public double X, Y = double.NaN;
    }
    private readonly SelectionMotionState _trackSelectionMotion = new(), _queueSelectionMotion = new(), _sidebarSelectionMotion = new();
    private readonly HashSet<ScrollViewer> _selectionScrollers = [];
    private void InitializeSelectionMotion()
    {
        Nav.SelectionChanged += (_, _) => UpdateSidebarSelection(true);
        Nav.LayoutUpdated += (_, _) => UpdateSidebarSelection(false);
        foreach (var list in new[] { TrackList, QueueList })
        {
            list.Resources["ListViewItemSelectionIndicatorVisualEnabled"] = false;
            list.SelectionChanged += (_, _) =>
            {
                var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
                if (!ReferenceEquals(list.SelectedItem, state.Item)) state.PendingItem = list.SelectedItem;
                UpdateSelectionIndicators(true);
            };
            list.LayoutUpdated += (_, _) => UpdateSelectionIndicator(list, false);
            list.Loaded += (_, _) =>
            {
                if (FindDescendant<ScrollViewer>(list) is { } scroll && _selectionScrollers.Add(scroll))
                    scroll.ViewChanged += (_, _) => UpdateSelectionIndicator(list, false);
            };
        }
    }
    private void StopSelectionMotion()
    {
        foreach (var marker in new[] { TrackSelectionIndicator, QueueSelectionIndicator, SidebarSelectionIndicator })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
            var visual = ElementCompositionPreview.GetElementVisual(marker);
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            var state = marker == TrackSelectionIndicator ? _trackSelectionMotion : marker == QueueSelectionIndicator ? _queueSelectionMotion : _sidebarSelectionMotion;
            if (double.IsFinite(state.Y)) visual.Properties.InsertVector3("Translation", new((float)state.X, (float)state.Y, 0));
        }
    }
    private void UpdateSelectionIndicators(bool animate)
    {
        UpdateSidebarSelection(animate);
        UpdateSelectionIndicator(TrackList, animate);
        UpdateSelectionIndicator(QueueList, animate);
    }
    private void UpdateSidebarSelection(bool animate)
    {
        var marker = SidebarSelectionIndicator; var state = _sidebarSelectionMotion;
        if (_hidden || Nav.Visibility != Visibility.Visible || !Nav.IsLoaded || Nav.DisplayMode == NavigationViewDisplayMode.Minimal && !Nav.IsPaneOpen || Nav.SelectedItem is not NavigationViewItem item || !item.IsLoaded)
        { marker.Visibility = Visibility.Collapsed; state.Item = null; state.Y = double.NaN; return; }
        var bounds = item.TransformToVisual(Nav).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
        for (DependencyObject? parent = VisualTreeHelper.GetParent(item); parent != null && parent != Nav; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                var row = item.TransformToVisual(scroll).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
                if (row.Bottom <= 0 || row.Y >= scroll.ActualHeight) { marker.Visibility = Visibility.Collapsed; state.Item = null; return; }
                break;
            }
        var x = bounds.X + 2; var y = bounds.Y + (bounds.Height - marker.Height) / 2;
        if (bounds.Y < 0 || bounds.Bottom > Nav.ActualHeight) { marker.Visibility = Visibility.Collapsed; state.Item = null; return; }
        if (ReferenceEquals(item, state.Item) && Math.Abs(x - state.X) < .1 && Math.Abs(y - state.Y) < .1) return;
        ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
        var visual = ElementCompositionPreview.GetElementVisual(marker);
        if (animate && state.Item != null && double.IsFinite(state.Y) && PlayerAnimationEnabled)
            FluentMotion.MoveIndicator(marker, (float)state.Y, new((float)x, (float)y, 0), (float)marker.Height);
        else
        {
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", new((float)x, (float)y, 0));
        }
        marker.Visibility = Visibility.Visible; state.Item = item; state.X = x; state.Y = y;
    }
    private void UpdateSelectionIndicator(ListView list, bool animate)
    {
        var marker = list == TrackList ? TrackSelectionIndicator : QueueSelectionIndicator;
        var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
        var item = list.SelectedItem;
        if (_hidden || !list.IsLoaded || list.ActualHeight <= 0 || ReferenceEquals(_batchList, list) || item == null)
        {
            marker.Visibility = Visibility.Collapsed; state.Item = null; state.PendingItem = null; state.Y = double.NaN; return;
        }
        // SelectionChanged can precede the realization of a virtualized row.
        // Preserve the previous position until LayoutUpdated can animate to the new row.
        if (list.ContainerFromItem(item) is not ListViewItem row || !row.IsLoaded) return;
        var bounds = row.TransformToVisual(list).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
        var y = bounds.Y + (bounds.Height - marker.Height) / 2;
        if (bounds.Bottom <= 0 || bounds.Y >= list.ActualHeight || y < 0 || y + marker.Height > list.ActualHeight)
        {
            marker.Visibility = Visibility.Collapsed; state.Item = null; state.Y = double.NaN; return;
        }
        var changed = !ReferenceEquals(item, state.Item);
        if (!changed && Math.Abs(y - state.Y) < .1 && marker.Visibility == Visibility.Visible) return;
        ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
        var visual = ElementCompositionPreview.GetElementVisual(marker);
        if ((animate || ReferenceEquals(state.PendingItem, item)) && changed && state.Item != null &&
            double.IsFinite(state.Y) && PlayerAnimationEnabled && Math.Abs(y - state.Y) < list.ActualHeight)
        {
            FluentMotion.MoveIndicator(marker, (float)state.Y, new(0, (float)y, 0), (float)marker.Height);
        }
        else
        {
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", new(0, (float)y, 0));
        }
        marker.Visibility = Visibility.Visible; state.Item = item; state.PendingItem = null; state.Y = y;
    }
}
