using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLocalActionChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-local-actions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldCatalog = _downloadedLibrary; var oldPlaybackCatalog = _playback.Downloads;
        _checkingLibraryWorkflow = true; _connectionOverride = false; _localLibraryPathOverride = Path.Combine(fixture, "library.bin");
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        _downloadedLibrary = new(Path.Combine(fixture, "catalog.json")); _playback.Downloads = _downloadedLibrary;
        using var api = new ApiClient((method, p, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { count = 0 }, ids = Array.Empty<long>() })), Path.Combine(fixture, "metadata")); SongInfoApiOverride = api;
        RestoreWindow(); _sessionReady = true; _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); _playback.SetVolume(0);
        var scale = Root.XamlRoot.RasterizationScale; AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(900 * scale)));
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) { await Task.Delay(35); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Local actions check did not settle");
        }
        async Task Primary(Task action, bool delete)
        {
            await Wait(() => _dialog is { IsLoaded: true });
            Descendants<CheckBox>(_dialog!).Single().IsChecked = delete;
            Invoke(Descendants<Button>(_dialog!).First(b => b.Content as string == _dialog!.PrimaryButtonText)); await action;
        }
        bool Visible(Track track)
        {
            Root.UpdateLayout(); if (TrackList.ContainerFromItem(track) is not FrameworkElement row) return false;
            var y = row.TransformToVisual(TrackList).TransformPoint(new Windows.Foundation.Point()).Y;
            return y >= -1 && y + row.ActualHeight <= TrackList.ActualHeight + 1;
        }
        void Wave(string path, int seconds = 1)
        {
            using var writer = new BinaryWriter(File.Create(path)); var length = 44100 * 2 * seconds;
            writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
        }
        Track Tagged(string path, long id)
        {
            Wave(path, 8); AudioTags.Write(path, "本地下载歌曲", "晚风电台", "回家的路", "", null, id, "lossless");
            _downloadedLibrary.Register(new() { Track = new(id, "本地下载歌曲", "晚风电台", "回家的路", "", 8000), FilePath = path, ActualLevel = "lossless" });
            return LocalMusicFiles.Read(path, _downloadedLibrary);
        }
        try
        {
            await NavigateAsync("settings");
            check("Settings reads the current application version instead of the old 0.2 label", AppVersionLabel.Text == "NetEaseNEXT " + typeof(App).Assembly.GetName().Version!.ToString(4) && !AppVersionLabel.Text.StartsWith("NetEaseNEXT 0.2"), null);
            await SaveLibraryImageAsync(Root, "local-actions-version.png");
            var tracks = new List<Track>();
            for (var i = 0; i < 150; i++) { var path = Path.Combine(fixture, $"song-{i:D3}.wav"); Wave(path); tracks.Add(new(-i - 1, $"本地歌曲 {i + 1:D3}", "晚风电台", "回家的路", "", 1000) { LocalPath = path }); }
            var current = Tagged(Path.Combine(fixture, "正在播放.wav"), 9001); tracks.Add(current);
            var folderA = Path.Combine(fixture, "歌单甲"); var folderB = Path.Combine(fixture, "歌单乙"); Directory.CreateDirectory(folderA); Directory.CreateDirectory(folderB);
            var keep = Tagged(Path.Combine(folderA, "保留文件.wav"), 9100); var deleteA = Tagged(Path.Combine(folderA, "同曲.wav"), 9001); var copyB = Tagged(Path.Combine(folderB, "同曲.wav"), 9001);
            tracks.AddRange([keep, deleteA, copyB]);
            var manual = new LocalPlaylist { Name = "本地收藏", Paths = tracks.Take(151).Select(t => t.LocalPath).ToList() };
            var shared = new LocalPlaylist { Name = "其他收藏", Paths = [current.LocalPath, deleteA.LocalPath, copyB.LocalPath] };
            var bound = new LocalPlaylist { Name = "文件夹收藏", FolderPath = folderA };
            _localLibrary = new() { StoragePath = _localLibraryPathOverride, Tracks = tracks, Playlists = [manual, shared, bound] }; _localLibrary.Save();
            _localPlaylistId = manual.Id; await NavigateAsync("local-playlist");
            _restoringPlayback = true; await _playback.PlayAsync(current, [current]); _restoringPlayback = false;
            if (_playback.IsPlaying) _playback.Toggle();
            TrackList.ScrollIntoView(Tracks[0]); await Task.Delay(45); UpdateTrackLocator();
            check("Local playlist shows the current-song locator", TrackLocateButton.Visibility == Visibility.Visible && TrackLocateButton.IsEnabled, null);
            var before = api.RequestCount; await LocateCurrentAsync(api); var shown = Tracks.Single(t => t.LocalPath == current.LocalPath);
            check("Local locator scrolls the actual row into view without a song-detail request", TrackList.SelectedItem == shown && Visible(shown) && api.RequestCount == before, null);
            BeginTrackBatch(TrackList, Tracks[0]); TrackList.SelectedItems.Add(Tracks[1]); var selected = SelectedBatchTracks().Select(t => t.Id).ToArray();
            await LocateCurrentAsync(api);
            check("Local locating preserves multiselection", SelectedBatchTracks().Select(t => t.Id).SequenceEqual(selected) && Visible(shown), null); EndTrackBatch();
            await SaveLibraryImageAsync(Root, "local-actions-locator.png");
            var cancelTarget = Tracks[0]; var cancel = ConfirmRemoveLocalTracksAsync([cancelTarget]); await Wait(() => _dialog is { IsLoaded: true });
            check("Removal offers explicit file deletion with preservation selected by default", Descendants<CheckBox>(_dialog!).Single().IsChecked == false && _dialog!.PrimaryButtonText == "移除" && _dialog.DefaultButton == ContentDialogButton.None && Descendants<Button>(_dialog).Where(b => b.Content as string is "移除" or "取消").All(b => b.ActualHeight >= 44), null);
            _dialog!.Hide(); await cancel;
            check("Cancelling removal changes neither the playlist nor the audio", _localLibrary.Playlists.Single(p => p.Id == manual.Id).Paths.Contains(cancelTarget.LocalPath) && File.Exists(cancelTarget.LocalPath), null);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(current.LocalPath));
            await Primary(ConfirmRemoveLocalTracksAsync([shown]), false);
            check("Default removal keeps the file, download catalog and other playlist references", File.Exists(current.LocalPath) && SHA256.HashData(await File.ReadAllBytesAsync(current.LocalPath)).SequenceEqual(hash) && !_localLibrary.Playlists.Single(p => p.Id == manual.Id).Paths.Contains(current.LocalPath) && _localLibrary.Playlists.Single(p => p.Id == shared.Id).Paths.Contains(current.LocalPath) && _downloadedLibrary.FindPath(current.LocalPath) != null, null);
            _localPlaylistId = bound.Id; await NavigateAsync("local-playlist");
            check("Folder playlists also provide batch removal", CanRemoveBatch, null);
            await Primary(ConfirmRemoveLocalTracksAsync([Tracks.Single(t => t.LocalPath == keep.LocalPath)]), false);
            await RefreshLocalFoldersAsync(CancellationToken.None);
            var persisted = LocalLibraryStore.Load(_localLibrary.StoragePath).Playlists.Single(p => p.Id == bound.Id);
            check("A retained file does not reappear after folder refresh or library reload", File.Exists(keep.LocalPath) && !Tracks.Any(t => t.LocalPath == keep.LocalPath) && persisted.ExcludedPaths.Contains(keep.LocalPath) && !persisted.Paths.Contains(keep.LocalPath), null);
            var old = Tracks.Single(t => t.LocalPath == deleteA.LocalPath);
            _restoringPlayback = true; await _playback.PlayAsync(old, [old]); _restoringPlayback = false;
            var deletion = ConfirmRemoveLocalTracksAsync([old]); await Wait(() => _dialog is { IsLoaded: true }); Descendants<CheckBox>(_dialog!).Single().IsChecked = true;
            check("Selecting file deletion makes the confirmation action explicit", _dialog!.PrimaryButtonText == "移除并删除", null);
            await SaveLibraryImageAsync(_dialog!, "local-actions-remove-file.png");
            Invoke(Descendants<Button>(_dialog!).First(b => b.Content as string == "移除并删除")); await deletion;
            check("Deleting a playing local file releases playback and removes shared references", !File.Exists(deleteA.LocalPath) && _playback.Current == null && !_playback.IsPlaying && _localLibrary.Playlists.All(p => !p.Paths.Contains(deleteA.LocalPath)) && !_localLibrary.Tracks.Any(t => t.LocalPath == deleteA.LocalPath), null);
            check("Deletion affects only the selected path and preserves same-song copies in other folders", File.Exists(copyB.LocalPath) && File.Exists(current.LocalPath) && _downloadedLibrary.FindPath(copyB.LocalPath) != null && _downloadedLibrary.FindPath(deleteA.LocalPath) == null, null);
            await RefreshLocalFoldersAsync(CancellationToken.None);
            check("Removed folder rows and their locator stay cleared after synchronization", Tracks.Count == 0 && TrackLocateButton.Visibility == Visibility.Collapsed, null);
            _localPlaylistId = shared.Id; await NavigateAsync("local-playlist");
            var online = new Track(9001, "本地下载歌曲", "晚风电台", "回家的路", "", 8000);
            _restoringPlayback = true; await _playback.PlayAsync(online, [online]); _restoringPlayback = false; if (_playback.IsPlaying) _playback.Toggle();
            before = api.RequestCount; await LocateCurrentAsync(api);
            var located = TrackList.SelectedItem as Track;
            var locatedPath = _playback.CurrentDownloadedPath;
            var locatedVisible = located != null && Visible(located);
            check("An online song playing a download locates its original local file identity", located is { Id: < 0 } && string.Equals(located.LocalPath, locatedPath, StringComparison.OrdinalIgnoreCase) && locatedVisible && api.RequestCount == before,
                new { downloadedPath = locatedPath, selectedPath = located?.LocalPath, selectedId = located?.Id, visible = locatedVisible, requestsBefore = before, requestsAfter = api.RequestCount, candidates = Tracks.Select(t => new { t.Id, t.LocalPath, t.SourceSongId }).ToArray() });
            // A different downloaded copy of the same online song must not be
            // presented as the file currently being played.
            CommitLocal(() => _localLibrary.Playlists.Single(p => p.Id == shared.Id).Paths.Remove(current.LocalPath));
            LoadLocalTracks(); UpdateTrackLocator();
            check("Local locator does not select a different copy when the playing download is absent",
                Tracks.Any(t => t.LocalPath == copyB.LocalPath && t.SourceSongId == online.Id) &&
                Tracks.All(t => !string.Equals(t.LocalPath, _playback.CurrentDownloadedPath, StringComparison.OrdinalIgnoreCase)) &&
                TrackLocateButton.Visibility == Visibility.Collapsed,
                new { playingPath = _playback.CurrentDownloadedPath, availableCopy = copyB.LocalPath, locatorVisible = TrackLocateButton.Visibility.ToString() });
            CommitLocal(() => _localLibrary.Playlists.Single(p => p.Id == shared.Id).Paths.Add(current.LocalPath));
            LoadLocalTracks();
            var ok = Tagged(Path.Combine(fixture, "可删除.wav"), 9201); var locked = Tagged(Path.Combine(fixture, "占用中.wav"), 9202);
            CommitLocal(() => { _localLibrary.Tracks.AddRange([ok, locked]); _localLibrary.Playlists.Single(p => p.Id == shared.Id).Paths.AddRange([ok.LocalPath, locked.LocalPath]); }); LoadLocalTracks();
            using (var held = new FileStream(locked.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Primary(ConfirmRemoveLocalTracksAsync([ok, locked]), true);
                check("Mixed batch failures keep undeleted files and their playlist entries", !File.Exists(ok.LocalPath) && File.Exists(locked.LocalPath) && _localLibrary.Playlists.Single(p => p.Id == shared.Id).Paths.Contains(locked.LocalPath) && !_localLibrary.Tracks.Any(t => t.LocalPath == ok.LocalPath) && Notice.IsOpen && Notice.Message.Contains("1 个文件未能删除"), null);
            }
            var store = _localLibrary.StoragePath; var blocked = Path.Combine(fixture, "blocked-store"); Directory.CreateDirectory(blocked); _localLibrary.StoragePath = blocked;
            try
            {
                await Primary(ConfirmRemoveLocalTracksAsync([locked]), true);
                check("A failed library save prevents deleting the selected audio", File.Exists(locked.LocalPath) && _localLibrary.Tracks.Any(t => t.LocalPath == locked.LocalPath) && _downloadedLibrary.FindPath(locked.LocalPath) != null, null);
            }
            finally { _localLibrary.StoragePath = store; }
            var other = new LocalPlaylist { Name = "无此歌曲", Paths = [locked.LocalPath] }; CommitLocal(() => _localLibrary.Playlists.Add(other)); _localPlaylistId = other.Id; await NavigateAsync("local-playlist");
            check("Unrelated local playlists do not show a false current-song locator", TrackLocateButton.Visibility == Visibility.Collapsed, null);
            HideWindow(); UpdateTrackLocator(); check("Hiding the window hides the local locator", TrackLocateButton.Visibility == Visibility.Collapsed, null); RestoreWindow();
        }
        finally
        {
            _dialog?.Hide(); _dialog = null; EndTrackBatch(); StopLocalMonitoring(); _playback.Restore(new()); _checkingLibraryWorkflow = false; _connectionOverride = null; SongInfoApiOverride = null; _localLibraryPathOverride = null;
            _downloadedLibrary = oldCatalog; _playback.Downloads = oldPlaybackCatalog;
            await DeleteWorkerFixtureAsync(fixture);
        }
    }
}
