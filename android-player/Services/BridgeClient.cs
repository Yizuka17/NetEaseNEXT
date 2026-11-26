using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NetEaseNEXTMobile.Services;

// USB loopback developer bridge. Cookies never enter the Android process.
// A standalone phone version will require a native on-device API client.
internal sealed class BridgeClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:32157/"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<JsonElement> RequestAsync(string action, object? parameters = null)
    {
        using var request = new StringContent(
            JsonSerializer.Serialize(new { action, @params = parameters ?? new { } }),
            Encoding.UTF8,
            "application/json");
        using var response = await Http.PostAsync("api", request);
        var json = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(root.TryGetProperty("error", out var err)
                ? err.ToString() : "无法连接电脑端服务，请检查 USB 调试与 adb reverse。");
        return root.GetProperty("data").Clone();
    }
}
