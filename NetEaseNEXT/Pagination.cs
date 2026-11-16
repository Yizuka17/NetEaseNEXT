using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NetEaseNEXT.Controls;
using System.Text.Json;
using Microsoft.UI.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _tracksMore, _catalogMore, _socialMore, _playlistsMore, _trackPagingBlocked, _autoPageQueued, _checkingAutoPaging;
    private readonly Dictionary<ListViewBase, PageLoadingFooter> _pageFooters = [];
    private void InitializePagination()
    {
        foreach (var list in new ListViewBase[] { TrackList, CatalogGrid, SocialList, PlaylistGrid })
        {
            var footer = new PageLoadingFooter(); list.Footer = footer; _pageFooters.Add(list, footer);
            footer.Retry.Click += (_, _) => RequestNextPage(list, true);
            AttachPagingRequests(list, fromInput => RequestNextPage(list, fromInput));
        }
    }
    private void AttachPagingRequests(ListViewBase list, Action<bool> request)
    {
        ScrollViewer? bound = null;
        void Changed(object? sender, ScrollViewerViewChangedEventArgs args) => request(false);
        void Manipulated(object? sender, object args) => request(true);
        void Bind()
        {
            var scroll = FindDescendant<ScrollViewer>(list);
            if (ReferenceEquals(bound, scroll)) return;
            if (bound != null) { bound.ViewChanged -= Changed; bound.DirectManipulationCompleted -= Manipulated; }
            bound = scroll;
            if (bound != null) { bound.ViewChanged += Changed; bound.DirectManipulationCompleted += Manipulated; }
        }
        list.Loaded += (_, _) => { Bind(); list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (list.IsLoaded) Bind(); }); };
        list.SizeChanged += (_, _) => Bind();
        list.Unloaded += (_, _) => { if (bound != null) { bound.ViewChanged -= Changed; bound.DirectManipulationCompleted -= Manipulated; } bound = null; };
        list.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            if (args.GetCurrentPoint(list).Properties.MouseWheelDelta < 0) request(true);
        }), true);
        uint pointer = 0; double start = 0; var swiped = false;
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
            pointer = args.Pointer.PointerId; start = args.GetCurrentPoint(list).Position.Y; swiped = false;
        }), true);
        list.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (!swiped && args.Pointer.PointerId == pointer && args.Pointer.IsInContact && start - args.GetCurrentPoint(list).Position.Y > 36)
            { swiped = true; request(true); }
        }), true);
        list.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End) request(true);
        }), true);
    }
    private ListViewBase? ActivePageList() => TracksPanel.Visibility == Visibility.Visible ? TrackList : CatalogPanel.Visibility == Visibility.Visible ? CatalogGrid : SocialPanel.Visibility == Visibility.Visible ? SocialList : BrowsePanel.Visibility == Visibility.Visible ? PlaylistGrid : null;
    private static bool HasNextPage(JsonElement result, int count, int pageSize)
    {
        if (count == 0) return false;
        foreach (var key in new[] { "hasMore", "more" })
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return count == pageSize;
    }
    private bool HasMoreFor(ListViewBase list) => ReferenceEquals(list, TrackList) ? _tracksMore : ReferenceEquals(list, CatalogGrid) ? _catalogMore : ReferenceEquals(list, PlaylistGrid) ? _playlistsMore : _socialMore;
    private void ResetPageFooters() { foreach (var footer in _pageFooters.Values) footer.Clear(); }
    private void RequestNextPage(ListViewBase list, bool fromInput = false)
    {
        if (_hidden || _exiting || !ReferenceEquals(list, ActivePageList()) || _pageLoad != null || _trackLocateLoad != null || _trackPagingBlocked || !HasMoreFor(list) || Notice.IsOpen) return;
        if (_pageFooters[list].Failed && !fromInput) return;
        if (FindDescendant<ScrollViewer>(list) is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset < 180)
        {
            if (fromInput) _pageFooters[list].Clear();
            QueueAutoPage();
        }
    }
    private void QueueAutoPage()
    {
        if (_autoPageQueued || _runningSmokeTest && !_checkingAutoPaging) return;
        _autoPageQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            _autoPageQueued = false;
            if (_runningSmokeTest && !_checkingAutoPaging) return;
            if (_hidden || _exiting || _pageLoad != null || _trackLocateLoad != null || _trackPagingBlocked || Notice.IsOpen) return;
            var list = ActivePageList();
            if (list == null || !HasMoreFor(list) || _pageFooters[list].Failed) return;
            var scroll = FindDescendant<ScrollViewer>(list);
            if (scroll != null && scroll.ScrollableHeight - scroll.VerticalOffset < 180) await LoadPageAsync(true);
        });
    }
}
