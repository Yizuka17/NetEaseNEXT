using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Services;
using Windows.Media;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    // Opt-in executable integration check: real XAML, API, MediaPlayer and Shell.
    // Never signs in, modifies a remote account or produces audible playback.
    private async Task RunSmokeTestAsync()
    {
        var results = new List<object>();
        var oldVolume = _settings.Volume;
        var oldBackdrop = _settings.Backdrop;
        var oldTheme = _settings.Theme;
        var oldFont = _settings.FontFamily;
        void Check(string name, bool condition, object? detail = null)
        {
            Diagnostics.StartupTrace.Mark($"Check {name}: {condition}");
            results.Add(new { name, passed = condition, detail });
            if (!condition) throw new InvalidOperationException("Smoke test failed: " + name);
        }
        try
        {
            Check("XAML root loaded", Root.IsLoaded);
            using var outgoing = JsonDocument.Parse("{\"fromUser\":{\"userId\":1},\"toUser\":{\"userId\":2}}");
            Check("Outgoing private conversation selects recipient", ConversationPeer(outgoing.RootElement, 1).Number("userId") == 2);
            Check("Incoming private conversation selects sender", ConversationPeer(outgoing.RootElement, 2).Number("userId") == 1);
            using var noticeFixture = JsonDocument.Parse("{\"notice\":{\"type\":6,\"user\":{\"nickname\":\"A\"},\"comment\":{\"commentId\":22,\"content\":\"original comment\",\"threadId\":\"R_SO_4_123\"},\"resource\":{\"name\":\"song name\"}}}");
            var notification = ParseSocialItem(noticeFixture.RootElement, "messages", "notices", 1);
            Check("Comment notification preserves content and reply target", notification.Text == "original comment" && notification.OriginalText == "song name" && notification.CommentId == 22 && notification.ResourceType == 0 && notification.ResourceId == 123);
            using var eventFixture = JsonDocument.Parse("{\"id\":12,\"json\":\"{\\\"msg\\\":\\\"event text\\\"}\",\"info\":{\"threadId\":\"A_EV_2_12_1\",\"liked\":true,\"likedCount\":3,\"commentCount\":4}}");
            var eventItem = ParseSocialItem(eventFixture.RootElement, "events", "notices", 1);
            Check("Event actions retain full thread identifier", eventItem.ThreadId == "A_EV_2_12_1" && eventItem.ResourceType == 6 && eventItem.Liked && eventItem.CommentCount == 4);
            using var replyFixture = JsonDocument.Parse("{\"user\":{\"nickname\":\"B\"},\"content\":\"reply body\",\"beRepliedContent\":\"my original comment\",\"commentId\":23,\"threadId\":\"R_SO_4_123\",\"resourceJson\":\"{\\\"name\\\":\\\"song name\\\"}\"}");
            var replyNotice = ParseSocialItem(replyFixture.RootElement, "messages", "comments", 1);
            Check("Reply notification keeps original and new comments", replyNotice.Text == "reply body" && replyNotice.OriginalText == "my original comment" && replyNotice.CommentId == 23 && replyNotice.Name == "B");
            using var mentionFixture = JsonDocument.Parse("{\"json\":\"{\\\"comment\\\":{\\\"user\\\":{\\\"nickname\\\":\\\"C\\\"},\\\"content\\\":\\\"mention body\\\",\\\"commentId\\\":24,\\\"threadId\\\":\\\"R_SO_4_123\\\"}}\"}");
            var mention = ParseSocialItem(mentionFixture.RootElement, "messages", "mentions", 1);
            Check("Mention notification resolves author and comment", mention.Text == "mention body" && mention.Name == "C" && mention.CommentId == 24);
            var bilingual = LyricLine.Parse("[00:01.00]original", "[00:01.00]translation");
            Check("Translated lyrics align by timestamp", bilingual[0].Translation == "translation");
            await NavigateAsync("discover");
            Check("Discover binds real playlists", Playlists.Count > 0, Playlists.Count);
            var playlistFixture = Playlists[0]; _playlistId = playlistFixture.Id; _playlistName = playlistFixture.Name;
            await NavigateAsync("playlist");
            Check("Playlist details load cover and title", PlaylistDetails.Visibility == Visibility.Visible && PlaylistDetailTitle.Text.Length > 0 && Artwork.GetUri(PlaylistDetailCover).Length > 0);
            Check("Playlist subscription and comments are available", PlaylistSubscribeButton.Content != null && PlaylistCommentsButton.IsEnabled);
            Check("Playlist details keep song list", Tracks.Count > 0);
            _query = "陈奕迅";
            await NavigateAsync("search");
            Check("Search binds tracks", Tracks.Count > 0, Tracks.Count);
            Root.UpdateLayout();
            var center = TransportButtons.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(TransportButtons.ActualWidth / 2, 0));
            Check("Playback bar controls are centered", Math.Abs(center.X - Root.ActualWidth / 2) < 2);
            Check("Favorite playlist picker has cover template", Root.Resources["PlaylistChoiceTemplate"] is DataTemplate);
            Mode_Click(ModeButton, new RoutedEventArgs()); Check("Playback mode cycles directly with notice", _playback.Mode == "Repeat" && PlayerNotice.IsOpen && PlayerNotice.Message == "列表循环");
            Mode_Click(ModeButton, new RoutedEventArgs()); Mode_Click(ModeButton, new RoutedEventArgs()); Mode_Click(ModeButton, new RoutedEventArgs());
            Check("Playback mode cycle returns to sequential", _playback.Mode == "Sequential");
            _playback.SetVolume(0);
            await _playback.PlayAsync(Tracks[0], Tracks);
            var watch = Stopwatch.StartNew();
            while ((!_playback.IsPlaying || _playback.Position.TotalSeconds < .5) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
            Check("MediaPlayer advances", _playback.IsPlaying && _playback.Position.TotalSeconds > 0,
                new { seconds = _playback.Position.TotalSeconds, duration = _playback.Duration.TotalSeconds });
            Check("SMTC reports playing", _playback.SystemPlaybackStatus == MediaPlaybackStatus.Playing);
            await LoadSongEngagementAsync(_playback.Current!);
            Check("Song red-heart count is real and visible", _redCount is >= 0 && RedCountText.Text != "—" && SongCommentsButton.IsEnabled);
            await NavigateAsync("playing");
            Check("Independent player hosts shared controls", TransportControls.Parent == PageTransportHost && PlayerTools.Parent == PageToolsHost && PlayerFooter.Visibility == Visibility.Collapsed);
            Check("Heart and song comments follow player controls", SongActions.Parent == PageSongActionsHost);
            Check("Lyrics bind", _lyrics.Count > 0, _lyrics.Count);
            await UpdateCoverBackgroundAsync(_playback.Current!);
            Check("Cover background renders", BlurredCover.Source != null);
            _playback.Seek(TimeSpan.FromSeconds(10)); await Task.Delay(500);
            Check("Seek", _playback.Position.TotalSeconds >= 9);
            HideWindow(); UpdateVisibility(); await Task.Delay(800);
            Check("Hidden window stops UI timer", _hidden && !_timer.IsEnabled);
            Check("Hidden window keeps audio playing", _playback.IsPlaying && _playback.Position.TotalSeconds > 10);
            Check("Shell tray registered", _tray.IsAvailable);
            _playback.Toggle(); await Task.Delay(300);
            Check("Pause and SMTC", !_playback.IsPlaying && _playback.SystemPlaybackStatus == MediaPlaybackStatus.Paused);
            if (Environment.GetCommandLineArgs().Contains("--measure-background"))
            {
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(47));
                process.Refresh();
                results.Add(new { name = "Background resource sample", elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
                    privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1) });
                Check("Idle Node process exited", !_api.IsWorkerRunning);
            }
            RestoreWindow(); await NavigateAsync("settings");
            _settings.Backdrop = "Solid"; _settings.Theme = "Dark"; ApplyAppearance();
            Check("Solid backdrop", SystemBackdrop == null && SolidLayer.Visibility == Visibility.Visible);
            _settings.Backdrop = "Mica"; ApplyAppearance();
            Check("Mica supported or fallback", SystemBackdrop != null || MaterialHint.Text.Contains("不支持"));
            await NavigateAsync("queue");
            Check("Leaving player restores playback bar", TransportControls.Parent == FooterTransportHost && PlayerTools.Parent == FooterToolsHost && PlayerFooter.Visibility == Visibility.Visible);
            ShowQueue_Click(QueueButton, new RoutedEventArgs()); Check("Playback queue opens without navigation", QueuePane.Visibility == Visibility.Visible && _view == "queue" && QueueList.Items.Count == _playback.Queue.Count);
            ShowQueue_Click(QueueButton, new RoutedEventArgs()); Check("Second queue click closes panel", QueuePane.Visibility == Visibility.Collapsed && QueueList.ItemsSource == null);
            Check("Queue preserved", Tracks.Count == _playback.Queue.Count && Tracks.Count > 0);
            await Task.Delay(300);
            var listScroll = FindDescendant<ScrollViewer>(TrackList)!;
            Check("Queue scroll range while paused", listScroll.ScrollableHeight > 0, listScroll.ScrollableHeight);
            listScroll.ChangeView(null, 400, null, true); await Task.Delay(200);
            var firstOffset = listScroll.VerticalOffset;
            Check("Paused queue scrolls down", firstOffset > 0, firstOffset);
            listScroll.ChangeView(null, 0, null, true); await Task.Delay(200);
            Check("Paused queue scrolls up", listScroll.VerticalOffset < firstOffset);
            await NavigateAsync("playing"); await Task.Delay(300); SuspendLyrics();
            var lyricsScroll = FindDescendant<ScrollViewer>(LyricsList)!;
            lyricsScroll.ChangeView(null, Math.Min(500, lyricsScroll.ScrollableHeight), null, true); await Task.Delay(200);
            // Variable-height bilingual rows refine their virtualized extent after
            // scrolling. Wait for layout anchoring to settle before testing that
            // a progress update cannot pull the paused view back to the current line.
            for (var pass = 0; pass < 10; pass++) { var previous = lyricsScroll.VerticalOffset; await Task.Delay(100); if (Math.Abs(previous - lyricsScroll.VerticalOffset) < .1) break; }
            var lyricOffset = lyricsScroll.VerticalOffset; UpdateProgress(); await Task.Delay(200);
            Check("Paused lyrics keep manual position", lyricOffset > 0 && Math.Abs(lyricsScroll.VerticalOffset - lyricOffset) < 1, new { lyricOffset, afterUpdate = lyricsScroll.VerticalOffset, range = lyricsScroll.ScrollableHeight, _followLyrics });
            lyricsScroll.ChangeView(null, 0, null, true); await Task.Delay(200);
            Check("Paused lyrics scroll up", lyricsScroll.VerticalOffset < lyricOffset);
            SetFullScreen(true); Check("Player enters full screen", _fullScreen && AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            Check("Fullscreen uses same page and native exit icon", TransportControls.Parent == PageTransportHost && FullScreenIcon.Glyph == "\uE73F");
            SetFullScreen(false); Check("Player exits full screen", !_fullScreen && FullScreenIcon.Glyph == "\uE740");
            var songCommentTask = ShowCommentsAsync(new SocialItem { Kind = "song", ResourceId = _playback.Current!.Id, ResourceKey = _playback.Current.Id.ToString(), ResourceType = 0, ThreadId = "R_SO_4_" + _playback.Current.Id });
            try
            {
                await Task.Delay(800);
                Check("Song comment window opens", _dialog is { IsLoaded: true });
                var commentsList = FindDescendant<ListView>(_dialog!)!;
                Check("Real song comments bind", commentsList.Items.Count > 0);
            }
            finally { _dialog?.Hide(); await songCommentTask; }
            Check("System fonts available", FontCombo.Items.Count > 0);
            _settings.FontFamily = "Microsoft YaHei UI"; ApplyFont();
            Check("Font inherits into player", MiniTitle.FontFamily.Source == _settings.FontFamily);
            await LoadQualityAsync(_playback.Current!); Check("Account quality checked", _qualityTrack == _playback.Current!.Id);
            await NavigateAsync("artists"); Check("Artists loaded", Catalog.Count > 0);
            var artist = Catalog.First(); _artistId = artist.Id; _playlistName = artist.Name;
            await NavigateAsync("albums"); Check("Artist albums loaded", Catalog.Count > 0);
            var albumItem = Catalog.First(); _playlistId = albumItem.Id; _playlistName = albumItem.Name;
            await NavigateAsync("album"); Check("Album tracks loaded", Tracks.Count > 0 && Tracks.All(t => t.Cover.Length > 0));
            await NavigateAsync("radio"); Check("Radio loaded", Catalog.Count > 0);
            var radio = Catalog.First(); _playlistId = radio.Id; _playlistName = radio.Name;
            await NavigateAsync("programs"); Check("Radio programs loaded", Tracks.Count > 0);
            var program = Tracks[0]; await _playback.PlayAsync(program, Tracks); watch.Restart();
            while ((!_playback.IsPlaying || _playback.Position.TotalSeconds < .2) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
            Check("Radio program plays", _playback.Current?.Id == program.Id && _playback.IsPlaying && _playback.Position.TotalSeconds > 0);
            if (Environment.GetCommandLineArgs().Contains("--social-read") && _api.HasSession)
            {
                await UpdateAccountAsync();
                if (_authenticated)
                {
                    foreach (var view in new[] { "events", "messages", "private" })
                    {
                        await NavigateAsync(view);
                        Check("Authenticated " + view + " loads", !Notice.IsOpen && !Busy.IsActive);
                        if (view == "events" && SocialItems.FirstOrDefault(i => i.ThreadId.Length > 0) is SocialItem eventSource)
                        {
                            var commentsTask = ShowCommentsAsync(eventSource);
                            try
                            {
                                await Task.Delay(800);
                                Check("Native comment dialog opens", _dialog is { IsLoaded: true, PrimaryButtonText: "发送" });
                                Check("Empty comment cannot be sent", _dialog is { IsPrimaryButtonEnabled: false });
                            }
                            finally { _dialog?.Hide(); await commentsTask; }
                        }
                    }
                    _messageCategory = "comments"; await NavigateAsync("messages");
                    Check("Comment reply inbox loads", !Notice.IsOpen && !Busy.IsActive);
                    _messageCategory = "mentions"; await RefreshSocialAsync();
                    Check("Mention inbox loads", !Notice.IsOpen && !Busy.IsActive);
                    Check("Private composer is hidden outside conversation", PrivateComposer.Visibility == Visibility.Collapsed);
                    await NavigateAsync("library"); Check("Library offers playlist creation", CreatePlaylistButton.Visibility == Visibility.Visible);
                    var createTask = CreatePlaylistAsync();
                    try
                    {
                        await Task.Delay(300);
                        Check("Native create playlist dialog opens", _dialog is { IsLoaded: true, Title: "创建歌单", IsPrimaryButtonEnabled: false });
                    }
                    finally { _dialog?.Hide(); await createTask; }
                }
            }
        }
        catch (Exception e) { results.Add(new { name = "exception", passed = false, detail = e.ToString() }); }
        finally
        {
            _settings.Volume = oldVolume; _settings.Backdrop = oldBackdrop; _settings.Theme = oldTheme; _settings.FontFamily = oldFont;
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "smoke-test.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            ExitApp();
        }
    }
}
