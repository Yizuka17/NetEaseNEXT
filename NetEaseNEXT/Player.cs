using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Automation;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.System;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _downloads = new();
    private readonly HashSet<long> _activeDownloads = [];
    private int _maxQuality;
    private long _qualityTrack;
    private static readonly (string Level, string Name)[] Qualities = [("standard", "标准"), ("higher", "较高"), ("exhigh", "极高"), ("lossless", "无损"), ("hires", "Hi-Res"), ("jyeffect", "高清臻音"), ("sky", "沉浸环绕声"), ("jymaster", "超清母带")];
    private void PlayPause_Click(object sender, RoutedEventArgs args) => _playback.Toggle();
    private async void Previous_Click(object sender, RoutedEventArgs args) => await _playback.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs args) => await _playback.NextAsync();
    private async void ShowPlaying_Click(object sender, RoutedEventArgs args) { if (_view != "playing") await NavigateAsync("playing"); else await GoBackAsync(); }
    private void ShowQueue_Click(object sender, RoutedEventArgs args)
    {
        var open = QueuePane.Visibility != Visibility.Visible;
        QueuePane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QueueList.ItemsSource = open ? _playback.Queue : null;
        if (open && _playback.Current != null) { QueueList.SelectedItem = _playback.Current; QueueList.ScrollIntoView(_playback.Current); }
    }
    private async void QueueTrack_Click(object sender, ItemClickEventArgs args) => await _playback.PlayAsync((Track)args.ClickedItem);
    private async void CurrentArtist_Click(object sender, RoutedEventArgs args) { if (_playback.Current is Track track) await OpenArtistAsync(track); }
    private void Playback_Changed()
    {
        if (_exiting) return;
        PlayPauseIcon.Glyph = _playback.IsPlaying ? "\uE769" : "\uE768";
        if (_displayedTrack != _playback.Current)
        {
            _displayedTrack = _playback.Current;
            if (_displayedTrack is Track track)
            {
                MiniTitle.Text = track.Title; MiniArtist.Text = track.Subtitle; Artwork.SetUri(MiniCover, track.Cover);
                _followLyrics = true; FollowLyricsButton.Visibility = Visibility.Collapsed;
                _qualityTrack = 0; _ = LoadQualityAsync(track);
                _ = LoadSongEngagementAsync(track);
                if (_view == "playing" && !_hidden) { UpdateNowPlaying(); _ = LoadLyricsAsync(); }
            }
        }
        QualityButton.Content = Qualities.FirstOrDefault(q => q.Level == _playback.CurrentQuality).Name ?? "音质";
        if (QueuePane.Visibility == Visibility.Visible) { QueueList.ItemsSource = _playback.Queue; QueueList.SelectedItem = _playback.Current; }
        UpdateProgress(); UpdateTimer();
    }
    private void UpdateNowPlaying()
    {
        if (_playback.Current is not Track track) return;
        NowTitle.Text = track.Title; NowArtist.Text = track.Subtitle;
        if (LargeCover.Source == null) LargeCover.Source = MiniCover.Source;
        if (Artwork.GetUri(LargeCover) == track.Cover) Artwork.Refresh(LargeCover); else Artwork.SetUri(LargeCover, track.Cover);
        _ = UpdateCoverBackgroundAsync(track);
    }
    private void UpdateTimer() { if (!_hidden && _playback.IsPlaying) _timer.Start(); else _timer.Stop(); }
    private void UpdateProgress()
    {
        if (_hidden) return;
        _updating = true; var duration = _playback.Duration;
        SeekSlider.Maximum = Math.Max(1, duration.TotalSeconds); SeekSlider.Value = Math.Clamp(_playback.Position.TotalSeconds, 0, SeekSlider.Maximum);
        SeekSlider.IsEnabled = duration.TotalSeconds > 0;
        ElapsedText.Text = _playback.Position.ToString(@"m\:ss"); DurationText.Text = duration.ToString(@"m\:ss"); _updating = false;
        if (_view == "playing" && _followLyrics && _lyrics.Count > 0)
        {
            var index = _lyrics.FindLastIndex(line => line.Position <= _playback.Position);
            if (index >= 0 && LyricsList.SelectedIndex != index) { LyricsList.SelectedIndex = index; LyricsList.ScrollIntoView(_lyrics[index], ScrollIntoViewAlignment.Leading); }
        }
    }
    private void Seek_Changed(object sender, RangeBaseValueChangedEventArgs args) { if (_ready && !_updating) _playback.Seek(TimeSpan.FromSeconds(args.NewValue)); }
    private async Task LoadLyricsAsync()
    {
        if (_playback.Current is not Track track || _view != "playing" || _hidden || _lyricId == track.Id) return;
        _lyricsLoad?.Cancel(); using var load = new CancellationTokenSource(); _lyricsLoad = load; _lyricId = track.Id;
        _lyrics = []; LyricsList.ItemsSource = null;
        try
        {
            var response = await _api.CallAsync("lyric", new { id = track.Id }, load.Token); load.Token.ThrowIfCancellationRequested();
            _lyrics = response.TryGetProperty("lrc", out var lrc) ? LyricLine.Parse(lrc.Text("lyric"), response.TryGetProperty("tlyric", out var translated) ? translated.Text("lyric") : "") : [];
            LyricsList.ItemsSource = _lyrics.Count > 0 ? _lyrics : new List<LyricLine> { new(TimeSpan.Zero, "暂无歌词") };
            UpdateProgress();
        }
        catch (OperationCanceledException) { }
        catch { if (!load.IsCancellationRequested) { _lyricId = 0; LyricsList.ItemsSource = new List<LyricLine> { new(TimeSpan.Zero, "歌词加载失败") }; } }
        finally { if (ReferenceEquals(_lyricsLoad, load)) _lyricsLoad = null; }
    }
    private void SuspendLyrics() { _followLyrics = false; FollowLyricsButton.Visibility = Visibility.Visible; }
    private void FollowLyrics_Click(object sender, RoutedEventArgs args) { _followLyrics = true; LyricsList.SelectedIndex = -1; FollowLyricsButton.Visibility = Visibility.Collapsed; UpdateProgress(); }
    private void Lyric_Click(object sender, ItemClickEventArgs args) { if (args.ClickedItem is LyricLine line) { _playback.Seek(line.Position); } }
    private void SetFullScreen(bool enabled)
    {
        _fullScreen = enabled; AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        FullScreenIcon.Glyph = enabled ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullScreenButton, enabled ? "退出全屏" : "全屏");
        AutomationProperties.SetName(FullScreenButton, enabled ? "退出全屏" : "全屏");
        TitleRow.Height = new GridLength(enabled ? 0 : 48); BackButton.IsEnabled = _history.Count > 0 || enabled; UpdateLayout();
        ((FrameworkElement)TitleBar.Parent).Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled && AppWindow.Presenter is OverlappedPresenter p) p.PreferredMinimumWidth = 900;
    }
    private async void FullScreen_Click(object sender, RoutedEventArgs args) { if (_view != "playing") await NavigateAsync("playing"); SetFullScreen(!_fullScreen); }
    private async void FullScreen_Key(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; if (_view != "playing") await NavigateAsync("playing"); SetFullScreen(!_fullScreen); }
    private async void Escape_Key(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialog != null) return;
        if (QueuePane.Visibility == Visibility.Visible) { QueuePane.Visibility = Visibility.Collapsed; QueueList.ItemsSource = null; }
        else if (_fullScreen) SetFullScreen(false);
        else if (_view == "playing") await GoBackAsync();
        else return;
        args.Handled = true;
    }
    private void Mode_Click(object sender, RoutedEventArgs args)
    {
        var modes = new[] { ("Sequential", "顺序播放", "\uE8EE"), ("Repeat", "列表循环", "\uE8EE"), ("One", "单曲循环", "\uE8ED"), ("Shuffle", "随机播放", "\uE8B1") };
        var index = (Array.FindIndex(modes, m => m.Item1 == _playback.Mode) + 1) % modes.Length;
        var (mode, name, glyph) = modes[index]; _playback.Mode = mode; ModeIcon.Glyph = glyph;
        ToolTipService.SetToolTip(ModeButton, name); ShowPlayerMessage(name);
    }
    private readonly DispatcherTimer _playerNoticeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private void ShowPlayerMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_exiting) return;
        PlayerNotice.Message = text; PlayerNotice.Severity = severity; PlayerNotice.IsOpen = true;
        _playerNoticeTimer.Stop(); _playerNoticeTimer.Start();
    }
    private bool PlayerAnimationEnabled => _settings.Animations && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !_hidden;
    private void MovePlayerControls(bool toPage)
    {
        (TransportControls.Parent as Microsoft.UI.Xaml.Controls.Panel)?.Children.Remove(TransportControls);
        (PlayerTools.Parent as Microsoft.UI.Xaml.Controls.Panel)?.Children.Remove(PlayerTools);
        (SongActions.Parent as Microsoft.UI.Xaml.Controls.Panel)?.Children.Remove(SongActions);
        (toPage ? PageTransportHost : FooterTransportHost).Children.Add(TransportControls);
        (toPage ? PageToolsHost : FooterToolsHost).Children.Add(PlayerTools);
        (toPage ? PageSongActionsHost : FooterSongActionsHost).Children.Add(SongActions);
        Grid.SetRow(TransportButtons, toPage ? 1 : 0); Grid.SetRow(ProgressControls, toPage ? 0 : 1);
        PlayerFooter.Visibility = toPage ? Visibility.Collapsed : Visibility.Visible;
    }
    private void AnimatePlayerControls()
    {
        if (!PlayerAnimationEnabled) return;
        foreach (var element in new UIElement[] { TransportControls, PlayerTools, SongActions })
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation(); fade.InsertKeyFrame(0, .15f); fade.InsertKeyFrame(1, 1); fade.Duration = TimeSpan.FromMilliseconds(280); visual.StartAnimation("Opacity", fade);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var move = visual.Compositor.CreateVector3KeyFrameAnimation(); move.InsertKeyFrame(0, new System.Numerics.Vector3(0, 18, 0)); move.InsertKeyFrame(1, System.Numerics.Vector3.Zero); move.Duration = TimeSpan.FromMilliseconds(280); visual.StartAnimation("Translation", move);
        }
    }
    private async void FavoriteCurrent_Click(object sender, RoutedEventArgs args) { if (_playback.Current is Track track) await FavoriteAsync(track); }
    private async void ContextFavorite_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is Track track) await FavoriteAsync(track); }
    private async Task FavoriteAsync(Track track)
    {
        if (_dialog != null) return;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession) return; }
        try
        {
            var profile = await GetProfileAsync(); if (profile == null) return;
            var uid = profile.Value.Number("userId"); var choices = new List<Playlist>(); var offset = 0;
            while (offset < 1000)
            {
                var result = await _api.CallAsync("user_playlist", new { uid, limit = 100, offset });
                var array = result.GetProperty("playlist");
                choices.AddRange(array.EnumerateArray().Where(p => p.TryGetProperty("creator", out var creator) && creator.Number("userId") == uid).Select(Playlist.FromJson));
                offset += array.GetArrayLength(); if (array.GetArrayLength() < 100) break;
            }
            if (choices.Count == 0) { ShowPlayerMessage("没有可添加的歌单"); return; }
            var list = new ListView { ItemsSource = choices, SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 340, Width = 380, ItemTemplate = (DataTemplate)Root.Resources["PlaylistChoiceTemplate"] };
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "收藏到歌单", Content = list, PrimaryButtonText = "添加", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is Playlist;
            _dialog = dialog;
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is Playlist playlist)
                {
                    await _api.CallAsync("playlist_tracks", new { op = "add", pid = playlist.Id, tracks = track.Id.ToString() });
                    ShowPlayerMessage("已添加到歌单", InfoBarSeverity.Success);
                }
            }
            finally { _dialog = null; }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task LoadQualityAsync(Track track)
    {
        _maxQuality = -1;
        try
        {
            var result = await _api.CallAsync("song_detail", new { ids = track.Id.ToString() });
            if (_playback.Current?.Id != track.Id || _exiting) return;
            if (result.TryGetProperty("privileges", out var privileges) && privileges.GetArrayLength() > 0)
            {
                var privilege = privileges[0]; var level = privilege.Text("plLevel");
                _maxQuality = Array.FindIndex(Qualities, item => item.Level == level);
                if (_maxQuality < 0)
                {
                    var bitrate = privilege.Number("pl");
                    _maxQuality = bitrate >= 999000 ? 3 : bitrate >= 320000 ? 2 : bitrate >= 192000 ? 1 : bitrate > 0 ? 0 : -1;
                }
                if (_maxQuality < 0 && _playback.Current?.Id == track.Id) _maxQuality = 0;
            }
            _qualityTrack = track.Id;
        }
        catch { _qualityTrack = 0; }
    }
    private async void Quality_Click(object sender, RoutedEventArgs args)
    {
        if (_playback.Current is Track track && _qualityTrack != track.Id) await LoadQualityAsync(track);
        var menu = new MenuFlyout();
        for (var i = 0; i < Qualities.Length; i++)
        {
            var quality = Qualities[i];
            var item = new ToggleMenuFlyoutItem { Text = quality.Name, IsChecked = _playback.CurrentQuality == quality.Level, IsEnabled = i <= _maxQuality && _playback.Current != null };
            item.Click += async (_, _) => { _settings.Quality = quality.Level; SaveSettings(); QualityButton.Content = quality.Name; await _playback.ReloadQualityAsync(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(QualityButton);
    }
    private async void Sound_Click(object sender, RoutedEventArgs args)
    {
        var panel = new StackPanel { Spacing = 14, Width = 280 };
        panel.Children.Add(new TextBlock { Text = "音量" });
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = _settings.Volume * 100 };
        volume.ValueChanged += (_, value) => _playback.SetVolume(value.NewValue / 100); panel.Children.Add(volume);
        panel.Children.Add(new TextBlock { Text = "输出设备" });
        var devices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        devices.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });
        try
        {
            foreach (var device in await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector())) devices.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            SelectTag(devices, _settings.AudioDeviceId);
            devices.SelectionChanged += async (_, _) => { try { var id = Tag(devices); await _playback.SetOutputAsync(id); _settings.AudioDeviceId = id; SaveSettings(); } catch (Exception ex) { ShowError(ex.Message); } };
        }
        catch { devices.SelectedIndex = 0; }
        panel.Children.Add(devices);
        foreach (var label in new[] { "空间音效", "音频增强" }) { var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch }; button.Click += async (_, _) => await OpenSystemAudioAsync(); panel.Children.Add(button); }
        new Flyout { Content = panel }.ShowAt(SoundButton);
    }
    private async Task OpenSystemAudioAsync()
    {
        var uri = _settings.AudioDeviceId.Length == 0 ? "ms-settings:sound-defaultoutputproperties" : "ms-settings:sound-properties?interfaceId=" + Uri.EscapeDataString(_settings.AudioDeviceId);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) uri = "ms-settings:sound";
        if (!await Launcher.LaunchUriAsync(new Uri(uri))) await Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
    }
    private async void ContextDownload_Click(object sender, RoutedEventArgs args) { if (((FrameworkElement)sender).DataContext is Track track) await DownloadAsync(track); }
    private async void DownloadCurrent_Click(object sender, RoutedEventArgs args) { if (_playback.Current is Track track) await DownloadAsync(track); }
    private async Task DownloadAsync(Track track)
    {
        if (!_activeDownloads.Add(track.Id)) return;
        string? partial = null;
        try
        {
            var response = await _api.CallAsync("song_download_url_v1", new { id = track.Id, level = _settings.Quality }, _downloads.Token);
            var data = response.GetProperty("data"); var url = data.Text("url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) { ShowError("当前账号不能下载此歌曲"); return; }
            Directory.CreateDirectory(_settings.DownloadDirectory);
            var name = string.Concat($"{track.Artist} - {track.Title}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            name = name[..Math.Min(100, name.Length)];
            var extension = data.Text("type").ToLowerInvariant(); if (extension is not ("mp3" or "flac" or "m4a" or "wav")) extension = "mp3";
            var path = Path.Combine(_settings.DownloadDirectory, $"{name} ({track.Id}).{extension}");
            if (File.Exists(path)) { ShowError("文件已存在"); return; }
            partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
            ShowError("正在下载"); using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var result = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, _downloads.Token); result.EnsureSuccessStatusCode();
            await using (var file = File.Create(partial)) await result.Content.CopyToAsync(file, _downloads.Token);
            File.Move(partial, path); Notice.Message = "下载完成"; Notice.Severity = InfoBarSeverity.Success; Notice.IsOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _activeDownloads.Remove(track.Id); try { if (partial != null && File.Exists(partial)) File.Delete(partial); } catch { } }
    }
}
