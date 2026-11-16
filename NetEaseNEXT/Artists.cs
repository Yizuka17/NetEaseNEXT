using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private string _artistTab = "songs";
    private long _artistHeaderId;
    private string _artistOrder = "hot";
    private bool _artistHeaderReady, _changingArtistSort;
    private void RestoreArtistContent()
    {
        foreach (var element in new FrameworkElement[] { TracksPanel, CatalogPanel })
        {
            if (ArtistContentHost.Children.Contains(element)) { ArtistContentHost.Children.Remove(element); PageBody.Children.Insert(0, element); }
        }
        ArtistBioScroll.Visibility = Visibility.Collapsed;
    }
    private void HostArtistContent(FrameworkElement element)
    {
        foreach (var body in new FrameworkElement[] { TracksPanel, CatalogPanel })
        {
            if (!ArtistContentHost.Children.Contains(body)) { PageBody.Children.Remove(body); ArtistContentHost.Children.Add(body); }
            body.Visibility = ReferenceEquals(body, element) ? Visibility.Visible : Visibility.Collapsed;
        }
        ArtistBioScroll.Visibility = Visibility.Collapsed;
    }
    private async Task LoadArtistAsync(CancellationToken ct)
    {
        if (_artistHeaderId != _artistId || !_artistHeaderReady)
        {
            var data = await _api.CallAsync("artists", new { id = _artistId }, ct); ct.ThrowIfCancellationRequested();
            var artist = Object(data, "artist"); _artistHeaderId = _artistId; _artistHeaderReady = true;
            ArtistName.Text = FirstText(artist.Text("name"), _playlistName);
            ArtistAlias.Text = artist.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.Array ? string.Join(" / ", alias.EnumerateArray().Select(a => a.GetString())) : "";
            ArtistSongCount.Text = "单曲数：" + artist.Number("musicSize"); ArtistAlbumCount.Text = "专辑数：" + artist.Number("albumSize");
            Artwork.SetUri(ArtistCover, FirstText(artist.Text("picUrl"), artist.Text("cover")));
            ArtistBio.Text = artist.Text("briefDesc");
        }
        ArtistSongSort.Visibility = _artistTab == "songs" ? Visibility.Visible : Visibility.Collapsed;
        if (_artistTab == "songs")
        {
            // Stabilize the visual parent before rows start their artwork requests.
            HostArtistContent(TracksPanel);
            var songs = await _api.CallAsync("artist_songs", new { id = _artistId, order = _artistOrder, limit = 100, offset = _offset }, ct); ct.ThrowIfCancellationRequested();
            var tracks = songs.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToList();
            var missing = tracks.Where(t => t.Cover.Length == 0).Select(t => t.Id).ToArray();
            if (missing.Length > 0)
            {
                var details = await _api.CallAsync("song_detail", new { ids = string.Join(',', missing) }, ct); ct.ThrowIfCancellationRequested();
                var replacements = details.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToDictionary(t => t.Id);
                tracks = tracks.Select(t => replacements.GetValueOrDefault(t.Id, t)).ToList();
            }
            var known = Tracks.Select(t => t.Id).ToHashSet(); foreach (var track in tracks) if (known.Add(track.Id)) Tracks.Add(track);
            _offset += tracks.Count;
            var total = songs.Number("total");
            if (total > 0) ArtistSongCount.Text = "单曲数：" + total;
            TrackCount.Text = $"{(total > 0 ? total : Tracks.Count)} 首";
            _tracksMore = HasNextPage(songs, tracks.Count, 100) && (total <= 0 || _offset < total);
        }
        else if (_artistTab is "albums" or "mvs")
        {
            HostArtistContent(CatalogPanel);
            var result = await _api.CallAsync(_artistTab == "albums" ? "artist_album" : "artist_mv", new { id = _artistId, limit = 48, offset = _offset }, ct); ct.ThrowIfCancellationRequested();
            var array = result.GetProperty(_artistTab == "albums" ? "hotAlbums" : "mvs");
            foreach (var item in array.EnumerateArray()) Catalog.Add(_artistTab == "albums" ? CatalogItem.Album(item) : new CatalogItem(item.Number("id"), item.Text("name"), FirstText(item.Text("imgurl"), item.Text("cover")), "mv"));
            _offset += array.GetArrayLength(); _catalogMore = HasNextPage(result, array.GetArrayLength(), 48) && Catalog.Count < 1000;
        }
        else
        {
            TracksPanel.Visibility = CatalogPanel.Visibility = Visibility.Collapsed;
            var result = await _api.CallAsync("artist_desc", new { id = _artistId }, ct); ct.ThrowIfCancellationRequested();
            var sections = new List<string> { result.Text("briefDesc") };
            if (result.TryGetProperty("introduction", out var intro) && intro.ValueKind == JsonValueKind.Array) sections.AddRange(intro.EnumerateArray().Select(i => i.Text("ti") + "\n" + i.Text("txt")));
            ArtistBio.Text = string.Join("\n\n", sections.Where(s => s.Length > 0)); ArtistBioScroll.Visibility = Visibility.Visible;
        }
    }
    private async void ArtistTab_Click(object sender, RoutedEventArgs args)
    {
        if (_view != "artist" || (sender as FrameworkElement)?.Tag is not string tab) return;
        if (tab == _artistTab && _pageLoad == null) return;
        _artistTab = tab; _offset = 0; _tracksMore = _catalogMore = false; ResetPageFooters(); Catalog.Clear(); Tracks.Clear(); await LoadPageAsync(false);
    }
    private async void ArtistSongSort_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _changingArtistSort || _view != "artist" || _artistTab != "songs") return;
        var order = ArtistSongSort.SelectedIndex == 1 ? "time" : "hot";
        if (order == _artistOrder) return;
        _artistOrder = order; _offset = 0; _tracksMore = false; ResetPageFooters(); Tracks.Clear(); await LoadPageAsync(false);
    }
    private async Task<List<Track>> GetArtistPlaybackContextAsync(List<Track> context)
    {
        var artist = _artistId; var order = _artistOrder; var offset = _offset; var more = _tracksMore;
        _playlistQueueLoad?.Cancel(); using var load = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _playlistQueueLoad = load;
        try
        {
            while (more)
            {
                var page = await _api.CallAsync("artist_songs", new { id = artist, order, limit = 500, offset }, load.Token); load.Token.ThrowIfCancellationRequested();
                var songs = page.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToArray(); context.AddRange(songs); offset += songs.Length;
                more = HasNextPage(page, songs.Length, 500) && (page.Number("total") <= 0 || offset < page.Number("total"));
            }
            if (_hidden || _view != "artist" || _artistId != artist || _artistOrder != order || _artistTab != "songs") throw new OperationCanceledException();
            return context.DistinctBy(t => t.Id).ToList();
        }
        finally { if (ReferenceEquals(_playlistQueueLoad, load)) _playlistQueueLoad = null; }
    }
    private async Task OpenMvAsync(CatalogItem item)
    {
        if (_dialog != null) return;
        try
        {
            var result = await _api.CallAsync("mv_url", new { id = item.Id });
            if (!Uri.TryCreate(Object(result, "data").Text("url"), UriKind.Absolute, out var uri)) { ShowError("此 MV 暂不可播放"); return; }
            using var player = new MediaPlayer(); using var source = MediaSource.CreateFromUri(uri);
            var resume = _playback.IsPlaying; if (resume) _playback.Toggle();
            var video = new MediaPlayerElement { AreTransportControlsEnabled = true, Width = Math.Min(700, Root.ActualWidth - 160), Height = Math.Min(394, Root.ActualHeight - 200) }; video.SetMediaPlayer(player); player.Source = source;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = item.Name, Content = video, CloseButtonText = "关闭" }; _dialog = dialog;
            dialog.Opened += (_, _) => player.Play();
            try { await ShowThemedDialogAsync(dialog); }
            finally { player.Pause(); player.Source = null; video.SetMediaPlayer(null); _dialog = null; if (resume) _playback.Toggle(); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
