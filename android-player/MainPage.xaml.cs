using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXTMobile.Services;

namespace NetEaseNEXTMobile;

public sealed partial class MainPage : Page
{
    private readonly BridgeClient _api = new();
    private readonly AndroidPlayback _playback = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _settingSlider;
    private bool _loading;

    public MainPage()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdatePlayback();
        Unloaded += (_, _) =>
        {
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

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!long.TryParse(SongIdBox.Text.Trim(), out var id) || id <= 0)
        {
            StatusText.Text = "请输入有效的网易云音乐歌曲 ID。";
            return;
        }

        _loading = true;
        LoadButton.IsEnabled = false;
        StatusText.Text = "正在查询歌曲与播放地址…";
        try
        {
            var detail = await _api.CallAsync("song_detail", new { ids = id.ToString() });
            var songs = detail.GetProperty("songs");
            if (songs.GetArrayLength() == 0)
                throw new InvalidOperationException("找不到这首歌曲。");
            var track = Track.FromJson(songs[0]);

            var result = await _api.CallAsync("song_url_v1", new
            {
                id = id.ToString(),
                level = "standard"
            });
            var data = result.GetProperty("data");
            if (data.GetArrayLength() == 0)
                throw new InvalidOperationException("没有返回歌曲播放信息。");
            var url = data[0].Text("url");
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("无法播放：可能受版权或账号权限限制。");

            StatusText.Text = "正在缓冲音频…";
            await _playback.PlayAsync(url);
            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist + " - " + track.Album;
            if (Uri.TryCreate(track.Cover, UriKind.Absolute, out var cover))
                CoverImage.Source = new BitmapImage(cover);
            ToggleButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            StatusText.Text = "正在播放";
            _clock.Start();
            UpdatePlayback();
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
            LoadButton.IsEnabled = true;
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _playback.Toggle();
            StatusText.Text = _playback.IsPlaying ? "正在播放" : "已暂停";
            UpdatePlayback();
        }
        catch (Exception ex)
        {
            StatusText.Text = "播放失败：" + ex.Message;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _playback.Stop();
        _clock.Stop();
        ToggleButton.IsEnabled = false;
        StopButton.IsEnabled = false;
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
        finally
        {
            _settingSlider = false;
        }
    }
}
