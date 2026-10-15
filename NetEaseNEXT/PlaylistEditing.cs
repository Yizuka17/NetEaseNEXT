using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (_dialog != null || _view != "playlist" || _accountId <= 0 || _playlistOwner != _accountId) return;
        var id = _playlistId;
        var error = new TextBlock { Text = "删除后无法恢复。", TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除歌单", Content = error, PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        var deleted = false;
        dialog.PrimaryButtonClick += async (_, eventArgs) =>
        {
            var deferral = eventArgs.GetDeferral();
            try { await _api.CallAsync("playlist_delete", new { id }); deleted = true; }
            catch (Exception ex) { error.Text = ex.Message; eventArgs.Cancel = true; }
            finally { deferral.Complete(); }
        };
        _dialog = dialog; try { await dialog.ShowAsync(); } finally { _dialog = null; }
        if (deleted) { _history.Clear(); await NavigateAsync("library", false); }
    }
    private async void EditPlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (_dialog != null || _view != "playlist" || _accountId <= 0 || _playlistOwner != _accountId) return;
        var id = _playlistId;
        var items = new ObservableCollection<Track>(); var original = new List<long>();
        var list = new ListView { ItemsSource = items, ItemTemplate = (DataTemplate)Root.Resources["TrackEditorTemplate"], SelectionMode = ListViewSelectionMode.Multiple, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = Math.Clamp(Root.ActualHeight - 340, 180, 480), CanDragItems = true, CanReorderItems = true, AllowDrop = true, IsItemClickEnabled = false };
        AttachWheelFallback(list);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var remove = new Button { Content = "移除选中" }; var up = new Button { Content = "上移" }; var down = new Button { Content = "下移" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(remove); actions.Children.Add(up); actions.Children.Add(down);
        var panel = new StackPanel { Width = Math.Max(240, Math.Min(500, Root.ActualWidth - 160)), Spacing = 12 }; panel.Children.Add(list); panel.Children.Add(actions); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "编辑曲目", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        using var load = new CancellationTokenSource(); _playlistEditLoad = load;
        remove.Click += (_, _) => { foreach (var track in list.SelectedItems.OfType<Track>().ToArray()) items.Remove(track); };
        void Move(int delta)
        {
            if (list.SelectedItems.Count != 1 || list.SelectedItem is not Track track) return;
            var from = items.IndexOf(track); var to = from + delta;
            if (to >= 0 && to < items.Count) { items.Move(from, to); list.SelectedItem = track; list.ScrollIntoView(track); }
        }
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        var saved = false;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                error.Text = "正在加载…";
                var offset = 0;
                while (true)
                {
                    var page = await _api.PlaylistTracksAsync(id, offset, load.Token); load.Token.ThrowIfCancellationRequested();
                    foreach (var track in page) { if (!original.Contains(track.Id)) { items.Add(track); original.Add(track.Id); } }
                    offset += page.Count; if (page.Count < 100) break;
                }
                error.Text = ""; dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        dialog.PrimaryButtonClick += async (_, eventArgs) =>
        {
            var deferral = eventArgs.GetDeferral();
            try
            {
                var kept = items.Select(t => t.Id).ToArray(); var removed = original.Except(kept).ToArray();
                if (removed.Length > 0) { await _api.CallAsync("playlist_tracks", new { op = "del", pid = id, tracks = string.Join(",", removed) }, load.Token); original.RemoveAll(n => removed.Contains(n)); }
                if (kept.Length > 0 && !kept.SequenceEqual(original)) await _api.CallAsync("song_order_update", new { pid = id, ids = JsonSerializer.Serialize(kept) }, load.Token);
                saved = true;
            }
            catch (OperationCanceledException) { eventArgs.Cancel = true; }
            catch (Exception ex) { error.Text = ex.Message; eventArgs.Cancel = true; }
            finally { deferral.Complete(); }
        };
        dialog.Closed += (_, _) => load.Cancel(); _dialog = dialog;
        try { await dialog.ShowAsync(); } finally { load.Cancel(); _playlistEditLoad = null; _dialog = null; }
        if (saved && _view == "playlist" && _playlistId == id) { Tracks.Clear(); _offset = 0; await LoadPageAsync(false); }
    }
    private CancellationTokenSource? _playlistEditLoad;
}
