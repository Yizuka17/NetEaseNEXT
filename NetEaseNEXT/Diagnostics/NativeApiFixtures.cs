using System.Text.Json;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

internal static class NativeApiFixtures
{
    internal static ApiClient Create(string kind, string directory) => new(async (method, p, ct) =>
    {
        long N(string name, long fallback = 0) => p.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), out var number) ? number : fallback;
        string S(string name) => p.TryGetProperty(name, out var value) ? value.ToString() : "";
        await Task.Delay(kind == "events" ? 250 : kind == "startup" ? (int)N("delay", 10) : 80, ct);
        object body = new { code = 200 };
        if (kind == "loading") throw new InvalidOperationException("fixture failure");
        if (kind == "events") body = new { code = 200, result = Array.Empty<object>(), playlist = new { }, songs = Array.Empty<object>() };
        if (kind == "startup")
        {
            if (method == "exit") throw new IOException("fixture transport disconnected");
            body = method == "song_url_v1" ? (object)new { code = 200, data = new[] { new { id = N("id"), url = "https://example.invalid/audio", expi = 300 } } } : new { code = 200, method };
        }
        if (kind == "library" && method == "album_sublist")
        {
            var offset = N("offset");
            body = offset == 999 ? (object)new { code = 500, msg = "fixture album failure" } : new
            {
                code = 200, count = offset == 700 ? 0 : 49, hasMore = offset == 0,
                data = Enumerable.Range(0, offset == 700 ? 0 : offset == 0 ? 48 : 1).Select(i => new { id = 7000 + offset + i, name = "Album " + (offset + i), picUrl = "", artists = new[] { new { name = "Artist A" }, new { name = "Artist B" } } }).ToArray()
            };
        }
        if (kind == "locator")
        {
            var ids = S("ids").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
            body = ids.Contains(9999) ? (object)new { code = 500, msg = "fixture locator denied" } : new { code = 200, songs = ids.Reverse().Select(id => new { id, name = "Song " + id, ar = new[] { new { id = 1, name = "Artist" } }, al = new { id = 1, name = "Album", picUrl = "" }, dt = 60000 }).ToArray() };
        }
        if (kind == "theme" && method is "comment_music" or "comment_new")
        {
            var data = new { total = 1, more = false, hasMore = false, cursor = "0", comments = new[] { new { commentId = 1, content = "Theme check comment", time = 1700000000000L, user = new { nickname = "Test user", avatarUrl = "" } } } };
            body = new { code = 200, data, data.total, data.more, data.comments };
        }
        if (method == "playlist_tracks") body = N("pid") == 901 ? (object)new { code = 403, msg = "fixture removal denied" } : new { code = 200, op = S("op"), pid = N("pid"), tracks = S("tracks") };
        return JsonSerializer.SerializeToElement(body);
    }, directory);
}
