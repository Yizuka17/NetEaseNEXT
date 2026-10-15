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
    private void MoveArtistHeader(Panel? destination = null)
    {
        destination ??= ArtistPanel;
        if (destination.Children.Contains(ArtistHeader)) return;
        if (ReferenceEquals(CatalogGrid.Header, ArtistHeader)) CatalogGrid.Header = null;
        ArtistPanel.Children.Remove(ArtistHeader); ArtistBioBody.Children.Remove(ArtistHeader);
        ((Panel)TrackList.Header).Children.Remove(ArtistHeader);
        destination.Children.Insert(0, ArtistHeader);
    }
    private void RestoreArtistContent()
    {
        MoveArtistHeader();
        foreach (var element in new FrameworkElement[] { TracksPanel, CatalogPanel })
        {
            if (ArtistContentHost.Children.Contains(element)) { ArtistContentHost.Children.Remove(element); PageBody.Children.Insert(0, element); }
        }
        ArtistBioScroll.Visibility = Visibility.Collapsed;
    }
    private void HostArtistContent(FrameworkElement element)
    {
        if (!ArtistContentHost.Children.Contains(element))
        { RestoreArtistContent(); PageBody.Children.Remove(element); ArtistContentHost.Children.Add(element); }
        if (ReferenceEquals(element, TracksPanel)) MoveArtistHeader((Panel)TrackList.Header);
        else
        {
            ArtistPanel.Children.Remove(ArtistHeader); ArtistBioBody.Children.Remove(ArtistHeader);
            ((Panel)TrackList.Header).Children.Remove(ArtistHeader); CatalogGrid.Header = ArtistHeader;
        }
        element.Visibility = Visibility.Visible;
    }
    private async Task LoadArtistAsync(CancellationToken ct)
    {
        JsonElement songs = default;
        if (_artistHeaderId != _artistId || _artistTab == "songs")
        {
            var data = await _api.CallAsync("artists", new { id = _artistId }, ct); ct.ThrowIfCancellationRequested();
            var artist = Object(data, "artist"); _artistHeaderId = _artistId;
            ArtistName.Text = FirstText(artist.Text("name"), _playlistName);
            ArtistAlias.Text = artist.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.Array ? string.Join(" / ", alias.EnumerateArray().Select(a => a.GetString())) : "";
            ArtistSongCount.Text = "单曲数：" + artist.Number("musicSize"); ArtistAlbumCount.Text = "专辑数：" + artist.Number("albumSize");
            Artwork.SetUri(ArtistCover, FirstText(artist.Text("picUrl"), artist.Text("cover")));
            ArtistBio.Text = artist.Text("briefDesc");
            data.TryGetProperty("hotSongs", out songs);
        }
        TracksPanel.Visibility = CatalogPanel.Visibility = ArtistBioScroll.Visibility = Visibility.Collapsed;
        if (_artistTab == "songs")
        {
            // Stabilize the visual parent before rows start their artwork requests.
            HostArtistContent(TracksPanel);
            var tracks = songs.ValueKind == JsonValueKind.Array ? songs.EnumerateArray().Take(50).Select(Track.FromJson).ToList() : [];
            var missing = tracks.Where(t => t.Cover.Length == 0).Select(t => t.Id).ToArray();
            if (missing.Length > 0)
            {
                var details = await _api.CallAsync("song_detail", new { ids = string.Join(',', missing) }, ct); ct.ThrowIfCancellationRequested();
                var replacements = details.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToDictionary(t => t.Id);
                tracks = tracks.Select(t => replacements.GetValueOrDefault(t.Id, t)).ToList();
            }
            Tracks.Clear(); foreach (var track in tracks) Tracks.Add(track);
            PlaylistDetails.Visibility = PlaylistSubscribeButton.Visibility = PlaylistCommentsButton.Visibility = EditPlaylistButton.Visibility = DeletePlaylistButton.Visibility = Visibility.Collapsed;
            TrackCount.Text = $"{Tracks.Count} 首"; LoadMoreButton.Visibility = Visibility.Collapsed;
        }
        else if (_artistTab is "albums" or "mvs")
        {
            HostArtistContent(CatalogPanel);
            var result = await _api.CallAsync(_artistTab == "albums" ? "artist_album" : "artist_mv", new { id = _artistId, limit = 48, offset = _offset }, ct); ct.ThrowIfCancellationRequested();
            var array = result.GetProperty(_artistTab == "albums" ? "hotAlbums" : "mvs");
            foreach (var item in array.EnumerateArray()) Catalog.Add(_artistTab == "albums" ? CatalogItem.Album(item) : new CatalogItem(item.Number("id"), item.Text("name"), FirstText(item.Text("imgurl"), item.Text("cover")), "mv"));
            _offset += array.GetArrayLength(); CatalogMore.Visibility = array.GetArrayLength() == 48 && Catalog.Count < 1000 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            RestoreArtistContent();
            MoveArtistHeader(ArtistBioBody);
            var result = await _api.CallAsync("artist_desc", new { id = _artistId }, ct); ct.ThrowIfCancellationRequested();
            var sections = new List<string> { result.Text("briefDesc") };
            if (result.TryGetProperty("introduction", out var intro) && intro.ValueKind == JsonValueKind.Array) sections.AddRange(intro.EnumerateArray().Select(i => i.Text("ti") + "\n" + i.Text("txt")));
            ArtistBio.Text = string.Join("\n\n", sections.Where(s => s.Length > 0)); ArtistBioScroll.Visibility = Visibility.Visible;
        }
    }
    private async void ArtistTab_Click(object sender, RoutedEventArgs args)
    {
        if (_view != "artist" || (sender as FrameworkElement)?.Tag is not string tab) return;
        _artistTab = tab; _offset = 0; Catalog.Clear(); Tracks.Clear(); await LoadPageAsync(false);
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
            try { await dialog.ShowAsync(); }
            finally { player.Pause(); player.Source = null; video.SetMediaPlayer(null); _dialog = null; if (resume) _playback.Toggle(); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
