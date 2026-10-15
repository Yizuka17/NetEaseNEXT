using Microsoft.UI.Dispatching;
using NetEaseNEXT.Models;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;
using System.Net.Http;

namespace NetEaseNEXT.Services;

public sealed class PlaybackService : IDisposable
{
    private readonly ApiClient _streamApi;
    private readonly SettingsStore _settings;
    private readonly DispatcherQueue _dispatcher;
    private MediaPlayer _player = CreatePlayer();
    private readonly SystemMediaTransportControls _smtc;
    private MediaSource? _source;
    private string? _localPath;
    private readonly AudioCache _audioCache = new();
    private readonly AudioCache _nextAudioCache = new();
    private readonly TrackUrls _urls;
    private CancellationTokenSource? _nextCacheLoad;
    private Prepared? _nextPrepared;
    private long _nextPreparedId;
    private string? _nextPreparedPath, _nextPreparedCookie, _nextPreparedQuality;
    private Task _nextPreparationTask = Task.CompletedTask;
    internal Task NextPreparationTask => _nextPreparationTask;
    internal bool LastPlayUsedPreparedDecoder { get; private set; }
    internal string? NextPreparationStatus { get; private set; }
    private TimeSpan _restoredPosition;
    private IReadOnlyList<Track> _orderedQueue = Array.Empty<Track>();
    internal bool LastPlayUsedCachedUrl { get; private set; }
    internal int QueueRevision { get; private set; }
    internal int ShuffleGeneration { get; private set; }
    private bool _background;
    private (Uri Uri, string? Path, long Size, string Md5)? _cacheRequest;
    internal bool LastPlayUsedCache { get; private set; }
    internal Task CacheCompletion => _audioCache.Completion;
    internal bool IsCaching => _audioCache.IsDownloading || _nextAudioCache.IsDownloading;
    internal void SetBackgroundMode(bool background)
    {
        _background = background;
        if (background) { _audioCache.Stop(); _nextCacheLoad?.Cancel(); _nextAudioCache.Stop(); DisposeNextPrepared(); _urls.SuspendWarm(); }
        else if (_cacheRequest is { } request) { _audioCache.Start(request.Uri, request.Path, request.Size, request.Md5); WarmFollowing(); }
    }
    private CancellationTokenSource? _load;
    private bool _disposed, _desiredPlaying;
    private int _index = -1;
    private static readonly HttpClient AudioClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private string _mode = "Sequential";
    public string Mode
    {
        get => _mode;
        set
        {
            if (value is not ("Sequential" or "Repeat" or "One" or "Shuffle") || value == _mode) return;
            var wasShuffle = _mode == "Shuffle"; _mode = value;
            if (value == "Shuffle") Queue = Shuffle(_orderedQueue);
            else if (wasShuffle) Queue = _orderedQueue.ToArray();
            _index = Current == null ? -1 : Queue.ToList().FindIndex(t => t.Id == Current.Id);
            Changed?.Invoke(); WarmFollowing();
        }
    }
    private Track[] Shuffle(IEnumerable<Track> tracks)
    {
        ShuffleGeneration++;
        var rest = tracks.Where(t => t.Id != Current?.Id).ToArray();
        Random.Shared.Shuffle(rest);
        return Current != null && tracks.Any(t => t.Id == Current.Id) ? [Current, .. rest] : rest;
    }
    public IReadOnlyList<Track> Queue { get; private set; } = Array.Empty<Track>();
    public Track? Current { get; private set; }
    public string CurrentQuality { get; private set; } = "standard";
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public TimeSpan Position => _source == null ? _restoredPosition : _player.PlaybackSession.Position;
    public TimeSpan Duration => _source == null ? TimeSpan.FromMilliseconds(Current?.Duration ?? 0) : _player.PlaybackSession.NaturalDuration;
    internal MediaPlaybackStatus SystemPlaybackStatus => _smtc.PlaybackStatus;
    internal bool IsWorkerRunning => _streamApi.IsWorkerRunning;
    internal bool LastSwitchUsedLocalStream { get; private set; }
    public event Action? Changed;
    public event Action<string>? Error;
    public PlaybackService(ApiClient api, SettingsStore settings, DispatcherQueue dispatcher, nint window)
    {
        _streamApi = new(api); _urls = new(_streamApi); _settings = settings; _dispatcher = dispatcher;
        _player.Volume = Math.Clamp(settings.Volume, 0, 1);
        if (settings.AudioDeviceId.Length > 0) _ = RestoreOutputAsync(settings.AudioDeviceId);
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(window);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
        _smtc.DisplayUpdater.AppMediaId = ApplicationIdentity.Id;
        _smtc.ButtonPressed += OnButton; _smtc.PlaybackPositionChangeRequested += OnPosition;
        Attach(_player);
    }
    private static MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer { AutoPlay = false, Volume = 0, AudioCategory = MediaPlayerAudioCategory.Media };
        player.CommandManager.IsEnabled = false;
        return player;
    }
    private void Attach(MediaPlayer player)
    {
        player.PlaybackSession.PlaybackStateChanged += OnState; player.MediaEnded += OnEnded; player.MediaFailed += OnFailed;
    }
    private void Detach(MediaPlayer player)
    {
        player.PlaybackSession.PlaybackStateChanged -= OnState; player.MediaEnded -= OnEnded; player.MediaFailed -= OnFailed;
    }
    private void Dispatch(Action action) { if (!_disposed) _dispatcher.TryEnqueue(() => { if (!_disposed) action(); }); }
    private void OnState(MediaPlaybackSession sender, object args) => Dispatch(() => { if (!ReferenceEquals(sender, _player.PlaybackSession)) return; UpdateTimeline(); Changed?.Invoke(); });
    private void OnEnded(MediaPlayer sender, object args) => Dispatch(() => { if (!ReferenceEquals(sender, _player)) return; if (Mode == "One" && Current != null) _ = LoadAsync(Current); else _ = NextAsync(Mode != "Sequential"); });
    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Dispatch(() =>
    {
        if (!ReferenceEquals(sender, _player)) return;
        Log.Write(new InvalidOperationException($"Audio failure: {args.Error}, HRESULT 0x{args.ExtendedErrorCode.HResult:X8}, quality {CurrentQuality}"));
        Error?.Invoke("音频加载失败，请重试或切换歌曲。"); UpdateTimeline(); Changed?.Invoke();
    });
    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) => Dispatch(() =>
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: SetPlaying(true); break;
            case SystemMediaTransportControlsButton.Pause: SetPlaying(false); break;
            case SystemMediaTransportControlsButton.Next: _ = NextAsync(); break;
            case SystemMediaTransportControlsButton.Previous: _ = PreviousAsync(); break;
        }
    });
    private void OnPosition(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) => Dispatch(() => Seek(args.RequestedPlaybackPosition));
    public async Task PlayAsync(Track track, IEnumerable<Track>? context = null)
    {
        var requestedContext = context?.ToArray();
        if (Current?.Id == track.Id && _source != null && Position < Duration - TimeSpan.FromSeconds(1)) SetPlaying(true);
        else
        {
            if (_pendingPlayTask == null || _pendingPlayId != track.Id)
            { _pendingPlayId = track.Id; _pendingPlayTask = LoadAsync(track, resume: Current?.Id == track.Id && _restoredPosition > TimeSpan.Zero, hints: requestedContext); }
            var pending = _pendingPlayTask;
            try { if (!await pending) return; }
            finally { if (ReferenceEquals(_pendingPlayTask, pending)) _pendingPlayTask = null; }
        }
        var queue = requestedContext ?? _orderedQueue.ToArray();
        var index = Array.FindIndex(queue, t => t.Id == track.Id);
        if (index < 0) { queue = [.. queue, track]; index = queue.Length - 1; }
        else queue[index] = track;
        _orderedQueue = queue;
        if (requestedContext != null) Queue = Mode == "Shuffle" ? Shuffle(queue) : queue;
        else { var effective = Queue.ToList(); var match = effective.FindIndex(t => t.Id == track.Id); if (match < 0) effective.Add(track); else effective[match] = track; Queue = effective.ToArray(); }
        _index = Queue.ToList().FindIndex(t => t.Id == track.Id); QueueRevision++; Changed?.Invoke(); WarmFollowing();
    }
    public void AddNext(Track track) => InsertNext(track, prepare: true);
    private void InsertNext(Track track, bool prepare)
    {
        if (Current?.Id == track.Id) return;
        var queue = Queue.ToList();
        var current = Current == null ? -1 : queue.FindIndex(t => t.Id == Current.Id);
        for (var i = queue.Count - 1; i >= 0; i--) if (i != current && queue[i].Id == track.Id) { queue.RemoveAt(i); if (i < current) current--; }
        queue.Insert(Math.Clamp(current + 1, 0, queue.Count), track);
        Queue = queue.ToArray(); _index = current;
        var original = _orderedQueue.Where(t => t.Id != track.Id || t.Id == Current?.Id).ToList();
        original.Insert(Math.Clamp(original.FindIndex(t => t.Id == Current?.Id) + 1, 0, original.Count), track); _orderedQueue = original;
        QueueRevision++; Changed?.Invoke(); if (prepare) WarmFollowing();
    }
    public Task PlayNextAsync(Track track)
    {
        // Direct playback prepares the selected song itself; avoid starting a
        // second speculative download/decoder just before switching to it.
        InsertNext(track, prepare: false);
        return PlayAsync(track);
    }
    public void Remove(Track track)
    {
        if (Current?.Id == track.Id) return;
        Queue = Queue.Where(t => t.Id != track.Id).ToArray(); _index = Current == null ? -1 : Queue.ToList().FindIndex(t => t.Id == Current.Id); Changed?.Invoke();
        _orderedQueue = _orderedQueue.Where(t => t.Id != track.Id).ToArray(); QueueRevision++; WarmFollowing();
    }
    internal void CompleteQueue(IEnumerable<Track> tracks, int revision)
    {
        if (_disposed || revision != QueueRevision) return;
        _orderedQueue = tracks.DistinctBy(t => t.Id).ToArray();
        if (Mode == "Shuffle") { var present = Queue.Select(t => t.Id).ToHashSet(); var missing = _orderedQueue.Where(t => !present.Contains(t.Id)).ToArray(); Random.Shared.Shuffle(missing); Queue = [.. Queue, .. missing]; }
        else Queue = _orderedQueue.ToArray();
        _index = Queue.ToList().FindIndex(t => t.Id == Current?.Id); QueueRevision++; Changed?.Invoke(); WarmFollowing();
    }
    internal PlaybackSnapshot Capture() => new() { Queue = Queue.Take(10000).ToList(), OriginalQueue = _orderedQueue.Take(10000).ToList(), CurrentId = Current?.Id ?? 0, PositionSeconds = Position.TotalSeconds, Mode = Mode, Quality = CurrentQuality };
    internal void Restore(PlaybackSnapshot state)
    {
        _load?.Cancel(); _audioCache.Stop(); _nextAudioCache.Stop();
        DisposeNextPrepared();
        _player.Pause(); _player.Source = null; _source?.Dispose(); _source = null; DeleteLocal(_localPath); _localPath = null; _cacheRequest = null;
        Queue = state.Queue.Where(t => t.Id > 0).Take(10000).ToArray();
        _orderedQueue = state.OriginalQueue.Count > 0 ? state.OriginalQueue.Where(t => t.Id > 0).Take(10000).ToArray() : Queue;
        _mode = state.Mode is "Shuffle" or "One" or "Repeat" ? state.Mode : "Sequential";
        Current = Queue.FirstOrDefault(t => t.Id == state.CurrentId); CurrentQuality = state.Quality;
        _index = Queue.ToList().FindIndex(t => t.Id == Current?.Id);
        _restoredPosition = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(state.PositionSeconds) ? state.PositionSeconds : 0, 0, Math.Max(0, (Current?.Duration ?? 0) / 1000d - 1)));
        _desiredPlaying = false; QueueRevision++; UpdateTimeline(); Changed?.Invoke();
    }
    internal Task WarmAsync(IEnumerable<Track> tracks)
    {
        if (_background || _disposed) return Task.CompletedTask;
        var context = tracks.ToArray();
        var candidates = Current != null && context.Any(t => t.Id == Current.Id) ? Neighbors(Current, context).Prepend(Current).Concat(context.Take(4)) : context.Take(8);
        return _urls.WarmAsync(candidates.DistinctBy(t => t.Id), _settings.Quality);
    }
    private IEnumerable<Track> Neighbors(Track track, IEnumerable<Track>? hints = null)
    {
        var context = (hints ?? Queue).ToArray(); var index = Array.FindIndex(context, t => t.Id == track.Id);
        return index < 0 ? context.Take(3) : context.Skip(index + 1).Take(2).Concat(context.Skip(Math.Max(0, index - 1)).Take(1));
    }
    private void WarmFollowing() => _nextPreparationTask = PrepareFollowingAsync();
    private void DisposeNextPrepared()
    {
        var prepared = _nextPrepared; _nextPrepared = null; _nextPreparedPath = null;
        if (prepared != null) { prepared.Player.Pause(); prepared.Player.Source = null; prepared.Source.Dispose(); prepared.Player.Dispose(); }
    }
    private async Task PrepareFollowingAsync()
    {
        NextPreparationStatus = "pending";
        _nextCacheLoad?.Cancel(); _nextAudioCache.Stop();
        if (_disposed || _background || _source == null || Current == null || Queue.Count < 2 || Mode == "One") { NextPreparationStatus = "inactive"; return; }
        using var load = new CancellationTokenSource(); _nextCacheLoad = load;
        Prepared? prepared = null;
        try
        {
            var next = Queue[(_index + 1) % Queue.Count]; var level = _settings.Quality;
            await _urls.WarmAsync(Neighbors(Current).Prepend(next), level);
            await _audioCache.Completion.WaitAsync(load.Token);
            if (_background || _disposed || level != _settings.Quality) return;
            var result = await _urls.GetAsync(next, level, Array.Empty<Track>(), load.Token);
            if (!Uri.TryCreate(result.Info.Text("url"), UriKind.Absolute, out var uri)) return;
            var actual = result.Info.Text("level") is { Length: > 0 } quality ? quality : level;
            var path = AudioCache.PathFor(_streamApi.CacheDirectory, next.Id, actual, result.Info, uri);
            _nextAudioCache.ActivePath = _audioCache.ActivePath;
            _nextAudioCache.Start(uri, path, result.Info.Number("size"), result.Info.Text("md5"));
            await _nextAudioCache.Completion.WaitAsync(load.Token);
            var local = AudioCache.Find(path, result.Info.Number("size"));
            if (local == null || _background || _disposed) { NextPreparationStatus = "no complete file"; return; }
            var cookie = _streamApi.Cookie;
            prepared = await PrepareAsync(uri, local, false, load.Token); prepared.Player.Pause();
            if (prepared.Player.PlaybackSession.CanSeek) prepared.Player.PlaybackSession.Position = TimeSpan.Zero;
            load.Token.ThrowIfCancellationRequested();
            DisposeNextPrepared(); _nextPrepared = prepared; prepared = null;
            _nextPreparedId = next.Id; _nextPreparedPath = local; _nextPreparedCookie = cookie; _nextPreparedQuality = level;
            NextPreparationStatus = "ready";
        }
        catch (OperationCanceledException) { NextPreparationStatus = "cancelled"; }
        catch (Exception ex) { NextPreparationStatus = ex.Message; }
        finally
        {
            if (prepared != null) { prepared.Player.Source = null; prepared.Source.Dispose(); prepared.Player.Dispose(); }
            if (ReferenceEquals(_nextCacheLoad, load)) _nextCacheLoad = null;
        }
    }
    private sealed record Prepared(MediaPlayer Player, MediaSource Source, string? LocalPath);
    private async Task<Prepared> PrepareAsync(Uri uri, string? localPath, bool resume, CancellationToken ct)
    {
        var player = CreatePlayer(); var source = localPath == null ? MediaSource.CreateFromUri(uri) : MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(localPath));
        player.AudioDevice = _player.AudioDevice;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(MediaPlayer _, object args) => opened.TrySetResult();
        void Failed(MediaPlayer _, MediaPlayerFailedEventArgs args)
        {
            var error = new InvalidOperationException($"音频准备失败 ({args.Error}, 0x{args.ExtendedErrorCode.HResult:X8})");
            failure.TrySetResult(error); opened.TrySetException(error);
        }
        player.MediaOpened += Opened; player.MediaFailed += Failed;
        var success = false;
        try
        {
            player.Source = source; await opened.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            // Prime decoding before seeking. A metadata-only OpenAsync is insufficient
            // for some high-resolution streams; keep the old player audible meanwhile.
            player.Play();
            async Task WaitForAudio(TimeSpan position)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing || player.PlaybackSession.Position < position)
                {
                    if (failure.Task.IsCompleted) throw await failure.Task;
                    ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow > deadline) throw new TimeoutException("音频加载超时");
                    await Task.Delay(40, ct);
                }
                if (failure.Task.IsCompleted) throw await failure.Task;
            }
            await WaitForAudio(TimeSpan.FromMilliseconds(80));
            if (resume && Position > TimeSpan.Zero)
            {
                if (!player.PlaybackSession.CanSeek) throw new InvalidOperationException("当前音频流暂不支持定位");
                var position = Position;
                if (position < player.PlaybackSession.NaturalDuration - TimeSpan.FromMilliseconds(150))
                {
                    player.PlaybackSession.Position = position;
                    await WaitForAudio(position + TimeSpan.FromMilliseconds(80));
                }
            }
            ct.ThrowIfCancellationRequested();
            success = true; return new(player, source, localPath);
        }
        finally { player.MediaOpened -= Opened; player.MediaFailed -= Failed; if (!success) { player.Source = null; source.Dispose(); player.Dispose(); } }
    }
    private async Task<string> CacheSwitchStreamAsync(Uri uri, CancellationToken ct)
    {
        var directory = Path.Combine(_streamApi.CacheDirectory, "AudioSwitch"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".audio");
        try
        {
            using var response = await AudioClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[65536]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read; if (total > 512L * 1024 * 1024) throw new InvalidDataException("音频文件过大");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return path;
        }
        catch { DeleteLocal(path); throw; }
    }
    private static void DeleteLocal(string? path) { if (path == null) return; try { File.Delete(path); } catch { } }
    private async Task<bool> LoadAsync(Track track, bool resume = false, bool autoplay = true, string? quality = null, IEnumerable<Track>? hints = null)
    {
        _load?.Cancel(); _audioCache.Stop(); _nextCacheLoad?.Cancel(); _nextAudioCache.Stop(); using var load = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _load = load;
        _desiredPlaying = autoplay; Prepared? prepared = null; string? localPath = null; LastSwitchUsedLocalStream = false;
        try
        {
            var requested = quality ?? _settings.Quality;
            var metadata = await _urls.GetAsync(track, requested, Neighbors(track, hints), load.Token);
            var info = metadata.Info; LastPlayUsedCachedUrl = metadata.Cached;
            if (!Uri.TryCreate(info.Text("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("这首歌暂不可播放，可能需要登录、会员或版权授权。");
            var actualQuality = info.Text("level") is { Length: > 0 } actual ? actual : requested;
            // Refresh authorization before considering a persistent file, including
            // account changes, quality downgrades and trial-only playback.
            var cachePath = AudioCache.PathFor(_streamApi.CacheDirectory, track.Id, actualQuality, info, uri);
            var cached = AudioCache.Find(cachePath, info.Number("size")); LastPlayUsedCache = false;
            LastPlayUsedPreparedDecoder = false;
            if (!resume && _nextPrepared != null && _nextPreparedId == track.Id && _nextPreparedPath == cachePath && _nextPreparedCookie == _streamApi.Cookie && _nextPreparedQuality == requested)
            {
                prepared = _nextPrepared; _nextPrepared = null; _nextPreparedPath = null;
                prepared.Player.Play();
                for (var step = 0; prepared.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing && step < 100; step++) await Task.Delay(10, load.Token);
                if (prepared.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) throw new InvalidOperationException("音频准备失败，请重试。");
                LastPlayUsedCache = LastPlayUsedPreparedDecoder = true;
            }
            else DisposeNextPrepared();
            if (prepared == null && cached != null)
            {
                try { prepared = await PrepareAsync(uri, cached, resume, load.Token); LastPlayUsedCache = true; }
                catch (Exception) when (!load.IsCancellationRequested) { DeleteLocal(cached); }
            }
            try { prepared ??= await PrepareAsync(uri, null, resume, load.Token); }
            catch (Exception) when (resume && !load.IsCancellationRequested)
            {
                // Some FLAC servers reject seek/range requests. A bounded temporary
                // file provides normal random access, then is deleted when released.
                localPath = await CacheSwitchStreamAsync(uri, load.Token);
                prepared = await PrepareAsync(uri, localPath, true, load.Token); LastSwitchUsedLocalStream = true;
            }
            load.Token.ThrowIfCancellationRequested();
            var oldPlayer = _player; var oldSource = _source; var oldPath = _localPath;
            oldPlayer.Pause(); Detach(oldPlayer);
            _player = prepared.Player; _source = prepared.Source; _localPath = LastPlayUsedCache ? null : prepared.LocalPath; prepared = null; localPath = null;
            _audioCache.ActivePath = LastPlayUsedCache ? cached ?? cachePath : null;
            Attach(_player); Current = track; CurrentQuality = actualQuality;
            _restoredPosition = TimeSpan.Zero;
            if (!_desiredPlaying) _player.Pause(); _player.Volume = Math.Clamp(_settings.Volume, 0, 1);
            oldPlayer.Source = null; oldSource?.Dispose(); oldPlayer.Dispose(); DeleteLocal(oldPath);
            _cacheRequest = (uri, cachePath, info.Number("size"), info.Text("md5"));
            if (!LastPlayUsedCache && !_background) _audioCache.Start(uri, cachePath, info.Number("size"), info.Text("md5"));
            var updater = _smtc.DisplayUpdater; updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title; updater.MusicProperties.Artist = track.Artist; updater.MusicProperties.AlbumTitle = track.Album;
            updater.Thumbnail = Uri.TryCreate(track.Cover, UriKind.Absolute, out var cover) ? RandomAccessStreamReference.CreateFromUri(cover) : null;
            updater.Update(); UpdateTimeline(); Changed?.Invoke();
            if (info.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == System.Text.Json.JsonValueKind.Object) Error?.Invoke("当前为试听片段，完整播放取决于账号权限。");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { _urls.Invalidate(track.Id, quality ?? _settings.Quality); Error?.Invoke(ex.Message); Changed?.Invoke(); return false; }
        finally
        {
            if (prepared != null) { prepared.Player.Source = null; prepared.Source.Dispose(); prepared.Player.Dispose(); }
            DeleteLocal(localPath); if (ReferenceEquals(_load, load)) _load = null;
        }
    }
    public async Task NextAsync(bool wrap = true)
    {
        if (Queue.Count == 0) return;
        if (_index + 1 >= Queue.Count && !wrap) { SetPlaying(false); return; }
        var index = (_index + 1) % Queue.Count;
        if (await LoadAsync(Queue[index])) { _index = index; WarmFollowing(); }
    }
    public async Task PreviousAsync()
    {
        if (Position.TotalSeconds > 3) { Seek(TimeSpan.Zero); return; }
        if (Queue.Count == 0) return;
        var index = (_index - 1 + Queue.Count) % Queue.Count;
        if (await LoadAsync(Queue[index])) { _index = index; WarmFollowing(); }
    }
    private void SetPlaying(bool play) { _desiredPlaying = play; if (_source == null && play && Current is Track restored) { _ = LoadAsync(restored, resume: _restoredPosition > TimeSpan.Zero); return; } if (_source != null) { if (play) _player.Play(); else _player.Pause(); } UpdateTimeline(); Changed?.Invoke(); }
    public void Toggle() => SetPlaying(!_desiredPlaying);
    public Task ReloadQualityAsync() => Current is Track track ? LoadAsync(track, true, IsPlaying) : Task.CompletedTask;
    private long _pendingPlayId;
    private Task<bool>? _pendingPlayTask;
    public bool ScheduleQuality(string quality)
    {
        if (quality is not ("standard" or "higher" or "exhigh" or "lossless" or "hires" or "jyeffect" or "sky" or "jymaster" or "vivid")) return false;
        _settings.Quality = quality; WarmFollowing(); return true;
    }
    public async Task<bool> ChangeQualityAsync(string quality)
    {
        if (quality is not ("standard" or "higher" or "exhigh" or "lossless" or "hires" or "jyeffect" or "sky" or "jymaster" or "vivid")) return false;
        if (Current is not Track track) return false;
        if (!await LoadAsync(track, true, IsPlaying, quality)) return false;
        _settings.Quality = quality; return true;
    }
    public async Task SetOutputAsync(string id) { var device = id.Length == 0 ? null : await DeviceInformation.CreateFromIdAsync(id); _player.AudioDevice = device; _nextCacheLoad?.Cancel(); DisposeNextPrepared(); WarmFollowing(); }
    private async Task RestoreOutputAsync(string id) { try { await SetOutputAsync(id); } catch { } }
    public void SetVolume(double value) { _settings.Volume = _player.Volume = Math.Clamp(value, 0, 1); }
    public void Seek(TimeSpan position)
    {
        if (_source == null || !_player.PlaybackSession.CanSeek) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds))); UpdateTimeline();
    }
    private void UpdateTimeline()
    {
        _smtc.IsEnabled = true;
        _smtc.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing : Current == null ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Paused;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, EndTime = Duration, Position = Position, MinSeekTime = TimeSpan.Zero, MaxSeekTime = Duration });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _load?.Cancel(); _nextCacheLoad?.Cancel(); DisposeNextPrepared(); _urls.Dispose(); _nextAudioCache.Dispose(); _audioCache.Dispose(); _streamApi.Dispose();
        _smtc.ButtonPressed -= OnButton; _smtc.PlaybackPositionChangeRequested -= OnPosition; _smtc.IsEnabled = false;
        Detach(_player); _player.Source = null; _source?.Dispose(); _player.Dispose(); DeleteLocal(_localPath);
    }
}
