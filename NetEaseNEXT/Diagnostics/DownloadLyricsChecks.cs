using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunDownloadLyricsChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-download-lyrics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldCatalog = _downloadedLibrary; var oldPlaybackCatalog = _playback.Downloads; var oldLocalId = _localPlaylistId;
        _downloadedLibrary = new(Path.Combine(fixture, "catalog.json")); _playback.Downloads = _downloadedLibrary; _checkingLibraryWorkflow = true; _connectionOverride = false; _sessionReady = true;
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount(); _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        RestoreWindow(); _playback.SetVolume(0); _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); await NavigateAsync("downloads");
        const string original = "[ar:Evening Radio]\n[00:01.00]The night is quiet\n[00:03.00]We follow the lights";
        const string translated = "[00:01.00]夜色安静\n[00:03.00]我们追随灯火";
        var source = Path.Combine(fixture, "source.wav");
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(35);
            if (!condition()) throw new TimeoutException("Downloaded lyrics did not settle");
        }
        void Restore(PlaybackSnapshot state) { _restoringPlayback = true; try { _playback.Restore(state); } finally { _restoringPlayback = false; } }
        try
        {
            using (var writer = new BinaryWriter(File.Create(source)))
            {
                const int length = 44100 * 2 * 8;
                writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
            }
            check("Missing translation leaves the original lyrics byte-for-byte unchanged", DownloadedLyrics.Combine(original, "") == original && DownloadedLyrics.Combine(original, original) == original, null);
            var combined = DownloadedLyrics.Combine(original, translated); var parsed = LyricLine.Parse(combined);
            check("Bilingual standard lyrics preserve headers, original text and translated timestamps", combined.StartsWith("[ar:Evening Radio]") && parsed.Count == 4 && parsed[0].Text == "The night is quiet" && parsed[1].Text == "夜色安静" && parsed[0].Position == parsed[1].Position, null);
            check("Untimed translations and translation-only lyrics are retained", DownloadedLyrics.Combine("plain lyrics", "中文译文").Contains("中文译文") && DownloadedLyrics.Combine("", translated) == translated, null);
            AudioTags.Write(source, "夜航", "晚风电台", "回家的路", original, null, translation: translated);
            var read = AudioTags.Read(source);
            check("WAV tags retain independent original and translated lyrics for local playback", read.Lyrics == original && read.Translation == translated, null);
            using (var standard = TagLib.File.Create(source)) check("Other players can read both languages from the standard lyrics field", standard.Tag.Lyrics.Contains("The night is quiet") && standard.Tag.Lyrics.Contains("夜色安静"), null);
            var flac = Path.Combine(fixture, "bilingual.flac"); var streamInfo = new byte[34];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(0), 4096); System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(2), 4096);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10), ((ulong)44100 << 44) | ((ulong)1 << 41) | ((ulong)15 << 36) | 44100);
            await File.WriteAllBytesAsync(flac, "fLaC"u8.ToArray().Concat(new byte[] { 0x80, 0, 0, 34 }).Concat(streamInfo).ToArray());
            AudioTags.Write(flac, "夜航", "晚风电台", "回家的路", original, null, translation: translated);
            check("FLAC Vorbis tags preserve both synchronized lyric languages", AudioTags.Read(flac) is { Lyrics: original, Translation: translated }, null);
            var raw = await File.ReadAllBytesAsync(source);
            using var api = new ApiClient((method, p, ct) => Task.FromResult(method == "lyric" ? JsonSerializer.SerializeToElement(new { code = 200, lrc = new { lyric = original }, tlyric = new { lyric = translated } }) : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 88101, url = "https://example.invalid/bilingual.wav", type = "wav", level = "lossless", size = raw.Length, md5 = Convert.ToHexString(MD5.HashData(raw)) } })), Path.Combine(fixture, "metadata"));
            using var transport = new HttpClient(new WorkflowAudioHandler(raw, false));
            var job = new DownloadJob { Track = new(88101, "夜航", "晚风电台", "回家的路", "", 8000), Quality = "lossless", Directory = fixture, Cancellation = new() };
            await RunDownloadAsync(job, job.Cancellation, api, transport);
            read = AudioTags.Read(job.FilePath);
            check("The actual download pipeline embeds the API translation together with the original lyrics", job.Finished && read.Lyrics == original && read.Translation == translated, new { job.Status, job.Error });
            var local = LocalMusicFiles.Read(job.FilePath, _downloadedLibrary); var requests = _api.RequestCount;
            _restoringPlayback = true; await _playback.PlayAsync(local, [local]); _restoringPlayback = false; await NavigateAsync("playing");
            await Wait(() => _lyrics.Count == 2 && _lyrics[0].Translation == "夜色安静");
            check("Offline local playback renders translated lines without a lyric API request", _lyrics[1].Translation == "我们追随灯火" && _api.RequestCount == requests, null);
            if (_playback.IsPlaying) _playback.Toggle(); _playback.Seek(TimeSpan.FromSeconds(1.1)); await Wait(() => _playback.Position.TotalSeconds >= 1); UpdateProgress();
            await Wait(() => LyricsList.ContainerFromIndex(1) is Microsoft.UI.Xaml.Controls.ListViewItem { ContentTemplateRoot: Microsoft.UI.Xaml.FrameworkElement body } && body.FindName("LyricTranslation") is Microsoft.UI.Xaml.Controls.TextBlock { Opacity: >= .85 });
            await Wait(() => LyricsList.ContainerFromIndex(0) is Microsoft.UI.Xaml.Controls.ListViewItem { ContentTemplateRoot: Microsoft.UI.Xaml.FrameworkElement active } && active.FindName("LyricTranslation") is Microsoft.UI.Xaml.Controls.TextBlock block && Math.Abs(block.Opacity - .65) < .001);
            check("Upcoming translations remain readable without changing the active-line hierarchy", _activeLyricIndex == 0 && LyricsList.ContainerFromIndex(1) is Microsoft.UI.Xaml.Controls.ListViewItem { ContentTemplateRoot: Microsoft.UI.Xaml.FrameworkElement upcoming } && upcoming.FindName("LyricTranslation") is Microsoft.UI.Xaml.Controls.TextBlock { Opacity: >= .85 }, null);
            await SaveLibraryImageAsync(Root, "downloaded-translation-player.png");
            var second = Path.Combine(fixture, "second.wav"); File.Copy(source, second); var next = LocalMusicFiles.Read(second, _downloadedLibrary);
            _playback.Seek(TimeSpan.FromSeconds(3)); await Wait(() => _playback.Position.TotalSeconds >= 2.5); if (_playback.IsPlaying) _playback.Toggle();
            await NavigateAsync("downloads"); _localPlaylistId = "fixture-local-playlist";
            var snapshot = CapturePlaybackSnapshot(); snapshot.Queue = [local, next]; snapshot.OriginalQueue = [next, local]; snapshot.Mode = "Shuffle";
            var statePath = Path.Combine(fixture, "playback.bin"); PlaybackStore.Save(snapshot, statePath); var saved = PlaybackStore.Load(statePath);
            check("A local playback snapshot persists playlist identity, current song and position", saved.CurrentId == local.Id && saved.CurrentId < 0 && saved.PositionSeconds >= 2.5 && saved.LocalPlaylistId == _localPlaylistId && saved.View == "local-playlist", null);
            Restore(new()); Restore(saved);
            check("Restoring local playback keeps negative IDs, progress and shuffle order paused", _playback.Current?.Id == local.Id && _playback.Position.TotalSeconds >= 2.5 && !_playback.IsPlaying && _playback.Queue.Select(t => t.Id).SequenceEqual(new[] { local.Id, next.Id }) && _playback.Capture().OriginalQueue.Select(t => t.Id).SequenceEqual(new[] { next.Id, local.Id }), null);
            var streams = _playback.StreamRequestCount; _playback.Toggle(); await Wait(() => _playback.IsPlaying && _playback.Position.TotalSeconds >= 2.5);
            check("Pressing play resumes a restored local file at the saved position without streaming", _playback.Current?.Id == local.Id && _playback.StreamRequestCount == streams, null);
            using var startupApi = new ApiClient((method, p, ct) => throw new InvalidOperationException("Local startup must not prefetch online lyrics"), Path.Combine(fixture, "startup"));
            var startup = new StartupSession(new(), startupApi, () => saved);
            try { await startup.InitialPage; check("Local playlist startup restores its route without an online page prefetch", StartupSession.InitialView(saved) == "local-playlist" && startupApi.RequestCount == 0, null); } finally { startup.Cancel(); }
            var playingState = JsonSerializer.Deserialize<PlaybackSnapshot>(JsonSerializer.Serialize(saved))!; playingState.View = "playing";
            var playingStartup = new StartupSession(new(), startupApi, () => playingState);
            try { await playingStartup.InitialPage; check("Restoring the local player page does not send a negative song ID to the API", StartupSession.InitialView(playingState) == "playing" && startupApi.RequestCount == 0, null); } finally { playingStartup.Cancel(); }
            Restore(new()); File.Delete(job.FilePath); Restore(saved);
            check("A missing local file is skipped and cannot transfer its old progress to a different song", _playback.Queue.Count == 1 && _playback.Current?.Id == next.Id && _playback.Position == TimeSpan.Zero && !_playback.IsPlaying, null);
            Restore(new() { Queue = [next, new(88000, "Online song", "Artist", "Album", "", 30000)], OriginalQueue = [next], CurrentId = next.Id, PositionSeconds = 2 });
            check("Mixed local and online queues retain both kinds of valid song identities", _playback.Queue.Count == 2 && _playback.Queue.Any(t => t.Id < 0) && _playback.Queue.Any(t => t.Id > 0), null); Restore(new());
            await File.WriteAllTextAsync(Path.ChangeExtension(source, ".lrc"), "[00:00.00]User sidecar"); var sidecar = DownloadedLyrics.ReadLocal(source);
            check("An explicit local LRC sidecar takes precedence over embedded translations", sidecar.Original.Contains("User sidecar") && sidecar.Translation == "", null);
            AudioTags.Write(source, "夜航", "晚风电台", "回家的路", original, null);
            check("Writing lyrics without translation clears old translated metadata", AudioTags.Read(source) is { Lyrics: original, Translation.Length: 0 }, null);
        }
        finally
        {
            _lyricsLoad?.Cancel(); _restoringPlayback = true; _playback.Restore(new()); _restoringPlayback = false; _checkingLibraryWorkflow = false; _connectionOverride = null;
            _downloadedLibrary = oldCatalog; _playback.Downloads = oldPlaybackCatalog; _localPlaylistId = oldLocalId; await DeleteWorkerFixtureAsync(fixture);
        }
    }
}
