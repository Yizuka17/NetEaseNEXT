using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _eventsRefreshing, _socialRefreshInputHooked;
    private void ConfigureSocialRefresh()
    {
        if (SocialRefresh.Visualizer is { } visualizer) visualizer.Visibility = _view == "events" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SocialRefresh_Loaded(object sender, RoutedEventArgs args)
    {
        ConfigureSocialRefresh();
        if (_socialRefreshInputHooked) return;
        _socialRefreshInputHooked = true;
        SocialList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(SocialList).Properties.MouseWheelDelta > 0 && FindDescendant<ScrollViewer>(SocialList) is { VerticalOffset: <= .5 } && RequestEventsRefresh()) e.Handled = true;
        }), true);
        SocialList.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.F5 && RequestEventsRefresh()) e.Handled = true; };
    }
    private bool RequestEventsRefresh()
    {
        if (_view != "events" || _eventsRefreshing || _pageLoad != null || _hidden || _exiting) return false;
        SocialRefresh.RequestRefresh(); return true;
    }
    private async void SocialRefresh_Requested(RefreshContainer sender, RefreshRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        if (_view != "events" || _eventsRefreshing || _pageLoad != null || _hidden || _exiting) return;
        var route = _route; var scope = _myEvents;
        var offset = _offset; var cursor = _socialCursor; var more = _socialMore;
        using var load = new CancellationTokenSource(); _pageLoad = load; _eventsRefreshing = true;
        Notice.IsOpen = false; _offset = 0; _socialCursor = -1;
        try
        {
            await LoadSocialAsync(load.Token, replace: true);
            if (ReferenceEquals(route, _route) && scope == _myEvents)
            {
                EmptyPanel.Visibility = Visibility.Collapsed;
                if (SocialItems.Count == 0) ShowEmpty("暂无内容", "重试");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_pageLoad, load) && ReferenceEquals(route, _route) && scope == _myEvents)
            { _offset = offset; _socialCursor = cursor; _socialMore = more; }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pageLoad, load) && ReferenceEquals(route, _route) && scope == _myEvents)
            { _offset = offset; _socialCursor = cursor; _socialMore = more; ShowError(ex.Message); }
        }
        finally
        {
            if (ReferenceEquals(_pageLoad, load)) _pageLoad = null;
            _eventsRefreshing = false;
        }
    }
}
