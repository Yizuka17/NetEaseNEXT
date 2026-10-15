using NetEaseNEXT.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetEaseNEXT.Services;

internal sealed class PlaybackSnapshot
{
    public List<Track> Queue { get; set; } = [];
    public List<Track> OriginalQueue { get; set; } = [];
    public string Quality { get; set; } = "standard";
    public long CurrentId { get; set; }
    public double PositionSeconds { get; set; }
    public string Mode { get; set; } = "Sequential";
    public long PlaylistId { get; set; }
    public string PlaylistName { get; set; } = "";
    public string View { get; set; } = "discover";
}

internal static class PlaybackStore
{
    internal static string PathName => Path.Combine(SettingsStore.DataDirectory, "playback.bin");
    internal static PlaybackSnapshot Load(string? path = null)
    {
        try
        {
            path ??= PathName;
            if (new FileInfo(path).Length > 12 * 1024 * 1024) return new();
            return JsonSerializer.Deserialize<PlaybackSnapshot>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) ?? new();
        }
        catch { return new(); }
    }
    internal static void Save(PlaybackSnapshot snapshot, string? path = null)
    {
        path ??= PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(snapshot), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
}
