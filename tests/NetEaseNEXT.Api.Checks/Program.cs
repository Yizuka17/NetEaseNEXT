using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetEaseNEXT.Api;

var checks = new List<object>();
void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new Exception(name); }
using (var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/crypto.json"))))
{
    var v = vectors.RootElement;
    Check("EAPI matches the pinned upstream encryption vector", ApiEncryption.Eapi("/api/song/lyric", v.GetProperty("json").GetString()!)["params"] == v.GetProperty("eapi").GetProperty("params").GetString());
    var weapi = ApiEncryption.Weapi(v.GetProperty("json").GetString()!, new string('a', 16));
    Check("WEAPI AES and raw RSA match the pinned upstream vector", weapi["params"] == v.GetProperty("weapi").GetProperty("params").GetString() && weapi["encSecKey"] == v.GetProperty("weapi").GetProperty("encSecKey").GetString());
}
using (var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/endpoints.json"))))
foreach (var fixture in fixtures.RootElement.EnumerateArray())
{
    var method = fixture.GetProperty("method").GetString()!;
    var spec = ApiEndpoints.Create(method, fixture.GetProperty("query"));
    Check(method + " preserves the upstream endpoint and payload", spec.Path == fixture.GetProperty("path").GetString() && spec.Crypto == fixture.GetProperty("crypto").GetString() &&
        JsonNode.DeepEquals(JsonNode.Parse(ApiEncryption.Json(spec.Data)), JsonNode.Parse(fixture.GetProperty("data").GetRawText())));
}
try { ApiEndpoints.Create("api", JsonSerializer.SerializeToElement(new { uri = "/api/anything" })); Check("Unknown dispatcher rejected", false); }
catch (ArgumentException) { Check("Unknown dispatcher rejected", true); }
using (var api = new NativeApi())
{
    var qr = await api.CallAsync("login_qr_create", new { key = "native-local-check", qrimg = true });
    var image = Convert.FromBase64String(qr.GetProperty("data").GetProperty("qrimg").GetString()!.Split(',')[1]);
    Check("QR login renders a PNG without a network request", image.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
}
var events = new List<string>(); var sync = new object(); var scheduler = new ApiScheduler();
Task Run(string id, bool write, int delay, CancellationToken ct = default) => scheduler.Schedule(write, async () => { lock(sync) events.Add("start:" + id); await Task.Delay(delay, ct); lock(sync) events.Add("end:" + id); return id; }, ct);
await Task.WhenAll(Run("slow", false, 100), Run("fast", false, 5), Run("write", true, 5), Run("after", false, 5));
Check("Reads overlap and writes remain FIFO barriers", events.IndexOf("end:fast") < events.IndexOf("end:slow") && events.IndexOf("start:write") > events.IndexOf("end:slow") && events.IndexOf("start:after") > events.IndexOf("end:write"));
using (var cancel = new CancellationTokenSource())
{
    var discarded = Run("cancel", false, 1000, cancel.Token); var retained = Run("retained", false, 20); cancel.Cancel();
    try { await discarded; Check("Canceled request stops", false); } catch(OperationCanceledException) { Check("Canceled request stops", true); }
    await retained; Check("Canceling one request preserves other requests", true);
}
using (var api = new NativeApi(new FixtureHandler()))
{
    var first = await api.CallAsync("song_red_count", new { id = 123 });
    Check("Native HTTP decodes encrypted responses", first.GetProperty("data").GetProperty("count").GetInt32() == 10);
    try { await api.CallAsync("playlist_tracks", new { op = "add", pid = 9, tracks = "123" }); Check("Server failure preserved", false); }
    catch (MusicApiException ex) { Check("Server failure preserves code and message", ex.Code == 502 && ex.Message == "fixture rejected"); }
    var qr = await api.CallAsync("login_qr_check", new { key = "fixture" });
    Check("QR login preserves authorization cookies", qr.GetProperty("cookie").GetString() == "MUSIC_U=fixture-only");
    try { await api.CallAsync("send_text", new { user_ids = "42", msg = "fixture only" }); Check("Rejected private send cannot succeed", false); }
    catch (MusicApiException) { Check("Rejected private send cannot succeed", true); }
    try { await api.CallAsync("comment", new { type = 0, id = 123, t = 1, content = "fixture only" }); Check("Rejected comment cannot succeed", false); }
    catch (MusicApiException) { Check("Rejected comment cannot succeed", true); }
}
using (var legacyHandler = new LegacyHandler())
using (var legacyApi = new NativeApi(legacyHandler))
{
    var legacy = await legacyApi.CallAsync("song_download_legacy", new { id = 42L, br = 999000 }, "MUSIC_U=fixture-session; unrelated=ignored");
    Check("MusicUnlock direct endpoint uses only the official host and selected session cookies", legacyHandler.Host == "music.163.com" && legacyHandler.Cookie == "MUSIC_U=fixture-session" && legacy.GetProperty("data").GetProperty("id").GetInt64() == 42);
    try { await legacyApi.CallAsync("song_download_legacy", new { id = 42L, br = 1 }); Check("Direct source rejects an unknown bitrate", false); }
    catch (ArgumentException) { Check("Direct source rejects an unknown bitrate", true); }
}
if (args.Contains("--network"))
{
    using var api = new NativeApi();
    var discover = await api.CallAsync("personalized", new { limit = 6 });
    Check("Native WEAPI reads real recommendations", discover.GetProperty("result").GetArrayLength() > 0);
    var search = await api.CallAsync("cloudsearch", new { keywords = "陈奕迅", type = 1, limit = 5 });
    var songs = search.GetProperty("result").GetProperty("songs");
    Check("Native EAPI searches real songs", songs.GetArrayLength() > 0);
    var id = songs[0].GetProperty("id").GetInt64();
    var lyric = await api.CallAsync("lyric", new { id }); Check("Native lyrics read", lyric.TryGetProperty("lrc", out _));
    var stream = await api.CallAsync("song_url_v1", new { id, level = "standard" }); Check("Native stream URL response", stream.GetProperty("data").GetArrayLength() == 1);
    var direct = await api.CallAsync("song_download_legacy", new { id, br = 999000 }); Check("MusicUnlock official direct endpoint responds without an external provider", direct.GetProperty("data").ValueKind == JsonValueKind.Object);
    var playlist = await api.CallAsync("playlist_track_all", new { id = discover.GetProperty("result")[0].GetProperty("id").GetInt64(), limit = 5 }); Check("Native playlist composition reads tracks", playlist.GetProperty("songs").GetArrayLength() > 0);
    var qr = await api.CallAsync("login_qr_key"); Check("Native QR key available", qr.GetProperty("data").TryGetProperty("unikey", out _));
    var comments = await api.CallAsync("comment_new", new { id, type = 0, sortType = 3, pageNo = 1, pageSize = 20 }); Check("Native comments sort by time", comments.GetProperty("data").GetProperty("comments").GetArrayLength() > 0);
    var hot = await api.CallAsync("comment_new", new { id, type = 0, sortType = 2, pageNo = 1, pageSize = 20 }); Check("Native comments sort by popularity", hot.GetProperty("data").GetProperty("comments").GetArrayLength() > 0);
    await api.PreparePublishingAsync(); Check("Native publishing initializes a signed XEAPI key and a fresh token without posting", true);
}
Console.WriteLine(JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {checks.Count} checks.");

sealed class LegacyHandler : HttpMessageHandler
{
    internal string Host = "", Cookie = "";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Host = request.RequestUri!.Host; Cookie = request.Headers.GetValues("Cookie").Single();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200,\"data\":[{\"id\":42,\"url\":\"https://example.invalid/audio\",\"type\":\"flac\",\"br\":999000}]}") });
    }
}
sealed class FixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.Host == "ac.dun.163yun.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null([200,0,\"fixture-token\"])") });
        var json = path.Contains("manipulate") ? "{\"code\":502,\"msg\":\"fixture rejected\"}" : path.Contains("qrcode") ? "{\"code\":803}" : path.Contains("/msg/private/send") || path.Contains("/resource/comments/add") ? "{\"code\":200,\"data\":false}" : "{\"code\":200,\"data\":{\"count\":10}}";
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(aes.EncryptEcb(Encoding.UTF8.GetBytes(json), PaddingMode.PKCS7)) };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "MUSIC_U=fixture-only; HttpOnly; Path=/");
        return Task.FromResult(response);
    }
}
