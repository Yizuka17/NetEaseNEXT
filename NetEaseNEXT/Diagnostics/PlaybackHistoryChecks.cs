using System.Diagnostics;
using System.Text.Json;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunPlaybackHistoryChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var folder = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 15) await Task.Delay(20);
            if (!condition()) throw new TimeoutException("Playback history check did not settle");
        }
        try
        {
            var failed = true; var attempts = 0;
            using var retryApi = new ApiClient((method, query, ct) =>
            {
                attempts++; if (failed) throw new HttpRequestException("Disconnected fixture");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200 }));
            }, folder, "MUSIC_U=history-fixture");
            using (var reporter = new PlaybackHistoryReporter(retryApi) { RetryDelay = _ => TimeSpan.FromMilliseconds(10) })
            {
                reporter.Record(42); reporter.Record(42); await Wait(() => attempts == 3); await Task.Delay(80);
                check("Offline recent-play reports use three bounded attempts and deduplicate the pending song", attempts == 3 && reporter.Pending == 1 && reporter.Sent == 0, null);
                await Task.Delay(100); check("Failed recent-play reporting does not keep polling", attempts == 3, null);
                failed = false; reporter.NetworkAvailable(); await Wait(() => reporter.Sent == 1);
                check("Connectivity recovery delivers the pending recent-play record", attempts == 4 && reporter.Pending == 0, null);
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var canceled = false;
            var delivered = new List<(long Id, string Cookie)>(); ApiClient? accountApi = null;
            using (accountApi = new ApiClient(async (method, query, ct) =>
            {
                var cookie = accountApi!.Cookie;
                if (query.Number("id") == 201)
                {
                    entered.TrySetResult(); try { await Task.Delay(30000, ct); } catch (OperationCanceledException) { canceled = true; throw; }
                }
                delivered.Add((query.Number("id"), cookie)); return JsonSerializer.SerializeToElement(new { code = 200 });
            }, folder, "MUSIC_U=first-fixture"))
            using (var reporter = new PlaybackHistoryReporter(accountApi))
            {
                reporter.Record(201); await entered.Task; accountApi.SetCookie("MUSIC_U=second-fixture"); reporter.Record(202);
                await Wait(() => reporter.Sent == 1);
                check("Account changes cancel the old outbox before accepting new-account records", canceled && delivered.Count == 1 && delivered[0] == (202L, "MUSIC_U=second-fixture"), null);
                accountApi.SetCookie(""); reporter.Record(203); await Task.Delay(50);
                check("Signed-out users do not submit listening records", delivered.Count == 1 && reporter.Pending == 0, null);
            }

            var audio = LocalLinkFixtures.Audio(folder, "linked"); var plain = LocalLinkFixtures.Audio(folder, "plain", 0);
            using var server = new LoopbackWaveServer(File.ReadAllBytes(audio.LocalPath));
            var starts = new List<long>(); var offline = false;
            using var api = new ApiClient((method, query, ct) =>
            {
                if (method == "song_play_record") { starts.Add(query.Number("id")); return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200 })); }
                if (method != "song_url_v1") throw new InvalidOperationException("Unexpected history fixture request");
                if (offline) throw new HttpRequestException("Disconnected fixture");
                var id = long.Parse(query.Text("id").Split(',')[0]);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new[] { new { id, url = server.Url, level = "standard", type = "wav", expi = 300 } } }));
            }, folder, "MUSIC_U=player-fixture");
            var settings = new SettingsStore { Volume = 0, Quality = "standard", CacheDirectory = folder, LocalOnlineInfo = true };
            using (var player = new PlaybackService(api, settings, DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(this)))
            {
                player.HistoryReportingEnabled = true; player.Downloads = new(Path.Combine(folder, "downloads.json")); player.SetBackgroundMode(true);
                var first = new Track(920001, "第一首", "测试", "测试", "", 4000); var second = new Track(920002, "第二首", "测试", "测试", "", 4000);
                player.Restore(new() { Queue = [first], CurrentId = first.Id }); await Task.Delay(80);
                check("Restoring a paused playlist never reports a song as played", starts.Count == 0, null);
                await player.PlayAsync(first, [first, second]); await Wait(() => starts.Count == 1);
                check("Real MediaPlayer playback starts one recent-play record", player.IsPlaying && starts.SequenceEqual(new[] { first.Id }), null);
                player.Toggle(); player.Toggle(); player.Seek(TimeSpan.FromMilliseconds(100)); await player.PlayAsync(first); await Task.Delay(100);
                check("Pause resume seeking and reselecting the active song do not duplicate history", starts.Count == 1, null);
                await player.ReloadQualityAsync(); await Task.Delay(100);
                check("Reloading the same song quality does not duplicate recent playback", starts.Count == 1, null);
                await player.PlayAsync(second); await Wait(() => starts.Count == 2);
                check("Changing songs reports their actual start order", starts.SequenceEqual(new[] { first.Id, second.Id }), null);
                await player.PlayAsync(plain); await Task.Delay(80);
                check("Unlinked local audio is not sent with a guessed online ID", starts.Count == 2, null);
                await player.PlayAsync(first); await Wait(() => starts.Count == 3);
                check("Returning after another local song records a new listening session", starts.Last() == first.Id, null);
                await player.PlayAsync(audio); await Wait(() => starts.Count == 4);
                check("Verified linked local playback reports its embedded NetEase song ID", starts.Last() == 137, null);
                settings.LocalOnlineInfo = false; await player.PlayAsync(plain); await player.PlayAsync(audio); await Task.Delay(80);
                check("Disabling local online information also suppresses local history reporting", starts.Count == 4, null);
                offline = true; player.RecoveryDelayOverride = _ => TimeSpan.FromSeconds(30);
                await player.PlayAsync(new Track(920003, "未加载", "", "", "", 4000)); await Task.Delay(80);
                check("Failed audio loading never produces a playback-start record", starts.Count == 4 && player.WaitingForNetwork, null);
                player.Toggle();
            }
        }
        finally { await DeleteWorkerFixtureAsync(folder); }
    }

    // Explicit opt-in: real account, actual muted playback, then server readback.
    private async Task RunPlaybackHistoryLiveChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _restoringPlayback = true; _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        check("A real signed-in session is available", _api.HasSession && !_api.IsFixture, null);
        var state = await Task.Run(() => PlaybackStore.Load());
        var track = state.Queue.FirstOrDefault(t => t.Id == state.CurrentId);
        check("The user's saved current song has a verified NetEase identity", track != null && (track.IsLocal ? track.HasValidLocalSource() : track.Id > 0) && track.ProgramId == 0 && track.PodcastId == 0, null);
        var id = track!.IsLocal ? track.SourceSongId : track.Id; var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var localInfo = _settings.LocalOnlineInfo;
        try
        {
            _settings.LocalOnlineInfo = true; _playback.SetVolume(0); _playback.SetBackgroundMode(true);
            await _playback.PlayAsync(track, [track]);
            var watch = Stopwatch.StartNew();
            while (_playback.HistoryRecordsSent == 0 && watch.Elapsed.TotalSeconds < 25) await Task.Delay(100);
            check("Actual muted playback reached the audio engine", _playback.Current?.Id == track.Id && _playback.IsPlaying, null);
            check("The server accepted the real playback-start record", _playback.HistoryRecordsSent > 0, null);
            _playback.Toggle();
            var present = false; var fresh = false;
            for (var attempt = 0; attempt < 6 && !fresh; attempt++)
            {
                var history = await _api.CallAsync("record_recent_song", new { limit = 20 });
                var data = Object(history, "data");
                if (data.TryGetProperty("list", out var list))
                    foreach (var item in list.EnumerateArray().Where(item => Object(item, "data").Number("id") == id))
                    { present = true; fresh = item.Number("playTime") >= start - 2000; }
                if (!fresh) await Task.Delay(1000);
            }
            check("The played song is present in the server's recent-play list", present, null);
            check("Server history contains a new playback timestamp from this verification", fresh, null);
        }
        finally { _settings.LocalOnlineInfo = localInfo; _restoringPlayback = false; _playback.Restore(new()); }
    }
}
