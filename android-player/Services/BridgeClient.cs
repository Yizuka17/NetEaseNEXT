using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NetEaseNEXTMobile.Services;

// Local, read-only development bridge. USB adb reverse maps phone loopback
// to the computer. Never transmit account credentials over this link.
internal sealed class BridgeClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:32157/"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<JsonElement> CallAsync(string method, object parameters)
    {
        using var request = new StringContent(
            JsonSerializer.Serialize(new { method, @params = parameters }),
            Encoding.UTF8,
            "application/json");
        using var response = await Http.PostAsync("api", request);
        var json = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(root.TryGetProperty("error", out var err)
                ? err.ToString() : "无法连接本机 API。请检查 adb reverse 和 Node 服务。");

        var data = root.GetProperty("data");
        if (data.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            && code.GetInt32() is not (200 or 800 or 801 or 802 or 803))
            throw new InvalidOperationException(data.TryGetProperty("msg", out var msg)
                ? msg.ToString() : "网易云 API 返回错误");
        return data.Clone();
    }
}
