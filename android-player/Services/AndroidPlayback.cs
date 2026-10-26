using Android.Media;

namespace NetEaseNEXTMobile.Services;

// One foreground player only. Background media sessions are intentionally out of scope.
internal sealed class AndroidPlayback : IDisposable
{
    private MediaPlayer? _player;

    public bool HasAudio => _player != null;

    public bool IsPlaying
    {
        get { try { return _player?.IsPlaying ?? false; } catch { return false; } }
    }

    public int PositionMs
    {
        get { try { return Math.Max(0, _player?.CurrentPosition ?? 0); } catch { return 0; } }
    }

    public int DurationMs
    {
        get { try { return Math.Max(0, _player?.Duration ?? 0); } catch { return 0; } }
    }

    public async Task PlayAsync(string url)
    {
        Stop();
        var next = new MediaPlayer();
        try
        {
            next.SetAudioStreamType(Android.Media.Stream.Music);
            next.SetDataSource(url);
            // Preparation may perform network I/O. Never block the UI dispatcher.
            await Task.Run(() => next.Prepare());
            next.Start();
            _player = next;
        }
        catch
        {
            next.Release();
            next.Dispose();
            throw;
        }
    }

    public void Toggle()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Start();
    }

    public void SeekTo(int milliseconds)
    {
        if (_player is null) return;
        _player.SeekTo(Math.Clamp(milliseconds, 0, Math.Max(0, DurationMs)));
    }

    public void Stop()
    {
        var old = _player;
        _player = null;
        if (old is null) return;
        try { old.Stop(); } catch { }
        old.Release();
        old.Dispose();
    }

    public void Dispose() => Stop();
}
