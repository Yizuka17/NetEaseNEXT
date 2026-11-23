using System.Globalization;
using System.IO.Enumeration;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace NetEaseNEXT.Services;

internal static class FloatingCornerPolicy
{
    internal sealed record Corners(CornerRadius Overlay, CornerRadius ToolTip, string Source, bool HasCustomOverlay = false);
    internal static Corners Read(CornerRadius overlay, CornerRadius toolTip)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var mod = root.OpenSubKey(@"SOFTWARE\Windhawk\Engine\Mods\custom-corner-radius");
                if (mod == null || Number(mod.GetValue("Disabled"), 1) != 0) continue;
                using var settings = mod.OpenSubKey("Settings");
                if (settings == null) continue;
                var path = Environment.ProcessPath ?? "";
                var name = Path.GetFileName(path);
                if (settings.GetValueNames().Where(n => n.StartsWith("excludedPrograms[", StringComparison.Ordinal)).Select(n => settings.GetValue(n) as string ?? "").Where(p => p.Length > 0).Any(p => Matches(p, path) || Matches(p, name) || Matches(p, ApplicationIdentity.Id))) continue;
                return Resolve(overlay, toolTip, Number(settings.GetValue("radius"), -1), settings.GetValue("perCornerRadius") as string, Number(settings.GetValue("tooltipRadius"), -1));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return new(overlay, toolTip, "WinUI");
    }
    internal static Corners Resolve(CornerRadius overlay, CornerRadius toolTip, double radius, string? perCorner, double tooltipRadius)
    {
        static bool Valid(double value) => double.IsFinite(value) && value >= 0 && value <= 4096;
        var chosen = Valid(radius) ? new CornerRadius(radius) : overlay;
        var custom = Valid(radius);
        var parts = perCorner?.Split(',');
        if (parts is { Length: 4 })
        {
            var values = new double[4];
            if (parts.Select((part, index) => double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) && Valid(values[index])).All(valid => valid))
            { chosen = new(values[0], values[1], values[2], values[3]); custom = true; }
        }
        return new(chosen, Valid(tooltipRadius) ? new CornerRadius(tooltipRadius) : toolTip, "Windhawk", custom);
    }
    private static double Number(object? value, double fallback) => value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static bool Matches(string pattern, string value) => FileSystemName.MatchesSimpleExpression(Environment.ExpandEnvironmentVariables(pattern), value, ignoreCase: true);
}
