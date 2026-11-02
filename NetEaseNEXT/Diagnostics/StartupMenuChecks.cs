using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;
using NetEaseNEXT.Models;
using Windows.Graphics;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunStartupMenuChecksAsync(Action<string, bool, object?> check)
    {
        if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) AppWindow.Title = "NetEaseNEXT 菜单检查";
        check("Startup does not scan the installed font collection", !_fontsLoaded && ((IEnumerable<string>)FontCombo.ItemsSource).Count() <= 3, null);
        if (await Task.Run(() => AccountCache.Load(_api.Cookie)) is { } profile)
        {
            var requests = _api.ProfileRequestCount;
            ApplyAccountProfile(profile);
            // Count account validation separately from independent page loading.
            // A hidden window intentionally defers avatar I/O.
            check("Cached identity applies without account validation and defers hidden avatar work", _authenticated && AccountName.Text.Length > 0 && AccountAvatar.DisplayName == AccountName.Text && _api.ProfileRequestCount == requests && (_hidden ? _accountAvatarLoad == null : _accountAvatarUrl == profile.Text("avatarUrl")), new { validationRequests = _api.ProfileRequestCount - requests, _hidden });
            _accountAvatarLoad?.Cancel(); ResetAccount();
        }
        RestoreWindow();
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-native-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            using var client = NativeApiFixtures.Create("startup", fixture);
            var ticks = 0; var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            pulse.Tick += (_, _) => ticks++; pulse.Start();
            try
            {
                var slow = client.CallAsync("slow", new { delay = 800 });
                var fast = client.CallAsync("fast", new { delay = 10 });
                check("A slow account-sized request does not block an independent page request", await Task.WhenAny(slow, fast) == fast, null);
                check("Out-of-order HTTP responses match the requesting task", (await fast).GetProperty("method").GetString() == "fast" && (await slow).GetProperty("method").GetString() == "slow", null);
                check("UI dispatcher continues running during HTTP requests", ticks >= 10, new { ticks });
            }
            finally { pulse.Stop(); }
            using (var cancel = new CancellationTokenSource())
            {
                var abandoned = client.CallAsync("abandoned", new { delay = 800 }, cancel.Token);
                var retained = client.CallAsync("retained", new { delay = 200 });
                await Task.Delay(50); cancel.Cancel();
                try { await abandoned; check("Canceled request stops waiting", false, null); }
                catch (OperationCanceledException) { check("Canceled request stops waiting", true, null); }
                check("Canceling one request preserves other pending responses", (await retained).GetProperty("method").GetString() == "retained", null);
            }
            using (var cancelLast = new CancellationTokenSource())
            {
                var last = client.CallAsync("last", new { delay = 800 }, cancelLast.Token);
                await Task.Delay(50); cancelLast.Cancel();
                try { await last; } catch (OperationCanceledException) { }
                check("Canceled native requests never require a worker process", !client.IsWorkerRunning, null);
            }
            try { await client.CallAsync("exit"); check("HTTP failure completes outstanding tasks", false, null); }
            catch (IOException) { check("HTTP failure completes outstanding tasks", true, null); }
            check("Requests recover after a transport failure", (await client.CallAsync("restarted")).GetProperty("method").GetString() == "restarted", null);
            using (var urls = new TrackUrls(client))
            {
                var track = new Models.Track(123, "fixture", "artist", "album", "", 60000);
                var warm = urls.WarmAsync([track], "standard");
                urls.SuspendWarm();
                var resumed = await urls.GetAsync(track, "standard", [], CancellationToken.None);
                await warm;
                check("Playback after hiding never joins a canceled URL warm-up", resumed.Info.GetProperty("id").GetInt64() == track.Id, null);
            }
            var cache = new MetadataCache(); var generation = cache.Version;
            using var payload = JsonDocument.Parse("{\"code\":200}");
            cache.Clear(fixture);
            cache.Put(fixture, "stale", payload.RootElement, TimeSpan.FromMinutes(1), true, generation);
            check("A response from before a write cannot refill invalidated metadata", !cache.TryGet(fixture, "stale", true, out _), null);
        }
        finally { if (Directory.Exists(fixture)) await DeleteWorkerFixtureAsync(fixture); }
        await RunNativeMenuChecksAsync(check);
        using var fontLoad = new CancellationTokenSource();
        await LoadFontsAsync(fontLoad.Token);
        check("Settings can load the complete font collection on demand", _fontsLoaded && ((IEnumerable<string>)FontCombo.ItemsSource).Count() > 3, null);
        RestoreWindow();
    }
}
