using System.Security.Cryptography;
using System.Text;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

internal static class LocalMusicFiles
{
    internal static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".wav", ".aac", ".ogg", ".opus", ".wma"];
    internal static bool IsDownloadPartial(string path) => Path.GetFileName(path).StartsWith('.') && Path.GetFileNameWithoutExtension(path).EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    internal static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !IsDownloadPartial(path);
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
        var excluded = playlist.ExcludedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(playlist.FolderPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAudioFile(path)) continue;
            if (excluded.Contains(Path.GetFullPath(path))) continue;
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
    private readonly object _sync = new();
    private readonly HashSet<string> _dirty = [];
    private readonly Timer _debounce;
    private int _disposed;
    internal LocalFolderMonitor(IEnumerable<LocalPlaylist> playlists, Action<IReadOnlySet<string>> changed)
    {
        _debounce = new(_ =>
        {
            HashSet<string> batch;
            lock (_sync)
            {
                if (_disposed != 0 || _dirty.Count == 0) return;
                batch = [.. _dirty]; _dirty.Clear();
            }
            changed(batch);
        }, null, Timeout.Infinite, Timeout.Infinite);
        var bindings = new List<(string Root, string Id, bool Recursive)>();
        foreach (var playlist in playlists.Where(p => p.FolderPath.Length > 0))
        {
            try { bindings.Add((Path.TrimEndingDirectorySeparator(Path.GetFullPath(playlist.FolderPath)), playlist.Id, playlist.IncludeSubfolders)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { Log.Write(ex); }
        }
        foreach (var group in bindings.GroupBy(p => p.Root, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            var recursive = entries.Where(p => p.Recursive).Select(p => p.Id).ToArray();
            var all = entries.Select(p => p.Id).ToArray();
            var root = group.Key;
            bool Direct(string path) => string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase);
            void FileChanged(object sender, FileSystemEventArgs args)
            {
                var path = args.FullPath;
                var previous = (args as RenamedEventArgs)?.OldFullPath;
                var hasNew = LocalMusicFiles.IsAudioFile(path);
                var hasOld = previous != null && LocalMusicFiles.IsAudioFile(previous);
                if (hasNew || hasOld) Schedule(hasNew && Direct(path) || hasOld && Direct(previous!) ? all : recursive);
            }
            Watch(root, recursive.Length > 0, NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, FileChanged, all);
            if (recursive.Length > 0) Watch(root, true, NotifyFilters.DirectoryName, (_, _) => Schedule(recursive), recursive);
        }
    }
    private void Watch(string root, bool recursive, NotifyFilters filters, FileSystemEventHandler changed, string[] ids)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new(root) { IncludeSubdirectories = recursive, NotifyFilter = filters };
            watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
            watcher.Renamed += (sender, args) => changed(sender, args);
            watcher.Error += (_, _) => Schedule(ids); watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher?.Dispose(); Log.Write(ex);
        }
    }
    private void Schedule(string[] ids)
    {
        if (ids.Length == 0) return;
        lock (_sync)
        {
            if (_disposed != 0) return;
            var start = _dirty.Count == 0;
            _dirty.UnionWith(ids);
            if (start) _debounce.Change(650, Timeout.Infinite);
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0) return;
            _disposed = 1; _debounce.Dispose();
        }
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
