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
    private readonly MobileMusicClient _api = new();
    private readonly AndroidPlayback _player = new();
    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly ObservableCollection<Track> _tracks = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _qrSession;
    private CancellationTokenSource _signedInSession = new();
    private bool _isUpdatingSlider, _loadingSong;

    public MainPage()
    {
        InitializeComponent();
        PlaylistList.ItemsSource = _playlists;
        TrackList.ItemsSource = _tracks;
        _timer.Tick += (_, _) => RefreshTimeline();
        Unloaded += (_, _) =>
        {
            _qrSession?.Cancel();
            _signedInSession.Cancel();
            _timer.Stop();
            _player.Dispose();
            _api.Dispose();
        };
    }

    private static string TimeText(int millis)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, millis));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        _qrSession?.Cancel();
        _qrSession = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = _qrSession.Token;
        LoginButton.IsEnabled = false;
        QrImage.Visibility = Visibility.Collapsed;
        LoginStatusText.Text = "正在获取二维码…";
        try
        {
            var (key, image) = await _api.CreateQrAsync(token);
            token.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(image);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            token.ThrowIfCancellationRequested();
            QrImage.Source = bitmap;
            QrImage.Visibility = Visibility.Visible;
            LoginStatusText.Text = "请扫码并在网易云音乐中确认。";
            LoginButton.Content = "刷新二维码";

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                var (code, profile) = await _api.CheckQrAsync(key, token);
                token.ThrowIfCancellationRequested();
                if (code == 800) { LoginStatusText.Text = "二维码过期，请刷新。"; break; }
                if (code == 802) LoginStatusText.Text = "已经扫码，等待确认…";
                if (code == 803 && profile.HasValue)
                {
                    _qrSession?.Cancel();
                    await SignedInAsync(profile.Value);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { LoginStatusText.Text = "二维码已取消或超时，请刷新。"; }
        catch (Exception ex) { LoginStatusText.Text = "登录失败：" + ex.Message; }
        finally { LoginButton.IsEnabled = true; }
    }

    private async Task SignedInAsync(System.Text.Json.JsonElement profile)
    {
        AccountText.Text = profile.Text("nickname");
        AccountAvatar.DisplayName = AccountText.Text;
        if (Uri.TryCreate(profile.Text("avatarUrl"), UriKind.Absolute, out var avatar))
            AccountAvatar.ProfilePicture = new BitmapImage(avatar);
        LogoutButton.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        StatusText.Text = "正在加载我的歌单…";
        try
        {
            var list = await _api.MyPlaylistsAsync(profile.Number("userId"), _signedInSession.Token);
            _playlists.Clear();
            foreach (var item in list) _playlists.Add(item);
            StatusText.Text = list.Count > 0 ? "选择歌单，再点选歌曲播放。" : "当前账号没有歌单。";
            if (_playlists.Count > 0) PlaylistList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "歌单加载失败：" + ex.Message; }
    }

    private async void Playlist_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist selected) return;
        TrackList.SelectedItem = null;
        _tracks.Clear();
        StatusText.Text = "正在加载歌单歌曲…";
        try
        {
            var list = await _api.PlaylistTracksAsync(selected.Id, _signedInSession.Token);
            if (PlaylistList.SelectedItem is not Playlist current || current.Id != selected.Id) return;
            foreach (var item in list) _tracks.Add(item);
            StatusText.Text = list.Count > 0 ? "点击歌曲直接播放。" : "歌单为空。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "获取歌曲失败：" + ex.Message; }
    }

    private async void Track_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSong || TrackList.SelectedItem is not Track song) return;
        _loadingSong = true;
        StatusText.Text = "正在获取播放地址…";
        try
        {
            var url = await _api.GetStreamUrlAsync(song.Id, _signedInSession.Token);
            StatusText.Text = "正在缓冲音频…";
            await _player.PlayAsync(url);
            TitleText.Text = song.Title;
            ArtistText.Text = song.Subtitle;
            CoverImage.Source = Uri.TryCreate(song.Cover, UriKind.Absolute, out var cover)
                ? new BitmapImage(cover) : null;
            PlayerPanel.Visibility = Visibility.Visible;
            StatusText.Text = "正在播放";
            ToggleButton.Content = "暂停";
            _timer.Start();
            RefreshTimeline();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "播放失败：" + ex.Message; }
        finally { _loadingSong = false; }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _qrSession?.Cancel();
        _signedInSession.Cancel();
        _signedInSession = new CancellationTokenSource();
        _timer.Stop();
        _player.Stop();
        _api.SignOut();
        _playlists.Clear();
        _tracks.Clear();
        PlayerPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        LogoutButton.Visibility = Visibility.Collapsed;
        AccountAvatar.ProfilePicture = null;
        QrImage.Visibility = Visibility.Collapsed;
        LoginStatusText.Text = "已退出登录。";
        StatusText.Text = "";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _player.Toggle();
            StatusText.Text = _player.IsPlaying ? "正在播放" : "已暂停";
            ToggleButton.Content = _player.IsPlaying ? "暂停" : "播放";
            RefreshTimeline();
        }
        catch (Exception ex) { StatusText.Text = "播放器错误：" + ex.Message; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _timer.Stop();
        PlayerPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "已停止";
        RefreshTimeline();
    }

    private void Position_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingSlider && _player.HasAudio) _player.SeekTo((int)e.NewValue);
    }

    private void RefreshTimeline()
    {
        _isUpdatingSlider = true;
        try
        {
            var duration = _player.DurationMs;
            PositionSlider.Maximum = Math.Max(1, duration);
            PositionSlider.Value = Math.Min(duration, _player.PositionMs);
            PositionText.Text = TimeText(_player.PositionMs);
            DurationText.Text = TimeText(duration);
        }
        finally { _isUpdatingSlider = false; }
    }
}
