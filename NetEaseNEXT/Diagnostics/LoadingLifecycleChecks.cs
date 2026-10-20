using System.Diagnostics;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunBackgroundNetworkChecksAsync(Action<string, bool, object?> check, List<object> results)
    {
        RestoreWindow(); _sessionReady = true; _settings.Quality = "standard"; _playback.SetVolume(0);
        var tracks = await _api.SearchAsync("陈奕迅", 0, CancellationToken.None);
        await _playback.PlayAsync(tracks[0], tracks.Take(3));
        for (var attempt = 0; !_playback.IsPlaying && attempt < 100; attempt++) await Task.Delay(50);
        check("Network audio plays before hiding", _playback.IsPlaying, null);
        HideWindow();
        check("Hiding stops UI polling and speculative audio downloads", _hidden && !_timer.IsEnabled && !_inboxTimer.IsEnabled && !_playback.IsCaching, null);
        await _playback.NextAsync();
        for (var attempt = 0; !_playback.IsPlaying && attempt < 100; attempt++) await Task.Delay(50);
        check("Background next song obtains a usable network resource", _hidden && _playback.Current?.Id == tracks[1].Id && _playback.IsPlaying, null);
        var position = _playback.Position;
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(20));
        process.Refresh();
        results.Add(new { name = "Background network playback sample", elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
            privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1) });
        check("Network playback advances while hidden", _hidden && _playback.IsPlaying && _playback.Position > position + TimeSpan.FromSeconds(15), null);
        check("Hidden playback keeps UI polling and extra downloads stopped", !_timer.IsEnabled && !_inboxTimer.IsEnabled && !_playback.IsCaching, null);
        check("Idle API workers exit without losing the active stream", !_api.IsWorkerRunning && !_playback.IsWorkerRunning && _playback.IsPlaying, null);
        _playback.Toggle(); RestoreWindow();
        check("Restoring shows the song selected in the background", _displayedTrack?.Id == tracks[1].Id && MiniTitle.Text == tracks[1].Title && !_playback.IsPlaying, null);
    }

    private static async Task DeleteWorkerFixtureAsync(string directory)
    {
        // Process.Kill completes before Windows releases the worker's cwd.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(directory, true); return; }
            catch (IOException) when (attempt < 10) { await Task.Delay(50); }
        }
    }
    private async Task RunLoadingLifecycleChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var script = Path.Combine(fixture, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import {createInterface} from 'node:readline';
                createInterface({input:process.stdin}).on('line', async line => {
                  const r = JSON.parse(line);
                  await new Promise(resolve => setTimeout(resolve, 100));
                  process.stdout.write(JSON.stringify({id:r.id,error:'fixture failure'})+'\n');
                }).on('close',()=>process.exit(0));
                """);
            using var api = new ApiClient(script, fixture);
            var startup = new StartupSession(new(), api, () => new());
            try
            {
                var failed = false;
                try { await startup.WaitForPageAsync("discover", CancellationToken.None); }
                catch (InvalidOperationException) { failed = true; }
                check("Startup prefetch reports its first failure without an automatic second request", failed && api.RequestCount == 1, new { api.RequestCount });
                await startup.WaitForPageAsync("discover", CancellationToken.None);
                try { await api.DiscoverAsync(CancellationToken.None); } catch (InvalidOperationException) { }
                check("An explicit retry proceeds after failed startup prefetch", api.RequestCount == 2, null);
            }
            finally { startup.Cancel(); }
        }
        finally { await DeleteWorkerFixtureAsync(fixture); }

        async Task SettleAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while ((_pageLoad != null || _lyricsLoad != null) && elapsed.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(25);
            check("Restored loading finishes", _pageLoad == null && _lyricsLoad == null && !Busy.IsActive, null);
        }
        RestoreWindow();
        _sessionReady = true;
        var playlists = await _api.DiscoverAsync(CancellationToken.None);
        check("Network fixture provides a playlist", playlists.Count > 0, null);
        _playlistId = playlists[0].Id; _playlistName = playlists[0].Name;
        var playlist = NavigateAsync("playlist");
        HideWindow();
        check("Hiding records the interrupted playlist request", _pageInterrupted && _hidden, null);
        await playlist;
        RestoreWindow(); await SettleAsync();
        check("Restoring resumes playlist details and songs", Tracks.Count > 0 && !Notice.IsOpen && PlaylistDetailTitle.Text.Length > 0, new { count = Tracks.Count });
        var retained = Tracks.ToArray();
        HideWindow(); RestoreWindow(); await Task.Delay(150);
        check("A completed page survives hiding without rebuilding its collection", Tracks.SequenceEqual(retained) && !_pageInterrupted && _pageLoad == null, null);

        _query = "陈奕迅";
        await NavigateAsync("search");
        check("Search loads its first page", Tracks.Count == 50 && _offset == 50, new { count = Tracks.Count, _offset });
        var firstPage = Tracks.ToArray();
        var more = LoadPageAsync(true);
        HideWindow();
        check("Hiding remembers that the interrupted request was pagination", _pageInterrupted && _pageWasAppend, null);
        await more;
        RestoreWindow(); await SettleAsync();
        check("Restoring appends the interrupted search page and keeps existing rows", Tracks.Count == 100 && _offset == 100 && Tracks.Take(50).SequenceEqual(firstPage) && !Notice.IsOpen, new { count = Tracks.Count, _offset });

        var track = firstPage[0];
        _restoringPlayback = true; _playback.Restore(new() { Queue = [track], CurrentId = track.Id }); _restoringPlayback = false;
        var lyrics = NavigateAsync("playing");
        HideWindow(); await lyrics;
        RestoreWindow(); await SettleAsync();
        check("Restoring resumes interrupted lyrics for the current song", _lyricId == track.Id && _lyrics.Count > 0 && LyricsList.ItemsSource != null, new { count = _lyrics.Count });
        check("Lifecycle checks keep playback paused", !_playback.IsPlaying, null);
    }
}
