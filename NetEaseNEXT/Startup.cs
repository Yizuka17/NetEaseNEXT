using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _playbackSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _restoringPlayback;
    private bool _sessionReady, _initializingSession;
    private long _resumePlaylistId;
    private string _resumePlaylistName = "";
    private CancellationTokenSource? _metadataWarmLoad;
    private async void WarmVisibleMetadata()
    {
        _metadataWarmLoad?.Cancel();
        using var load = new CancellationTokenSource(); _metadataWarmLoad = load;
        try { await _api.WarmDetailsAsync(Tracks.ToArray(), load.Token); }
        finally { if (ReferenceEquals(_metadataWarmLoad, load)) _metadataWarmLoad = null; }
    }
    private async Task InitializeSessionAsync()
    {
        if (_initializingSession || _sessionReady || _exiting) return;
        _initializingSession = true;
        _restoringPlayback = true;
        try
        {
            var profileTask = _startup?.CachedProfile ?? Task.Run(() => AccountCache.Load(_api.Cookie));
            var stateTask = _startup?.State ?? Task.Run(() => PlaybackStore.Load());
            var cachedProfile = await profileTask;
            if (_hidden || _exiting) return;
            if (cachedProfile is { } profile) ApplyAccountProfile(profile);
            // Start account validation before any homepage request. Cached account
            // identity is already visible while the server check is in flight.
            var account = _api.HasSession ? UpdateAccountAsync() : Task.CompletedTask;
            var state = await stateTask;
            if (_hidden || _exiting) return;
            _resumePlaylistId = state.PlaylistId; _resumePlaylistName = state.PlaylistName;
            _playback.Restore(state);
            _sessionReady = true;
            _playlistId = state.PlaylistId; _playlistName = state.PlaylistName;
            var view = state.View == "playlist" && state.PlaylistId > 0 ? "playlist" : state.View == "playing" && _playback.Current != null ? "playing" : state.Queue.Count > 0 ? "queue" : "discover";
            await NavigateAsync(view);
            _ = _playback.WarmAsync(_playback.Queue);
            _ = account;
        }
        finally
        {
            _restoringPlayback = false;
            _initializingSession = false;
            if (!_hidden && !_exiting && _playback.Current is { } track) { _ = LoadQualityAsync(track); _ = LoadSongEngagementAsync(track); }
        }
    }
    private void SchedulePlaybackSave()
    {
        if (_runningSmokeTest || _restoringPlayback || _exiting) return;
        _playbackSaveTimer.Stop(); _playbackSaveTimer.Start();
    }
    private void SavePlaybackState()
    {
        _playbackSaveTimer.Stop();
        if (_runningSmokeTest || _restoringPlayback) return;
        try
        {
            var state = _playback.Capture(); state.PlaylistId = _resumePlaylistId; state.PlaylistName = _resumePlaylistName;
            state.View = _view is "playlist" or "playing" ? _view : _resumePlaylistId > 0 ? "playlist" : "queue";
            PlaybackStore.Save(state);
        }
        catch (Exception ex) { Log.Write(ex); }
    }
}
