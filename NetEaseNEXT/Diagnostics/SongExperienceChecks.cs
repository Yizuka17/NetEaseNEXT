using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSongExperienceChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var oldSort = _settings.CommentSort; var oldAnimations = _settings.Animations;
        var oldDownloads = _playback.Downloads;
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-song-experience-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed.TotalSeconds < 15) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Song experience check did not settle");
        }
        var calls = new List<(string Method, JsonElement Parameters)>(); var subscribed = false; var failWrite = false;
        var songJson = JsonSerializer.SerializeToElement(new { id = 1, name = "合作歌曲", ar = new[] { new { id = 301, name = "歌手甲" }, new { id = 302, name = "歌手乙" } }, al = new { id = 401, name = "测试专辑", picUrl = "" }, dt = 60000 });
        var song = Track.FromJson(songJson);
        using var api = new ApiClient((method, p, ct) =>
        {
            calls.Add((method, p.Clone()));
            object response = new { code = 200 };
            if (method == "song_detail") response = new { code = 200, songs = p.Text("ids").Split(',').Select(long.Parse).Select(id => id == 1 ? songJson : JsonSerializer.SerializeToElement(new { id, name = "Song " + id, ar = new[] { new { id = 301, name = "歌手" } }, al = new { id = 401, name = "专辑", picUrl = "" }, dt = 30000 })).ToArray() };
            if (method == "album_detail_dynamic") response = new { code = 200, isSub = subscribed };
            if (method == "album_sub") { if (failWrite) throw new IOException("fixture denied"); subscribed = p.Number("t") == 1; }
            if (method == "comment_new") response = new { code = 200, data = new { totalCount = 0, hasMore = false, comments = Array.Empty<object>() } };
            if (method == "dj_program") response = new { code = 200, programs = Enumerable.Range((int)p.Number("offset"), 50).Select(i => new { name = "节目 " + i, mainSong = new { id = 1000 + i }, coverUrl = "", duration = 60000, dj = new { nickname = "主播" } }).ToArray() };
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }, fixture, "MUSIC_U=synthetic-song-check");
        try
        {
            _restoringPlayback = true; _playback.Restore(new() { Queue = [song], CurrentId = song.Id }); _restoringPlayback = false;
            UpdateSongActions(); Root.UpdateLayout();
            check("Song information sits to the right of comments", SongInfoButton.TransformToVisual(SongActions).TransformPoint(new()).X > SongCommentsButton.TransformToVisual(SongActions).TransformPoint(new()).X, null);
            check("All artist identities survive JSON playback persistence", JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(song))?.Artists.Select(a => a.Id).SequenceEqual(new long[] { 301, 302 }) == true, null);
            var artistsTask = ShowTrackArtistsAsync(song);
            await Wait(() => _dialog is { IsLoaded: true });
            check("Multi-artist navigation presents every singer", Descendants<HyperlinkButton>(_dialog!).Any(b => b.Content as string == "歌手乙"), null);
            Invoke(Descendants<HyperlinkButton>(_dialog!).Single(b => b.Content as string == "歌手乙")); await artistsTask;
            check("Choosing the second singer opens that artist ID", _view == "artist" && _artistId == 302, null);

            var info = ShowSongDetailsAsync(song, api); await Wait(() => _dialog is { IsLoaded: true } && CreditLinks(_dialog).Any(l => CreditText(l) == "测试专辑"));
            check("Song information displays all singers and the album action", CreditLinks(_dialog!).Any(l => CreditText(l) == "歌手乙"), null);
            await SaveLibraryImageAsync(_dialog!, "song-details.png");
            InvokeCredit(_dialog!, "测试专辑"); await info;
            check("Song album action opens its matching album", _view == "album" && _playlistId == 401, null);
            await LoadAlbumSubscriptionAsync(api, 401, CancellationToken.None);
            await ToggleAlbumSubscriptionAsync(api);
            check("Album subscription writes the selected album and updates the control", subscribed && _albumSubscribed && calls.Last(c => c.Method == "album_sub").Parameters.Number("id") == 401 && AlbumSubscribeLabel.Text == "已收藏", null);
            failWrite = true; await ToggleAlbumSubscriptionAsync(api);
            check("Failed album subscription preserves the known state", _albumSubscribed && subscribed && !_albumSubscribeWriting, null); failWrite = false;
            await ToggleAlbumSubscriptionAsync(api); check("Album subscription can be removed", !subscribed && !_albumSubscribed, null);
            check("Album writes use the serialized API write lane", NetEaseNEXT.Api.ApiEndpoints.Writes.Contains("album_sub"), null);

            _settings.CommentSort = "Hot";
            var source = new SocialItem { Kind = "song", ResourceType = 0, ResourceKey = "1", ThreadId = "R_SO_4_1" };
            var comments = ShowCommentsAsync(source, api); await Wait(() => calls.Any(c => c.Method == "comment_new") && _dialog is { IsLoaded: true });
            var sort = FindDescendant<ComboBox>(_dialog!)!;
            check("Comments default to hottest and request hot sorting", sort.SelectedIndex == 1 && calls.Last(c => c.Method == "comment_new").Parameters.Number("sortType") == 2, null);
            sort.SelectedIndex = 0; await Wait(() => calls.Last(c => c.Method == "comment_new").Parameters.Number("sortType") == 3);
            _dialog!.Hide(); await comments;
            comments = ShowCommentsAsync(source, api); await Wait(() => _dialog is { IsLoaded: true });
            check("Reopening comments remembers the latest sort selection", FindDescendant<ComboBox>(_dialog!)?.SelectedIndex == 0, null);
            _settings.SaveTo(Path.Combine(fixture, "settings.json")); check("Comment sort persists across a settings reload", SettingsStore.LoadFrom(Path.Combine(fixture, "settings.json")).CommentSort == "Latest", null);
            _dialog!.Hide(); await comments;

            var audio = Path.Combine(fixture, "silence.wav");
            using (var w = new BinaryWriter(File.Create(audio))) { const int bytes = 88200 * 30; w.Write("RIFF"u8); w.Write(bytes + 36); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]); }
            var songs = Enumerable.Range(1, 5).Select(i => new Track(-i, "本地歌曲 " + i, "歌手", "专辑", "", 30000) { LocalPath = audio }).ToArray();
            await NavigateAsync("queue"); _restoringPlayback = true;
            _playback.Mode = "Reverse"; Tracks.Clear(); foreach (var t in songs) Tracks.Add(t);
            await PlayAllAsync(); check("Reverse play all starts at the last song", _playback.Current?.Id == -5, null);
            await _playback.NextAsync(); check("Reverse next moves toward the start of the original list", _playback.Current?.Id == -4, null);
            await _playback.PreviousAsync(); check("Reverse previous moves toward the end", _playback.Current?.Id == -5, null);
            _playback.AddNext(songs[0]); await _playback.NextAsync();
            check("Add next follows the reverse playback direction", _playback.Current?.Id == -1, null);
            _playback.AddNext(new[] { songs[1], songs[2] }); await _playback.NextAsync();
            var firstAdded = _playback.Current?.Id; await _playback.NextAsync();
            check("Batch add next preserves the requested order in reverse mode", firstAdded == -2 && _playback.Current?.Id == -3, null);
            var snapshot = _playback.Capture(); _playback.Restore(snapshot); check("Reverse playback mode survives restore", _playback.Mode == "Reverse", null);
            var starts = new HashSet<long>(); _playback.Mode = "Shuffle";
            for (var i = 0; i < 12; i++) { await PlayAllAsync(); starts.Add(_playback.Current!.Id); }
            check("Repeated shuffle play all chooses different first songs", starts.Count > 1, starts.ToArray());
            check("Shuffle retains every song exactly once", _playback.Queue.Count == 5 && _playback.Queue.Select(t => t.Id).Distinct().Count() == 5, null);
            _restoringPlayback = false;
            var beforeLocalInfo = api.RequestCount;
            info = ShowSongDetailsAsync(_playback.Current!, api); await Wait(() => _dialog is { IsLoaded: true });
            check("Plain local song information remains available without API requests", SongInfoButton.IsEnabled && api.RequestCount == beforeLocalInfo && Descendants<TextBlock>(_dialog!).Any(t => t.Text == audio), null);
            _dialog!.Hide(); await info;

            _restoringPlayback = true; _playback.Downloads = new(Path.Combine(fixture, "downloads.json"));
            for (var i = 1; i <= 5; i++)
            {
                var path = Path.Combine(fixture, $"download-{i}.wav"); File.Copy(audio, path);
                _playback.Downloads.Register(new DownloadJob { Track = new Track(i, "Song " + i, "", "", "", 30000), FilePath = path, ActualLevel = "lossless" });
            }
            _view = "playlist"; _playlistId = 101; _playlistTrackIds = [1, 2, 3, 4, 5]; Tracks.Clear(); Tracks.Add(song);
            _playback.Mode = "Reverse"; await PlayAllAsync(api);
            check("Reverse starts at the real end even when only the first page is loaded", _playback.Current?.Id == 5 && _playback.Queue.Select(t => t.Id).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }), null);
            _playback.Mode = "Shuffle"; starts.Clear();
            for (var i = 0; i < 12; i++) { await PlayAllAsync(api); starts.Add(_playback.Current!.Id); }
            check("Online shuffle can start on an unloaded song and completes the queue", starts.Any(id => id != 1) && _playback.Queue.Count == 5, starts.ToArray());
            _restoringPlayback = false;

            _restoringPlayback = true; _playback.Restore(new() { Queue = [new Track(1124, "节目", "主播", "播客", "", 60000) { PodcastId = 201 }], CurrentId = 1124 }); _restoringPlayback = false;
            _view = "programs"; _playlistId = 201; _tracksMore = true; _offset = 50; Tracks.Clear(); foreach (var t in ParseProgramTracks(await api.CallAsync("dj_program", new { offset = 0 }), "播客", 201)) Tracks.Add(t);
            UpdateTrackLocator(); check("Podcast locator appears before the playing episode has loaded", TrackLocateButton.Visibility == Visibility.Visible, null);
            await LocateCurrentAsync(api); check("Podcast locator pages to the playing episode", Tracks.Any(t => t.Id == 1124) && (TrackList.SelectedItem as Track)?.Id == 1124 && _offset == 150, null);
            _playlistId = 202; Tracks.Clear(); UpdateTrackLocator(); check("A different podcast does not show a false locator", TrackLocateButton.Visibility == Visibility.Collapsed, null);

            await NavigateAsync("queue"); Tracks.Clear(); foreach (var i in Enumerable.Range(0, 160)) Tracks.Add(new Track(2000 + i, "滚动歌曲 " + i, "歌手", "专辑", "", 60000));
            _settings.Animations = true; ConfigureInterfaceMotion(); TrackList.SelectedItem = Tracks[0]; Root.UpdateLayout(); UpdateSelectionIndicators(true); await Task.Delay(600);
            var scroll = FindDescendant<ScrollViewer>(TrackList)!;
            check("Song indicator binds the live scroll property set", _trackSelectionMotion.ScrollBound && ReferenceEquals(_trackSelectionMotion.Scroll, scroll), null);
            scroll.ChangeView(null, 1500, null, true); await Task.Delay(150); Root.UpdateLayout(); UpdateSelectionIndicators(false);
            check("Virtualized selected songs leave no stranded indicator", TrackSelectionIndicator.Visibility == Visibility.Collapsed, null);
            var visible = Tracks.First(t => TrackList.ContainerFromItem(t) is ListViewItem row && row.TransformToVisual(TrackList).TransformPoint(new()).Y >= 0);
            TrackList.SelectedItem = visible; await Task.Delay(50);
            check("A cross-viewport selection stretches in from the previous direction", TrackSelectionIndicator.Visibility == Visibility.Visible && ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).TryGetAnimationController("Scale") != null, null);
            await Task.Delay(600); var contentY = _trackSelectionMotion.Y;
            scroll.ChangeView(null, scroll.VerticalOffset + 35, null, true); await Task.Delay(150); Root.UpdateLayout(); UpdateSelectionIndicators(false);
            check("Scrolling preserves the selected row content anchor", Math.Abs(contentY - _trackSelectionMotion.Y) < 1 && ReferenceEquals(_trackSelectionMotion.Item, visible), new { contentY, target = _trackSelectionMotion.Y });
            await SaveLibraryImageAsync(Root, "song-indicator-scroll.png");
            _settings.Animations = false; ConfigureInterfaceMotion(); TrackList.SelectedItem = Tracks[Tracks.IndexOf(visible) + 1]; await Task.Delay(50);
            check("Reduced motion skips cross-viewport stretch", ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).TryGetAnimationController("Scale") == null, null);
            _settings.Animations = true; ConfigureInterfaceMotion();
            _restoringPlayback = true; _playback.Restore(new() { Queue = Tracks.ToList(), CurrentId = Tracks[50].Id }); _restoringPlayback = false;
            ShowQueue_Click(QueueButton, new()); Root.UpdateLayout(); await Task.Delay(650); UpdateSelectionIndicators(false);
            var queueScroll = FindDescendant<ScrollViewer>(QueueList)!;
            if (QueueList.SelectedItem != null) QueueList.ScrollIntoView(QueueList.SelectedItem);
            await Task.Delay(150); Root.UpdateLayout(); UpdateSelectionIndicators(false);
            var queueY = _queueSelectionMotion.Y;
            queueScroll.ChangeView(null, Math.Max(0, queueScroll.VerticalOffset - 25), null, true); await Task.Delay(150); Root.UpdateLayout(); UpdateSelectionIndicators(false);
            await SaveLibraryImageAsync(Root, "queue-indicator-scroll.png");
            check("Queue indicator shares compositor scroll following and a fixed content anchor", _queueSelectionMotion.ScrollBound && Math.Abs(queueY - _queueSelectionMotion.Y) < 1, new { queueY, target = _queueSelectionMotion.Y, QueueList.ActualHeight, count = QueueList.Items.Count, queueScroll.VerticalOffset, selected = (QueueList.SelectedItem as Track)?.Id });
            ShowQueue_Click(QueueButton, new());
        }
        finally { _dialog?.Hide(); _restoringPlayback = false; _settings.CommentSort = oldSort; _settings.Animations = oldAnimations; _settings.Save(); _playback.Restore(new()); _playback.Downloads = oldDownloads; }
    }
}
