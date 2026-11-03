using Android.Media;

namespace NetEaseNEXTMobile.Services;

// Foreground-only prototype, not a background media service.
internal sealed class AndroidPlayback : IDisposable
{
    private MediaPlayer? _player;
    public bool HasAudio => _player != null;
    public bool IsPlaying { get { try { return _player?.IsPlaying ?? false; } catch { return false; } } }
    public int PositionMs { get { try { return Math.Max(0, _player?.CurrentPosition ?? 0); } catch { return 0; } } }
    public int DurationMs { get { try { return Math.Max(0, _player?.Duration ?? 0); } catch { return 0; } } }

    public async Task PlayAsync(string url)
    {
        Stop();
        var next = new MediaPlayer();
        try
        {
            using (var audio = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build())
                next.SetAudioAttributes(audio);
            next.SetDataSource(url);
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

    public void SeekTo(int position)
    {
        if (_player is null) return;
        _player.SeekTo(Math.Clamp(position, 0, Math.Max(0, DurationMs)));
    }

    public void Stop()
    {
        var player = _player;
        _player = null;
        if (player is null) return;
        try { player.Stop(); } catch { }
        player.Release();
        player.Dispose();
    }
    public void Dispose() => Stop();
}
