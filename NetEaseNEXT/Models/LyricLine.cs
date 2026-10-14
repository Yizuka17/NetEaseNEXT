using System.Globalization;
using System.Text.RegularExpressions;

namespace NetEaseNEXT.Models;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class LyricLine(TimeSpan position, string text)
{
    public TimeSpan Position { get; } = position;
    public string Text { get; } = text;
    public string Translation { get; private set; } = "";
    public Microsoft.UI.Xaml.Visibility TranslationVisibility => Translation.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public static List<LyricLine> Parse(string lrc, string translation = "")
    {
        var result = new List<LyricLine>();
        foreach (var line in lrc.Split('\n'))
        {
            var matches = Regex.Matches(line, @"\[(\d+):(\d+(?:\.\d+)?)\]");
            if (matches.Count == 0) continue;
            var text = line[(matches[^1].Index + matches[^1].Length)..].Trim();
            if (text.Length == 0) continue;
            foreach (Match m in matches)
                if (double.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var seconds))
                    result.Add(new(TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(seconds), text));
        }
        if (!string.IsNullOrWhiteSpace(translation))
        {
            var translated = Parse(translation).GroupBy(l => l.Position).ToDictionary(g => g.Key, g => string.Join("\n", g.Select(l => l.Text)));
            foreach (var item in result) if (translated.TryGetValue(item.Position, out var value) && value != item.Text) item.Translation = value;
        }
        return result.OrderBy(l => l.Position).ToList();
    }
}
