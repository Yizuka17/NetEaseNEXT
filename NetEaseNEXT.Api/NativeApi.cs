using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QRCoder;

namespace NetEaseNEXT.Api;

public sealed class MusicApiException(int code, string message) : InvalidOperationException(message)
{
    public int Code { get; } = code;
}

public sealed class NativeApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private JsonElement _publicKey;
    private string _keyDevice = "";
    private readonly string _deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private const string AndroidAgent = "NeteaseMusic/9.5.61.260802021928(9005061);Dalvik/2.1.0 (Linux; U; Android 12; HBN-AL00 Build/cd737a2.0)";
    public NativeApi(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new SocketsHttpHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, string cookie = "", CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var query = JsonSerializer.SerializeToElement(parameters ?? new { }, ApiEncryption.JsonOptions);
        if (method == "song_download_legacy") return await LegacyDownloadAsync(query, cookie, timeout.Token).ConfigureAwait(false);
        if (method == "login_qr_create")
        {
            var url = "https://music.163.com/login?codekey=" + Uri.EscapeDataString(query.GetProperty("key").ToString());
            using var qr = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var image = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(qr).GetGraphic(8));
            return JsonSerializer.SerializeToElement(new { code = 200, data = new { qrurl = url, qrimg = image } });
        }
        if (method == "playlist_track_all")
        {
            var detail = await CallAsync("playlist_detail", parameters, cookie, timeout.Token).ConfigureAwait(false);
            var offset = query.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = query.TryGetProperty("limit", out var l) ? l.GetInt32() : 1000;
            var ids = detail.GetProperty("playlist").GetProperty("trackIds").EnumerateArray().Skip(offset).Take(limit).Select(item => item.GetProperty("id").GetInt64()).ToArray();
            if (ids.Length == 0) return JsonSerializer.SerializeToElement(new { code = 200, songs = Array.Empty<object>(), privileges = Array.Empty<object>() });
            return await CallAsync("song_detail", new { ids = string.Join(',', ids) }, cookie, timeout.Token).ConfigureAwait(false);
        }
        var spec = ApiEndpoints.Create(method, query);
        var result = await SendAsync(spec, cookie, timeout.Token).ConfigureAwait(false);
        if (method == "login_qr_key") return JsonSerializer.SerializeToElement(new { code = 200, data = result.Body });
        if (method == "login_status") return JsonSerializer.SerializeToElement(new { data = result.Body });
        if (method == "login_qr_check")
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(result.Body.GetRawText())!;
            data["cookie"] = string.Join(';', result.Cookies); return JsonSerializer.SerializeToElement(data);
        }
        var body = result.Body;
        if (ApiEndpoints.Writes.Contains(method) && (Failed(body, "data") || Failed(body, "success")))
            throw new MusicApiException(400, body.TryGetProperty("msg", out var message) && message.ToString().Length > 0 ? message.ToString() : "操作未成功，请重试");
        if (method.StartsWith("send_", StringComparison.Ordinal) &&
            (body.TryGetProperty("blacklist", out var blacklist) && blacklist.ValueKind == JsonValueKind.Array && blacklist.GetArrayLength() > 0 || Failed(body, "data")))
            throw new MusicApiException(403, "对方当前无法接收这条私信");
        if (method == "share_resource" && (Failed(body, "data") || Failed(body, "success"))) throw new MusicApiException(400, "发布失败，请重试");
        return body;
    }
    public async Task PreparePublishingAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var key = await GetPublicKeyAsync(_deviceId, timeout.Token).ConfigureAwait(false);
        var token = await GetTokenAsync(timeout.Token).ConfigureAwait(false);
        if (token.Length == 0 || ApiEncryption.Xeapi("type=noresource&msg=", key)["S"].Length == 0) throw new MusicApiException(400, "发布初始化失败");
    }
    private static bool Failed(JsonElement body, string name) => body.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Object && value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False);
    private static string Value(object? value) => value switch { null => "", bool flag => flag ? "true" : "false", JsonElement e when e.ValueKind is JsonValueKind.True or JsonValueKind.False => e.GetBoolean() ? "true" : "false", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
    private static Dictionary<string, string> Cookies(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('='); if (separator < 1) continue;
            try { result[item[..separator]] = Uri.UnescapeDataString(item[(separator + 1)..]); } catch (UriFormatException) { }
        }
        return result;
    }
    private async Task<(JsonElement Body, string[] Cookies)> SendAsync(ApiRequest spec, string cookie, CancellationToken ct)
    {
        var cookies = Cookies(cookie);
        string C(string name, string fallback = "") => cookies.GetValueOrDefault(name, fallback);
        var device = C("deviceId", _deviceId);
        var header = new Dictionary<string, object?>
        {
            ["osver"] = C("osver", "16.2"), ["deviceId"] = device, ["os"] = C("os", "iPhone OS"),
            ["appver"] = C("appver", "9.0.90"), ["versioncode"] = "140", ["mobilename"] = "", ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["resolution"] = "1920x1080", ["__csrf"] = C("__csrf"), ["channel"] = C("channel", "distribution"),
            ["requestId"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + RandomNumberGenerator.GetInt32(1000).ToString("000")
        };
        foreach (var name in new[] { "MUSIC_U", "MUSIC_A", "NMTID" }) if (cookies.TryGetValue(name, out var v)) header[name] = v;
        var data = new Dictionary<string, object?>(spec.Data) { ["e_r"] = false };
        var token = spec.CheckToken ? await GetTokenAsync(ct).ConfigureAwait(false) : "";
        var domain = spec.Crypto == "weapi" ? "https://music.163.com" : spec.Crypto == "xeapi" ? "https://interface3.music.163.com" : "https://interfacepc.music.163.com";
        var path = "/" + spec.Crypto + "/" + spec.Path[5..];
        using var request = new HttpRequestMessage(HttpMethod.Post, domain + path);
        request.Headers.TryAddWithoutValidation("User-Agent", spec.Crypto == "weapi" ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36" : AndroidAgent);
        Dictionary<string, string> encrypted;
        if (spec.Crypto == "weapi")
        {
            data["csrf_token"] = C("__csrf"); request.Headers.Referrer = new Uri("https://music.163.com/");
            encrypted = ApiEncryption.Weapi(ApiEncryption.Json(data));
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
        }
        else if (spec.Crypto == "xeapi")
        {
            var publicKey = await GetPublicKeyAsync(device, ct).ConfigureAwait(false);
            cookies["deviceId"] = device; cookies["sDeviceId"] = device; cookies["os"] = "android"; cookies["osver"] = "16"; cookies["appver"] = "9.5.61";
            foreach (var pair in new Dictionary<string, string> { ["X-Client-Enc-State"] = "ENCRYPTED", ["x-aeapi"] = "true", ["x-deviceid"] = device, ["x-sdeviceid"] = device, ["x-os"] = "android", ["x-osver"] = "16", ["x-appver"] = "9.5.61", ["x-buildver"] = Value(header["buildver"]) }) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            if (C("MUSIC_U").Length > 0) request.Headers.TryAddWithoutValidation("x-music-u", C("MUSIC_U"));
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
            data.Remove("e_r");
            using var content = new FormUrlEncodedContent(data.Select(p => new KeyValuePair<string, string>(p.Key, Value(p.Value))));
            encrypted = ApiEncryption.Xeapi(await content.ReadAsStringAsync(ct).ConfigureAwait(false), publicKey);
        }
        else
        {
            if (token.Length > 0) header["X-antiCheatToken"] = token;
            data["header"] = header;
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", header.Select(p => p.Key + "=" + Uri.EscapeDataString(Value(p.Value)))));
            encrypted = ApiEncryption.Eapi(spec.Path, ApiEncryption.Json(data));
        }
        if (token.Length > 0 && spec.Crypto != "eapi") request.Headers.TryAddWithoutValidation("X-antiCheatToken", token);
        request.Content = new FormUrlEncodedContent(encrypted);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        JsonElement body;
        try { body = ApiEncryption.DecodeResponse(bytes); }
        catch (Exception ex) when (ex is JsonException or CryptographicException) { throw new MusicApiException((int)response.StatusCode, "接口响应无法解析，请稍后重试"); }
        if (body.ValueKind != JsonValueKind.Object) throw new MusicApiException((int)response.StatusCode, "接口返回了无效数据");
        var code = body.TryGetProperty("code", out var c) && int.TryParse(c.ToString(), out var parsed) ? parsed : (int)response.StatusCode;
        if (!response.IsSuccessStatusCode || code is not (200 or 800 or 801 or 802 or 803))
            throw new MusicApiException(code, body.TryGetProperty("msg", out var msg) ? msg.ToString() : body.TryGetProperty("message", out msg) ? msg.ToString() : $"接口返回 {code}，请稍后重试或登录。");
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.Select(v => v.Split(';')[0]).ToArray() : [];
        return (body, setCookies);
    }
    private async Task<JsonElement> LegacyDownloadAsync(JsonElement query, string cookie, CancellationToken ct)
    {
        var id = query.GetProperty("id").GetInt64(); var bitrate = query.GetProperty("br").GetInt32();
        if (id <= 0 || bitrate is not (128000 or 192000 or 320000 or 999000)) throw new ArgumentException("无效的歌曲或下载码率");
        // Same official NetEase endpoint used by MusicUnlock at commit 58c4f9b.
        // Credentials are sent exclusively to music.163.com, never the audio CDN.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://music.163.com/api/song/enhance/player/url?id={id}&ids=%5B{id}%5D&br={bitrate}");
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies(cookie).Where(p => p.Key is "MUSIC_U" or "MUSIC_A" or "__csrf" or "NMTID").Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value))));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var root = body.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("code", out var code) || code.GetInt32() != 200)
            throw new MusicApiException(root.TryGetProperty("code", out var error) ? error.GetInt32() : (int)response.StatusCode, "网易直连接口暂不可用");
        var item = root.GetProperty("data").EnumerateArray().FirstOrDefault(p => p.TryGetProperty("id", out var songId) && songId.GetInt64() == id);
        return JsonSerializer.SerializeToElement(new { code = 200, data = item.ValueKind == JsonValueKind.Object ? item : JsonSerializer.SerializeToElement(new { }) });
    }
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        // A fresh token for this request, obtained from the provider's registration endpoint.
        var response = await _http.GetStringAsync("https://ac.dun.163yun.com/v3/b?pn=YD00000558929251", ct).ConfigureAwait(false);
        var match = Regex.Match(response, "null\\(\\[200,\\d+,\"([^\"]+)\"\\]\\)");
        if (!match.Success) throw new MusicApiException(400, "请求验证暂不可用，请稍后重试");
        return match.Groups[1].Value;
    }
    private async Task<JsonElement> GetPublicKeyAsync(string deviceId, CancellationToken ct)
    {
        await _keyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_publicKey.ValueKind == JsonValueKind.Object && _keyDevice == deviceId) return _publicKey;
            var nonce = string.Concat(Enumerable.Range(0, 16).Select(_ => RandomNumberGenerator.GetInt32(10)));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com/api/bsr/sk/get");
            request.Headers.TryAddWithoutValidation("User-Agent", AndroidAgent); request.Headers.TryAddWithoutValidation("Cookie", "deviceId=" + deviceId);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["appVersion"] = "9.5.61", ["currentKeyVersion"] = "", ["deviceId"] = deviceId, ["nonce"] = nonce, ["os"] = "android", ["requestType"] = "active", ["signature"] = ApiEncryption.Sign(timestamp, nonce), ["t1"] = "", ["t2"] = "", ["timestamp"] = timestamp, ["uid"] = "" });
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            var data = document.RootElement.GetProperty("data");
            var expected = Encoding.ASCII.GetBytes(ApiEncryption.Sign(data.GetProperty("timestamp").ToString(), nonce));
            var actual = Encoding.ASCII.GetBytes(data.GetProperty("signature").GetString() ?? "");
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new CryptographicException("接口密钥签名不匹配");
            _publicKey = ApiEncryption.DecodeKey(data.GetProperty("encryptedData").GetString()!);
            _keyDevice = deviceId;
            return _publicKey;
        }
        finally { _keyGate.Release(); }
    }
    public void Dispose() { _lifetime.Cancel(); _http.Dispose(); _lifetime.Dispose(); }
}
