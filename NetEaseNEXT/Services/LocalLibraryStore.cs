using System.Security.Cryptography;
using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

public sealed class LocalPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public string CoverPath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;
    public Dictionary<string, string> FileStamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FolderStatus { get; set; } = "";
    public override string ToString() => Name;
}
public sealed class LocalLibraryStore
{
    public List<Track> Tracks { get; set; } = [];
    public List<LocalPlaylist> Playlists { get; set; } = [];
    private static string PathName => Path.Combine(SettingsStore.DataDirectory, "local-library.bin");
    [System.Text.Json.Serialization.JsonIgnore] internal string StoragePath { get; set; } = PathName;
    public static LocalLibraryStore Load(string? path = null)
    {
        path ??= PathName;
        if (!File.Exists(path)) return new() { StoragePath = path };
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("本地音乐库文件过大");
        var library = JsonSerializer.Deserialize<LocalLibraryStore>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) ?? new();
        library.StoragePath = path; return library;
    }
    public void Save()
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        File.WriteAllBytes(StoragePath + ".tmp", ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser)); File.Move(StoragePath + ".tmp", StoragePath, true);
    }
}
