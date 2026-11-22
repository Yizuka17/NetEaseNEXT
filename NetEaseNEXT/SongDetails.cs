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
        if (_hidden || artist.Id <= 0) return;
        _artistId = artist.Id; _playlistName = artist.Name; await NavigateAsync("artist");
    }
    private static Grid SongDetailHeading(string title, string closeName, out Button close)
    {
        close = new Button { Content = IconFonts.Action("\uE711"), Style = (Style)Application.Current.Resources["BareButtonStyle"], UseSystemFocusVisuals = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, closeName);
        var heading = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(close, 1); heading.Children.Add(close); return heading;
    }

    private async Task ShowTrackArtistsAsync(Track track)
    {
        if (_dialog != null || _hidden || OnlineSongId(track) <= 0) return;
        _songDetailLoad?.Cancel();
        using var load = new CancellationTokenSource(); _songDetailLoad = load; var route = _route;
        try
        {
            if (track.Artists.Length == 0) track = await ResolveSongDetailsAsync(track, SongInfoApiOverride ?? _api, load.Token);
            if (_hidden || load.IsCancellationRequested || !ReferenceEquals(route, _route)) return;
            var artists = TrackArtists(track).Where(a => a.Id > 0).ToArray();
            if (artists.Length == 1) { await NavigateToArtistAsync(artists[0]); return; }
            if (artists.Length == 0) { ShowPlayerMessage("暂无可查看的歌手信息"); return; }
            SongArtist? selected = null;
            var content = new StackPanel { Spacing = 8, MinWidth = 240 };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = content };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, "歌手");
            content.Children.Add(SongDetailHeading("歌手", "关闭歌手列表", out var close)); close.Click += (_, _) => dialog.Hide();
            foreach (var artist in artists)
            {
                var button = new HyperlinkButton { Content = artist.Name, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(0), MinHeight = 32 };
                button.Click += (_, _) => { selected = artist; dialog.Hide(); }; content.Children.Add(button);
            }
            _dialog = dialog;
            try { await ShowThemedDialogAsync(dialog); } finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
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
        var body = new Grid { RowSpacing = 12, Width = Math.Max(240, Math.Min(480, Root.ActualWidth - 120)) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(SongDetailHeading("歌曲信息", "关闭歌曲信息", out var close));
        var scroll = new ScrollViewer { Content = content, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = body };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, "歌曲信息");
        close.Click += (_, _) => dialog.Hide();
        long albumId = 0; string albumName = ""; SongArtist? selectedArtist = null;
        void Render(Track detail)
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock { Text = detail.Title, FontSize = 22, TextWrapping = TextWrapping.Wrap });
            var online = OnlineSongId(track) > 0;
            var artists = new Controls.WrapPanel { Spacing = 4 };
            artists.Children.Add(new TextBlock { Text = "歌手：", VerticalAlignment = VerticalAlignment.Center });
            foreach (var artist in TrackArtists(detail))
            {
                if (artists.Children.Count > 1) artists.Children.Add(new TextBlock { Text = "/", Opacity = .6, VerticalAlignment = VerticalAlignment.Center });
                if (online && artist.Id > 0)
                {
                    var link = new HyperlinkButton { Content = artist.Name, Padding = new(0), MinHeight = 32 };
                    link.Click += (_, _) => { selectedArtist = artist; dialog.Hide(); }; artists.Children.Add(link);
                }
                else artists.Children.Add(new TextBlock { Text = artist.Name, VerticalAlignment = VerticalAlignment.Center });
            }
            content.Children.Add(artists);
            var album = new Controls.WrapPanel { Spacing = 4 };
            album.Children.Add(new TextBlock { Text = "专辑：", VerticalAlignment = VerticalAlignment.Center });
            if (online && detail.AlbumId > 0)
            {
                var link = new HyperlinkButton { Content = detail.Album, Padding = new(0), MinHeight = 32 };
                link.Click += (_, _) => { albumId = detail.AlbumId; albumName = detail.Album; dialog.Hide(); }; album.Children.Add(link);
            }
            else album.Children.Add(new TextBlock { Text = detail.Album, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(album);
            content.Children.Add(new TextBlock { Text = "时长  " + detail.DurationText + "\n音质  " + (_playback.Current?.Id == track.Id ? AudioQuality.Name(_playback.CurrentQuality) : AudioQuality.Name(track.SourceLevel)), TextWrapping = TextWrapping.Wrap });
            if (track.IsLocal) content.Children.Add(new TextBlock { Text = track.LocalPath, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        async Task Fetch()
        {
            try { var detail = await ResolveSongDetailsAsync(track, api, load.Token); if (!load.IsCancellationRequested && !_hidden) Render(detail); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!load.IsCancellationRequested && !_hidden) { Render(track); content.Children.Add(new TextBlock { Text = "网络信息加载失败：" + ex.Message, TextWrapping = TextWrapping.Wrap }); } }
        }
        _dialog = dialog;
        var showing = ShowThemedDialogAsync(dialog); var fetching = Fetch();
        try { await showing; }
        finally { load.Cancel(); await fetching; if (ReferenceEquals(_dialog, dialog)) _dialog = null; if (ReferenceEquals(_songDetailLoad, load)) _songDetailLoad = null; }
        if (_hidden) return;
        if (albumId > 0) { _playlistId = albumId; _playlistName = albumName; await NavigateAsync("album"); }
        else if (selectedArtist != null) await NavigateToArtistAsync(selectedArtist);
    }
}
