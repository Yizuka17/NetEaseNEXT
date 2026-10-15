using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Foundation;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _playlistQueueLoad;
    private async Task<List<Track>> GetPlaybackContextAsync()
    {
        var context = Tracks.ToList();
        if (_view != "playlist") return context;
        var id = _playlistId; _playlistQueueLoad?.Cancel();
        using var load = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _playlistQueueLoad = load;
        try
        {
            var offset = context.Count;
            while (offset % 100 == 0 && offset > 0)
            {
                var page = await _api.PlaylistTracksAsync(id, offset, load.Token);
                load.Token.ThrowIfCancellationRequested(); context.AddRange(page); offset += page.Count;
                if (page.Count < 100) break;
            }
            load.Token.ThrowIfCancellationRequested();
            if (_view != "playlist" || _playlistId != id || _hidden) throw new OperationCanceledException();
            return context.DistinctBy(t => t.Id).ToList();
        }
        finally { if (ReferenceEquals(_playlistQueueLoad, load)) _playlistQueueLoad = null; }
    }
    private long _tapTrack, _doubleTrack;
    private long _tapStamp, _doubleStamp;
    private bool _tapWasTouch;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    private void ConnectTrackGestures(ListView list)
    {
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => _tapWasTouch = args.Pointer.PointerDeviceType == PointerDeviceType.Touch), true);
        list.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(Track_DoubleTapped), true);
    }
    private void ResetTrackGesture() { _tapTrack = _tapStamp = _doubleTrack = _doubleStamp = 0; }
    private void UpdateWindowChrome()
    {
        var player = _view == "playing";
        WindowChrome.Visibility = player || _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        TitleRow.Height = new GridLength(player || _fullScreen ? 0 : 48);
        PlayerBackButton.Visibility = player && !_fullScreen ? Visibility.Visible : Visibility.Collapsed;
        PlayerDragArea.Visibility = player && !_fullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(true, true);
        if (_fullScreen)
        {
            SetTitleBar(null);
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).ClearRegionRects(NonClientRegionKind.Caption);
        }
        else SetTitleBar(player ? PlayerDragArea : TitleBar);
    }
    private async Task ActivateTrackAsync(Track track)
    {
        var now = Environment.TickCount64;
        var threshold = _tapWasTouch ? Math.Max(650, GetDoubleClickTime()) : GetDoubleClickTime();
        var doubleTap = _tapTrack == track.Id && now - _tapStamp <= threshold;
        _tapTrack = track.Id; _tapStamp = now;
        if (doubleTap) await ReplaceTrackContextAsync(track);
        else await _playback.PlayAsync(track);
    }
    private async Task ReplaceTrackContextAsync(Track track)
    {
        var now = Environment.TickCount64;
        if (_doubleTrack == track.Id && now - _doubleStamp < 700) return;
        _doubleTrack = track.Id; _doubleStamp = now;
        try { await PlayContextImmediatelyAsync(track); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task PlayContextImmediatelyAsync(Track track)
    {
        var initial = Tracks.ToList(); var playlist = _view == "playlist" ? _playlistId : 0;
        var trackIds = _playlistTrackIds.ToArray();
        _playlistQueueLoad?.Cancel();
        await _playback.PlayAsync(track, initial);
        if (_playback.Current?.Id != track.Id || playlist == 0 || initial.Count == 0 || trackIds.Length <= initial.Count) return;
        var revision = _playback.QueueRevision;
        using var load = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _playlistQueueLoad = load;
        try
        {
            var complete = initial; var offset = complete.Count;
            while (offset < trackIds.Length)
            {
                var batch = trackIds.Skip(offset).Take(500).ToArray();
                var details = await _api.CallCachedAsync("song_detail", new { ids = string.Join(',', batch) }, load.Token); load.Token.ThrowIfCancellationRequested();
                var songs = details.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToDictionary(t => t.Id);
                complete.AddRange(batch.Where(songs.ContainsKey).Select(id => songs[id])); offset += batch.Length;
            }
            _playback.CompleteQueue(complete, revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (ReferenceEquals(_playlistQueueLoad, load)) _playlistQueueLoad = null; }
    }
    private async void Track_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element != null && (element as FrameworkElement)?.DataContext is not Track) element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        if ((element as FrameworkElement)?.DataContext is not Track track) return;
        args.Handled = true;
        if (ReferenceEquals(sender, TrackList)) await ReplaceTrackContextAsync(track);
        else await _playback.PlayAsync(track);
    }
    private void ContextNext_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is Track track) { _playback.AddNext(track); ShowPlayerMessage("已添加至下一首"); }
    }
    private void ContextRemoveQueue_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        if (_playback.Current?.Id == track.Id) { ShowPlayerMessage("正在播放的歌曲暂不能移出"); return; }
        _playback.Remove(track);
        if (_view == "queue") { Tracks.Clear(); foreach (var item in _playback.Queue) Tracks.Add(item); }
    }
    private void UpdateTrackLocator()
    {
        var match = _playback.Current == null ? null : Tracks.FirstOrDefault(t => t.Id == _playback.Current.Id);
        TrackLocateButton.Visibility = !_hidden && (_view is "playlist" or "queue") && match != null ? Visibility.Visible : Visibility.Collapsed;
        QueueLocateButton.Visibility = _playback.Current != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void LocateCurrent_Click(object sender, RoutedEventArgs args)
    {
        var match = _playback.Current == null ? null : Tracks.FirstOrDefault(t => t.Id == _playback.Current.Id);
        if (match == null) return;
        TrackList.SelectedItem = match; TrackList.ScrollIntoView(match);
    }
    private void QueueLocate_Click(object sender, RoutedEventArgs args)
    {
        var match = _playback.Current == null ? null : _playback.Queue.FirstOrDefault(t => t.Id == _playback.Current.Id);
        if (match == null) return;
        QueueList.SelectedItem = match; QueueList.ScrollIntoView(match);
    }
}
