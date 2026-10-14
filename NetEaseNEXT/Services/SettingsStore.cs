using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetEaseNEXT.Services;

public sealed class SettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEaseNEXT");
    public bool CloseToTray { get; set; } = true;
    public string Backdrop { get; set; } = "Mica";
    public string Theme { get; set; } = "Default";
    public bool Animations { get; set; } = true;
    public double Volume { get; set; } = .65;
    public string Quality { get; set; } = "exhigh";
    public string FontFamily { get; set; } = "Segoe UI";
    public double MicaTransparency { get; set; } = 65;
    public double AcrylicTransparency { get; set; } = 65;
    public string CacheDirectory { get; set; } = Path.Combine(DataDirectory, "Cache");
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "NetEaseNEXT");
    public string AudioDeviceId { get; set; } = "";
    public static SettingsStore Load()
    {
        try { return JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(Path.Combine(DataDirectory, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }
    public static string ReadCookie()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(DataDirectory, "session.bin")), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
    public static void SaveCookie(string cookie)
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "session.bin");
        if (cookie.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser));
    }
}
public static class Log
{
    public static void Write(Exception e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var path = Path.Combine(SettingsStore.DataDirectory, "app.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {e.GetType().Name}: {e.Message}\n{e.StackTrace}\n");
        }
        catch { }
    }
}
