using Microsoft.UI.Dispatching;
using NetEaseNEXT.Models;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;

namespace NetEaseNEXT.Services;

public sealed class PlaybackService : IDisposable
{
    private readonly ApiClient _api;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly MediaPlayer _player = new();
    private readonly SystemMediaTransportControls _smtc;
    private MediaSource? _source;
    private CancellationTokenSource? _load;
    private bool _disposed;
    private int _index = -1;
    private TimeSpan _resumeAt;
    public string Mode { get; set; } = "Sequential";
    public IReadOnlyList<Track> Queue { get; private set; } = Array.Empty<Track>();
    public Track? Current { get; private set; }
    public string CurrentQuality { get; private set; } = "standard";
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public TimeSpan Position => _player.PlaybackSession.Position;
    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;
    internal MediaPlaybackStatus SystemPlaybackStatus => _smtc.PlaybackStatus;
    public event Action? Changed;
    public event Action<string>? Error;
    public PlaybackService(ApiClient api, SettingsStore settings, DispatcherQueue dispatcher)
    {
        _api = api; _settings = settings; _dispatcher = dispatcher;
        _player.Volume = Math.Clamp(settings.Volume, 0, 1);
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        if (settings.AudioDeviceId.Length > 0) _ = RestoreOutputAsync(settings.AudioDeviceId);
        _player.CommandManager.IsEnabled = false;
        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += OnButton;
        _smtc.PlaybackPositionChangeRequested += OnPosition;
        _player.PlaybackSession.PlaybackStateChanged += OnState;
        _player.MediaOpened += OnOpened;
        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
    }
    private void Dispatch(Action action) { if (!_disposed) _dispatcher.TryEnqueue(() => { if (!_disposed) action(); }); }
    private void OnState(MediaPlaybackSession sender, object args) => Dispatch(() => { UpdateTimeline(); Changed?.Invoke(); });
    private void OnOpened(MediaPlayer sender, object args) => Dispatch(() => { if (_resumeAt > TimeSpan.Zero) { _player.PlaybackSession.Position = _resumeAt < Duration ? _resumeAt : TimeSpan.Zero; _resumeAt = TimeSpan.Zero; } UpdateTimeline(); Changed?.Invoke(); });
    private void OnEnded(MediaPlayer sender, object args) => Dispatch(() => { if (Mode == "One" && Current != null) _ = LoadAsync(Current); else _ = NextAsync(Mode != "Sequential"); });
    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Dispatch(() => { Error?.Invoke("音频加载失败，请重试或切换歌曲。" ); Changed?.Invoke(); });
    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) => Dispatch(() =>
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: if (_source != null) _player.Play(); break;
            case SystemMediaTransportControlsButton.Pause: _player.Pause(); break;
            case SystemMediaTransportControlsButton.Next: _ = NextAsync(); break;
            case SystemMediaTransportControlsButton.Previous: _ = PreviousAsync(); break;
        }
    });
    private void OnPosition(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) => Dispatch(() => Seek(args.RequestedPlaybackPosition));
    public Task PlayAsync(Track track, IEnumerable<Track>? context = null)
    {
        if (context != null) Queue = context.Take(1000).ToArray();
        _index = Queue.ToList().FindIndex(t => t.Id == track.Id);
        if (_index < 0) { Queue = [track]; _index = 0; }
        return LoadAsync(track);
    }
    private async Task LoadAsync(Track track, TimeSpan? resumeAt = null, bool autoplay = true)
    {
        _load?.Cancel();
        using var load = new CancellationTokenSource();
        _load = load;
        _player.Pause();
        try
        {
            var json = await _api.CallAsync("song_url_v1", new { id = track.Id, level = _settings.Quality }, load.Token);
            var info = json.GetProperty("data")[0];
            var url = info.Text("url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("这首歌暂不可播放，可能需要登录、会员或版权授权。");
            load.Token.ThrowIfCancellationRequested();
            _player.Source = null;
            _source?.Dispose();
            _source = MediaSource.CreateFromUri(uri);
            _resumeAt = resumeAt ?? TimeSpan.Zero;
            Current = track;
            CurrentQuality = info.Text("level") is { Length: > 0 } actual ? actual : "standard";
            _player.Source = _source;
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album;
            updater.Thumbnail = Uri.TryCreate(track.Cover, UriKind.Absolute, out var cover) ? RandomAccessStreamReference.CreateFromUri(cover) : null;
            updater.Update();
            if (autoplay) _player.Play();
            Changed?.Invoke();
            if (info.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == System.Text.Json.JsonValueKind.Object)
                Error?.Invoke("当前为试听片段，完整播放取决于账号权限。");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error?.Invoke(e.Message); Changed?.Invoke(); }
        finally { if (ReferenceEquals(_load, load)) _load = null; }
    }
    public Task NextAsync(bool wrap = true)
    {
        if (Queue.Count == 0) return Task.CompletedTask;
        if (_index + 1 >= Queue.Count && !wrap) { _player.Pause(); Changed?.Invoke(); return Task.CompletedTask; }
        _index = Mode == "Shuffle" && Queue.Count > 1 ? (_index + Random.Shared.Next(1, Queue.Count)) % Queue.Count : (_index + 1) % Queue.Count;
        return LoadAsync(Queue[_index]);
    }
    public Task PreviousAsync()
    {
        if (Position.TotalSeconds > 3) { Seek(TimeSpan.Zero); return Task.CompletedTask; }
        if (Queue.Count == 0) return Task.CompletedTask;
        _index = (_index - 1 + Queue.Count) % Queue.Count;
        return LoadAsync(Queue[_index]);
    }
    public void Toggle() { if (_source == null) return; if (IsPlaying) _player.Pause(); else _player.Play(); }
    public Task ReloadQualityAsync() => Current is Track track ? LoadAsync(track, Position, IsPlaying) : Task.CompletedTask;
    public async Task SetOutputAsync(string id) => _player.AudioDevice = id.Length == 0 ? null : await DeviceInformation.CreateFromIdAsync(id);
    private async Task RestoreOutputAsync(string id) { try { await SetOutputAsync(id); } catch { } }
    public void SetVolume(double value) { _player.Volume = Math.Clamp(value, 0, 1); _settings.Volume = _player.Volume; }
    public void Seek(TimeSpan position)
    {
        if (_source == null) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds)));
        UpdateTimeline();
    }
    private void UpdateTimeline()
    {
        _smtc.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing : Current == null ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Paused;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero, EndTime = Duration, Position = Position,
            MinSeekTime = TimeSpan.Zero, MaxSeekTime = Duration
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _load?.Cancel();
        _smtc.ButtonPressed -= OnButton; _smtc.PlaybackPositionChangeRequested -= OnPosition;
        _player.PlaybackSession.PlaybackStateChanged -= OnState;
        _player.MediaOpened -= OnOpened; _player.MediaEnded -= OnEnded; _player.MediaFailed -= OnFailed;
        _smtc.IsEnabled = false;
        _player.Source = null; _source?.Dispose(); _player.Dispose();
    }
}
