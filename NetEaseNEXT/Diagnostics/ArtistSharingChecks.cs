using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunArtistSharingChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); _accountId = 42; _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var oldOnlineInfo = _settings.LocalOnlineInfo;
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-artist-actions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed.TotalSeconds < 15) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Artist and resource action checks did not settle");
        }
        void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task Tab(string tab)
        {
            Invoke(Descendants<Button>(ArtistHeader).Single(b => b.Tag as string == tab)); await Wait(() => _artistTab == tab && _pageLoad == null); Root.UpdateLayout();
        }
        async Task Page(string view, long id)
        {
            _playlistId = id; _playlistName = view == "album" ? "测试专辑" : view == "programs" ? "整播客" : "测试歌单"; await NavigateAsync(view); Root.UpdateLayout();
        }
        async Task Comment(SocialItem source, int type, string thread)
        {
            var before = ArtistSharingFixtures.Calls.Count(c => c.Method == "comment_new");
            var comments = ShowCommentsAsync(source, _api);
            try
            {
                await Wait(() => _dialog is { IsLoaded: true } && ArtistSharingFixtures.Calls.Count(c => c.Method == "comment_new") > before && Descendants<ListView>(_dialog).Any(l => l.Items.Count == 1));
                var query = ArtistSharingFixtures.Calls.Last(c => c.Method == "comment_new").Query;
                check("Comment requests preserve resource type " + type, query.Number("type") == type && query.Text("threadId") == thread, null);
                check("Comments identify the selected resource " + type, Descendants<TextBlock>(_dialog!).Any(t => t.Text == source.Name), null);
                await SaveLibraryImageAsync(_dialog!, "resource-comments-" + type + ".png");
            }
            finally { _dialog?.Hide(); await comments; }
        }
        try
        {
            _artistId = 301; _playlistName = "测试歌手"; await NavigateAsync("artist"); await Wait(() => Artwork.IsReady(ArtistCover));
            await Task.Delay(500); Root.UpdateLayout();
            var source = ArtistCover.Source; var origin = ArtistCover.TransformToVisual(Root).TransformPoint(new()); var unloaded = 0;
            void AvatarUnloaded(object sender, RoutedEventArgs args) => unloaded++;
            ArtistCover.Unloaded += AvatarUnloaded;
            var metadata = ArtistSharingFixtures.Calls.Count(c => c.Method == "artists");
            check("Artist songs start with a paged complete catalog in hot order", Tracks.Count == 100 && Tracks[0].Id == 1 && _tracksMore && _artistOrder == "hot" && ArtistSongSort.SelectedIndex == 0, null);
            check("The song tab no longer advertises a fixed hot 50", Descendants<Button>(ArtistHeader).Any(b => b.Content as string == "歌曲") && !Descendants<Button>(ArtistHeader).Any(b => b.Content as string == "热门50"), null);
            await SaveLibraryImageAsync(Root, "artist-hot.png");
            foreach (var tab in new[] { "albums", "bio", "mvs", "songs" })
            {
                await Tab(tab); var point = ArtistCover.TransformToVisual(Root).TransformPoint(new());
                check("Switching artist " + tab + " keeps the avatar anchored", ReferenceEquals(ArtistHeader.Parent, ArtistPanel) && ReferenceEquals(source, ArtistCover.Source) && Math.Abs(point.X - origin.X) < 1 && Math.Abs(point.Y - origin.Y) < 1 && unloaded == 0, new { point.X, point.Y, originX = origin.X, originY = origin.Y, sourceSame = ReferenceEquals(source, ArtistCover.Source), parentSame = ReferenceEquals(ArtistHeader.Parent, ArtistPanel), unloaded });
            }
            ArtistCover.Unloaded -= AvatarUnloaded;
            check("Tab switches reuse the existing artist metadata", ArtistSharingFixtures.Calls.Count(c => c.Method == "artists") == metadata, null);
            var context = await GetPlaybackContextAsync();
            check("Play-all context includes every artist song without replacing displayed rows", context.Count == 165 && context.Select(t => t.Id).SequenceEqual(Enumerable.Range(1, 165).Select(i => (long)i)) && Tracks.Count == 100, null);
            ArtistSongSort.SelectedIndex = 1; await Wait(() => _pageLoad == null && Tracks.FirstOrDefault()?.Id == 165);
            check("Time sorting requests the full catalog in server order", _artistOrder == "time" && Tracks.Count == 100 && Tracks.Last().Id == 66 && ArtistSharingFixtures.Calls.Last(c => c.Method == "artist_songs").Query.Text("order") == "time", null);
            await SaveLibraryImageAsync(Root, "artist-time.png");
            _checkingAutoPaging = true; Root.UpdateLayout(); TrackList.ScrollIntoView(Tracks.Last()); await Wait(() => Tracks.Count == 165 && _pageLoad == null); _checkingAutoPaging = false;
            check("Artist songs continue beyond the old 50 and stop after their final page", !_tracksMore && Tracks.Select(t => t.Id).SequenceEqual(Enumerable.Range(1, 165).Reverse().Select(i => (long)i)), null);
            ArtistSongSort.SelectedIndex = 0; await Wait(() => _pageLoad == null && Tracks.FirstOrDefault()?.Id == 1);
            ArtistSharingFixtures.Hold("artist_songs"); var stale = LoadPageAsync(true); await Wait(() => ArtistSharingFixtures.GateMethod.Length == 0);
            ArtistSongSort.SelectedIndex = 1; await stale; await Wait(() => _pageLoad == null && Tracks.FirstOrDefault()?.Id == 165); ArtistSharingFixtures.Release();
            check("Changing sort cancels stale continuation rows", Tracks.Count == 100 && Tracks.All(t => t.Id >= 66), null);

            await Page("album", 401);
            check("Album comments and favorite actions are visible together", PlaylistCommentsButton.Visibility == Visibility.Visible && AlbumSubscribeButton.Visibility == Visibility.Visible && ResourceShareButton.Visibility == Visibility.Visible, null);
            await Comment(PageCommentsSource()!, 3, "R_AL_3_401");
            await ToggleAlbumSubscriptionAsync(_api); var savedGlyph = AlbumSubscribeIcon.Glyph;
            check("Saved album uses the same checked collection glyph as playlists", _albumSubscribed && savedGlyph == "\uE735", null);
            await SaveLibraryImageAsync(Root, "album-resource-actions.png");
            var albumShare = ShareForPage()!;
            await Page("programs", 201); var episode = Tracks.First();
            check("Podcast episodes retain program IDs separately from audio song IDs", episode.Id == 6001 && episode.ProgramId == 9001 && JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(episode))?.ProgramId == 9001, null);
            check("Whole podcast comments use radio type 7", PageCommentsSource() is { ResourceType: 7, ThreadId: "A_DR_14_201" }, null);
            check("Episode comments use program type 4", SongCommentsSource(episode) is { ResourceType: 4, ResourceId: 9001, ThreadId: "A_DJ_1_9001" }, null);
            await Comment(PageCommentsSource()!, 7, "A_DR_14_201"); await Comment(SongCommentsSource(episode)!, 4, "A_DJ_1_9001");
            await ToggleRadioSubscriptionAsync();
            check("Podcast collection reuses the playlist saved icon and writes the radio ID", _radioSubscribed && PlaylistSubscribeIcon.Glyph == savedGlyph && ArtistSharingFixtures.Calls.Last(c => c.Method == "dj_sub").Query.Number("rid") == 201, null);
            await SaveLibraryImageAsync(Root, "radio-resource-actions.png");
            var radioShare = ShareForPage()!; var programShare = ShareForTrack(episode)!;
            check("Podcast sharing distinguishes the whole radio from its episode", radioShare.Kind == "djradio" && radioShare.Link.AbsoluteUri.EndsWith("djradio?id=201") && programShare.Kind == "djprogram" && programShare.Link.AbsoluteUri.EndsWith("program?id=9001"), null);
            var package = SharePackage(programShare).GetView();
            check("Windows share data contains the selected resource title and public link", package.Properties.Title == episode.Title && (await package.GetWebLinkAsync()).AbsoluteUri == programShare.Link.AbsoluteUri && (await package.GetTextAsync()).Contains("9001"), null);

            ArtistSharingFixtures.Hold("album"); var loadingAlbum = Page("album", 402); await Wait(() => _pageLoad != null);
            check("Supported buttons keep their place while details are pending", AlbumSubscribeButton.Visibility == Visibility.Visible && PlaylistCommentsButton.Visibility == Visibility.Visible && ResourceShareButton.Visibility == Visibility.Visible, null);
            await Page("playlist", 101); await loadingAlbum; ArtistSharingFixtures.Release();
            check("Canceled detail requests cannot hide the new page's actions", PlaylistSubscribeButton.Visibility == Visibility.Visible && PlaylistCommentsButton.Visibility == Visibility.Visible, null);
            var playerUnloaded = 0; void PlayerUnloaded(object sender, RoutedEventArgs args) => playerUnloaded++;
            SongActions.Unloaded += PlayerUnloaded;
            await Page("album", 401); await Page("programs", 201); await Page("playlist", 101);
            SongActions.Unloaded -= PlayerUnloaded;
            check("Ordinary page switches do not unmount player action buttons", playerUnloaded == 0 && ReferenceEquals(SongActions.Parent, FooterSongActionsHost), null);

            var file = Path.Combine(fixture, "local-source.bin"); await File.WriteAllBytesAsync(file, new byte[64]); var info = new FileInfo(file);
            var local = new Track(-77, "已下载歌曲", "测试歌手", "测试专辑", "", 60000) { LocalPath = file }; local.SetLocalSource(77, "standard", info.Length, info.LastWriteTimeUtc.Ticks);
            _connectionOverride = true; _settings.LocalOnlineInfo = true; _restoringPlayback = true; _playback.Restore(new() { Queue = [local], CurrentId = local.Id }); _restoringPlayback = false;
            ArtistSharingFixtures.Hold("song_red_count"); _songStatsTask = Task.CompletedTask; _songStatsTrack = 0; var pending = LoadSongEngagementAsync(local, _api); await Wait(() => ArtistSharingFixtures.GateMethod.Length == 0);
            check("Refreshing linked local metadata does not hide heart and comment buttons", !_localOnlineReady && HeartButton.Visibility == Visibility.Visible && SongCommentsButton.Visibility == Visibility.Visible && SongCommentsButton.IsEnabled, null);
            var localShare = ShareForTrack(local)!;
            check("Sharing a downloaded source exposes its online ID rather than its local path", localShare.Id == 77 && !(await SharePackage(localShare).GetView().GetTextAsync()).Contains(file), null);
            await SaveLibraryImageAsync(Root, "stable-player-actions-loading.png"); ArtistSharingFixtures.Release(); await pending;
            _settings.LocalOnlineInfo = false; UpdateSongActions();
            check("The explicit local-online-info switch still hides network actions", HeartButton.Visibility == Visibility.Collapsed && SongCommentsButton.Visibility == Visibility.Collapsed, null);
            _settings.LocalOnlineInfo = true; UpdateSongActions();

            var beforePublish = ArtistSharingFixtures.Calls.Count(c => c.Method == "share_resource");
            var publish = PublishResourceAsync(radioShare); await Wait(() => _dialog is { IsLoaded: true });
            check("Sharing previews the selected resource before a write", ArtistSharingFixtures.Calls.Count(c => c.Method == "share_resource") == beforePublish && Descendants<TextBlock>(_dialog!).Any(t => t.Text == radioShare.Name), null);
            await SaveLibraryImageAsync(_dialog!, "share-resource-preview.png"); Invoke(Descendants<Button>(_dialog!).Single(b => b.Content as string == "发布")); await publish;
            var posted = ArtistSharingFixtures.Calls.Last(c => c.Method == "share_resource").Query;
            check("Confirming a share posts the captured whole-radio resource", posted.Text("type") == "djradio" && posted.Text("id") == "201", null);
            ArtistSharingFixtures.FailShare = true; publish = PublishResourceAsync(albumShare); await Wait(() => _dialog is { IsLoaded: true });
            Invoke(Descendants<Button>(_dialog!).Single(b => b.Content as string == "发布")); await Wait(() => _dialog != null && Descendants<TextBlock>(_dialog).Any(t => t.Text.Contains("模拟发布失败")));
            check("A rejected share keeps the preview open for retry", _dialog is { IsLoaded: true }, null); _dialog!.Hide(); await publish; ArtistSharingFixtures.FailShare = false;
        }
        finally
        {
            ArtistSharingFixtures.Release(); _checkingAutoPaging = false; _restoringPlayback = false; _settings.LocalOnlineInfo = oldOnlineInfo; _connectionOverride = null; _dialog?.Hide(); _playback.Restore(new());
            var file = Path.Combine(fixture, "local-source.bin"); if (File.Exists(file)) File.Delete(file); Directory.Delete(fixture);
        }
    }
}
