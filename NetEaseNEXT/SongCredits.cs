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
    private long CreditSourceId(Track track) => !track.IsLocal ? track.Id : _settings.LocalOnlineInfo ? SourceSongId(track) : 0;

    private static SongArtist[] TrackArtists(Track track)
    {
        var artists = track.Artists.Where(a => a.Name.Length > 0).DistinctBy(a => (a.Id, a.Name)).ToArray();
        if (artists.Length > 0) return artists;
        var names = track.Artist.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Select(name => new SongArtist(names.Length == 1 ? track.ArtistId : 0, name)).ToArray();
    }
    private async Task NavigateToAlbumAsync(long id, string name)
    {
        if (_hidden || id <= 0) return;
        _playlistId = id; _playlistName = name; await NavigateAsync("album");
    }
    private static void CreditName(TextBlock text, string name, Func<Task>? navigate)
    {
        if (navigate == null) { text.Inlines.Add(new Run { Text = name }); return; }
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None, TextDecorations = Windows.UI.Text.TextDecorations.None };
        link.Inlines.Add(new Run { Text = name });
        link.Click += async (_, _) => await navigate(); text.Inlines.Add(link);
    }
    private void RenderSongCredits(TextBlock text, Track track, Track detail)
    {
        text.Inlines.Clear(); var links = CreditSourceId(track) > 0;
        var artists = TrackArtists(detail);
        for (var i = 0; i < artists.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new Run { Text = " / " });
            var artist = artists[i]; CreditName(text, artist.Name, links ? () => OpenTrackCreditAsync(track, artist) : null);
        }
        if (artists.Length > 0 && detail.Album.Length > 0) text.Inlines.Add(new Run { Text = " - " });
        CreditName(text, detail.Album, links && detail.Album.Length > 0 ? () => OpenTrackCreditAsync(track) : null);
    }
    private void RefreshSongCredits(bool reload = false)
    {
        if (_hidden || _exiting || _playback.Current is not { } track) return;
        var id = CreditSourceId(track);
        if (reload || !ReferenceEquals(_creditsTrack, track) || _creditsSourceId != id)
        {
            _songCreditsLoad?.Cancel(); _songCreditsLoad = null;
            _creditsTrack = track; _creditsDetail = track; _creditsSourceId = id;
        }
        RenderSongCredits(MiniArtist, track, _creditsDetail ?? track);
        RenderSongCredits(NowArtist, track, _creditsDetail ?? track);
    }
    private async Task OpenTrackCreditAsync(Track track, SongArtist? artist = null)
    {
        if (_hidden || _dialog != null || CreditSourceId(track) <= 0 || _songCreditsLoad != null) return;
        using var load = new CancellationTokenSource(); _songCreditsLoad = load; var route = _route; var id = CreditSourceId(track);
        try
        {
            var detail = ReferenceEquals(_creditsTrack, track) ? _creditsDetail ?? track : track;
            if (artist == null ? detail.AlbumId <= 0 : artist.Id <= 0)
                detail = await ResolveSongDetailsAsync(track, SongInfoApiOverride ?? _api, load.Token);
            if (load.IsCancellationRequested || _hidden || !ReferenceEquals(route, _route) || CreditSourceId(track) != id) return;
            if (ReferenceEquals(_creditsTrack, track)) { _creditsDetail = detail; RefreshSongCredits(); }
            if (artist == null)
            {
                if (detail.AlbumId > 0) await NavigateToAlbumAsync(detail.AlbumId, detail.Album);
                else ShowPlayerMessage("暂无可查看的专辑信息");
            }
            else
            {
                // Resolve the clicked name only; never send every singer to the first ID.
                var target = artist.Id > 0 ? artist : TrackArtists(detail).FirstOrDefault(a => a.Id > 0 && string.Equals(a.Name, artist.Name, StringComparison.OrdinalIgnoreCase));
                if (target != null) await NavigateToArtistAsync(target);
                else ShowPlayerMessage("暂无可查看的歌手信息");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (ReferenceEquals(_songCreditsLoad, load)) _songCreditsLoad = null; }
    }
    private async Task OpenTrackAlbumAsync(Track track)
    {
        if (_hidden || _dialog != null || CreditSourceId(track) <= 0) return;
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
