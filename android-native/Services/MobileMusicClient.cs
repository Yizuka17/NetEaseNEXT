using System.Text.Json;
using NetEaseNEXT.Api;
using NetEaseNEXT.Models;

namespace NetEaseNEXTMobile.Services;

// NetEaseNEXT.Api runs directly inside the Android app. No Node or bridge.
internal sealed class MobileMusicClient : IDisposable
{
    private readonly NativeApi _native = new();
    private readonly ApiScheduler _scheduler = new();
    // Preview keeps the login cookie only in process memory.
    private string _cookie = "";

    private Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var session = _cookie;
        return _scheduler.Schedule(ApiEndpoints.Writes.Contains(method),
            () => _native.CallAsync(method, parameters, session, ct), ct);
    }

    public async Task<(string Key, byte[] Png)> CreateQrAsync(CancellationToken ct)
    {
        var keyReply = await CallAsync("login_qr_key", null, ct);
        var key = keyReply.GetProperty("data").Text("unikey");
        if (key.Length == 0) throw new InvalidOperationException("无法获取二维码密钥");
        var reply = await CallAsync("login_qr_create", new { key, qrimg = true }, ct);
        var image = reply.GetProperty("data").Text("qrimg");
        if (!image.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            throw new InvalidOperationException("登录二维码格式错误");
        return (key, Convert.FromBase64String(image[(image.IndexOf(',') + 1)..]));
    }

    public async Task<(int Code, JsonElement? Profile)> CheckQrAsync(string key, CancellationToken ct)
    {
        var response = await CallAsync("login_qr_check", new { key }, ct);
        var code = response.GetProperty("code").GetInt32();
        if (code != 803) return (code, null);
        var cookie = response.Text("cookie");
        if (cookie.Length == 0) throw new InvalidOperationException("登录没有返回会话");
        _cookie = cookie;
        try { return (code, await GetProfileAsync(ct)); }
        catch { _cookie = ""; throw; }
    }

    private async Task<JsonElement> GetProfileAsync(CancellationToken ct)
    {
        var response = await CallAsync("login_status", null, ct);
        var profile = response.GetProperty("data").GetProperty("profile");
        if (profile.ValueKind != JsonValueKind.Object || profile.Number("userId") <= 0)
            throw new InvalidOperationException("账号登录已失效");
        return profile.Clone();
    }

    public async Task<IReadOnlyList<Playlist>> MyPlaylistsAsync(long uid, CancellationToken ct)
    {
        var data = await CallAsync("user_playlist", new { uid, limit = 50, offset = 0 }, ct);
        return data.GetProperty("playlist").EnumerateArray().Select(Playlist.FromJson).ToArray();
    }

    public async Task<IReadOnlyList<Track>> PlaylistTracksAsync(long id, CancellationToken ct)
    {
        var data = await CallAsync("playlist_track_all", new { id, limit = 100, offset = 0 }, ct);
        return data.GetProperty("songs").EnumerateArray().Select(Track.FromJson).ToArray();
    }

    public async Task<string> GetStreamUrlAsync(long id, CancellationToken ct)
    {
        var data = await CallAsync("song_url_v1", new { id = id.ToString(), level = "standard" }, ct);
        foreach (var item in data.GetProperty("data").EnumerateArray())
        {
            if (item.Number("id") != id) continue;
            var url = item.Text("url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
                return url;
        }
        throw new InvalidOperationException("没有可用的播放地址（可能受账号权益限制）");
    }

    public void SignOut() => _cookie = "";
    public void Dispose() { _cookie = ""; _native.Dispose(); }
}
