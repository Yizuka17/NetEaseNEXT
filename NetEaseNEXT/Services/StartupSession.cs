using System.Text.Json;

namespace NetEaseNEXT.Services;

// Starts before the XAML window is constructed; never touches UI objects.
internal sealed class StartupSession
{
    private readonly CancellationTokenSource _load = new();
    private readonly CancellationTokenSource _pageLoad;
    internal SettingsStore Settings { get; }
    internal ApiClient Api { get; }
    internal Task<JsonElement?> CachedProfile { get; }
    internal Task<PlaybackSnapshot> State { get; }
    internal Task<JsonElement?> Profile { get; }
    internal Task InitialPage { get; }
    internal static string InitialView(PlaybackSnapshot state) => state.View == "playlist" && state.PlaylistId > 0 ? "playlist" : state.View == "playing" && state.Queue.Any(t => t.Id == state.CurrentId) ? "playing" : state.Queue.Count > 0 ? "queue" : "discover";
    internal StartupSession(SettingsStore settings, ApiClient api, Func<PlaybackSnapshot>? stateLoader = null)
    {
        Settings = settings; Api = api;
        _pageLoad = CancellationTokenSource.CreateLinkedTokenSource(_load.Token);
        CachedProfile = Task.Run(() => AccountCache.Load(api.Cookie));
        State = Task.Run(stateLoader ?? (() => PlaybackStore.Load()));
        Profile = api.HasSession ? Task.Run(async () =>
        {
            var result = await api.CallCachedAsync("login_status", cancellation: _load.Token);
            return result.GetProperty("data").TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object ? (JsonElement?)profile.Clone() : null;
        }) : Task.FromResult<JsonElement?>(null);
        // Observe errors even if the window is closed before it consumes Profile.
        _ = Profile.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        InitialPage = Task.Run(async () =>
        {
            try
            {
                var state = await State; _pageLoad.Token.ThrowIfCancellationRequested();
                Diagnostics.StartupTrace.Mark("Startup initial requests: " + InitialView(state));
                switch (InitialView(state))
                {
                    case "playlist":
                        await Task.WhenAll(api.CallCachedAsync("playlist_detail", new { id = state.PlaylistId, s = 0 }, _pageLoad.Token),
                            api.CallCachedAsync("playlist_track_all", new { id = state.PlaylistId, limit = 100, offset = 0 }, _pageLoad.Token)); break;
                    case "discover": await api.DiscoverAsync(_pageLoad.Token); break;
                    case "playing": await api.CallCachedAsync("lyric", new { id = state.CurrentId }, _pageLoad.Token); break;
                }
                Diagnostics.StartupTrace.Mark("Startup initial data ready");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diagnostics.StartupTrace.Mark("Startup prefetch failed: " + ex.GetType().Name); }
        });
    }
    internal void Cancel() => _load.Cancel();
    internal async Task WaitForPageAsync(string view, CancellationToken token)
    {
        if (_pageLoad.IsCancellationRequested) return;
        if (view is not ("playlist" or "discover" or "playing") || view != InitialView(await State)) { _pageLoad.Cancel(); return; }
        await InitialPage.WaitAsync(token);
    }
}
