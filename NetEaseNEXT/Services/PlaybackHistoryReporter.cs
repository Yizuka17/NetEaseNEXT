using NetEaseNEXT.Api;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

// A small, in-memory outbox for actual playback starts. No progress polling or
// credential persistence; logout discards pending records for that account.
internal sealed class PlaybackHistoryReporter : IDisposable
{
    private sealed record Entry(long Id, long SourceId, string Cookie);
    private readonly ApiClient _api;
    private readonly object _gate = new();
    private readonly Queue<Entry> _pending = new();
    private CancellationTokenSource? _active;
    private bool _running, _disposed;
    internal Func<int, TimeSpan>? RetryDelay { get; set; }
    internal int Pending { get { lock (_gate) return _pending.Count; } }
    internal int Sent { get; private set; }
    internal PlaybackHistoryReporter(ApiClient api) { _api = api; api.SessionChanged += SessionChanged; }
    internal void Record(long id, long sourceId = 0)
    {
        if (id <= 0 || !_api.HasSession) return;
        lock (_gate)
        {
            if (_disposed) return;
            var cookie = _api.Cookie;
            if (_pending.Any(e => e.Id == id && e.Cookie == cookie)) return;
            if (_pending.Count == 64) _pending.Dequeue();
            _pending.Enqueue(new(id, sourceId, cookie));
        }
        Start();
    }
    private void SessionChanged()
    {
        lock (_gate) { _pending.Clear(); _active?.Cancel(); }
    }
    internal void NetworkAvailable() => Start();
    private void Start()
    {
        CancellationTokenSource work;
        lock (_gate)
        {
            if (_disposed || _running || _pending.Count == 0 || !_api.HasSession) return;
            _running = true; _active = work = new();
        }
        _ = SendAsync(work);
    }
    private async Task SendAsync(CancellationTokenSource work)
    {
        string cookie = "";
        try
        {
            while (true)
            {
                Entry entry;
                lock (_gate)
                {
                    if (_disposed || work.IsCancellationRequested || !_pending.TryPeek(out entry!)) return;
                    cookie = entry.Cookie;
                    if (_api.Cookie != cookie || !_api.HasSession) { _pending.Clear(); return; }
                }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (attempt > 0) await Task.Delay(RetryDelay?.Invoke(attempt) ?? TimeSpan.FromSeconds(attempt == 1 ? 2 : 5), work.Token);
                        var response = await _api.RecordPlaybackAsync(entry.Id, entry.SourceId, cookie, work.Token);
                        if (response.Number("code") != 200) throw new MusicApiException((int)response.Number("code"), "播放记录未被接受");
                        lock (_gate) { if (_pending.TryPeek(out var head) && ReferenceEquals(head, entry)) _pending.Dequeue(); Sent++; }
                        break;
                    }
                    catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        if (PlaybackService.IsTransientPlaybackFailure(error)) { if (attempt >= 2) return; continue; }
                        // Playback remains usable even when history is rejected.
                        Log.Write(new InvalidOperationException("播放记录上报未成功", error));
                        lock (_gate) { if (_pending.TryPeek(out var head) && ReferenceEquals(head, entry)) _pending.Dequeue(); }
                        break;
                    }
                }
            }
        }
        finally
        {
            bool restart;
            lock (_gate)
            {
                _running = false; if (ReferenceEquals(_active, work)) _active = null;
                restart = !_disposed && work.IsCancellationRequested && _pending.TryPeek(out var next) && next.Cookie != cookie;
            }
            work.Dispose(); if (restart) Start();
        }
    }
    public void Dispose()
    {
        _api.SessionChanged -= SessionChanged;
        lock (_gate) { _disposed = true; _pending.Clear(); _active?.Cancel(); }
    }
}
