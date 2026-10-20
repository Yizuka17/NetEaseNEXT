using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetEaseNEXT.Services;

internal sealed class MetadataCache
{
    private sealed record Entry(DateTimeOffset Expires, JsonElement Data);
    private readonly Dictionary<string, Entry> _memory = new();
    private readonly object _gate = new();
    private long _version;
    internal long Version => Interlocked.Read(ref _version);
    internal static string Key(string cookie, string method, object? parameters) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie + "\n" + method + "\n" + JsonSerializer.Serialize(parameters))));
    internal static (TimeSpan Lifetime, bool Disk) Policy(string method) => method switch
    {
        "lyric" or "song_detail" or "song_music_detail" or "album" or "album_sublist" or "playlist_track_all" or "playlist_detail" or "personalized" or "user_playlist" => (TimeSpan.FromMinutes(5), true),
        "login_status" or "song_red_count" or "song_like_check" => (TimeSpan.FromMinutes(5), false),
        _ => (TimeSpan.Zero, false)
    };
    internal bool TryGet(string directory, string key, bool disk, out JsonElement data)
    {
        var version = Version;
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) { data = cached.Data; return true; }
            _memory.Remove(key);
        }
        data = default;
        if (!disk) return false;
        try
        {
            var file = Path.Combine(directory, "Metadata", key + ".bin");
            var info = new FileInfo(file);
            if (info.Length > 2 * 1024 * 1024) return false;
            var entry = JsonSerializer.Deserialize<Entry>(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
            if (entry != null && entry.Expires > info.LastWriteTimeUtc.AddMinutes(5)) entry = entry with { Expires = info.LastWriteTimeUtc.AddMinutes(5) };
            if (entry == null || entry.Expires <= DateTimeOffset.UtcNow) return false;
            lock (_gate)
            {
                if (_version != version) return false;
                Remember(key, entry); data = entry.Data; return true;
            }
        }
        catch { return false; }
    }
    private void Remember(string key, Entry entry)
    {
        lock (_gate)
        {
            if (_memory.Count >= 128) _memory.Remove(_memory.MinBy(p => p.Value.Expires).Key);
            _memory[key] = entry;
        }
    }
    internal void Put(string directory, string key, JsonElement data, TimeSpan lifetime, bool disk, long? expectedVersion = null)
    {
        lock (_gate)
        {
        if (expectedVersion.HasValue && _version != expectedVersion.Value) return;
        if (data.GetRawText().Length > 1024 * 1024) return;
        var entry = new Entry(DateTimeOffset.UtcNow + lifetime, data.Clone()); Remember(key, entry);
        if (!disk) return;
        try
        {
            var folder = Path.Combine(directory, "Metadata"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, key + ".bin"); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(entry), null, DataProtectionScope.CurrentUser)); File.Move(temp, path, true); }
            finally { try { File.Delete(temp); } catch { } }
            var files = new DirectoryInfo(folder).GetFiles("*.bin").OrderBy(f => f.LastWriteTimeUtc).ToArray(); var size = files.Sum(f => f.Length);
            foreach (var file in files) { if (size <= 16 * 1024 * 1024) break; try { var length = file.Length; file.Delete(); size -= length; } catch { } }
        }
        catch { }
        }
    }
    internal void Clear(string directory)
    {
        lock (_gate)
        {
        Interlocked.Increment(ref _version); _memory.Clear();
        try { foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "Metadata"), "*.bin")) File.Delete(file); } catch { }
        }
    }
}
