using System.Globalization;
using System.Text.RegularExpressions;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

internal static class DownloadedLyrics
{
    internal static string Combine(string original, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation) || original.Trim() == translation.Trim()) return original;
        if (string.IsNullOrWhiteSpace(original)) return translation;
        var main = LyricLine.Parse(original); var translated = LyricLine.Parse(translation);
        if (main.Count == 0 || translated.Count == 0) return original.TrimEnd() + "\n" + translation.Trim();
        var headers = original.Split('\n').Where(l => l.TrimStart().StartsWith('[') && !Regex.IsMatch(l, @"\[\d+:\d+(?:\.\d+)?\]"));
        var body = main.Concat(translated).OrderBy(l => l.Position).DistinctBy(l => (l.Position, l.Text)).Select(l =>
        {
            var ms = (long)Math.Round(l.Position.TotalMilliseconds);
            return "[" + (ms / 60000).ToString("00", CultureInfo.InvariantCulture) + ":" + ((ms % 60000) / 1000d).ToString("00.000", CultureInfo.InvariantCulture) + "]" + l.Text;
        });
        return string.Join('\n', headers.Concat(body));
    }
    internal static (string Original, string Translation) ReadLocal(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".lrc");
        if (File.Exists(sidecar)) return (File.ReadAllText(sidecar), "");
        var audio = AudioTags.Read(path); return (audio.Lyrics, audio.Translation);
    }
}
