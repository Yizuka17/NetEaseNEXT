using NetEaseNEXT.Services;

namespace NetEaseNEXT.Diagnostics;
internal static class StartupTrace
{
    public static void Mark(string stage)
    {
        if (!Environment.GetCommandLineArgs().Contains("--smoke-test")) return;
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "startup.log"), $"{DateTimeOffset.Now:O} {stage}\n");
    }
}
