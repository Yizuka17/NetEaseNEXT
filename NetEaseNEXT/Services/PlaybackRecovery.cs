using System.Net;
using System.Net.Http;
using NetEaseNEXT.Api;
using NetEaseNEXT.Models;
using Windows.Media.Playback;

namespace NetEaseNEXT.Services;

public sealed partial class PlaybackService
{
    private sealed record RecoveryRequest(Track Track, bool Resume, string Quality, Track[]? Context, string Cookie);
    private sealed class NetworkAudioException(string message) : IOException(message);
    private RecoveryRequest? _networkRecovery;
    private CancellationTokenSource? _recoveryWait, _bufferingWait;
    private int _networkAttempts;
    private bool _recoveryRunning, _mediaFailed, _activeStreamRemote;
    private static readonly int[] RecoverySeconds = [2, 5, 15, 30, 60];
    internal Func<int, TimeSpan>? RecoveryDelayOverride { get; set; }
    internal bool WaitingForNetwork => _networkRecovery != null && _desiredPlaying;
    internal long PendingRecoveryTrackId => _networkRecovery?.Track.Id ?? 0;
    internal static bool IsTransientPlaybackFailure(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode == null || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        MusicApiException api => api.Code is 408 or 429 or >= 500,
        TimeoutException or OperationCanceledException => true,
        IOException and not (FileNotFoundException or DirectoryNotFoundException) => true,
        _ => error.InnerException != null && IsTransientPlaybackFailure(error.InnerException)
    };
    private void CancelBufferingWait() { var wait = _bufferingWait; _bufferingWait = null; wait?.Cancel(); }
    private void CancelNetworkRecovery()
    {
        var wait = _recoveryWait; _recoveryWait = null; wait?.Cancel();
        _networkRecovery = null; _networkAttempts = 0; CancelBufferingWait();
    }
    private bool QueueNetworkRecovery(Track track, bool resume, string? quality, IEnumerable<Track>? context)
    {
        if (_disposed || !_desiredPlaying || track.IsLocal) return false;
        if (_networkRecovery is not { } current || current.Track.Id != track.Id || current.Cookie != _streamApi.Cookie)
        {
            _networkAttempts = 0;
            _networkRecovery = new(track, resume, quality ?? _settings.Quality, context?.ToArray(), _streamApi.Cookie);
        }
        if (!_recoveryRunning && _recoveryWait == null) ScheduleNetworkRecovery(false);
        Changed?.Invoke(); return true;
    }
    private void ScheduleNetworkRecovery(bool immediate)
    {
        if (_networkRecovery is not { } request || !_desiredPlaying || _disposed || _recoveryRunning || !immediate && _networkAttempts >= RecoverySeconds.Length) return;
        var previous = _recoveryWait; _recoveryWait = null; previous?.Cancel();
        var wait = new CancellationTokenSource(); _recoveryWait = wait;
        var delay = immediate ? TimeSpan.Zero : RecoveryDelayOverride?.Invoke(_networkAttempts) ?? TimeSpan.FromSeconds(RecoverySeconds[Math.Min(_networkAttempts, RecoverySeconds.Length - 1)]);
        _ = RecoverNetworkAsync(request, wait, delay);
    }
    private async Task RecoverNetworkAsync(RecoveryRequest request, CancellationTokenSource wait, TimeSpan delay)
    {
        var started = false;
        try
        {
            await Task.Delay(delay, wait.Token);
            if (wait.IsCancellationRequested || _disposed || !_desiredPlaying || _recoveryRunning || !ReferenceEquals(request, _networkRecovery)) return;
            if (_streamApi.Cookie != request.Cookie) { CancelNetworkRecovery(); return; }
            if (ReferenceEquals(_recoveryWait, wait)) _recoveryWait = null;
            _recoveryRunning = started = true; _networkAttempts++;
            _urls.Invalidate(request.Track.Id, request.Quality);
            await PlayCoreAsync(request.Track, request.Context, request);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex); CancelNetworkRecovery(); }
        finally
        {
            if (ReferenceEquals(_recoveryWait, wait)) _recoveryWait = null;
            wait.Dispose();
            if (started) { _recoveryRunning = false; if (_networkRecovery != null) ScheduleNetworkRecovery(false); }
        }
    }
    internal void NetworkAvailable()
    {
        if (_disposed) return;
        _history?.NetworkAvailable();
        if (_networkRecovery != null && _desiredPlaying) { _networkAttempts = 0; ScheduleNetworkRecovery(true); }
        else
        {
            if (_desiredPlaying && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering) ObserveBuffering(TimeSpan.FromSeconds(3));
            if (!_background) WarmFollowing();
        }
    }
    private void ObserveBuffering(TimeSpan? grace = null)
    {
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Buffering || !_desiredPlaying || !_activeStreamRemote || Current == null)
        { CancelBufferingWait(); return; }
        if (_bufferingWait != null && grace == null) return;
        CancelBufferingWait();
        var wait = new CancellationTokenSource(); _bufferingWait = wait;
        var track = Current; var player = _player;
        _ = CheckBufferingAsync();
        async Task CheckBufferingAsync()
        {
            try
            {
                await Task.Delay(grace ?? TimeSpan.FromSeconds(15), wait.Token);
                if (!_disposed && !wait.IsCancellationRequested && _desiredPlaying && ReferenceEquals(player, _player) && Current?.Id == track.Id && player.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering)
                { _restoredPosition = Position; _mediaFailed = true; QueueNetworkRecovery(track, true, CurrentQuality, null); }
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(_bufferingWait, wait)) _bufferingWait = null; wait.Dispose(); }
        }
    }
}
