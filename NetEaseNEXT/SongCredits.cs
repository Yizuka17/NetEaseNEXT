using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _songCreditsLoad;
    private Track? _creditsTrack, _creditsDetail;
    private long _creditsSourceId;
    private bool _creditsResolved;

    private static SongArtist[] TrackArtists(Track track)
    {
        var artists = track.Artists.Where(a => a.Name.Length > 0).DistinctBy(a => (a.Id, a.Name)).ToArray();
        if (artists.Length > 0) return artists;
        return track.Artist.Length == 0 ? [] : [new(track.Artist.Contains(" / ") ? 0 : track.ArtistId, track.Artist)];
    }
    private async Task NavigateToAlbumAsync(long id, string name)
    {
        if (_hidden || id <= 0) return;
        _playlistId = id; _playlistName = name; await NavigateAsync("album");
    }
    private void RenderSongCredits(TextBlock text, Track detail, bool links)
    {
        text.Inlines.Clear();
        void Name(string name, Func<Task>? navigate)
        {
            if (navigate == null) { text.Inlines.Add(new Run { Text = name }); return; }
            var link = new Hyperlink { TextDecorations = Windows.UI.Text.TextDecorations.None };
            link.Inlines.Add(new Run { Text = name });
            link.Click += async (_, _) =>
            {
                try { if (!_hidden) await navigate(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            text.Inlines.Add(link);
        }
        var artists = TrackArtists(detail);
        for (var i = 0; i < artists.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new Run { Text = " / " });
            var artist = artists[i]; Name(artist.Name, links && artist.Id > 0 ? () => NavigateToArtistAsync(artist) : null);
        }
        if (artists.Length > 0 && detail.Album.Length > 0) text.Inlines.Add(new Run { Text = " - " });
        Name(detail.Album, links && detail.AlbumId > 0 ? () => NavigateToAlbumAsync(detail.AlbumId, detail.Album) : null);
    }
    private void RefreshSongCredits(bool reload = false)
    {
        if (_hidden || _exiting || _playback.Current is not { } track) return;
        var id = OnlineSongId(track);
        if (reload || !ReferenceEquals(_creditsTrack, track) || _creditsSourceId != id)
        {
            _songCreditsLoad?.Cancel(); _songCreditsLoad = null;
            _creditsTrack = track; _creditsDetail = track; _creditsSourceId = id; _creditsResolved = false;
        }
        RenderSongCredits(MiniArtist, _creditsDetail ?? track, id > 0);
        RenderSongCredits(NowArtist, _creditsDetail ?? track, id > 0);
        if (id <= 0 || track.PodcastId > 0 || _creditsResolved || _songCreditsLoad != null || track.Artists.Length > 0 && track.AlbumId > 0) return;
        _ = LoadSongCreditsAsync(track, id);
    }
    private async Task LoadSongCreditsAsync(Track track, long id)
    {
        using var load = new CancellationTokenSource(); _songCreditsLoad = load;
        try
        {
            var detail = await ResolveSongDetailsAsync(track, SongInfoApiOverride ?? _api, load.Token);
            if (load.IsCancellationRequested || _hidden || !ReferenceEquals(_creditsTrack, track) || _playback.Current?.Id != track.Id || OnlineSongId(track) != id) return;
            _creditsDetail = detail; _creditsResolved = true;
            RenderSongCredits(MiniArtist, detail, true); RenderSongCredits(NowArtist, detail, true);
        }
        catch (OperationCanceledException) { }
        catch { /* Keep file tags readable when online metadata is unavailable. */ }
        finally { if (ReferenceEquals(_songCreditsLoad, load)) _songCreditsLoad = null; }
    }
    private async Task OpenTrackAlbumAsync(Track track)
    {
        if (_hidden || _dialog != null || OnlineSongId(track) <= 0) return;
        _songDetailLoad?.Cancel(); using var load = new CancellationTokenSource(); _songDetailLoad = load; var route = _route;
        try
        {
            var detail = track.AlbumId > 0 ? track : await ResolveSongDetailsAsync(track, SongInfoApiOverride ?? _api, load.Token);
            if (load.IsCancellationRequested || _hidden || !ReferenceEquals(route, _route)) return;
            if (detail.AlbumId > 0) await NavigateToAlbumAsync(detail.AlbumId, detail.Album);
            else ShowPlayerMessage("暂无可查看的专辑信息");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (ReferenceEquals(_songDetailLoad, load)) _songDetailLoad = null; }
    }
}
