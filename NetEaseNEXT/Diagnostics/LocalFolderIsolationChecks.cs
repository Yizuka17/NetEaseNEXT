using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLocalFolderIsolationChecksAsync(Action<string, bool, object?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-folder-isolation-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "one"); var second = Path.Combine(root, "two");
        var nested = Path.Combine(first, "nested");
        Directory.CreateDirectory(nested); Directory.CreateDirectory(second);
        var playlistA = new LocalPlaylist { Name = "A", FolderPath = first, IncludeSubfolders = true };
        var flat = new LocalPlaylist { Name = "Flat", FolderPath = first + Path.DirectorySeparatorChar, IncludeSubfolders = false };
        var playlistB = new LocalPlaylist { Name = "B", FolderPath = second, IncludeSubfolders = false };
        var oldCatalog = _downloadedLibrary;
        var events = new ConcurrentQueue<IReadOnlySet<string>>();
        var mutations = 0;
        void Changed(object? sender, NotifyCollectionChangedEventArgs args) => mutations++;
        LocalPlaylist Find(string id) => _localLibrary.Playlists.Single(p => p.Id == id);
        async Task Wait(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(40);
            if (!condition()) throw new TimeoutException("Local folder changes did not settle");
        }
        try
        {
            var original = Path.Combine(second, "original.wav");
            using (var writer = new BinaryWriter(File.Create(original)))
            {
                const int length = 44100 * 2;
                writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
            }
            _checkingLibraryWorkflow = true; _sessionReady = true;
            _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
            _localLibraryPathOverride = Path.Combine(root, "library.bin");
            _downloadedLibrary = new DownloadedLibrary(Path.Combine(root, "downloads.json"));
            _localLibrary = new() { StoragePath = _localLibraryPathOverride, Playlists = [playlistA, flat, playlistB] };
            _localLibrary.Save(); _localPlaylistId = playlistB.Id;
            await NavigateAsync("local-playlist");
            await Wait(() => Tracks.Count == 1 && Tracks[0].LocalPath == original);
            var visible = Tracks[0]; Tracks.CollectionChanged += Changed;
            TrackList.SelectedItem = visible;
            using var monitor = new LocalFolderMonitor(_localLibrary.Playlists, events.Enqueue);
            var part = Path.Combine(first, ".test-701-lossless.part.wav");
            File.Copy(original, part);
            for (var i = 0; i < 5; i++) { File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddSeconds(i)); await Task.Delay(70); }
            await Task.Delay(850);
            check("In-progress audio and tag writes do not schedule refreshes", events.IsEmpty && mutations == 0 && Find(playlistA.Id).Paths.Count == 0, null);
            var persisted = await File.ReadAllBytesAsync(_localLibraryPathOverride);
            await RefreshLocalFoldersAsync(CancellationToken.None);
            check("Even an explicit scan ignores partial audio and does not rewrite an unchanged library", Find(playlistA.Id).Paths.Count == 0 && !Find(flat.Id).Paths.Contains(part) && persisted.SequenceEqual(await File.ReadAllBytesAsync(_localLibraryPathOverride)), null);
            var completed = Path.Combine(first, "completed.wav");
            File.Move(part, completed);
            await Wait(() => Find(playlistA.Id).Paths.Contains(completed) && Find(flat.Id).Paths.Contains(completed) && !events.IsEmpty);
            check("Finalizing a download refreshes the two bindings for its directory only", events.All(ids => ids.SetEquals(new[] { playlistA.Id, flat.Id })) && !Find(playlistB.Id).Paths.Contains(completed), null);
            check("The other open playlist keeps its rows and selection intact", mutations == 0 && ReferenceEquals(Tracks.Single(), visible) && ReferenceEquals(TrackList.SelectedItem, visible), null);
            events.Clear();
            var child = Path.Combine(nested, "child.wav"); File.Copy(original, child);
            await Wait(() => Find(playlistA.Id).Paths.Contains(child) && !events.IsEmpty);
            check("A subfolder change refreshes recursive bindings only", events.All(ids => ids.SetEquals(new[] { playlistA.Id })) && !Find(flat.Id).Paths.Contains(child) && mutations == 0, null);
            Directory.Move(nested, Path.Combine(root, "moved"));
            await Wait(() => !Find(playlistA.Id).Paths.Contains(child));
            check("Moving a subfolder out is noticed without refreshing the unrelated view", mutations == 0 && ReferenceEquals(Tracks.Single(), visible), null);
            var own = Path.Combine(second, "own.wav"); File.Copy(original, own);
            await Wait(() => Tracks.Count == 2);
            check("The open playlist still updates when its own folder changes", mutations > 0 && Tracks.Any(t => t.LocalPath == own), null);
        }
        finally
        {
            Tracks.CollectionChanged -= Changed;
            StopLocalMonitoring(); _checkingLibraryWorkflow = false; _localLibraryPathOverride = null; _downloadedLibrary = oldCatalog;
            await DeleteWorkerFixtureAsync(root);
        }
    }
}
