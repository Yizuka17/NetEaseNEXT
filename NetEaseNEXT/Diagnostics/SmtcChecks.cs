using System.Diagnostics;
using Windows.Media;
using Windows.Media.Control;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSmtcChecksAsync(Action<string, bool, object?> check, List<object> results)
    {
        RestoreWindow(); _sessionReady = true; _settings.Quality = "standard"; _playback.SetVolume(0);
        var tracks = await _api.SearchAsync("陈奕迅", 0, CancellationToken.None);
        var first = new Models.Track(tracks[0].Id, "NetEaseNEXT SMTC check A", tracks[0].Artist, tracks[0].Album, tracks[0].Cover, tracks[0].Duration);
        var second = new Models.Track(tracks[1].Id, "NetEaseNEXT SMTC check B", tracks[1].Artist, tracks[1].Album, tracks[1].Cover, tracks[1].Duration);
        async Task<bool> Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            return predicate();
        }
        await _playback.PlayAsync(first, [first, second]);
        check("SMTC fixture plays silently", await Wait(() => _playback.IsPlaying), null);
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        GlobalSystemMediaTransportControlsSession? session = null;
        for (var attempt = 0; session == null && attempt < 40; attempt++)
        {
            foreach (var candidate in manager.GetSessions())
                if ((await candidate.TryGetMediaPropertiesAsync()).Title == first.Title) { session = candidate; break; }
            if (session == null) await Task.Delay(50);
        }
        check("Windows enumerates the current song's real media session", session != null, null);
        var active = session!;
        var metadata = await active.TryGetMediaPropertiesAsync();
        results.Add(new { name = "SMTC identity observation", expected = ApplicationIdentity.Id, actual = active.SourceAppUserModelId,
            packaged = ApplicationIdentity.IsPackaged, matched = ApplicationIdentity.Id == active.SourceAppUserModelId });
        check("System media metadata matches the playing song", metadata.Title == first.Title && metadata.Artist == first.Artist && metadata.AlbumTitle == first.Album, null);
        check("System media card reports playing", active.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, null);
        check("System pause command reaches the player", await active.TryPauseAsync() && await Wait(() => !_playback.IsPlaying), null);
        check("System play command reaches the player", await active.TryPlayAsync() && await Wait(() => _playback.IsPlaying), null);
        check("System next command reaches the player", await active.TrySkipNextAsync() && await Wait(() => _playback.Current?.Id == second.Id && _playback.IsPlaying), null);
        check("System seek command reaches the player", await active.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(10).Ticks) && await Wait(() => _playback.Position.TotalSeconds >= 9), null);
        for (var attempt = 0; attempt < 80; attempt++)
        {
            metadata = await active.TryGetMediaPropertiesAsync();
            if (metadata.Title == second.Title && metadata.Thumbnail != null) break;
            await Task.Delay(50);
        }
        check("System card follows the next song", metadata.Title == second.Title, null);
        if (metadata.Thumbnail != null)
        {
            using var thumbnail = await metadata.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            check("System card artwork is readable", thumbnail.Size > 0, new { thumbnail.Size });
        }
        else check("System card artwork is readable", false, null);
        HideWindow();
        check("System controls keep working while hidden", await active.TryPauseAsync() && await Wait(() => !_playback.IsPlaying) && _hidden, null);
        check("System playback starts while hidden", await active.TryPlayAsync() && await Wait(() => _playback.IsPlaying) && _hidden, null);
        await active.TryPauseAsync(); await Wait(() => !_playback.IsPlaying);
        RestoreWindow();
        _playback.Restore(new() { Queue = [first, second], CurrentId = first.Id, PositionSeconds = 7 });
        for (var attempt = 0; attempt < 80; attempt++)
        {
            metadata = await active.TryGetMediaPropertiesAsync();
            if (metadata.Title == first.Title && metadata.Thumbnail != null) break;
            await Task.Delay(50);
        }
        check("Restoring a paused queue publishes song details without starting audio", metadata.Title == first.Title && metadata.Artist == first.Artist && metadata.AlbumTitle == first.Album && !_playback.IsPlaying && _playback.Position.TotalSeconds == 7, null);
        check("Restored song artwork reaches the system session", metadata.Thumbnail != null, null);
    }
}
