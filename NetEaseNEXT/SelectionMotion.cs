using System.Numerics;
using NetEaseNEXT.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
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
        public bool DispatchQueued;
        public double X, Y = double.NaN;
        public ScrollViewer? Scroll;
        public bool ScrollBound;
        public int Index = -1;
        public bool Offscreen;
        public bool SilentSelection;
        public int SelectionVersion;
    }
    private readonly SelectionMotionState _trackSelectionMotion = new(), _queueSelectionMotion = new(), _sidebarSelectionMotion = new();
    private readonly HashSet<ScrollViewer> _selectionScrollers = [];
    private void InitializeSelectionMotion()
    {
        Nav.SelectionChanged += (_, _) => UpdateSidebarSelection(true);
        Nav.LayoutUpdated += (_, _) => UpdateSidebarSelection(false);
        foreach (var list in new[] { TrackList, QueueList })
        {
            var clip = list == TrackList ? TrackSelectionClip : QueueSelectionClip;
            clip.SizeChanged += (_, _) => UpdateSelectionIndicator(list, false);
            list.Resources["ListViewItemSelectionIndicatorVisualEnabled"] = false;
            list.BringIntoViewRequested += (_, args) =>
            {
                var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
                if (state.SilentSelection) args.Handled = true;
            };
            list.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AllowSelectionReveal(list)), true);
            list.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => AllowSelectionReveal(list)), true);
            list.SelectionChanged += (_, _) =>
            {
                var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
                // Pointer/touch selection can run before ItemClick changes the playing track.
                // Keep the previous visual position throughout this input event and commit
                // the selection after the event has finished, rather than letting an
                // intervening LayoutUpdated snap the marker to its destination.
                state.PendingItem = list.SelectedItem;
                if (state.DispatchQueued) return;
                state.DispatchQueued = true;
                list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    state.DispatchQueued = false;
                    UpdateSelectionIndicator(list, true);
                });
            };
            list.LayoutUpdated += (_, _) =>
            {
                var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
                if (!state.DispatchQueued) UpdateSelectionIndicator(list, false);
            };
            list.Loaded += (_, _) =>
            {
                if (FindDescendant<ScrollViewer>(list) is { } scroll && _selectionScrollers.Add(scroll))
                    scroll.ViewChanged += (_, _) => UpdateSelectionIndicator(list, false);
            };
        }
    }
    private void AllowSelectionReveal(ListView list)
    {
        var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
        state.SilentSelection = false; state.SelectionVersion++;
    }
    private void SelectPlayingTrack(ListView list, object? item)
    {
        if (ReferenceEquals(list.SelectedItem, item)) return;
        var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
        var scroll = FindDescendant<ScrollViewer>(list);
        var x = scroll?.HorizontalOffset ?? 0; var y = scroll?.VerticalOffset ?? 0;
        state.SilentSelection = true; var version = ++state.SelectionVersion;
        list.SelectedItem = item;
        list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != state.SelectionVersion) return;
            // Selection is bookkeeping, not a request to reveal the playing row.
            if (scroll != null && (Math.Abs(scroll.VerticalOffset - y) > .5 || Math.Abs(scroll.HorizontalOffset - x) > .5))
                scroll.ChangeView(x, y, null, true);
            state.SilentSelection = false;
        });
    }
    private static (double Top, double Bottom) SongRowBounds(ListView list, double offset)
    {
        if (list.ItemsPanelRoot is not { } panel) return (offset, offset + list.ActualHeight);
        var bounds = panel.TransformToVisual(list).TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight));
        return (bounds.Y + offset, bounds.Bottom + offset);
    }
    private void StopSelectionMotion()
    {
        foreach (var marker in new[] { TrackSelectionIndicator, QueueSelectionIndicator, SidebarSelectionIndicator })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
            var visual = ElementCompositionPreview.GetElementVisual(marker);
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            var state = marker == TrackSelectionIndicator ? _trackSelectionMotion : marker == QueueSelectionIndicator ? _queueSelectionMotion : _sidebarSelectionMotion;
            if (marker != SidebarSelectionIndicator)
            {
                var layer = marker == TrackSelectionIndicator ? TrackSelectionLayer : QueueSelectionLayer;
                ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
                ElementCompositionPreview.GetElementVisual(layer).StopAnimation("Translation.Y"); state.ScrollBound = false;
            }
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
    private void BindSelectionScroll(ListView list, SelectionMotionState state)
    {
        if (FindDescendant<ScrollViewer>(list) is not { } scroll) return;
        if (state.ScrollBound && ReferenceEquals(state.Scroll, scroll)) return;
        var layer = list == TrackList ? TrackSelectionLayer : QueueSelectionLayer;
        ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var follow = visual.Compositor.CreateExpressionAnimation("scroll.Translation.Y");
        follow.SetReferenceParameter("scroll", ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroll));
        visual.StartAnimation("Translation.Y", follow); state.Scroll = scroll; state.ScrollBound = true;
    }
    private void UpdateSelectionIndicator(ListView list, bool animate)
    {
        var marker = list == TrackList ? TrackSelectionIndicator : QueueSelectionIndicator;
        var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
        // Layout/scroll refreshes must not consume a click's pending transition.
        if (!animate && state.DispatchQueued) return;
        var item = list.SelectedItem;
        // WinUI can expose a new SelectedItem to LayoutUpdated BEFORE raising
        // SelectionChanged for pointer/touch input. Never let a non-animated
        // layout pass consume that transition, or the later event sees the
        // destination as the starting position and has nothing left to animate.
        if (!animate && item != null && state.Item != null && !ReferenceEquals(item, state.Item))
        {
            state.PendingItem = item;
            if (!state.DispatchQueued)
            {
                state.DispatchQueued = true;
                list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    state.DispatchQueued = false;
                    UpdateSelectionIndicator(list, true);
                });
            }
            return;
        }
        // A pointer selection can briefly clear SelectedItem before selecting the
        // next row. Do not discard the old anchor during that intermediate state:
        // programmatic Previous/Next typically switches directly and never hits it.
        if (item == null && !animate && state.Item != null)
        {
            if (!state.DispatchQueued)
            {
                state.DispatchQueued = true;
                list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    state.DispatchQueued = false;
                    UpdateSelectionIndicator(list, true);
                });
            }
            return;
        }
        if (_hidden || !list.IsLoaded || list.ActualHeight <= 0 || ReferenceEquals(_batchList, list) || item == null)
        {
            marker.Visibility = Visibility.Collapsed; state.Item = null; state.PendingItem = null; state.Y = double.NaN; return;
        }
        BindSelectionScroll(list, state);
        var index = list.Items.IndexOf(item);
        // Keep the previous song anchor when its container has been recycled.
        // The compositor scroll expression keeps the marker fixed to content;
        // UI scroll/layout events only manage visibility and virtualization.
        if (list.ContainerFromItem(item) is not ListViewItem row || !row.IsLoaded)
        {
            marker.Visibility = Visibility.Collapsed; state.Offscreen = true; return;
        }
        var bounds = row.TransformToVisual(list).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
        var viewportY = bounds.Y + (bounds.Height - marker.Height) / 2;
        var offset = state.Scroll?.VerticalOffset ?? 0;
        var y = viewportY + offset;
        var rows = SongRowBounds(list, offset);
        var first = Math.Max(offset, rows.Top);
        var last = Math.Max(first, Math.Min(offset + list.ActualHeight, rows.Bottom) - marker.Height);
        var clip = list == TrackList ? TrackSelectionClip : QueueSelectionClip;
        var clipTop = Math.Clamp(rows.Top - offset, 0, list.ActualHeight);
        var clipRect = new Windows.Foundation.Rect(0, clipTop, clip.ActualWidth, Math.Max(0, Math.Min(list.ActualHeight, rows.Bottom - offset) - clipTop));
        if (clip.Clip is RectangleGeometry geometry) { if (geometry.Rect != clipRect) geometry.Rect = clipRect; }
        else clip.Clip = new RectangleGeometry { Rect = clipRect };
        if (bounds.Bottom <= 0 || bounds.Y >= list.ActualHeight)
        {
            marker.Visibility = Visibility.Collapsed; state.Offscreen = true; return;
        }
        var changed = !ReferenceEquals(item, state.Item);
        if (!changed && Math.Abs(y - state.Y) < .5 && marker.Visibility == Visibility.Visible) return;
        ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
        var visual = ElementCompositionPreview.GetElementVisual(marker);
        var previous = state.Y;
        var previousIndex = state.Item == null ? -1 : list.Items.IndexOf(state.Item);
        if (previousIndex < 0 && _previousPlayingTrackId != 0)
            previousIndex = list.Items.OfType<NetEaseNEXT.Models.Track>().ToList().FindIndex(t => t.Id == _previousPlayingTrackId);
        var entering = marker.Visibility != Visibility.Visible && !ReferenceEquals(item, state.Item);
        if (entering || state.Offscreen && changed || !double.IsFinite(previous) || previous < offset || previous > offset + list.ActualHeight)
        {
            previous = previousIndex >= 0 && previousIndex > index ? last : first;
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", new(0, (float)previous, 0));
        }
        else if (previous < rows.Top || previous + marker.Height > rows.Bottom)
        {
            previous = Math.Clamp(previous, first, last);
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", new(0, (float)previous, 0));
        }
        var canAnimate = PlayerAnimationEnabled && (changed && (animate || entering || ReferenceEquals(state.PendingItem, item)));
        if (canAnimate)
        {
            FluentMotion.MoveIndicator(marker, (float)previous, new(0, (float)y, 0), (float)marker.Height);
        }
        else
        {
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", new(0, (float)y, 0));
        }
        marker.Visibility = Visibility.Visible; state.Item = item; state.PendingItem = null; state.Y = y; state.Index = index; state.Offscreen = false;
    }
}
