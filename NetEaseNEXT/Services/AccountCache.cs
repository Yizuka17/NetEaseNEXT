using NetEaseNEXT.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetEaseNEXT.Services;

internal static class AccountCache
{
    private sealed record Entry(string Session, JsonElement Profile);
    private static string PathName => Path.Combine(SettingsStore.DataDirectory, "profile.bin");
    internal static JsonElement? Load(string cookie, string? path = null)
    {
        if (cookie.Length == 0) return null;
        try
        {
            path ??= PathName;
            if (new FileInfo(path).Length > 128 * 1024) return null;
            var entry = JsonSerializer.Deserialize<Entry>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            return entry?.Session == MetadataCache.Key(cookie, "profile", null) ? entry.Profile : null;
        }
        catch { return null; }
    }
    internal static void Save(string cookie, JsonElement profile, string? path = null)
    {
        // Store only fields needed for immediate account display, with DPAPI.
        using var slim = JsonDocument.Parse(JsonSerializer.Serialize(new { userId = profile.Number("userId"), nickname = profile.Text("nickname"), avatarUrl = profile.Text("avatarUrl") }));
        path ??= PathName; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new Entry(MetadataCache.Key(cookie, "profile", null), slim.RootElement)), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
    internal static void Clear() { try { File.Delete(PathName); } catch { } }
}
