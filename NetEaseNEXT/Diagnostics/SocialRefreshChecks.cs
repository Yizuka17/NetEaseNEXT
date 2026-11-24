using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSocialRefreshChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow(); AppWindow.Move(new(0, 0)); _sessionReady = _authenticated = true; _accountId = 42;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        async Task Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed.TotalSeconds < 12) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!predicate()) throw new TimeoutException("Social refresh check did not settle");
        }
        async Task Record(string name)
        {
            if (!Environment.GetCommandLineArgs().Contains("--secondary-surfaces-capture")) return;
            await Task.Delay(200);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "secondary-stage.json"), JsonSerializer.Serialize(new { name, pid = Environment.ProcessId }));
            await Task.Delay(600);
        }
        SocialItem Private(object body)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { id = 77, time = 100000, fromUser = new { userId = 8, nickname = "测试发送者" }, msg = JsonSerializer.Serialize(body) }));
            return ParseSocialItem(document.RootElement, "conversation", "", 42);
        }
        JsonElement Events(int id, bool more = false) => JsonSerializer.SerializeToElement(new { code = 200, @event = new[] { new { id, user = new { userId = 42, nickname = "测试用户" }, eventTime = 100000 + id, json = JsonSerializer.Serialize(new { msg = "动态 " + id }) } }, events = new[] { new { id, user = new { userId = 42, nickname = "测试用户" }, eventTime = 100000 + id, json = JsonSerializer.Serialize(new { msg = "动态 " + id }) } }, more, lasttime = 100000 + id });
        try
        {
            var calls = LocalLinkFixtures.Calls.Count;
            foreach (var (kind, link) in new[] { ("album", "orpheus://album/501"), ("playlist", "https://music.163.com/#/playlist?id=501"), ("radio", "orpheus://djradio/501"), ("program", "https://music.163.com/program?id=601"), ("song", "https://music.163.com/song?id=137") })
            {
                var shared = Private(new { type = 23, msg = "推荐给你，值得听听", generalMsg = new { resName = "测试分享", cover = "", nativeUrl = "orpheus://open/?url1=" + Uri.EscapeDataString(link) } });
                check("General message " + kind + " restores its resource card", kind == "song" ? shared.SharedTrack?.Id == 137 : shared.SharedCatalog?.Kind == kind, null);
                check("General message " + kind + " preserves its attached text", shared.Text == "推荐给你，值得听听" && shared.OriginalVisibility == Visibility.Collapsed, null);
            }
            var classic = Private(new { album = new { id = "501", name = "测试专辑", blurPicUrl = "https://example.invalid/cover.jpg" }, msg = "这张专辑值得收藏" });
            check("Legacy album cards and cover fallback remain available", classic.SharedCatalog is { Kind: "album", Id: 501 } && classic.SharedCatalog.Cover.EndsWith("cover.jpg") && classic.Text == "这张专辑值得收藏", null);
            var duplicate = Private(new { song = LocalLinkFixtures.Song(137), msg = "合作歌曲" });
            check("A repeated resource name is removed without hiding its card", duplicate.SharedTrack?.Id == 137 && duplicate.TextVisibility == Visibility.Collapsed && duplicate.OriginalVisibility == Visibility.Collapsed, null);
            var nested = Private(new { msg = JsonSerializer.Serialize(new { album = new { id = 501, name = "测试专辑" }, message = "嵌套留言" }) });
            check("Nested JSON shares retain both card and message", nested.SharedCatalog?.Kind == "album" && nested.Text == "嵌套留言", null);
            var episode = Private(new { program = new { id = 601, name = "精简单集", coverUrl = "" }, msg = "单集留言" });
            check("Slim program payloads still have a usable card", episode.SharedCatalog is { Kind: "program", Id: 601 } && episode.Text == "单集留言", null);
            check("Parsing incoming shares makes no detail requests", LocalLinkFixtures.Calls.Count == calls, null);
            var unknown = Private(new { type = 23, msg = "原始通知正文", generalMsg = new { resName = "活动", nativeUrl = "orpheus://open/?url1=https%3A%2F%2Fexample.com%2Falbum%3Fid%3D501" } });
            check("Unrelated links are not misidentified as music resources", unknown.SharedCatalog == null && unknown.SharedTrack == null && unknown.Text == "原始通知正文", null);
            check("Repeated URL parameters do not break the private feed", PrivateResourceLink("https://music.163.com/album?id=1&id=501", "", "")?.Id == 501, null);
            await OpenSocialCatalogAsync(episode.SharedCatalog!);
            check("Episode details are requested only on click and open the parent podcast", _view == "programs" && _playlistId == 501 && LocalLinkFixtures.Calls.Count(c => c.Method == "dj_program_detail") == 1, null);

            _myEvents = false; EventScope.SelectedIndex = 0;
            LocalLinkFixtures.EventResponse = Events(1, true); await NavigateAsync("events");
            await Wait(() => SocialRefresh.Visualizer != null);
            check("Native refresh wraps the actual feed list", ReferenceEquals(SocialRefresh.Content, SocialList) && SocialRefresh.Visualizer!.Visibility == Visibility.Visible, null);
            var old = SocialItems.Single(); var before = LocalLinkFixtures.Calls.Count(c => c.Method == "event");
            LocalLinkFixtures.EventResponse = Events(2); LocalLinkFixtures.Hold("event");
            check("Requesting a feed refresh starts a native refresh", RequestEventsRefresh(), null);
            await Wait(() => _eventsRefreshing && SocialRefresh.Visualizer.State == RefreshVisualizerState.Refreshing);
            check("Refreshing keeps the existing feed visible", SocialItems.Count == 1 && ReferenceEquals(SocialItems[0], old) && !Busy.IsActive, null);
            check("A refresh in flight ignores repeated requests", !RequestEventsRefresh(), null);
            await Record("events-refreshing"); LocalLinkFixtures.Release();
            await Wait(() => !_eventsRefreshing && _pageLoad == null);
            check("Refreshing replaces rather than appends the first page", SocialItems.Count == 1 && SocialItems[0].Id == 2 && _offset == 1 && _socialCursor == 100002, null);
            check("Refresh sends one request with a reset cursor", LocalLinkFixtures.Calls.Count(c => c.Method == "event") == before + 1 && LocalLinkFixtures.Calls.Last(c => c.Method == "event").Query.Number("lasttime") == -1, null);
            await Wait(() => SocialRefresh.Visualizer.State == RefreshVisualizerState.Idle);
            check("Native progress stops when refresh completes", SocialRefresh.Visualizer.State == RefreshVisualizerState.Idle, null);
            await Record("events-refreshed");
            old = SocialItems.Single(); var offset = _offset; var cursor = _socialCursor;
            LocalLinkFixtures.EventFailures = 1; RequestEventsRefresh(); await Wait(() => !_eventsRefreshing && _pageLoad == null);
            check("A failed refresh retains items and pagination state", ReferenceEquals(SocialItems.Single(), old) && _offset == offset && _socialCursor == cursor && Notice.IsOpen, null);
            LocalLinkFixtures.EventResponse = Events(3); RequestEventsRefresh(); await Wait(() => !_eventsRefreshing && _pageLoad == null);
            check("A failed refresh can be retried without leaving the page", SocialItems.Single().Id == 3 && !Notice.IsOpen, null);
            LocalLinkFixtures.Hold("event"); RequestEventsRefresh(); await Wait(() => _eventsRefreshing);
            await Wait(() => LocalLinkFixtures.GateMethod.Length == 0);
            await NavigateAsync("local"); await Wait(() => !_eventsRefreshing);
            check("Leaving the feed cancels the pending refresh", LocalLinkFixtures.Canceled > 0 && !RequestEventsRefresh(), null);
            LocalLinkFixtures.Release();

            LocalLinkFixtures.PrivateResponse = JsonSerializer.SerializeToElement(new { code = 200, more = false, msgs = new object[] {
                new { id = 1, time = 100001, fromUser = new { userId = 8, nickname = "测试发送者" }, msg = JsonSerializer.Serialize(new { album = new { id = 501, name = "测试专辑" }, msg = "专辑附言" }) },
                new { id = 2, time = 100002, fromUser = new { userId = 8, nickname = "测试发送者" }, msg = JsonSerializer.Serialize(new { generalMsg = new { resName = "测试播客", nativeUrl = "orpheus://djradio/501" }, msg = "播客附言" }) },
                new { id = 3, time = 100003, fromUser = new { userId = 8, nickname = "测试发送者" }, msg = JsonSerializer.Serialize(new { song = LocalLinkFixtures.Song(137), msg = "歌曲附言" }) }
            } });
            _peerId = 8; _playlistName = "测试会话"; await NavigateAsync("conversation"); Root.UpdateLayout(); await Task.Delay(150);
            check("Incoming private history renders album podcast and song cards", SocialItems.Count == 3 && SocialItems[0].SharedCatalog?.Kind == "album" && SocialItems[1].SharedCatalog?.Kind == "radio" && SocialItems[2].SharedTrack != null, null);
            check("Private share captions are actually realized", new[] { "专辑附言", "播客附言", "歌曲附言" }.All(label => Descendants<TextBlock>(SocialList).Any(t => t.Text == label && t.ActualWidth > 0)), null);
            check("Private history does not trigger the event refresh handler", !RequestEventsRefresh() && SocialRefresh.Visualizer!.Visibility == Visibility.Collapsed, null);
            await Record("private-share-cards");
        }
        finally { LocalLinkFixtures.Release(); LocalLinkFixtures.EventResponse = LocalLinkFixtures.PrivateResponse = null; _socialResourceLoad?.Cancel(); }
    }
}
