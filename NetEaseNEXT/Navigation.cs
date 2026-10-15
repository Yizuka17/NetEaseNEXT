using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.UI.ViewManagement;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async void Navigation_Changed(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || _selectingNav || _runningSmokeTest) return;
        if (args.IsSettingsSelected) await NavigateAsync("settings");
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string view) await NavigateAsync(view);
    }
    private async Task NavigateAsync(string view, bool remember = true)
    {
        _pageLoad?.Cancel(); _lyricsLoad?.Cancel(); _coverLoad?.Cancel();
        ResetTrackGesture();
        if (_fullScreen && view != "playing") SetFullScreen(false);
        if (view != _view && (view == "playing" || _view == "playing"))
            PrepareCoverTransition("PlayerCover", view == "playing" ? MiniCover : LargeCover);
        if (remember && _route != null && (_route.View != view || view is "albums" or "album" or "playlist" or "search" or "conversation")) _history.Push(_route);
        _route = new(view, _playlistId, _artistId, _peerId, _playlistName, _query, _linkedResource);
        BackButton.IsEnabled = _history.Count > 0 || _fullScreen;
        _view = view; _offset = 0; _socialCursor = -1;
        if (view == "playlist") { _resumePlaylistId = _playlistId; _resumePlaylistName = _playlistName; }
        SchedulePlaybackSave();
        if (view == "artist") _artistTab = "songs";
        ResetInbox();
        Playlists.Clear(); Tracks.Clear(); Catalog.Clear(); SocialItems.Clear();
        _lyrics.Clear(); LyricsList.ItemsSource = null; _lyricId = 0; Artwork.SetUri(LargeCover, ""); BlurredCover.Source = null;
        Notice.IsOpen = false; EmptyPanel.Visibility = Visibility.Collapsed;
        RestoreArtistContent();
        BrowsePanel.Visibility = CatalogPanel.Visibility = TracksPanel.Visibility = SettingsPanel.Visibility = PlayingPanel.Visibility = SocialPanel.Visibility = ArtistPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = CatalogMore.Visibility = SocialMore.Visibility = Visibility.Collapsed;
        Nav.Visibility = view == "playing" ? Visibility.Collapsed : Visibility.Visible;
        UpdateWindowChrome();
        MovePlayerControls(view == "playing"); QueuePane.Visibility = Visibility.Collapsed; QueueList.ItemsSource = null;
        UpdateTimer();
        UpdateLayout();
        PlaylistDetails.Visibility = PlaylistSubscribeButton.Visibility = PlaylistCommentsButton.Visibility = view == "playlist" ? Visibility.Visible : Visibility.Collapsed;
        CreatePlaylistButton.Visibility = view == "library" ? Visibility.Visible : Visibility.Collapsed;
        if (view != "playlist") { Artwork.SetUri(PlaylistDetailCover, ""); PlaylistDescription.Text = ""; }
        else { PlaylistDetailTitle.Text = _playlistName; PlaylistDescription.Text = PlaylistTags.Text = PlaylistCreator.Text = ""; }
        MessageCategory.Visibility = Visibility.Collapsed;
        EventToolbar.Visibility = view == "events" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = view == "discover" ? Visibility.Visible : Visibility.Collapsed;
        ReadAllButton.Visibility = view is "messages" or "private" ? Visibility.Visible : Visibility.Collapsed;
        ReadAllButton.IsEnabled = _api.HasSession && !_markingAllRead;
        EditPlaylistButton.Visibility = DeletePlaylistButton.Visibility = PlaylistOwnerActions.Visibility = Visibility.Collapsed;
        PrivateComposer.Visibility = view == "conversation" ? Visibility.Visible : Visibility.Collapsed;
        PrivateText.Text = "";
        _selectingNav = true;
        var selected = view == "settings" ? Nav.SettingsItem : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == (view == "event-detail" ? "events" : view));
        if (selected == null && view is not ("messages" or "private" or "conversation")) selected = Nav.SelectedItem;
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()) if (!ReferenceEquals(item, selected)) item.IsSelected = false;
        if (selected != null) Nav.SelectedItem = selected;
        else if (view is "messages" or "private" or "conversation") Nav.SelectedItem = null;
        _selectingNav = false;
        PageTitle.Text = view switch { "discover" => "发现", "library" => "歌单", "artists" or "artist" => "歌手", "playlist" => "歌单", "albums" or "album" or "programs" or "conversation" => _playlistName, "search" => _query, "queue" => "播放列表", "playing" => "正在播放", "settings" => "设置", "events" or "event-detail" => "动态", "song" => "歌曲", "messages" => "消息", "private" => "私信", "radio" => "我的播客", _ => "" };
        switch (view)
        {
            case "discover": case "library": BrowsePanel.Visibility = Visibility.Visible; break;
            case "artists": case "albums": case "radio": CatalogPanel.Visibility = Visibility.Visible; break;
            case "search": case "playlist": case "album": case "programs": case "queue": case "song": TracksPanel.Visibility = Visibility.Visible; break;
            case "playing": PlayingPanel.Visibility = Visibility.Visible; UpdateNowPlaying(); break;
            case "settings": SettingsPanel.Visibility = Visibility.Visible; break;
            case "artist": ArtistPanel.Visibility = Visibility.Visible; break;
            default: SocialPanel.Visibility = Visibility.Visible; break;
        }
        if (_settings.Animations && new UISettings().AnimationsEnabled && !_hidden && view != "playing")
        {
            var visual = ElementCompositionPreview.GetElementVisual(PageBody); var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, .25f); animation.InsertKeyFrame(1, 1); animation.Duration = TimeSpan.FromMilliseconds(140); visual.StartAnimation("Opacity", animation);
        }
        // Start from the cached bitmap as soon as the destination is laid out;
        // waiting for network data can expire the native connected animation.
        if (_navigationCoverKey == "PlaylistCover" && view == "playlist") StartCoverTransition(PlaylistDetailCover);
        else if (_navigationCoverKey == "PlayerCover") StartCoverTransition(view == "playing" ? LargeCover : MiniCover);
        await LoadPageAsync(false);
    }
    private async Task LoadPageAsync(bool append)
    {
        _pageLoad?.Cancel(); using var load = new CancellationTokenSource(); _pageLoad = load;
        SetBusy(true); EmptyPanel.Visibility = Visibility.Collapsed;
        try
        {
            if (_startup != null) await _startup.WaitForPageAsync(_view, load.Token);
            switch (_view)
            {
                case "discover":
                    var recommendations = await _api.DiscoverAsync(load.Token); load.Token.ThrowIfCancellationRequested();
                    foreach (var item in recommendations) Playlists.Add(item); break;
                case "library":
                    if (!RequireLogin()) break;
                    var accountId = _accountId > 0 ? _accountId : (await GetProfileAsync(load.Token))?.Number("userId") ?? 0;
                    if (accountId == 0) { ShowEmpty("请重新登录", "登录"); break; }
                    var library = await _api.CallCachedAsync("user_playlist", new { uid = accountId, limit = 100 }, load.Token);
                    load.Token.ThrowIfCancellationRequested(); foreach (var item in library.GetProperty("playlist").EnumerateArray()) Playlists.Add(Playlist.FromJson(item)); break;
                case "artists": case "albums":
                    var data = _view switch
                    {
                        "artists" => await _api.CallAsync("top_artists", new { limit = 48, offset = _offset }, load.Token),
                        "albums" => await _api.CallAsync("artist_album", new { id = _artistId, limit = 48, offset = _offset }, load.Token),
                        _ => await _api.CallAsync("dj_recommend", cancellation: load.Token)
                    };
                    load.Token.ThrowIfCancellationRequested();
                    var array = data.GetProperty(_view == "artists" ? "artists" : _view == "albums" ? "hotAlbums" : "djRadios");
                    foreach (var item in array.EnumerateArray()) Catalog.Add(_view == "artists" ? CatalogItem.Artist(item) : _view == "albums" ? CatalogItem.Album(item) : new CatalogItem(item.Number("id"), item.Text("name"), item.Text("picUrl"), "radio", item.TryGetProperty("dj", out var dj) ? dj.Text("nickname") : ""));
                    _offset += array.GetArrayLength(); CatalogMore.Visibility = array.GetArrayLength() == 48 && Catalog.Count < 1000 ? Visibility.Visible : Visibility.Collapsed; break;
                case "radio":
                    if (!RequireLogin()) break;
                    var subscriptions = await _api.CallAsync("dj_sublist", new { limit = 48, offset = _offset }, load.Token); load.Token.ThrowIfCancellationRequested();
                    if (subscriptions.TryGetProperty("djRadios", out var radios))
                    {
                        foreach (var item in radios.EnumerateArray()) Catalog.Add(new CatalogItem(item.Number("id"), item.Text("name"), item.Text("picUrl"), "radio", Object(item, "dj").Text("nickname")));
                        _offset += radios.GetArrayLength(); CatalogMore.Visibility = Flag(subscriptions, "hasMore") || Flag(subscriptions, "more") ? Visibility.Visible : Visibility.Collapsed;
                    }
                    break;
                case "search": case "playlist": case "album": case "programs":
                    var page = new List<Track>();
                    if (_view == "search") page = await _api.SearchAsync(_query, _offset, load.Token);
                    else if (_view == "playlist") { if (!append) await LoadPlaylistDetailAsync(load.Token); page = await _api.PlaylistTracksAsync(_playlistId, _offset, load.Token); }
                    else if (_view == "album")
                    {
                        var album = await _api.CallAsync("album", new { id = _playlistId }, load.Token);
                        page = album.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToList();
                    }
                    else
                    {
                        var programs = await _api.CallAsync("dj_program", new { rid = _playlistId, limit = 50, offset = _offset }, load.Token);
                        foreach (var p in programs.GetProperty("programs").EnumerateArray())
                        {
                            var song = p.GetProperty("mainSong");
                            page.Add(new Track(song.Number("id"), p.Text("name"), p.TryGetProperty("dj", out var dj) ? dj.Text("nickname") : "", _playlistName, p.Text("coverUrl"), p.Number("duration")));
                        }
                    }
                    load.Token.ThrowIfCancellationRequested(); if (!append) Tracks.Clear(); foreach (var item in page.Take(1000 - Tracks.Count)) Tracks.Add(item);
                    _offset += page.Count;
                    LoadMoreButton.Visibility = _view != "album" && page.Count == (_view == "playlist" ? 100 : 50) && Tracks.Count < 1000 ? Visibility.Visible : Visibility.Collapsed;
                    TrackCount.Text = $"{(_view == "playlist" && _playlistTrackCount > 0 ? _playlistTrackCount : Tracks.Count)} 首"; break;
                case "song": case "event-detail": await LoadLinkedResourceAsync(load.Token); break;
                case "queue": foreach (var item in _playback.Queue) Tracks.Add(item); TrackCount.Text = $"{Tracks.Count} 首"; break;
                case "playing": await LoadLyricsAsync(); break;
                case "settings": await LoadOutputsAsync(load.Token); break;
                case "artist": await LoadArtistAsync(load.Token); break;
                case "events": case "messages": case "private": case "conversation": if (RequireLogin()) await LoadSocialAsync(load.Token); break;
            }
            if (_view is not ("playing" or "settings" or "artist") && Playlists.Count + Tracks.Count + Catalog.Count + SocialItems.Count == 0 && EmptyPanel.Visibility != Visibility.Visible) ShowEmpty("暂无内容", _view == "queue" ? "发现" : "重试");
            if (!_hidden && Tracks.Count > 0 && _view is "playlist" or "album" or "search" or "artist" or "queue") { _ = _playback.WarmAsync(Tracks); WarmVisibleMetadata(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!load.IsCancellationRequested) { ShowError(ex.Message); if (Playlists.Count + Tracks.Count + Catalog.Count + SocialItems.Count == 0) ShowEmpty("加载失败"); } }
        finally { if (ReferenceEquals(_pageLoad, load)) { SetBusy(false); _pageLoad = null; UpdateTrackLocator(); } }
    }
    private bool RequireLogin() { if (_api.HasSession) return true; ShowEmpty("请先登录", "登录"); return false; }
    private async void Search_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) { if (string.IsNullOrWhiteSpace(args.QueryText)) return; _query = args.QueryText.Trim(); await NavigateAsync("search"); }
    private async void Playlist_Click(object sender, ItemClickEventArgs args)
    {
        var item = (Playlist)args.ClickedItem;
        if (PlaylistGrid.ContainerFromItem(item) is DependencyObject container && FindDescendant<Microsoft.UI.Xaml.Controls.Image>(container) is Image image && PlayerAnimationEnabled)
        {
            PrepareCoverTransition("PlaylistCover", image);
        }
        _playlistId = item.Id; _playlistName = item.Name; await NavigateAsync("playlist");
    }
    private async void Catalog_Click(object sender, ItemClickEventArgs args)
    {
        var item = (CatalogItem)args.ClickedItem; _playlistName = item.Name;
        if (item.Kind == "artist") { _artistId = item.Id; await NavigateAsync("artist"); }
        else if (item.Kind == "mv") await OpenMvAsync(item);
        else { _playlistId = item.Id; await NavigateAsync(item.Kind == "album" ? "album" : "programs"); }
    }
    private async void Track_Click(object sender, ItemClickEventArgs args)
    {
        var track = (Track)args.ClickedItem;
        await ActivateTrackAsync(track);
    }
    private async void PlayAll_Click(object sender, RoutedEventArgs args)
    {
        if (Tracks.FirstOrDefault() is not Track track) return;
        try { await PlayContextImmediatelyAsync(track); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void LoadMore_Click(object sender, RoutedEventArgs args) => await LoadPageAsync(true);
    private async void EmptyAction_Click(object sender, RoutedEventArgs args) { if ((string)EmptyAction.Content == "登录") await ShowLoginAsync(); else await NavigateAsync((string)EmptyAction.Content == "发现" ? "discover" : _view, false); }
    private async Task GoBackAsync()
    {
        if (_fullScreen) { SetFullScreen(false); return; }
        if (!_history.TryPop(out var route)) return;
        _playlistId = route.Id; _artistId = route.ArtistId; _peerId = route.PeerId; _playlistName = route.Name; _query = route.Query; _linkedResource = route.Source;
        await NavigateAsync(route.View, false);
    }
    private async void Back_Click(object sender, RoutedEventArgs args) => await GoBackAsync();
    private async void Back_Key(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await GoBackAsync(); }
    private async void Search_Key(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await NavigateAsync("discover"); SearchBox.Focus(FocusState.Programmatic); }
    private async void ContextPlay_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is Track track) await _playback.PlayAsync(track); }
    private async void ContextArtist_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is Track track) await OpenArtistAsync(track); }
    private async void ContextAlbum_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is Track track && track.AlbumId > 0) { _playlistId = track.AlbumId; _playlistName = track.Album; await NavigateAsync("album"); } }
    private async Task OpenArtistAsync(Track track) { if (track.ArtistId <= 0) return; _artistId = track.ArtistId; _playlistName = track.Artist; await NavigateAsync("artist"); }
}
