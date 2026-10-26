using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXTMobile.Services;
using Windows.Storage.Streams;

namespace NetEaseNEXTMobile;

public sealed partial class MainPage : Page
{
    private readonly BridgeClient _api = new();
    private readonly AndroidPlayback _playback = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly ObservableCollection<Track> _tracks = new();
    private CancellationTokenSource? _polling;
    private bool _settingSlider;
    private bool _loading;

    public MainPage()
    {
        InitializeComponent();
        PlaylistList.ItemsSource = _playlists;
        TrackList.ItemsSource = _tracks;
        _clock.Tick += (_, _) => UpdatePlayback();
        Unloaded += (_, _) =>
        {
            _polling?.Cancel();
            _clock.Stop();
            _playback.Dispose();
        };
    }

    private static string TimeLabel(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        _polling?.Cancel();
        _polling = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = _polling.Token;
        LoginButton.IsEnabled = false;
        LoginStatusText.Text = "正在获取登录二维码…";
        QrImage.Visibility = Visibility.Collapsed;
        try
        {
            var response = await _api.RequestAsync("login_start");
            ct.ThrowIfCancellationRequested();
            var text = response.GetProperty("qrimg").GetString() ?? "";
            var comma = text.IndexOf(',');
            var bytes = Convert.FromBase64String(text[(comma + 1)..]);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            ct.ThrowIfCancellationRequested();
            QrImage.Source = bitmap;
            QrImage.Visibility = Visibility.Visible;
            LoginStatusText.Text = "请扫码并在网易云音乐中确认。";
            LoginButton.Content = "刷新二维码";

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var poll = await _api.RequestAsync("login_poll");
                ct.ThrowIfCancellationRequested();
                var code = poll.GetProperty("code").GetInt32();
                switch (code)
                {
                    case 800:
                        LoginStatusText.Text = "二维码已过期，请刷新。";
                        return;
                    case 802:
                        LoginStatusText.Text = "已扫码，请在网易云音乐中确认。";
                        break;
                    case 803:
                        _polling?.Cancel();
                        var profile = poll.TryGetProperty("profile", out var p) ? p : default;
                        if (profile.ValueKind != System.Text.Json.JsonValueKind.Object)
                        {
                            var status = await _api.RequestAsync("login_status");
                            profile = status.GetProperty("profile");
                        }
                        await OnLoggedInAsync(profile);
                        return;
                }
            }
        }
        catch (OperationCanceledException) { LoginStatusText.Text = "扫码已取消或超时，请刷新二维码。"; }
        catch (Exception ex) { LoginStatusText.Text = "登录失败：" + ex.Message; }
        finally { LoginButton.IsEnabled = true; }
    }

    private async Task OnLoggedInAsync(System.Text.Json.JsonElement profile)
    {
        AccountText.Text = profile.TryGetProperty("nickname", out var nickname)
            ? nickname.GetString() ?? "网易云音乐" : "网易云音乐";
        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        StatusText.Text = "正在加载我的歌单…";
        try
        {
            var data = await _api.RequestAsync("my_playlists");
            _playlists.Clear();
            foreach (var item in data.GetProperty("playlists").EnumerateArray())
                _playlists.Add(new Playlist(
                    item.GetProperty("id").GetInt64(),
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("cover").GetString() ?? "",
                    ""));
            StatusText.Text = _playlists.Count == 0 ? "当前账号没有歌单。" : "请选择歌单，再点选一首歌曲播放。";
            if (_playlists.Count > 0) PlaylistList.SelectedIndex = 0;
        }
        catch (Exception ex) { StatusText.Text = "歌单加载失败：" + ex.Message; }
    }

    private async void Playlist_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        TrackList.SelectedItem = null;
        _tracks.Clear();
        StatusText.Text = "正在加载歌单歌曲…";
        try
        {
            var data = await _api.RequestAsync("playlist_tracks", new { id = playlist.Id });
            if (PlaylistList.SelectedItem is not Playlist current || current.Id != playlist.Id) return;
            foreach (var song in data.GetProperty("songs").EnumerateArray())
                _tracks.Add(Track.FromJson(song));
            StatusText.Text = _tracks.Count == 0 ? "这个歌单没有歌曲。" : "点击歌曲即可播放。";
        }
        catch (Exception ex) { StatusText.Text = "加载歌曲失败：" + ex.Message; }
    }

    private async void Track_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TrackList.SelectedItem is not Track track || _loading) return;
        _loading = true;
        StatusText.Text = "正在获取播放地址…";
        try
        {
            var info = await _api.RequestAsync("play_url", new { id = track.Id });
            var url = info.GetProperty("url").GetString();
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("无可用播放地址");
            StatusText.Text = "正在缓冲…";
            await _playback.PlayAsync(url);
            TitleText.Text = track.Title;
            ArtistText.Text = track.Subtitle;
            CoverImage.Source = Uri.TryCreate(track.Cover, UriKind.Absolute, out var cover)
                ? new BitmapImage(cover) : null;
            PlayerPanel.Visibility = Visibility.Visible;
            StatusText.Text = "正在播放";
            _clock.Start();
            UpdatePlayback();
        }
        catch (Exception ex) { StatusText.Text = "播放失败：" + ex.Message; }
        finally { _loading = false; }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _polling?.Cancel();
        _clock.Stop();
        _playback.Stop();
        try { await _api.RequestAsync("logout"); }
        catch (Exception ex) { StatusText.Text = "退出时无法连接电脑端：" + ex.Message; }
        _tracks.Clear();
        _playlists.Clear();
        PlayerPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        QrImage.Visibility = Visibility.Collapsed;
        LoginStatusText.Text = "已退出登录，请重新获取二维码。";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _playback.Toggle();
            StatusText.Text = _playback.IsPlaying ? "正在播放" : "已暂停";
            UpdatePlayback();
        }
        catch (Exception ex) { StatusText.Text = "播放失败：" + ex.Message; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _playback.Stop();
        _clock.Stop();
        PlayerPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "已停止";
        UpdatePlayback();
    }

    private void Position_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingSlider || !_playback.HasAudio) return;
        _playback.SeekTo((int)e.NewValue);
    }

    private void UpdatePlayback()
    {
        _settingSlider = true;
        try
        {
            var duration = _playback.DurationMs;
            PositionSlider.Maximum = Math.Max(1, duration);
            PositionSlider.Value = Math.Min(duration, _playback.PositionMs);
            PositionText.Text = TimeLabel(_playback.PositionMs);
            DurationText.Text = TimeLabel(duration);
        }
        finally { _settingSlider = false; }
    }
}
