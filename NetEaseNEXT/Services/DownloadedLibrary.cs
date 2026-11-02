using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

public sealed record DownloadedFile(long SongId, string Path, string Level, long Size, long ModifiedUtcTicks, long ArtistId = 0, long AlbumId = 0);

// Completed downloads outlive the visible queue. No authorization URLs/cookies.
public sealed class DownloadedLibrary
{
    public static DownloadedLibrary Shared { get; } = new();
    private readonly string _path;
    private readonly object _gate = new();
    private List<DownloadedFile> _files = [];
    public DownloadedLibrary(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(SettingsStore.DataDirectory, "downloaded-library.json");
        try { if (File.Exists(_path) && new FileInfo(_path).Length < 8 * 1024 * 1024) _files = JsonSerializer.Deserialize<List<DownloadedFile>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception ex) { Log.Write(ex); }
    }
    public DownloadedFile? Find(long songId, string? ceiling = null, string? directory = null)
    {
        lock (_gate)
        {
            return _files.Where(f => f.SongId == songId && AudioQuality.Rank(f.Level) >= 0 && (ceiling == null || AudioQuality.Rank(f.Level) <= AudioQuality.Rank(ceiling)) && (directory == null || SameDirectory(System.IO.Path.GetDirectoryName(f.Path)!, directory)))
                .OrderByDescending(f => AudioQuality.Rank(f.Level)).FirstOrDefault(IsAvailable);
        }
    }
    internal static bool SameDirectory(string first, string second)
    {
        try { return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(first)), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
    private static bool IsAvailable(DownloadedFile entry)
    {
        try { var file = new FileInfo(entry.Path); return file.Exists && file.Length == entry.Size && file.LastWriteTimeUtc.Ticks == entry.ModifiedUtcTicks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public DownloadedFile? FindPath(string path)
    {
        lock (_gate) return _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && IsAvailable(f));
    }
    public void RegisterLocalSources(IEnumerable<Track> tracks)
    {
        var entries = tracks.Where(t => t.HasValidLocalSource() && AudioQuality.Rank(t.SourceLevel) >= 0).Select(t => new DownloadedFile(t.SourceSongId, Path.GetFullPath(t.LocalPath), t.SourceLevel, t.SourceFileSize, t.SourceModifiedUtcTicks, t.ArtistId, t.AlbumId)).ToArray();
        lock (_gate)
        {
            var additions = entries.Where(e => !_files.Contains(e)).ToArray(); if (additions.Length == 0) return;
            var paths = additions.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Save(_files.Where(f => !paths.Contains(f.Path)).Concat(additions).ToList());
        }
    }
    private void Save(List<DownloadedFile> next)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(next)); File.Move(_path + ".tmp", _path, true); _files = next;
    }
    public void Register(DownloadJob job)
    {
        if (job.Track.Id <= 0 || AudioQuality.Rank(job.ActualLevel) < 0) throw new InvalidDataException("下载歌曲或实际音质无效");
        var file = new FileInfo(job.FilePath); if (!file.Exists || file.Length == 0) throw new FileNotFoundException("下载文件不存在");
        var entry = new DownloadedFile(job.Track.Id, file.FullName, job.ActualLevel, file.Length, file.LastWriteTimeUtc.Ticks, job.Track.ArtistId, job.Track.AlbumId);
        lock (_gate)
        {
            var next = _files.Where(f => !string.Equals(f.Path, entry.Path, StringComparison.OrdinalIgnoreCase)).Append(entry).ToList();
            Save(next);
        }
    }
}
