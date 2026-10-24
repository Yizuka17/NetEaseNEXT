using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private ListView? _batchList;
    private bool _batchBusy;
    private int _batchGeneration;
    private readonly Dictionary<ListView, (TextBlock Count, Button Remove)> _batchBars = [];
    private void InitializeTrackBatch()
    {
        foreach (var (list, host) in new[] { (TrackList, TrackBatchHost), (QueueList, QueueBatchHost) })
        {
            var bar = new Controls.WrapPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 8) };
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 72 };
            bar.Children.Add(count);
            Button Add(string text, string action)
            {
                var button = new Button { Content = text, Tag = action, MinHeight = 44 };
                button.Click += BatchAction_Click; bar.Children.Add(button); return button;
            }
            Add("全选", "all"); Add("清空选择", "clear"); Add("下一首播放", "next");
            Add("收藏到歌单", "favorite"); Add("下载", "download");
            var remove = Add("移除", "remove"); Add("完成", "done");
            _batchBars.Add(list, (count, remove)); host.Content = bar;
            list.SelectionChanged += (_, _) => UpdateBatchBar();
        }
    }
    private void ContextBatch_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ListView list || (sender as FrameworkElement)?.DataContext is not Track track) return;
        BeginTrackBatch(list, track);
    }
    private void QueueContext_Opening(object sender, object args)
    {
        if (sender is MenuFlyout menu) PrepareBatchMenu(menu, QueueList);
    }
    private void PrepareBatchMenu(MenuFlyout menu, ListView list)
    {
        var batch = menu.Items.OfType<MenuFlyoutItem>().First(i => i.Text == "多选");
        batch.Tag = list; batch.IsEnabled = !_batchBusy;
        // Once selecting, keep the row menu focused on the batch entry point.
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>().Where(i => !ReferenceEquals(i, batch)))
            item.IsEnabled = !ReferenceEquals(_batchList, list) && !_batchBusy;
    }
    private void BeginTrackBatch(ListView list, Track track)
    {
        if (_batchBusy || !list.Items.Contains(track)) return;
        if (!ReferenceEquals(_batchList, list))
        {
            EndTrackBatch(); _batchList = list; _batchGeneration++;
            list.SelectedItem = null; list.SelectionMode = ListViewSelectionMode.Multiple; list.SelectedItems.Clear(); list.IsItemClickEnabled = false;
        }
        if (!list.SelectedItems.Contains(track)) list.SelectedItems.Add(track);
        ResetTrackGesture(); UpdateBatchBar();
    }
    private void EndTrackBatch()
    {
        var list = _batchList; _batchList = null; _batchGeneration++;
        if (list != null) { list.SelectedItem = null; list.SelectionMode = ListViewSelectionMode.Single; list.IsItemClickEnabled = true; list.IsEnabled = true; }
        TrackBatchHost.Visibility = QueueBatchHost.Visibility = Visibility.Collapsed;
    }
    private Track[] SelectedBatchTracks()
    {
        if (_batchList == null) return [];
        var selected = _batchList.SelectedItems.OfType<Track>().Select(t => t.Id).ToHashSet();
        return _batchList.Items.OfType<Track>().Where(t => selected.Contains(t.Id)).DistinctBy(t => t.Id).ToArray();
    }
    private bool CanRemoveBatch => ReferenceEquals(_batchList, QueueList) || _view == "queue" || OwnsDisplayedPlaylist && _api.HasSession;
    private void UpdateBatchBar()
    {
        TrackBatchHost.Visibility = ReferenceEquals(_batchList, TrackList) ? Visibility.Visible : Visibility.Collapsed;
        QueueBatchHost.Visibility = ReferenceEquals(_batchList, QueueList) ? Visibility.Visible : Visibility.Collapsed;
        if (_batchList == null) return;
        var (count, remove) = _batchBars[_batchList];
        var selected = SelectedBatchTracks(); count.Text = $"已选 {selected.Length} 首";
        remove.Visibility = CanRemoveBatch ? Visibility.Visible : Visibility.Collapsed;
        remove.Content = ReferenceEquals(_batchList, QueueList) || _view == "queue" ? "移出播放列表" : "从歌单移除";
        var host = ReferenceEquals(_batchList, QueueList) ? QueueBatchHost : TrackBatchHost;
        if (host.Content is Panel panel)
            foreach (var button in panel.Children.OfType<Button>())
            {
                button.IsEnabled = !_batchBusy && (button.Tag is "all" or "clear" or "done" || selected.Length > 0);
                if (button.Tag is "all") button.Content = ReferenceEquals(_batchList, TrackList) && _view == "playlist" && Tracks.Count < _playlistTrackCount ? "全选已加载" : "全选";
                if (button.Tag is "remove" && !ReferenceEquals(_batchList, QueueList) && _view == "playlist" && Busy.IsActive) button.IsEnabled = false;
            }
        _batchList.IsEnabled = !_batchBusy;
    }
    private async void BatchAction_Click(object sender, RoutedEventArgs args)
    {
        if (_batchList == null || _batchBusy || sender is not Button { Tag: string action }) return;
        var list = _batchList;
        if (action == "done") { EndTrackBatch(); return; }
        if (action == "all") { list.SelectAll(); return; }
        if (action == "clear") { list.SelectedItems.Clear(); return; }
        var tracks = SelectedBatchTracks(); if (tracks.Length == 0) return;
        var generation = _batchGeneration;
        var fromQueue = ReferenceEquals(list, QueueList) || _view == "queue";
        var target = new PlaylistTrackRemoval(_playlistId, tracks[0].Id, _api.Cookie);
        _batchBusy = true; UpdateBatchBar();
        try
        {
            switch (action)
            {
                case "next": _playback.AddNext(tracks); ShowPlayerMessage("已添加至下一首"); break;
                case "favorite": await FavoriteTracksAsync(tracks); break;
                case "download":
                    foreach (var track in tracks) { if (_exiting || _downloads.IsCancellationRequested) break; await DownloadAsync(track); }
                    break;
                case "remove" when fromQueue:
                    var removed = _playback.Remove(tracks);
                    ShowPlayerMessage(removed < tracks.Length ? $"已移除 {removed} 首，保留正在播放的歌曲" : $"已移除 {removed} 首");
                    break;
                case "remove" when CanRemoveBatch:
                    await RemovePlaylistTracksAsync(_api, target, tracks.Select(t => t.Id).ToArray()); break;
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _batchBusy = false; if (generation == _batchGeneration) UpdateBatchBar(); }
    }
    private void RefreshQueueList()
    {
        if (ReferenceEquals(QueueList.ItemsSource, _playback.Queue) && ReferenceEquals(_batchList, QueueList)) return;
        var selected = QueueList.SelectedItems.OfType<Track>().Select(t => t.Id).ToHashSet();
        if (!ReferenceEquals(QueueList.ItemsSource, _playback.Queue)) QueueList.ItemsSource = _playback.Queue;
        if (ReferenceEquals(_batchList, QueueList))
        {
            QueueList.SelectedItems.Clear();
            foreach (var track in _playback.Queue.Where(t => selected.Contains(t.Id))) QueueList.SelectedItems.Add(track);
        }
        else QueueList.SelectedItem = _playback.Queue.FirstOrDefault(t => t.Id == _playback.Current?.Id);
    }
    private void RefreshQueueTracks()
    {
        var selected = TrackList.SelectedItems.OfType<Track>().Select(t => t.Id).ToHashSet();
        Tracks.Clear(); foreach (var track in _playback.Queue) Tracks.Add(track);
        if (ReferenceEquals(_batchList, TrackList))
            foreach (var track in Tracks.Where(t => selected.Contains(t.Id))) TrackList.SelectedItems.Add(track);
        TrackCount.Text = $"{Tracks.Count} 首";
    }
}
