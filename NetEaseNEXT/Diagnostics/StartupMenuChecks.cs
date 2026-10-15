using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;
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
            var requests = _api.RequestCount;
            ApplyAccountProfile(profile);
            check("Cached account identity and avatar start without server validation", _authenticated && AccountName.Text.Length > 0 && _api.RequestCount == requests && _accountAvatarUrl == profile.GetProperty("avatarUrl").GetString(), null);
            _accountAvatarLoad?.Cancel(); ResetAccount();
        }
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-pipe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var script = Path.Combine(fixture, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import { createInterface } from 'node:readline';
                const input = createInterface({ input: process.stdin });
                input.on('line', async line => {
                  const request = JSON.parse(line);
                  if (request.method === 'exit') { process.exit(0); return; }
                  await new Promise(resolve => setTimeout(resolve, request.params?.delay ?? 10));
                  process.stdout.write(JSON.stringify({ id: request.id, data: { code: 200, method: request.method } }) + '\n');
                });
                input.on('close', () => process.exit(0));
                """);
            using var client = new ApiClient(script, fixture);
            var ticks = 0; var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            pulse.Tick += (_, _) => ticks++; pulse.Start();
            try
            {
                var slow = client.CallAsync("slow", new { delay = 800 });
                var fast = client.CallAsync("fast", new { delay = 10 });
                check("A slow account-sized request does not block an independent page request", await Task.WhenAny(slow, fast) == fast, null);
                check("Out-of-order pipe responses match the requesting task", (await fast).GetProperty("method").GetString() == "fast" && (await slow).GetProperty("method").GetString() == "slow", null);
                check("UI dispatcher continues running during worker startup and requests", ticks >= 10, new { ticks });
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
                check("Canceling the last interested request stops unnecessary worker work", !client.IsWorkerRunning, null);
            }
            try { await client.CallAsync("exit"); check("Worker exit completes outstanding tasks", false, null); }
            catch (IOException) { check("Worker exit completes outstanding tasks", true, null); }
            check("Request channel restarts after worker exit", (await client.CallAsync("restarted")).GetProperty("method").GetString() == "restarted", null);
            var cache = new MetadataCache(); var generation = cache.Version;
            using var payload = JsonDocument.Parse("{\"code\":200}");
            cache.Clear(fixture);
            cache.Put(fixture, "stale", payload.RootElement, TimeSpan.FromMinutes(1), true, generation);
            check("A response from before a write cannot refill invalidated metadata", !cache.TryGet(fixture, "stale", true, out _), null);
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        await RunNativeMenuChecksAsync(check);
        using var fontLoad = new CancellationTokenSource();
        await LoadFontsAsync(fontLoad.Token);
        check("Settings can load the complete font collection on demand", _fontsLoaded && ((IEnumerable<string>)FontCombo.ItemsSource).Count() > 3, null);
        RestoreWindow();
    }
}
