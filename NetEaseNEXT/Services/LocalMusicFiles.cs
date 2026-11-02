using System.Security.Cryptography;
using System.Text;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

internal static class LocalMusicFiles
{
    internal static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".wav", ".aac", ".ogg", ".opus", ".wma"];
    internal static string ArtworkDirectory => Path.Combine(SettingsStore.DataDirectory, "LocalArtwork");
    internal static string StoreCover(byte[] bytes)
    {
        Directory.CreateDirectory(ArtworkDirectory);
        var path = Path.Combine(ArtworkDirectory, Convert.ToHexString(SHA256.HashData(bytes)) + ".img");
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return path;
    }
    internal static Track Read(string path, DownloadedLibrary? downloads = null)
    {
        path = Path.GetFullPath(path);
        var info = AudioTags.Read(path);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        var id = -Math.Max(1, BitConverter.ToInt64(hash, 0) & long.MaxValue);
        var cover = info.Cover is { Length: > 0 } ? new Uri(StoreCover(info.Cover)).AbsoluteUri : "";
        var track = new Track(id, info.Title, info.Artist, info.Album, cover, info.Duration) { LocalPath = path };
        var origin = (downloads ?? DownloadedLibrary.Shared).FindPath(path); var file = new FileInfo(path);
        track.SetLocalSource(origin?.SongId ?? info.SongId, origin?.Level ?? info.Level, file.Length, file.LastWriteTimeUtc.Ticks, origin?.ArtistId ?? info.ArtistId, origin?.AlbumId ?? info.AlbumId);
        return track;
    }
    internal sealed record ScanResult(List<Track> Tracks, Dictionary<string, string> Stamps, int Failed, bool Limited);
    internal static ScanResult Scan(LocalPlaylist playlist, IReadOnlyList<Track> known, CancellationToken ct, DownloadedLibrary? downloads = null)
    {
        if (!Directory.Exists(playlist.FolderPath)) throw new DirectoryNotFoundException("绑定的文件夹不可用");
        var options = new EnumerationOptions { RecurseSubdirectories = playlist.IncludeSubfolders, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var paths = new List<string>(); var limited = false;
        foreach (var path in Directory.EnumerateFiles(playlist.FolderPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            if (paths.Count == 10000) { limited = true; break; }
            paths.Add(Path.GetFullPath(path));
        }
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        var tracks = known.DistinctBy(t => t.LocalPath, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.LocalPath, StringComparer.OrdinalIgnoreCase);
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<Track>(); var failed = 0;
        var oldStamps = new Dictionary<string, string>(playlist.FileStamps, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path); var stamp = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
                var track = oldStamps.GetValueOrDefault(path) == stamp && tracks.TryGetValue(path, out var cached) ? cached : Read(path, downloads);
                output.Add(track); stamps[path] = stamp;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                failed++; if (tracks.TryGetValue(path, out var old)) output.Add(old);
            }
        }
        return new(output, stamps, failed, limited);
    }
}

internal sealed class LocalFolderMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private int _disposed;
    internal LocalFolderMonitor(IEnumerable<LocalPlaylist> playlists, Action changed)
    {
        _debounce = new(_ => { if (Volatile.Read(ref _disposed) == 0) changed(); }, null, Timeout.Infinite, Timeout.Infinite);
        foreach (var playlist in playlists.Where(p => p.FolderPath.Length > 0).DistinctBy(p => p.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(playlist.FolderPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += Change; watcher.Created += Change; watcher.Deleted += Change; watcher.Renamed += Change;
                watcher.Error += (_, _) => Schedule(); watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Log.Write(ex); }
        }
    }
    private void Change(object sender, FileSystemEventArgs args) => Schedule();
    private void Schedule() { try { if (Volatile.Read(ref _disposed) == 0) _debounce.Change(650, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; foreach (var watcher in _watchers) watcher.Dispose(); _debounce.Dispose(); }
}
