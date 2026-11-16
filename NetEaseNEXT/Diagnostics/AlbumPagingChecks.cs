using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NetEaseNEXT.Controls;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunAlbumPagingChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _playback.SetVolume(0); _accountId = 42; _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed.TotalSeconds < 15) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Album paging checks did not settle");
        }
        async Task Bottom(ListViewBase list)
        {
            Root.UpdateLayout(); var scroll = FindDescendant<ScrollViewer>(list)!;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Task.Delay(100); Root.UpdateLayout();
                if (scroll.ScrollableHeight - scroll.VerticalOffset < 2) break;
            }
        }
        async Task Album()
        {
            _playlistId = 401; _playlistName = "完整专辑"; await NavigateAsync("album"); Root.UpdateLayout();
        }
        void Invoke(HyperlinkButton button) => ((IInvokeProvider)new HyperlinkButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        try
        {
            await Album();
            check("Album details show release date, company and type", AlbumFacts.Text.Contains("2024-01-01") && AlbumFacts.Text.Contains("测试唱片") && AlbumFacts.Text.Contains("录音室版"), AlbumFacts.Text);
            check("Album details contain a link for each album artist", AlbumArtists.Children.OfType<HyperlinkButton>().Select(b => b.Content as string).SequenceEqual(new[] { "歌手甲", "歌手乙" }), null);
            check("A long album introduction can be expanded", ExpandDescriptionButton.Visibility == Visibility.Visible && PlaylistDescription.MaxLines == 3, null);
            await SaveLibraryImageAsync(Root, "album-metadata.png");
            Invoke(AlbumArtists.Children.OfType<HyperlinkButton>().First()); await Wait(() => _view == "artist" && _pageLoad == null);
            check("The first album artist link opens its own artist page", _artistId == 301 && ArtistName.Text == "歌手甲", null);
            await Album(); Invoke(AlbumArtists.Children.OfType<HyperlinkButton>().Last()); await Wait(() => _view == "artist" && _pageLoad == null);
            check("The second album artist link opens its own artist page", _artistId == 302 && ArtistName.Text == "歌手乙", null);
            await Album(); var albumCalls = AlbumPagingFixtures.Calls.Count(c => c.Method == "album"); await Bottom(TrackList);
            _checkingAutoPaging = true; RequestNextPage(TrackList, true); await Task.Delay(100); _checkingAutoPaging = false;
            check("A complete album has no continuation request or loading footer", Tracks.Count == 60 && !_tracksMore && AlbumPagingFixtures.Calls.Count(c => c.Method == "album") == albumCalls && _pageFooters[TrackList].Visibility == Visibility.Collapsed, null);
            ApplyAlbumDetails(JsonSerializer.SerializeToElement(new { name = "单歌手", artist = new { id = 303, name = "独唱歌手" } }));
            check("Sparse album metadata uses the singular artist and clears previous facts", AlbumArtists.Children.OfType<HyperlinkButton>().Single().Content as string == "独唱歌手" && AlbumFacts.Visibility == Visibility.Collapsed, null);

            _query = "续页"; await NavigateAsync("search"); var first = Tracks.ToArray(); var route = _route; await Bottom(TrackList);
            AlbumPagingFixtures.Hold("cloudsearch"); _checkingAutoPaging = true; RequestNextPage(TrackList, true);
            await Wait(() => _pageLoad != null && AlbumPagingFixtures.GateMethod.Length == 0);
            check("Appending uses an active native ring after the rows without the center overlay", !Busy.IsActive && _pageFooters[TrackList].Ring.IsActive && _pageFooters[TrackList].Visibility == Visibility.Visible && ReferenceEquals(TrackList.Footer, _pageFooters[TrackList]), null);
            var requests = AlbumPagingFixtures.Calls.Count(c => c.Method == "cloudsearch");
            for (var i = 0; i < 8; i++) RequestNextPage(TrackList, true);
            check("Repeated bottom gestures share one continuation request", requests == AlbumPagingFixtures.Calls.Count(c => c.Method == "cloudsearch"), null);
            await Bottom(TrackList); await SaveLibraryImageAsync(Root, "track-bottom-ring.png");
            var heldOffset = FindDescendant<ScrollViewer>(TrackList)!.VerticalOffset;
            AlbumPagingFixtures.Release(); await Wait(() => _pageLoad == null && Tracks.Count == 100);
            check("Append preserves rows and route then removes the loading row", Tracks.Take(50).SequenceEqual(first) && ReferenceEquals(route, _route) && _pageFooters[TrackList].Visibility == Visibility.Collapsed && !_pageFooters[TrackList].Ring.IsActive, null);
            check("Appending does not jump past the newly loaded songs", Math.Abs(FindDescendant<ScrollViewer>(TrackList)!.VerticalOffset - heldOffset) <= 64, new { heldOffset, offset = FindDescendant<ScrollViewer>(TrackList)!.VerticalOffset });
            _checkingAutoPaging = false; await Bottom(TrackList); _checkingAutoPaging = true; RequestNextPage(TrackList, true); await Wait(() => Tracks.Count == 103 && _pageLoad == null);
            check("Continuing upward at an existing bottom loads the final search page", !_tracksMore && _offset == 103, null);
            _checkingAutoPaging = false;

            await NavigateAsync("saved-albums"); await Bottom(CatalogGrid); var albums = Catalog.ToArray();
            AlbumPagingFixtures.FailNext = "album_sublist"; _checkingAutoPaging = true; RequestNextPage(CatalogGrid, true);
            await Wait(() => _pageFooters[CatalogGrid].Failed && _pageLoad == null);
            check("Failed catalog continuation keeps existing albums and retries in its footer", Catalog.SequenceEqual(albums) && _offset == 48 && !Notice.IsOpen && !Busy.IsActive && !_pageFooters[CatalogGrid].Ring.IsActive, null);
            await Task.Delay(150); check("A failed page does not retry itself on layout", _pageLoad == null && _pageFooters[CatalogGrid].Failed, null);
            RequestNextPage(CatalogGrid, true); await Wait(() => Catalog.Count == 50 && _pageLoad == null);
            check("A new bottom gesture can retry and finish the catalog", !_catalogMore && _pageFooters[CatalogGrid].Visibility == Visibility.Collapsed && Catalog.Select(c => c.Id).Distinct().Count() == 50, null);
            _checkingAutoPaging = false;

            await NavigateAsync("library"); await Bottom(PlaylistGrid); _checkingAutoPaging = true; RequestNextPage(PlaylistGrid, true);
            await Wait(() => Playlists.Count == 102 && _pageLoad == null);
            check("The playlist library continues beyond its old first 100 playlists", !_playlistsMore && _offset == 102 && AlbumPagingFixtures.Calls.Any(c => c.Method == "user_playlist" && c.Offset == 100), null);
            _checkingAutoPaging = false;
            _playlistId = 201; _playlistName = "播客"; await NavigateAsync("programs"); await Bottom(TrackList); _checkingAutoPaging = true; RequestNextPage(TrackList, true);
            await Wait(() => Tracks.Count == 53 && _pageLoad == null);
            check("Podcast continuation uses the shared footer and stops at the server's final page", !_tracksMore && _pageFooters[TrackList].Visibility == Visibility.Collapsed, null);
            _checkingAutoPaging = false;

            _playlistId = 101; _playlistName = "续页歌单"; await NavigateAsync("playlist");
            BeginTrackBatch(TrackList, Tracks[0]); AlbumPagingFixtures.Hold("playlist_track_all");
            var batchAppend = LoadPageAsync(true); await Wait(() => _pageLoad != null);
            check("Playlist removal remains disabled during nonblocking continuation", !_batchBars[TrackList].Remove.IsEnabled && !Busy.IsActive, null);
            AlbumPagingFixtures.Release(); await batchAppend;
            check("Playlist removal is available again after continuation finishes", _batchBars[TrackList].Remove.IsEnabled && Tracks.Count == 115 && !_tracksMore, null);
            EndTrackBatch();

            _query = "取消续页"; await NavigateAsync("search"); await Bottom(TrackList); AlbumPagingFixtures.Hold("cloudsearch");
            var pending = LoadPageAsync(true); await Wait(() => _pageLoad != null); await Album(); await pending;
            check("Navigating during continuation cancels stale rows and clears every footer", _view == "album" && Tracks.Count == 60 && _pageFooters.Values.All(f => !f.Ring.IsActive && f.Visibility == Visibility.Collapsed), null);
            AlbumPagingFixtures.Release();

            var comments = ShowCommentsAsync(new() { ThreadId = "R_SO_4_1", ResourceId = 1, ResourceKey = "1", ResourceType = 0 }, _api);
            try
            {
                await Wait(() => _dialog is { IsLoaded: true } && Descendants<ListView>(_dialog).Any(l => l.Items.Count == 20));
                var list = Descendants<ListView>(_dialog!).First(l => l.Items.Count == 20); var footer = (PageLoadingFooter)list.Footer;
                AlbumPagingFixtures.Hold("comment_new"); await Bottom(list); await Wait(() => footer.Ring.IsActive);
                check("Comments show the same native ring inside the bottom loading row", footer.Visibility == Visibility.Visible && footer.Ring.IsIndeterminate && !Busy.IsActive, null);
                await SaveLibraryImageAsync(_dialog!, "comment-bottom-ring.png");
                AlbumPagingFixtures.Release(); await Wait(() => list.Items.Count == 21 && footer.Visibility == Visibility.Collapsed);
                check("Finishing comments removes the loading row", !footer.Ring.IsActive && list.Items.Count == 21, null);
            }
            finally { AlbumPagingFixtures.Release(); _dialog?.Hide(); await comments; }
            if (Environment.GetCommandLineArgs().Contains("--album-paging-preview"))
            {
                await Album(); _interactiveMotionPreview = true; _settings.CloseToTray = false;
                await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "album-paging-preview-ready.json"), JsonSerializer.Serialize(new { run = Environment.GetEnvironmentVariable("NETEASENEXT_VM_RUN"), ready = true }));
                await Task.Delay(Timeout.Infinite);
            }
        }
        finally { AlbumPagingFixtures.Release(); _checkingAutoPaging = false; _dialog?.Hide(); }
    }
}
