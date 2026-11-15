using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using System.Text.Json;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _songDetailLoad;

    private async Task<Track> ResolveSongDetailsAsync(Track track, ApiClient api, CancellationToken token)
    {
        var id = OnlineSongId(track);
        if (id <= 0) return track;
        var result = await api.CallCachedAsync("song_detail", new { ids = id.ToString() }, token);
        token.ThrowIfCancellationRequested();
        return result.GetProperty("songs").EnumerateArray().Where(s => s.Number("id") == id).Select(Track.FromJson).FirstOrDefault() ?? track;
    }

    private async Task NavigateToArtistAsync(SongArtist artist)
    {
        _artistId = artist.Id; _playlistName = artist.Name; await NavigateAsync("artist");
    }

    private async Task ShowTrackArtistsAsync(Track track)
    {
        if (_dialog != null || _hidden) return;
        _songDetailLoad?.Cancel();
        using var load = new CancellationTokenSource(); _songDetailLoad = load;
        try
        {
            if (track.Artists.Length == 0 && OnlineSongId(track) > 0) track = await ResolveSongDetailsAsync(track, _api, load.Token);
            if (_hidden || load.IsCancellationRequested) return;
            var artists = track.Artists.Where(a => a.Id > 0).DistinctBy(a => a.Id).ToArray();
            if (artists.Length == 0 && track.ArtistId > 0) artists = [new(track.ArtistId, track.Artist)];
            if (artists.Length == 1) { await NavigateToArtistAsync(artists[0]); return; }
            if (artists.Length == 0) { ShowPlayerMessage("暂无可查看的歌手信息"); return; }
            SongArtist? selected = null;
            var content = new StackPanel { Spacing = 8, MinWidth = 240 };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "选择歌手", Content = content, CloseButtonText = "关闭" };
            foreach (var artist in artists)
            {
                var button = new Button { Content = artist.Name, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
                button.Click += (_, _) => { selected = artist; dialog.Hide(); }; content.Children.Add(button);
            }
            _dialog = dialog;
            try { await dialog.ShowAsync(); } finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
            if (selected != null && !_hidden) await NavigateToArtistAsync(selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (ReferenceEquals(_songDetailLoad, load)) _songDetailLoad = null; }
    }

    private async void SongInfo_Click(object sender, RoutedEventArgs args)
    {
        if (_playback.Current is { } track) await ShowSongDetailsAsync(track, SongInfoApiOverride ?? _api);
    }

    private async Task ShowSongDetailsAsync(Track track, ApiClient api)
    {
        if (_dialog != null || _hidden) return;
        _songDetailLoad?.Cancel();
        using var load = new CancellationTokenSource(); _songDetailLoad = load;
        var content = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 480 };
        var status = new TextBlock { Text = "正在读取…", TextWrapping = TextWrapping.Wrap };
        content.Children.Add(status);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "歌曲信息", Content = new ScrollViewer { Content = content, MaxHeight = 480 }, CloseButtonText = "关闭" };
        long albumId = 0; string albumName = ""; SongArtist? selectedArtist = null;
        void Render(Track detail)
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock { Text = detail.Title, FontSize = 22, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = detail.Subtitle, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "时长  " + detail.DurationText + "\n音质  " + (_playback.Current?.Id == track.Id ? AudioQuality.Name(_playback.CurrentQuality) : AudioQuality.Name(track.SourceLevel)), TextWrapping = TextWrapping.Wrap });
            if (detail.AlbumId > 0)
            {
                var album = new Button { Content = "查看专辑：" + detail.Album, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
                album.Click += (_, _) => { albumId = detail.AlbumId; albumName = detail.Album; dialog.Hide(); }; content.Children.Add(album);
            }
            foreach (var artist in detail.Artists.Where(a => a.Id > 0).DistinctBy(a => a.Id))
            {
                var button = new Button { Content = artist.Name, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Click += (_, _) => { selectedArtist = artist; dialog.Hide(); }; content.Children.Add(button);
            }
            if (track.IsLocal) content.Children.Add(new TextBlock { Text = track.LocalPath, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        async Task Fetch()
        {
            try { var detail = await ResolveSongDetailsAsync(track, api, load.Token); if (!load.IsCancellationRequested && !_hidden) Render(detail); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!load.IsCancellationRequested && !_hidden) { Render(track); content.Children.Add(new TextBlock { Text = "网络信息加载失败：" + ex.Message, TextWrapping = TextWrapping.Wrap }); } }
        }
        _dialog = dialog;
        var showing = dialog.ShowAsync(); var fetching = Fetch();
        try { await showing; }
        finally { load.Cancel(); await fetching; if (ReferenceEquals(_dialog, dialog)) _dialog = null; if (ReferenceEquals(_songDetailLoad, load)) _songDetailLoad = null; }
        if (_hidden) return;
        if (albumId > 0) { _playlistId = albumId; _playlistName = albumName; await NavigateAsync("album"); }
        else if (selectedArtist != null) await NavigateToArtistAsync(selectedArtist);
    }
}
