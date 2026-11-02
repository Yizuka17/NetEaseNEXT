using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _tracksMore, _catalogMore, _socialMore, _trackPagingBlocked, _autoPageQueued, _checkingAutoPaging;
    private readonly HashSet<ScrollViewer> _pagingScrolls = [];
    private void InitializePagination()
    {
        foreach (var list in new ListViewBase[] { TrackList, CatalogGrid, SocialList })
        {
            list.Loaded += (_, _) =>
            {
                if (FindDescendant<ScrollViewer>(list) is { } scroll && _pagingScrolls.Add(scroll)) scroll.ViewChanged += (_, _) => RequestNextPage(list);
            };
            list.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
            {
                if (args.GetCurrentPoint(list).Properties.MouseWheelDelta < 0) RequestNextPage(list);
            }), true);
        }
    }
    private bool HasMoreFor(ListViewBase list) => ReferenceEquals(list, TrackList) ? _tracksMore : ReferenceEquals(list, CatalogGrid) ? _catalogMore : _socialMore;
    private void RequestNextPage(ListViewBase list)
    {
        if (_hidden || _exiting || list.Visibility != Visibility.Visible || _pageLoad != null || _trackLocateLoad != null || _trackPagingBlocked || !HasMoreFor(list) || Notice.IsOpen) return;
        if (FindDescendant<ScrollViewer>(list) is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset < 180) QueueAutoPage();
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
            var list = TracksPanel.Visibility == Visibility.Visible ? (ListViewBase)TrackList : CatalogPanel.Visibility == Visibility.Visible ? CatalogGrid : SocialPanel.Visibility == Visibility.Visible ? SocialList : null;
            if (list == null || !HasMoreFor(list)) return;
            var smallPlaylist = _view == "playlist" && _playlistTrackCount is > 0 and <= 500;
            var scroll = FindDescendant<ScrollViewer>(list);
            if (smallPlaylist || scroll != null && scroll.ScrollableHeight - scroll.VerticalOffset < 180) await LoadPageAsync(true);
        });
    }
}
