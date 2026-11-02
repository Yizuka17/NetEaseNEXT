namespace NetEaseNEXT.Services;

internal static class AudioQuality
{
    internal static readonly (string Level, string Name)[] Levels = [("standard", "标准"), ("higher", "较高"), ("exhigh", "极高"), ("lossless", "无损"), ("hires", "Hi-Res"), ("jyeffect", "高清臻音"), ("sky", "沉浸环绕声"), ("jymaster", "超清母带")];
    internal static int Rank(string level) => Array.FindIndex(Levels, q => q.Level == level);
    internal static string Name(string level) => Levels.FirstOrDefault(q => q.Level == level).Name ?? "原始音质";
    internal static string Infer(string format, long bitrate) => format.ToLowerInvariant() is "flac" or "wav" or "ape" ? "lossless" : bitrate >= 320000 ? "exhigh" : bitrate >= 192000 ? "higher" : "standard";
    internal static IEnumerable<string> Descending(string ceiling) => Levels.Take(Math.Max(0, Rank(ceiling)) + 1).Reverse().Select(q => q.Level);
}
