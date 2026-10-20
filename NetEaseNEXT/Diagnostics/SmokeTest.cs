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
    private bool _runningSmokeTest;
    // Opt-in executable integration check: real XAML, API, MediaPlayer and Shell.
    // Never signs in, modifies a remote account or produces audible playback.
    private async Task RunSmokeTestAsync()
    {
        _runningSmokeTest = true;
        var results = new List<object>();
        var oldVolume = _settings.Volume;
        var oldQualitySetting = _settings.Quality;
        var oldBackdrop = _settings.Backdrop;
        var oldTheme = _settings.Theme;
        var oldFont = _settings.FontFamily;
        var oldReadItems = _settings.ReadInboxItems.ToList();
        var oldReadOffsets = new Dictionary<string, long>(_settings.ReadInboxOffsets);
        var oldCounterSnapshots = new Dictionary<string, long>(_settings.InboxCounterSnapshots);
        var oldAcknowledged = _settings.AcknowledgedInboxItems.ToList();
        void Check(string name, bool condition, object? detail = null)
        {
            Diagnostics.StartupTrace.Mark($"Check {name}: {condition}");
            results.Add(new { name, passed = condition, detail });
            if (!condition) throw new InvalidOperationException("Smoke test failed: " + name);
        }
        try
        {
            Check("XAML root loaded", Root.IsLoaded);
            if (Environment.GetCommandLineArgs().Contains("--library-check"))
            {
                await RunLibraryChecksAsync(Check, results);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--font-check"))
            {
                await RunFontChecksAsync(Check);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--smtc-check"))
            {
                await RunSmtcChecksAsync(Check, results);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--loading-lifecycle-check"))
            {
                await RunLoadingLifecycleChecksAsync(Check);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--background-network-check"))
            {
                await RunBackgroundNetworkChecksAsync(Check, results);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--events-startup-check"))
            {
                await RunEventsStartupChecksAsync(Check);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--sidebar-check"))
            {
                await RunSidebarChecksAsync(Check);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--startup-path-check"))
            {
                _playback.SetVolume(0);
                _restoringPlayback = true; RestoreWindow(); _restoringPlayback = false;
                await InitializeSessionAsync();
                Check("Production startup restores session without eager font scan", _sessionReady && !_initializingSession && !_fontsLoaded);
                var saved = await Task.Run(() => PlaybackStore.Load());
                var startupSavedId = saved.Queue.Any(track => track.Id == saved.CurrentId) ? saved.CurrentId : saved.Queue.FirstOrDefault()?.Id ?? 0;
                Check("Production startup restores the saved queue and current song", _playback.Queue.Count == saved.Queue.Count && (_playback.Current?.Id ?? 0) == startupSavedId);
                Check("Restored playback remains paused", !_playback.IsPlaying);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--startup-menu-check"))
            {
                await RunStartupMenuChecksAsync(Check);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--shell-theme-check"))
            {
                await RunShellThemeChecksAsync(Check);
                return;
            }
            await Task.Delay(150); Root.UpdateLayout();
            Check("Playback cover button fits the entire square artwork", OpenPlayerButton.ActualHeight >= 54, new { buttonHeight = OpenPlayerButton.ActualHeight, height = double.IsNaN(OpenPlayerButton.Height) ? "Auto" : OpenPlayerButton.Height.ToString(), coverHeight = MiniCover.ActualHeight });
            var fixtureFolder = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-state-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var fixtureProfile = JsonDocument.Parse("{\"userId\":123,\"nickname\":\"cached account\",\"avatarUrl\":\"\"}");
                var profileFile = Path.Combine(fixtureFolder, "profile.bin"); AccountCache.Save("session-one", fixtureProfile.RootElement, profileFile);
                Check("Cached login identity is available without a request", AccountCache.Load("session-one", profileFile)?.GetProperty("nickname").GetString() == "cached account");
                Check("Cached login identity cannot cross sessions", AccountCache.Load("session-two", profileFile) == null);
                using var fixtureMetadata = JsonDocument.Parse("{\"code\":200,\"lrc\":{\"lyric\":\"[00:10.00]fixture\"}}");
                var cacheKey = MetadataCache.Key("session-one", "lyric", new { id = 123 });
                new MetadataCache().Put(fixtureFolder, cacheKey, fixtureMetadata.RootElement, TimeSpan.FromMinutes(1), true);
                Check("Metadata cache survives a new client instance", new MetadataCache().TryGet(fixtureFolder, cacheKey, true, out var persisted) && persisted.GetProperty("code").GetInt32() == 200);
                Check("Play URLs are never persisted as generic metadata", MetadataCache.Policy("song_url_v1").Lifetime == TimeSpan.Zero);
                var fakeTracks = new List<Track> { new(123, "a", "artist", "album", "", 60000), new(456, "b", "artist", "album", "", 60000) };
                var snapshotFile = Path.Combine(fixtureFolder, "playback.bin");
                PlaybackStore.Save(new() { Queue = fakeTracks.AsEnumerable().Reverse().ToList(), OriginalQueue = fakeTracks, CurrentId = 456, PositionSeconds = 12, PlaylistId = 999, PlaylistName = "saved playlist", Mode = "Shuffle" }, snapshotFile);
                var snapshot = PlaybackStore.Load(snapshotFile); _playback.Restore(snapshot);
                Check("Restart restores current song, position and fixed shuffle order paused", _playback.Current?.Id == 456 && _playback.Position.TotalSeconds == 12 && _playback.Mode == "Shuffle" && !_playback.IsPlaying && _playback.Queue.Select(t => t.Id).SequenceEqual(new long[] { 456, 123 }) && snapshot.PlaylistId == 999);
                _playback.Mode = "Sequential";
                Check("Leaving restored shuffle returns the original playlist order", _playback.Queue.Select(t => t.Id).SequenceEqual(new long[] { 123, 456 }));
                _playback.Restore(new());
            }
            finally { if (Directory.Exists(fixtureFolder)) Directory.Delete(fixtureFolder, true); }
            Check("Windows Shell resolves SMTC application name", ApplicationIdentity.ResolveDisplayName() == "NetEaseNEXT");
            Check("Artwork sizes reuse one normalized thumbnail request", Artwork.CacheRequest("http://p1.music.126.net/cover.jpg?param=192y192", 180) == Artwork.CacheRequest("https://p1.music.126.net/cover.jpg", 192));
            Check("Rotating NetEase image hosts share one cache entry", Artwork.CacheIdentity("https://p3.music.126.net/cover.jpg", 192) == Artwork.CacheIdentity("https://p4.music.126.net/cover.jpg", 180));
            Check("Non-NetEase signed image URL is preserved", Artwork.CacheRequest("https://example.com/cover.png?signature=abc", 192) == "https://example.com/cover.png?signature=abc");
            using var trialCacheFixture = JsonDocument.Parse("{\"size\":123,\"freeTrialInfo\":{\"end\":30}}");
            Check("Trial audio cannot use the full-song disk cache", AudioCache.PathFor("cache", 1, "standard", trialCacheFixture.RootElement, new Uri("https://example.com/audio")) == null);
            using var audioCacheFixture = JsonDocument.Parse("{\"size\":123,\"md5\":\"abc\",\"br\":320000,\"type\":\"mp3\"}");
            Check("Audio cache separates quality and ignores expiring URL tokens", AudioCache.PathFor("cache", 1, "standard", audioCacheFixture.RootElement, new Uri("https://example.com/audio?token=1")) != AudioCache.PathFor("cache", 1, "lossless", audioCacheFixture.RootElement, new Uri("https://example.com/audio?token=1")) && AudioCache.PathFor("cache", 1, "standard", audioCacheFixture.RootElement, new Uri("https://example.com/audio?token=1")) == AudioCache.PathFor("cache", 1, "standard", audioCacheFixture.RootElement, new Uri("https://example.com/audio?token=2")));
            Check("Keyboard accelerator hints are hidden", Root.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden);
            Check("Social pagination is embedded in list footer", ReferenceEquals(SocialList.Footer, SocialMore));
            Check("Track and catalog pagination use themed list footers", ReferenceEquals(TrackList.Footer, LoadMoreButton) && ReferenceEquals(CatalogGrid.Footer, CatalogMore) && ReferenceEquals(LoadMoreButton.Style, Application.Current.Resources["MoreButtonStyle"]) && ReferenceEquals(CatalogMore.Style, LoadMoreButton.Style));
            Check("NetEase emoji tokens display as symbols", Emojis.Display("[大笑] [爱心] [未知]") == "😄 ❤️ [未知]");
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
            Check("Liked events use a solid vector icon", eventItem.LikedVisibility == Visibility.Visible && eventItem.UnlikedVisibility == Visibility.Collapsed);
            _settings.ReadInboxItems = []; _settings.AcknowledgedInboxItems = []; _settings.ReadInboxOffsets = []; _settings.InboxCounterSnapshots = [];
            _pendingUnread.Clear(); _unreadByCategory.Clear(); UpdateUnreadCounts();
            using var unreadFixture = JsonDocument.Parse("{\"fromUser\":{\"userId\":2},\"toUser\":{\"userId\":1},\"newMsgCount\":3,\"lastMsgTime\":1234}");
            var unreadItem = ParseSocialItem(unreadFixture.RootElement, "private", "private", 1); ApplyUnread(unreadItem, "private", false); UpdateUnreadCounts();
            Check("Unread private item has a dot and separate count", unreadItem.UnreadVisibility == Visibility.Visible && _privateUnreadCount == 3 && _messageUnreadCount == 0);
            MarkInboxRead(unreadItem); Check("Opening unread item clears dot and count", unreadItem.UnreadVisibility == Visibility.Collapsed && _privateUnreadCount == 0);
            ReconcileUnread("private", 3); UpdateUnreadCounts();
            Check("Stale unread counter cannot resurrect read private messages", _privateUnreadCount == 0);
            using var newPrivateFixture = JsonDocument.Parse("{\"fromUser\":{\"userId\":2},\"toUser\":{\"userId\":1},\"newMsgCount\":4,\"lastMsgTime\":1235}");
            var newerPrivate = ParseSocialItem(newPrivateFixture.RootElement, "private", "private", 1);
            ReconcileUnread("private", 4); ApplyUnread(newerPrivate, "private", false); UpdateUnreadCounts();
            Check("A new private message counts only unread additions", newerPrivate.Unread && newerPrivate.UnreadCount == 1 && _privateUnreadCount == 1);
            MarkInboxRead(newerPrivate); ReconcileUnread("private", 0); ReconcileUnread("private", 2);
            using var laterPrivateFixture = JsonDocument.Parse("{\"fromUser\":{\"userId\":2},\"toUser\":{\"userId\":1},\"newMsgCount\":2,\"lastMsgTime\":1236}");
            var laterPrivate = ParseSocialItem(laterPrivateFixture.RootElement, "private", "private", 1); ApplyUnread(laterPrivate, "private", false); UpdateUnreadCounts();
            Check("Acknowledged server counters do not hide later private messages", laterPrivate.UnreadCount == 2 && _privateUnreadCount == 2);
            SocialItems.Add(laterPrivate); ApplyServerReadAll();
            Check("Server read-all acknowledgment clears all local reminders", !laterPrivate.Unread && _privateUnreadCount == 0 && _messageUnreadCount == 0 && _pendingUnread.Count == 0);
            ReconcileUnread("private", 0); ReconcileUnread("private", 1); UpdateUnreadCounts();
            Check("New message after server read-all is still unread", _privateUnreadCount == 1);
            SocialItems.Clear();
            _pendingUnread.Clear(); _unreadByCategory.Clear(); _settings.ReadInboxItems = oldReadItems.ToList(); _settings.ReadInboxOffsets = new(oldReadOffsets); _settings.InboxCounterSnapshots = new(oldCounterSnapshots); _settings.AcknowledgedInboxItems = oldAcknowledged.ToList(); UpdateUnreadCounts();
            if (Environment.GetCommandLineArgs().Contains("--visual-fixes"))
            {
                await NavigateAsync("settings");
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact; Nav.IsPaneOpen = false;
                UnreadBadge.Value = 99; UnreadBadge.Visibility = Visibility.Visible; await Task.Delay(500); UpdateCompactAccount(); Root.UpdateLayout();
                var avatar = AccountAvatar.TransformToVisual(Nav).TransformPoint(new Windows.Foundation.Point(AccountAvatar.ActualWidth / 2, 0)).X;
                Check("Compact avatar stays centered with two-digit badge", Math.Abs(avatar - Nav.CompactPaneLength / 2) < 2, new { avatar, expected = Nav.CompactPaneLength / 2 });
                _settings.FontFamily = "Microsoft YaHei UI"; ApplyFont();
                Check("Content font cannot affect Windows playback icon", PlayPauseIcon.FontFamily.Source.Contains("Icons") && PlayPauseIcon.Glyph.Length == 1);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--artist-check"))
            {
                await NavigateAsync("artists"); var entry = Catalog.First(); _artistId = entry.Id; _playlistName = entry.Name;
                await NavigateAsync("artist"); Root.UpdateLayout(); TrackList.ScrollIntoView(Tracks[0]); await Task.Delay(3000); Root.UpdateLayout();
                var images = Tracks.Take(3).Select(t => TrackList.ContainerFromItem(t)).OfType<DependencyObject>().Select(c => FindDescendant<Image>(c)).OfType<Image>().ToArray();
                Check("Artist first rows artwork", images.Length == 3 && images.All(i => i.Source != null), new { count = images.Length, images = images.Select(Artwork.Inspect), covers = Tracks.Take(3).Select(t => t.Cover), TrackList.ActualHeight, TracksPanelHeight = TracksPanel.ActualHeight, TrackList.Visibility, panel = TracksPanel.Visibility, parent = TracksPanel.Parent.GetType().Name, scroll = FindDescendant<ScrollViewer>(TrackList)?.VerticalOffset });
                if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) await Task.Delay(25000);
                return;
            }
            await NavigateAsync("discover");
            Check("Discover binds real playlists", Playlists.Count > 0, Playlists.Count);
            var playlistFixture = Playlists[0]; _playlistId = playlistFixture.Id; _playlistName = playlistFixture.Name;
            Root.UpdateLayout();
            var discoveryCover = FindDescendant<Image>((DependencyObject)PlaylistGrid.ContainerFromItem(playlistFixture))!;
            var coverWait = Stopwatch.StartNew();
            while (discoveryCover.Source == null && coverWait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            Check("Discover artwork decodes", discoveryCover.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 });
            var animationStarts = _coverAnimationStarts;
            PrepareCoverTransition("PlaylistCover", discoveryCover);
            await NavigateAsync("playlist");
            Check("Playlist cover transition starts before network detail load", !PlayerAnimationEnabled || _coverAnimationStarts > animationStarts);
            Check("Playlist details load cover and title", PlaylistDetails.Visibility == Visibility.Visible && PlaylistDetailTitle.Text.Length > 0 && Artwork.GetUri(PlaylistDetailCover).Length > 0);
            Check("Playlist subscription and comments are available", PlaylistSubscribeButton.Content != null && PlaylistCommentsButton.IsEnabled);
            Check("Playlist details keep song list", Tracks.Count > 0);
            Root.UpdateLayout();
            Check("Playlist actions have Windows icons and content sized rows", PlaylistSubscribeIcon is FontIcon && PlaylistSubscribeLabel.Text.Length > 0 && PlaylistCommentsLabel.Text.Length > 0 && PlaylistActionRows.ActualWidth <= TracksPanel.ActualWidth);
            Check("Playlist metadata and actions scroll with songs", TrackList.Header is StackPanel header && header.Children.Contains(PlaylistDetails) && header.Children.Contains(PlaylistActionRows));
            if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) await Task.Delay(TimeSpan.FromSeconds(25));
            Check("Artist sidebar is removed and podcast is named", !Nav.MenuItems.OfType<NavigationViewItem>().Any(i => (string)i.Tag == "artists") && Nav.MenuItems.OfType<NavigationViewItem>().Any(i => (string)i.Tag == "radio" && (string)i.Content == "我的播客"));
            _query = "陈奕迅";
            await NavigateAsync("search");
            Check("Search binds tracks", Tracks.Count > 0, Tracks.Count);
            Root.UpdateLayout();
            Check("Search songs remain bound to the visible native list", TrackList.Items.Count == Tracks.Count);
            var searchScroll = FindDescendant<ScrollViewer>(TrackList)!;
            if (LoadMoreButton.Visibility == Visibility.Visible)
            {
                var footerTop = LoadMoreButton.ActualHeight == 0 ? double.PositiveInfinity : LoadMoreButton.TransformToVisual(TrackList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                Check("Load more is outside the initial song viewport", footerTop >= TrackList.ActualHeight);
                searchScroll.ChangeView(null, searchScroll.ScrollableHeight, null, true); await Task.Delay(150); Root.UpdateLayout();
                var bottomTop = LoadMoreButton.TransformToVisual(TrackList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                Check("Load more is visible at the end of songs", LoadMoreButton.ActualHeight > 0 && bottomTop >= 0 && bottomTop + LoadMoreButton.ActualHeight <= TrackList.ActualHeight + 2);
                searchScroll.ChangeView(null, 0, null, true); await Task.Delay(100);
            }
            var center = TransportButtons.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(TransportButtons.ActualWidth / 2, 0));
            Check("Playback bar controls are centered", Math.Abs(center.X - Root.ActualWidth / 2) < 2);
            var toolCenters = PlayerTools.Children.OfType<Button>().Select(b => FindDescendant<FontIcon>(b)).Where(i => i != null).Select(i => i!.TransformToVisual(PlayerTools).TransformPoint(new Windows.Foundation.Point(0, i.ActualHeight / 2)).Y).ToArray();
            Check("Playback tool glyphs share a vertical center", toolCenters.Length >= 4 && toolCenters.Max() - toolCenters.Min() < 1);
            var actionCenters = SongActions.Children.OfType<Button>().Select(b => FindDescendant<FontIcon>(b)!).Select(i => i.TransformToVisual(SongActions).TransformPoint(new Windows.Foundation.Point(0, i.ActualHeight / 2)).Y).ToArray();
            Check("Heart and comment glyphs share a vertical center", actionCenters.Max() - actionCenters.Min() < 1);
            Check("Favorite playlist picker has cover template", Root.Resources["PlaylistChoiceTemplate"] is DataTemplate);
            Mode_Click(ModeButton, new RoutedEventArgs()); Check("Playback mode cycles directly with notice", _playback.Mode == "Repeat" && PlayerNotice.IsOpen && PlayerNotice.Message == "列表循环");
            Check("Popup uses an opaque theme background", PlayerNotice.Background is SolidColorBrush popupBrush && popupBrush.Color.A == 255);
            Mode_Click(ModeButton, new RoutedEventArgs()); Mode_Click(ModeButton, new RoutedEventArgs()); Mode_Click(ModeButton, new RoutedEventArgs());
            Check("Playback mode cycle returns to sequential", _playback.Mode == "Sequential");
            _playback.SetVolume(0);
            await _playback.PlayAsync(Tracks[0], Tracks);
            var watch = Stopwatch.StartNew();
            while ((!_playback.IsPlaying || _playback.Position.TotalSeconds < .5) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
            Check("MediaPlayer advances", _playback.IsPlaying && _playback.Position.TotalSeconds > 0,
                new { seconds = _playback.Position.TotalSeconds, duration = _playback.Duration.TotalSeconds });
            await _playback.CacheCompletion.WaitAsync(TimeSpan.FromSeconds(60));
            await _playback.PlayAsync(Tracks[1], Tracks); await _playback.PlayAsync(Tracks[0], Tracks);
            Check("Returning to a fully cached song decodes from disk", _playback.LastPlayUsedCache && _playback.IsPlaying);
            var shuffleOriginal = _playback.Queue.Select(t => t.Id).ToArray(); _playback.Mode = "Shuffle";
            var fixedShuffle = _playback.Queue.Select(t => t.Id).ToArray(); var generation = _playback.ShuffleGeneration;
            await _playback.NextAsync();
            Check("Shuffle advances through one fixed permutation without regenerating", _playback.Current?.Id == fixedShuffle[1] && _playback.ShuffleGeneration == generation && _playback.Queue.Select(t => t.Id).SequenceEqual(fixedShuffle));
            _playback.Mode = "Sequential"; _playback.Mode = "Shuffle";
            Check("Reentering shuffle generates exactly one new sequence", _playback.ShuffleGeneration == generation + 1);
            _playback.Mode = "Sequential";
            Check("Sequential restores the original queue after shuffle", _playback.Queue.Select(t => t.Id).SequenceEqual(shuffleOriginal));
            await _playback.WarmAsync(Tracks);
            await _playback.PlayAsync(Tracks[1], new[] { Tracks[1], Tracks[0] }); await _playback.NextPreparationTask.WaitAsync(TimeSpan.FromSeconds(60));
            var preparedStatus = _playback.NextPreparationStatus;
            var switchWatch = Stopwatch.StartNew(); await _playback.NextAsync(); switchWatch.Stop();
            Check("Preloaded song uses a valid cached playback authorization", _playback.LastPlayUsedCachedUrl, new { switchMilliseconds = switchWatch.Elapsed.TotalMilliseconds });
            Check("Next song reuses a prepared decoder", _playback.LastPlayUsedPreparedDecoder, new { preparedStatus, after = _playback.NextPreparationStatus });
            await _playback.PlayAsync(Tracks[0], Tracks);
            await _playback.PlayAsync(Tracks[0], new[] { Tracks[0] });
            ResetTrackGesture(); _tapWasTouch = true;
            var firstTap = ActivateTrackAsync(Tracks[1]); var secondTap = ActivateTrackAsync(Tracks[1]); await Task.WhenAll(firstTap, secondTap);
            Check("Two rapid touch activations replace the full queue during audio loading", _playback.Current!.Id == Tracks[1].Id && _playback.Queue.Select(t => t.Id).SequenceEqual(Tracks.Select(t => t.Id)));
            ResetTrackGesture(); _tapWasTouch = false; await _playback.PlayAsync(Tracks[0], Tracks);
            var originalQueue = _playback.Queue.Select(t => t.Id).ToArray();
            var nextTrack = Tracks.Last(); _playback.AddNext(nextTrack);
            Check("Play-next preserves current and places requested song next", _playback.Queue[1].Id == nextTrack.Id && _playback.Current?.Id == Tracks[0].Id);
            await _playback.PlayAsync(Tracks[0], new[] { Tracks[1], Tracks[0], Tracks[2] });
            var sharedCard = new Button { DataContext = new SocialItem { Kind = "event", SharedTrack = Tracks[1] } };
            SocialTrack_Click(sharedCard, new RoutedEventArgs());
            watch.Restart(); while ((_playback.Current?.Id != Tracks[1].Id || !_playback.IsPlaying) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            Check("Event song card moves an existing song after current and immediately plays it", _playback.Current?.Id == Tracks[1].Id && _playback.IsPlaying && _playback.Queue.Select(t => t.Id).SequenceEqual(new[] { Tracks[0].Id, Tracks[1].Id, Tracks[2].Id }));
            SocialTrack_Click(sharedCard, new RoutedEventArgs()); await Task.Delay(100);
            Check("Clicking the current shared song keeps one queue entry", _playback.Current?.Id == Tracks[1].Id && _playback.IsPlaying && _playback.Queue.Count == 3 && _playback.Queue.Count(t => t.Id == Tracks[1].Id) == 1);
            _playback.Mode = "Shuffle"; var sharedShuffle = _playback.Queue.Select(t => t.Id).ToArray(); var sharedGeneration = _playback.ShuffleGeneration;
            sharedCard.DataContext = new SocialItem { Kind = "message", SharedTrack = Tracks[0] };
            SocialTrack_Click(sharedCard, new RoutedEventArgs());
            watch.Restart(); while ((_playback.Current?.Id != Tracks[0].Id || !_playback.IsPlaying) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            var sharedAfter = _playback.Queue.Select(t => t.Id).ToArray();
            Check("Private song card inserts and plays without reshuffling remaining songs", _playback.Current?.Id == Tracks[0].Id && _playback.IsPlaying && _playback.Mode == "Shuffle" && _playback.ShuffleGeneration == sharedGeneration && Array.IndexOf(sharedAfter, Tracks[0].Id) == Array.IndexOf(sharedAfter, Tracks[1].Id) + 1 && sharedShuffle.Where(id => id != Tracks[0].Id).SequenceEqual(sharedAfter.Where(id => id != Tracks[0].Id)));
            _playback.Mode = "Sequential";
            await _playback.PlayAsync(Tracks[0], new[] { Tracks[0], Tracks[2] }); await _playback.PlayNextAsync(Tracks[1]);
            Check("A new shared song is inserted before the existing next song and plays", _playback.Current?.Id == Tracks[1].Id && _playback.IsPlaying && _playback.Queue.Select(t => t.Id).SequenceEqual(new[] { Tracks[0].Id, Tracks[1].Id, Tracks[2].Id }));
            _playback.Restore(new()); await _playback.PlayNextAsync(Tracks[0]);
            Check("A shared song starts playback when the queue is empty", _playback.Current?.Id == Tracks[0].Id && _playback.IsPlaying && _playback.Queue.Count == 1);
            await _playback.PlayAsync(Tracks[0], Tracks);
            watch.Restart(); while (!_playback.IsPlaying && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            var oldQuality = _playback.CurrentQuality;
            Check("Unsupported quality preserves the playing stream", !_playback.ScheduleQuality("unsupported-quality") && _playback.IsPlaying && _playback.CurrentQuality == oldQuality && _playback.Queue.Select(t => t.Id).SequenceEqual(originalQueue));
            Check("SMTC reports playing", _playback.SystemPlaybackStatus == MediaPlaybackStatus.Playing);
            var sessions = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var published = false;
            foreach (var session in sessions.GetSessions())
            {
                var metadata = await session.TryGetMediaPropertiesAsync();
                if (metadata.Title != _playback.Current?.Title || metadata.Artist != _playback.Current?.Artist) continue;
                published = true;
                results.Add(new { name = "SMTC source identity", expected = ApplicationIdentity.Id, actual = session.SourceAppUserModelId });
                break;
            }
            Check("SMTC publishes current song metadata to the system", published);
            var currentId = _playback.Current!.Id;
            var qualityPosition = _playback.Position;
            Check("Selecting quality is deferred without reloading current audio", _playback.ScheduleQuality("standard") && _playback.IsPlaying && _playback.Current.Id == currentId && _playback.CurrentQuality == oldQuality && _playback.Position >= qualityPosition);
            await _playback.NextAsync();
            Check("The next song uses the selected quality", _playback.IsPlaying && _playback.Current!.Id != currentId && _playback.CurrentQuality == "standard");
            await _playback.PlayAsync(Tracks[0], Tracks);
            if (Environment.GetCommandLineArgs().Contains("--quality-check"))
            {
                foreach (var quality in new[] { "lossless", "hires", "jymaster" })
                {
                    var previousId = _playback.Current!.Id;
                    Check("Pending quality " + quality + " leaves current audio intact", _playback.ScheduleQuality(quality) && _playback.IsPlaying && _playback.Current.Id == previousId);
                    await _playback.NextAsync();
                    if (_playback.Current!.Id != previousId) Check("Next song " + quality + " decodes", _playback.IsPlaying, new { requested = quality, actual = _playback.CurrentQuality });
                    else Check("Unavailable " + quality + " leaves old audio intact", _playback.IsPlaying);
                }
                return;
            }
            await LoadSongEngagementAsync(_playback.Current!);
            Check("Song red-heart count is real and visible", _redCount is >= 0 && RedCountText.Text != "—" && SongCommentsButton.IsEnabled);
            coverWait.Restart(); while (MiniCover.Source == null && coverWait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            Check("Playback bar cover decodes before entering player", MiniCover.Source != null, Artwork.Inspect(MiniCover));
            animationStarts = _coverAnimationStarts;
            await NavigateAsync("playing");
            Check("Entering player restores the cover transition", !PlayerAnimationEnabled || _coverAnimationStarts > animationStarts);
            Check("Independent player hosts shared controls", TransportControls.Parent == PageTransportHost && PlayerTools.Parent == PageToolsHost && PlayerFooter.Visibility == Visibility.Collapsed);
            Check("Heart and song comments follow player controls", SongActions.Parent == PageSongActionsHost);
            Check("Lyrics bind", _lyrics.Count > 0, _lyrics.Count);
            var resumeAfterLyricCheck = _playback.IsPlaying; if (resumeAfterLyricCheck) _playback.Toggle();
            _activeLyricIndex = -1; _initialLyricCenter = true; UpdateProgress();
            await Task.Delay(500); Root.UpdateLayout();
            var firstLyricScroll = FindDescendant<ScrollViewer>(LyricsList)!;
            var firstLyricRow = LyricsList.ContainerFromIndex(Math.Max(0, _activeLyricIndex)) as FrameworkElement;
            var firstLyricY = firstLyricRow?.TransformToVisual(firstLyricScroll).TransformPoint(new Windows.Foundation.Point(0, firstLyricRow.ActualHeight / 2)).Y ?? -999;
            Check("First opening centers the active lyric after viewport layout", firstLyricScroll.ViewportHeight > 0 && Math.Abs(firstLyricY - firstLyricScroll.ViewportHeight / 2) <= 12, new { firstLyricY, firstLyricScroll.ViewportHeight });
            var actualLyrics = _lyrics;
            _lyrics = new() { new(TimeSpan.FromMinutes(5), "first upcoming line"), new(TimeSpan.FromMinutes(6), "second upcoming line"), new(TimeSpan.FromMinutes(7), "third upcoming line") };
            _followLyrics = true; BindLyrics(_lyrics);
            await Task.Delay(350); Root.UpdateLayout();
            var upcomingRow = LyricsList.ContainerFromIndex(0) as FrameworkElement;
            var upcomingY = upcomingRow?.TransformToVisual(firstLyricScroll).TransformPoint(new Windows.Foundation.Point(0, upcomingRow.ActualHeight / 2)).Y ?? -999;
            Check("Lyrics before the first timestamp also start in the center", _activeLyricIndex == 0 && Math.Abs(upcomingY - firstLyricScroll.ViewportHeight / 2) <= 6, new { upcomingY, firstLyricScroll.ViewportHeight });
            _lyrics = actualLyrics; BindLyrics(_lyrics);
            if (resumeAfterLyricCheck) _playback.Toggle();
            Check("Player window provides native caption controls and drag area", WindowChrome.Visibility == Visibility.Collapsed && TitleRow.Height.Value == 0 && PlayerBackButton.Visibility == Visibility.Visible && PlayerDragArea.Visibility == Visibility.Visible && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { HasTitleBar: true });
            Root.UpdateLayout();
            var coverTop = CoverColumn.TransformToVisual(PlayingPanel).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            Check("Player cover and all controls stay within the window", coverTop >= 0 && coverTop + CoverColumn.ActualHeight <= PlayingPanel.ActualHeight + 1);
            await UpdateCoverBackgroundAsync(_playback.Current!);
            Check("Cover background renders", BlurredCover.Source != null);
            if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) await Task.Delay(TimeSpan.FromSeconds(25));
            _playback.Seek(TimeSpan.FromSeconds(10)); await Task.Delay(500);
            Check("Seek", _playback.Position.TotalSeconds >= 9);
            // Smoke tests skip production session initialization. Complete the
            // initial account/avatar load before checking reuse on restoration.
            if (_api.HasSession) await UpdateAccountAsync();
            var avatarWait = Stopwatch.StartNew();
            while (_accountAvatarLoad != null && avatarWait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
            Check("Account avatar load completes before lifecycle snapshot", _accountAvatarLoad == null);
            var retainedCover = MiniCover.Source; var retainedBackground = BlurredCover.Source; var retainedAvatar = AccountAvatar.ProfilePicture;
            var retainedLyrics = LyricsList.ItemsSource;
            HideWindow(); UpdateVisibility(); await Task.Delay(800);
            Check("Hidden window stops UI timer", _hidden && !_timer.IsEnabled);
            Check("Hidden playback stops extra audio cache downloads", !_playback.IsCaching);
            Check("Hidden window keeps audio playing", _playback.IsPlaying && _playback.Position.TotalSeconds > 10);
            Check("Hidden window retains completed images, lyrics and list bindings without polling", ReferenceEquals(TrackList.ItemsSource, Tracks) && ReferenceEquals(SocialList.ItemsSource, SocialItems) && ReferenceEquals(MiniCover.Source, retainedCover) && ReferenceEquals(BlurredCover.Source, retainedBackground) && ReferenceEquals(AccountAvatar.ProfilePicture, retainedAvatar) && ReferenceEquals(LyricsList.ItemsSource, retainedLyrics) && !_inboxTimer.IsEnabled);
            Check("Shell tray registered", _tray.IsAvailable);
            if (Environment.GetCommandLineArgs().Contains("--measure-background"))
            {
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(20));
                process.Refresh();
                results.Add(new { name = "Background playback resource sample", elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
                    privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1) });
                Check("Idle Node processes exited", !_api.IsWorkerRunning && !_playback.IsWorkerRunning);
            }
            _playback.Toggle(); await Task.Delay(300);
            Check("Pause and SMTC", !_playback.IsPlaying && _playback.SystemPlaybackStatus == MediaPlaybackStatus.Paused);
            RestoreWindow(); await Task.Delay(150);
            Check("Tray restore reuses the existing cover, background, avatar and lyrics", ReferenceEquals(MiniCover.Source, retainedCover) && ReferenceEquals(BlurredCover.Source, retainedBackground) && ReferenceEquals(AccountAvatar.ProfilePicture, retainedAvatar) && ReferenceEquals(LyricsList.ItemsSource, retainedLyrics),
                new { cover = ReferenceEquals(MiniCover.Source, retainedCover), background = ReferenceEquals(BlurredCover.Source, retainedBackground), avatar = ReferenceEquals(AccountAvatar.ProfilePicture, retainedAvatar), lyrics = ReferenceEquals(LyricsList.ItemsSource, retainedLyrics) });
            await NavigateAsync("settings");
            Root.UpdateLayout();
            Check("Settings fit the scroll viewport", SettingsContent.ActualWidth > 0 && SettingsContent.ActualWidth <= SettingsPanel.ActualWidth && BackdropCombo.ActualWidth <= SettingsContent.ActualWidth);
            var settingsLeft = SettingsContent.TransformToVisual(SettingsPanel).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            Check("Settings content is not clipped at either edge", settingsLeft >= 0 && settingsLeft + SettingsContent.ActualWidth <= SettingsPanel.ActualWidth);
            Check("Settings host audio output controls", OutputCombo.Items.Count > 0 && SettingsVolume.IsLoaded);
            var oldBadgeVisibility = UnreadBadge.Visibility; var oldBadgeValue = UnreadBadge.Value;
            Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact; Nav.IsPaneOpen = false;
            UnreadBadge.Value = 99; UnreadBadge.Visibility = Visibility.Visible; UpdateCompactAccount(); await Task.Delay(150); Root.UpdateLayout();
            var avatarCenter = AccountAvatar.TransformToVisual(Nav).TransformPoint(new Windows.Foundation.Point(AccountAvatar.ActualWidth / 2, 0)).X;
            Check("Unread count does not shift the compact sidebar avatar", Math.Abs(avatarCenter - Nav.CompactPaneLength / 2) < 2, new { avatarCenter, paneCenter = Nav.CompactPaneLength / 2 });
            UnreadBadge.Value = oldBadgeValue; UnreadBadge.Visibility = oldBadgeVisibility; Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 800)); await Task.Delay(150); Root.UpdateLayout();
            var narrowLeft = SettingsContent.TransformToVisual(SettingsPanel).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            Check("Narrow window keeps settings and navigation accessible", SettingsContent.ActualWidth > 0 && narrowLeft >= 0 && narrowLeft + SettingsContent.ActualWidth <= SettingsPanel.ActualWidth && (Nav.DisplayMode != NavigationViewDisplayMode.Minimal || ContentArea.Padding.Left >= 64));
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(150); Root.UpdateLayout();
            if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) await Task.Delay(TimeSpan.FromSeconds(25));
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
            animationStarts = _coverAnimationStarts;
            Check("Fullscreen suppresses window zoom and cancels cover animation", _suppressWindowTransitions && _activeCoverAnimation == null);
            Check("Fullscreen removes return button and caption drag region", PlayerBackButton.Visibility == Visibility.Collapsed && (Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id).GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption)?.Length ?? 0) == 0);
            Check("Fullscreen uses same page and native exit icon", TransportControls.Parent == PageTransportHost && FullScreenIcon.Glyph == "\uE73F");
            SetFullScreen(false); Check("Player exits full screen", !_fullScreen && FullScreenIcon.Glyph == "\uE740");
            Check("Fullscreen presenter switch starts no cover transition", _coverAnimationStarts == animationStarts);
            await Task.Delay(300); Check("Normal window animation policy is restored", !_suppressWindowTransitions);
            var songCommentTask = ShowCommentsAsync(new SocialItem { Kind = "song", ResourceId = _playback.Current!.Id, ResourceKey = _playback.Current.Id.ToString(), ResourceType = 0, ThreadId = "R_SO_4_" + _playback.Current.Id });
            try
            {
                await Task.Delay(800);
                Check("Song comment window opens", _dialog is { IsLoaded: true });
                var commentsList = FindDescendant<ListView>(_dialog!)!;
                watch.Restart(); while (commentsList.Items.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
                Check("Real song comments bind", commentsList.Items.Count > 0);
                Check("Comments use per-row actions without selection", commentsList.SelectionMode == ListViewSelectionMode.None && commentsList.Footer is Button && commentsList.Items.OfType<SocialItem>().All(i => i.Kind == "comment" && i.ActionsVisibility == Visibility.Visible && i.LikeAsync != null && i.Reply != null));
            }
            finally { _dialog?.Hide(); await songCommentTask; }
            Check("System fonts available", FontCombo.Items.Count > 0);
            _settings.FontFamily = "Microsoft YaHei UI"; ApplyFont();
            Check("Font inherits into player", MiniTitle.FontFamily.Source == _settings.FontFamily);
            Check("Changing text font preserves Windows playback icon", PlayPauseIcon.FontFamily.Source.Contains("Icons") && PlayPauseIcon.Glyph.Length == 1);
            ShowError("音质切换失败，当前播放已保留。"); await Task.Delay(100); Root.UpdateLayout();
            var warningNotice = _view == "playing" ? PlayerNotice : Notice;
            Check("Warning popup retains the bundled symbol font", warningNotice.IconSource is FontIconSource { Glyph: "\uE7BA" } warning && warning.FontFamily.Source == IconFonts.Source);
            if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) { _playerNoticeTimer.Stop(); await Task.Delay(TimeSpan.FromSeconds(15)); }
            warningNotice.IsOpen = false;
            await LoadQualityAsync(_playback.Current!); Check("Account quality checked", _qualityTrack == _playback.Current!.Id);
            await NavigateAsync("artists"); Check("Artists loaded", Catalog.Count > 0);
            var artist = Catalog.First(); _artistId = artist.Id; _playlistName = artist.Name;
            await NavigateAsync("artist"); Check("Artist detail starts with top songs and profile", ArtistName.Text.Length > 0 && Tracks.Count > 0 && TracksPanel.Parent == ArtistContentHost);
            Root.UpdateLayout(); await Task.Delay(100); Root.UpdateLayout();
            Check("Artist header scrolls with songs without consuming the viewport", ((Panel)TrackList.Header).Children.Contains(ArtistHeader) && TrackList.ActualHeight > 0);
            TrackList.ScrollIntoView(Tracks[0]);
            coverWait.Restart(); var firstArtistCovers = new List<Image>();
            do
            {
                Root.UpdateLayout(); firstArtistCovers = Tracks.Take(3).Select(t => TrackList.ContainerFromItem(t)).OfType<DependencyObject>().Select(c => FindDescendant<Image>(c)).OfType<Image>().ToList();
                if (firstArtistCovers.Count == 3 && firstArtistCovers.All(i => i.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 })) break;
                await Task.Delay(100);
            } while (coverWait.Elapsed < TimeSpan.FromSeconds(20));
            Check("First three artist songs display decoded artwork", firstArtistCovers.Count == 3 && firstArtistCovers.All(i => i.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 }), new { count = firstArtistCovers.Count, images = firstArtistCovers.Select(Artwork.Inspect), TrackList.ActualHeight, scroll = FindDescendant<ScrollViewer>(TrackList)?.VerticalOffset });
            var testCache = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-cache-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var coverUrl = Tracks[0].Cover;
                using var abandonedImage = new CancellationTokenSource();
                var abandoned = Artwork.GetFileInDirectoryAsync(coverUrl, 192, testCache, abandonedImage.Token);
                var retained = Artwork.GetFileInDirectoryAsync(coverUrl, 192, testCache, CancellationToken.None);
                abandonedImage.Cancel(); try { await abandoned; } catch (OperationCanceledException) { }
                var file = await retained;
                Check("Unloading one image does not cancel a shared cover download", File.Exists(file) && new FileInfo(file).Length > 12);
                var reused = await Artwork.GetFileInDirectoryAsync(coverUrl, 180, testCache, CancellationToken.None);
                Check("Repeated artwork request reuses the persisted thumbnail", reused == file && Directory.GetFiles(Path.Combine(testCache, "Artwork"), "*.img").Length == 1);
                await File.WriteAllTextAsync(file, "broken image");
                var repaired = await Artwork.GetFileInDirectoryAsync(coverUrl, 192, testCache, CancellationToken.None);
                Check("Broken disk artwork is replaced instead of remaining blank", repaired == file && new FileInfo(file).Length > 12);
            }
            finally { if (Directory.Exists(testCache)) Directory.Delete(testCache, true); }
            _artistTab = "bio"; await LoadPageAsync(false); Check("Artist biography tab renders", ArtistBioScroll.Visibility == Visibility.Visible);
            await NavigateAsync("albums"); Check("Artist albums loaded", Catalog.Count > 0);
            var albumItem = Catalog.First(); _playlistId = albumItem.Id; _playlistName = albumItem.Name;
            await NavigateAsync("album"); Check("Album tracks loaded", Tracks.Count > 0 && Tracks.All(t => t.Cover.Length > 0));
            if (_api.HasSession)
            {
                await NavigateAsync("radio"); Check("My podcasts loads subscriptions without recommendations", !Notice.IsOpen && !Busy.IsActive && PageTitle.Text == "我的播客");
                if (Catalog.Count > 0)
                {
                    var radio = Catalog.First(); _playlistId = radio.Id; _playlistName = radio.Name;
                    await NavigateAsync("programs"); Check("Subscribed podcast programs load", Tracks.Count > 0);
                    var program = Tracks[0]; await _playback.PlayAsync(program, Tracks); watch.Restart();
                    while ((!_playback.IsPlaying || _playback.Position.TotalSeconds < .2) && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(200);
                    Check("Subscribed podcast program plays", _playback.Current?.Id == program.Id && _playback.IsPlaying);
                }
            }
            if (Environment.GetCommandLineArgs().Contains("--social-read") && _api.HasSession)
            {
                await UpdateAccountAsync();
                if (_authenticated)
                {
                    foreach (var view in new[] { "events", "messages", "private" })
                    {
                        await NavigateAsync(view);
                        Check("Authenticated " + view + " loads", !Notice.IsOpen && !Busy.IsActive);
                        if (view is "messages" or "private") Check("Read-all is available in " + view, ReadAllButton.Visibility == Visibility.Visible && ReadAllButton.IsEnabled);
                        if (view == "messages") Check("Message inbox excludes private conversations", SocialItems.All(i => i.InboxCategory != "private" && i.PeerId == 0));
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
            _settings.ReadInboxItems = oldReadItems; _settings.ReadInboxOffsets = oldReadOffsets; _settings.InboxCounterSnapshots = oldCounterSnapshots; _settings.AcknowledgedInboxItems = oldAcknowledged; _settings.Quality = oldQualitySetting; _settings.Volume = oldVolume; _settings.Backdrop = oldBackdrop; _settings.Theme = oldTheme; _settings.FontFamily = oldFont;
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "smoke-test.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            ExitApp();
        }
    }
}
