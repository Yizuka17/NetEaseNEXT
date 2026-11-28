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
        public bool PlaybackSelection;
        public object? PlaybackItem;
    }
    private readonly SelectionMotionState _trackSelectionMotion = new(), _queueSelectionMotion = new(), _sidebarSelectionMotion = new();
    private readonly HashSet<ScrollViewer> _selectionScrollers = [];
    private readonly HashSet<ScrollViewer> _sidebarSelectionScrollers = [];
    private void InitializeSelectionMotion()
    {
        Nav.SelectionChanged += (_, _) => UpdateSidebarSelection(true);
        Nav.LayoutUpdated += (_, _) => UpdateSidebarSelection(false);
        Nav.Unloaded += (_, _) => UpdateSidebarSelection(false);
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
            list.SelectionChanged += (_, _) =>
            {
                var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
                if (!state.SilentSelection) { state.PlaybackSelection = false; state.PlaybackItem = null; }
                // Pointer/touch selection can run before ItemClick changes the playing track.
                // Keep the previous visual position throughout this input event and commit
                // the selection after the event has finished, rather than letting an
                // intervening LayoutUpdated snap the marker to its destination.
                state.PendingItem = state.PlaybackSelection ? state.PlaybackItem : list.SelectedItem;
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
            list.Unloaded += (_, _) => UpdateSelectionIndicator(list, false);
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
        state.SilentSelection = false;
        state.PlaybackSelection = false; state.PlaybackItem = null;
    }
    private void SelectPlayingTrack(ListView list, object? item)
    {
        var state = list == TrackList ? _trackSelectionMotion : _queueSelectionMotion;
        state.PlaybackSelection = true; state.PlaybackItem = item;
        // Playback only moves our marker. Native selection can implicitly reveal
        // a row, so it belongs exclusively to user selection and explicit locate.
        state.SilentSelection = true;
        try { list.SelectedItem = null; }
        finally { state.SilentSelection = false; }
        UpdateSelectionIndicator(list, true);
    }
    private static (double Top, double Bottom) SongRowBounds(ListView list, double offset)
    {
        if (list.ItemsPanelRoot is not { } panel) return (offset, offset + list.ActualHeight);
        var scroll = FindDescendant<ScrollViewer>(list);
        if (scroll?.Content is not FrameworkElement content) return (offset, offset + list.ActualHeight);
        var bounds = panel.TransformToVisual(content).TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight));
        return (bounds.Y, bounds.Bottom);
    }
    private void StopSelectionMotion()
    {
        foreach (var marker in new[] { TrackSelectionIndicator, QueueSelectionIndicator, SidebarSelectionIndicator })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
            var visual = ElementCompositionPreview.GetElementVisual(marker);
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
            var state = marker == TrackSelectionIndicator ? _trackSelectionMotion : marker == QueueSelectionIndicator ? _queueSelectionMotion : _sidebarSelectionMotion;
            var layer = marker == TrackSelectionIndicator ? TrackSelectionLayer : marker == QueueSelectionIndicator ? QueueSelectionLayer : SidebarSelectionLayer;
            StopIndicatorScroll(layer, state);
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
        { marker.Visibility = Visibility.Collapsed; state.Item = null; state.Y = double.NaN; StopIndicatorScroll(SidebarSelectionLayer, state); return; }
        // Locally loaded pages can complete navigation and force layout inside
        // the earlier SelectionChanged handler, before ours starts the motion.
        // Preserve the old anchor until the animated selection pass consumes it.
        if (!animate && state.Item != null && !ReferenceEquals(item, state.Item))
        {
            if (!state.DispatchQueued)
            {
                state.DispatchQueued = true;
                Nav.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    state.DispatchQueued = false;
                    UpdateSidebarSelection(true);
                });
            }
            return;
        }
        ScrollViewer? owner = null;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(item); parent != null && parent != Nav; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                owner = scroll; break;
            }
        var changedOwner = !ReferenceEquals(owner, state.Scroll);
        var previous = state.Y;
        if (changedOwner && double.IsFinite(previous))
            previous += (owner?.VerticalOffset ?? 0) - (state.Scroll?.VerticalOffset ?? 0);
        Windows.Foundation.Rect bounds;
        if (owner?.Content is FrameworkElement content && FindDescendant<ScrollContentPresenter>(owner) is { } viewport)
        {
            BindIndicatorScroll(SidebarSelectionLayer, owner, state);
            if (_sidebarSelectionScrollers.Add(owner)) owner.ViewChanged += (_, _) => UpdateSidebarSelection(false);
            // Keep the anchor in unscrolled content space. The outer viewport
            // stays fixed while the compositor moves both content and marker.
            bounds = item.TransformToVisual(content).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
            var origin = viewport.TransformToVisual(SidebarSelectionClip).TransformPoint(new());
            var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(content);
            bounds.X += origin.X + slot.X; bounds.Y += origin.Y + slot.Y;
            SetIndicatorClip(SidebarSelectionClip, viewport.TransformToVisual(SidebarSelectionClip).TransformBounds(new(0, 0, viewport.ActualWidth, viewport.ActualHeight)));
        }
        else
        {
            StopIndicatorScroll(SidebarSelectionLayer, state); state.Scroll = null;
            bounds = item.TransformToVisual(Nav).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
            SetIndicatorClip(SidebarSelectionClip, new(0, 0, Nav.ActualWidth, Nav.ActualHeight));
        }
        var x = bounds.X + 2; var y = bounds.Y + (bounds.Height - marker.Height) / 2;
        if (ReferenceEquals(item, state.Item) && !changedOwner && Math.Abs(x - state.X) < .1 && Math.Abs(y - state.Y) < .1 && marker.Visibility == Visibility.Visible) return;
        ElementCompositionPreview.SetIsTranslationEnabled(marker, true);
        var visual = ElementCompositionPreview.GetElementVisual(marker);
        if (animate && state.Item != null && double.IsFinite(state.Y) && PlayerAnimationEnabled)
        {
            if (changedOwner)
            {
                visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
                visual.Properties.InsertVector3("Translation", new((float)state.X, (float)previous, 0));
            }
            FluentMotion.MoveIndicator(marker, (float)previous, new((float)x, (float)y, 0), (float)marker.Height);
        }
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
        var layer = list == TrackList ? TrackSelectionLayer : QueueSelectionLayer;
        BindIndicatorScroll(layer, scroll, state);
    }
    private static void SetIndicatorClip(Grid element, Windows.Foundation.Rect rect)
    {
        if (element.Clip is RectangleGeometry geometry) { if (geometry.Rect != rect) geometry.Rect = rect; }
        else element.Clip = new RectangleGeometry { Rect = rect };
    }
    private static void StopIndicatorScroll(Grid layer, SelectionMotionState state)
    {
        if (!state.ScrollBound) return;
        ElementCompositionPreview.SetIsTranslationEnabled(layer, true);
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        visual.StopAnimation("Translation.Y"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        state.ScrollBound = false;
    }
    private static void BindIndicatorScroll(Grid layer, ScrollViewer scroll, SelectionMotionState state)
    {
        if (state.ScrollBound && ReferenceEquals(state.Scroll, scroll)) return;
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
        var item = state.PlaybackSelection ? state.PlaybackItem : list.SelectedItem;
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
        if (_hidden || !list.IsLoaded || list.ActualHeight <= 0 || (list == TrackList ? TracksPanel.Visibility != Visibility.Visible : QueuePane.Visibility != Visibility.Visible) || ReferenceEquals(_batchList, list) || item == null)
        {
            marker.Visibility = Visibility.Collapsed; state.Item = null; state.PendingItem = null; state.Y = double.NaN;
            StopIndicatorScroll(list == TrackList ? TrackSelectionLayer : QueueSelectionLayer, state); return;
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
        if (state.Scroll?.Content is not FrameworkElement content) return;
        // TransformToVisual(list) can already contain a compositor scroll transform
        // from a different frame than VerticalOffset. Keep the anchor entirely in
        // content coordinates; only the live scroll expression moves its layer.
        var bounds = row.TransformToVisual(content).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
        var offset = state.Scroll?.VerticalOffset ?? 0;
        var y = bounds.Y + (bounds.Height - marker.Height) / 2;
        var rows = SongRowBounds(list, offset);
        var first = Math.Max(offset, rows.Top);
        var last = Math.Max(first, Math.Min(offset + list.ActualHeight, rows.Bottom) - marker.Height);
        var clip = list == TrackList ? TrackSelectionClip : QueueSelectionClip;
        if (FindDescendant<ScrollContentPresenter>(state.Scroll!) is { } viewport)
            SetIndicatorClip(clip, viewport.TransformToVisual(clip).TransformBounds(new(0, 0, viewport.ActualWidth, viewport.ActualHeight)));
        var layer = list == TrackList ? TrackSelectionLayer : QueueSelectionLayer;
        // The song section clip moves with the content too. Updating a fixed
        // clip with VerticalOffset lags compositor-driven touch/inertia frames.
        SetIndicatorClip(layer, new(0, rows.Top, layer.ActualWidth, Math.Max(0, rows.Bottom - rows.Top)));
        var offscreen = bounds.Bottom <= offset || bounds.Y >= offset + list.ActualHeight;
        var changed = !ReferenceEquals(item, state.Item);
        if (!changed && Math.Abs(y - state.Y) < .5 && marker.Visibility == Visibility.Visible)
        {
            state.Offscreen = offscreen; return;
        }
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
        marker.Visibility = Visibility.Visible; state.Item = item; state.PendingItem = null; state.Y = y; state.Index = index; state.Offscreen = offscreen;
    }
}
