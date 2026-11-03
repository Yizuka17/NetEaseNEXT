using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _localRemovalBusy;
    private async Task ConfirmRemoveLocalTracksAsync(IReadOnlyList<Track> tracks)
    {
        if (_dialog != null || _localRemovalBusy || !IsLocalView) return;
        var known = _localLibrary.Tracks.Select(t => t.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = tracks.Where(t => t.IsLocal && known.Contains(t.LocalPath)).DistinctBy(t => t.LocalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) return;
        var route = _route; var playlist = _localPlaylistId; var store = _localLibrary.StoragePath;
        var delete = new CheckBox { Content = "同时删除本地音频文件", IsChecked = false, MinHeight = 44, IsEnabled = targets.Any(t => File.Exists(t.LocalPath)) };
        var body = new StackPanel { Width = 390, Spacing = 14 };
        body.Children.Add(new TextBlock { Text = targets.Length == 1 ? targets[0].Title : $"已选择 {targets.Length} 首歌曲", TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
        body.Children.Add(delete);
        body.Children.Add(new TextBlock { Text = "默认保留文件。删除文件会影响引用同一文件的其他歌单，其他文件夹里的副本保留。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = playlist.Length > 0 ? "从歌单移除？" : "从本地音乐移除？", Content = body, PrimaryButtonText = "移除", CloseButtonText = "取消" };
        delete.Checked += (_, _) => dialog.PrimaryButtonText = "移除并删除";
        delete.Unchecked += (_, _) => dialog.PrimaryButtonText = "移除";
        _dialog = dialog; _localRemovalBusy = true;
        try
        {
            if (await ShowThemedDialogAsync(dialog) != ContentDialogResult.Primary || _exiting || !ReferenceEquals(route, _route) || playlist != _localPlaylistId || store != _localLibrary.StoragePath) return;
            StopLocalMonitoring(); await _localRefreshGate.WaitAsync();
            try
            {
                // Reject a failed persistence target before touching any audio.
                _localLibrary.Save();
                if (delete.IsChecked != true) { RemoveLocalTracks(targets); return; }
                var paths = targets.Select(t => t.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _playback.ReleaseLocalFiles(paths);
                var result = await Task.Run(() =>
                {
                    var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var failed = new List<string>();
                    foreach (var path in paths)
                    {
                        try { File.Delete(path); removed.Add(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(Path.GetFileName(path)); Log.Write(ex); }
                    }
                    return (removed, failed);
                });
                if (result.removed.Count > 0)
                {
                    CommitLocal(() =>
                    {
                        _localLibrary.Tracks.RemoveAll(t => result.removed.Contains(t.LocalPath));
                        foreach (var item in _localLibrary.Playlists)
                        {
                            item.Paths.RemoveAll(result.removed.Contains);
                            foreach (var path in result.removed) item.FileStamps.Remove(path);
                        }
                    });
                    try { _downloadedLibrary.RemovePaths(result.removed); } catch (Exception ex) { Log.Write(ex); }
                    _playback.Remove(_playback.Queue.Where(t => t.IsLocal && result.removed.Contains(t.LocalPath)).ToArray());
                }
                UpdateLocalPlaylists(); LoadLocalTracks(); UpdateDownloadIndicator(); UpdateSongActions();
                if (result.failed.Count > 0) ShowError($"{result.failed.Count} 个文件未能删除，歌曲已保留：" + string.Join("、", result.failed.Take(3)));
                else ShowPlayerMessage($"已移除 {result.removed.Count} 首并删除文件");
            }
            finally { _localRefreshGate.Release(); StartLocalMonitoring(); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialog = null; _localRemovalBusy = false; }
    }
}
