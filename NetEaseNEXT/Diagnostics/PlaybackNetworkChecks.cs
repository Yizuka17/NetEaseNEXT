using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunPlaybackNetworkChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var folder = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-network-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var oldDownloads = _downloadedLibrary; var oldPlayerDownloads = _playback.Downloads; var oldPlayback = _playback.Capture();
        async Task Wait(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed.TotalSeconds < 15) await Task.Delay(25);
            if (!predicate()) throw new TimeoutException("Playback recovery check did not settle");
        }
        try
        {
            var wave = LocalLinkFixtures.Audio(folder, "source");
            using var server = new LoopbackWaveServer(File.ReadAllBytes(wave.LocalPath));
            var offline = true; var requests = 0; long heldId = 0;
            TaskCompletionSource? heldRequest = null;
            using var api = new ApiClient(async (method, parameters, ct) =>
            {
                await Task.Delay(10, ct);
                if (method != "song_url_v1") throw new InvalidOperationException("Unexpected playback fixture request");
                requests++;
                if (offline) throw new HttpRequestException("Simulated disconnected network");
                var id = long.Parse(parameters.Text("id").Split(',')[0]);
                if (id == heldId && heldRequest != null) await heldRequest.Task.WaitAsync(ct);
                return JsonSerializer.SerializeToElement(new { code = 200, data = new[] { new { id, url = server.Url, level = "standard", type = "wav", size = 0, expi = 300 } } });
            }, folder, "MUSIC_U=network-fixture");
            var settings = new SettingsStore { Volume = 0, Quality = "standard", CacheDirectory = folder };
            using (var player = new PlaybackService(api, settings, DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(this)))
            {
                player.Downloads = new(Path.Combine(folder, "network-downloads.json")); player.SetBackgroundMode(true);
                player.RecoveryDelayOverride = _ => TimeSpan.FromSeconds(30);
                var first = new Track(910001, "第一首", "测试", "测试", "", 4000);
                var second = new Track(910002, "第二首", "测试", "测试", "", 4000);
                var third = new Track(910003, "第三首", "测试", "测试", "", 4000);
                await player.PlayAsync(first, [first, second]);
                check("A failed initial request retains its intended track", player.WaitingForNetwork && player.PendingRecoveryTrackId == first.Id, null);
                offline = false; player.NetworkAvailable(); await Wait(() => player.Current?.Id == first.Id && player.IsPlaying);
                check("Connectivity recovery restores the initial playlist context", player.Queue.Select(t => t.Id).SequenceEqual(new[] { first.Id, second.Id }) && !player.WaitingForNetwork, null);
                offline = true; player.Seek(player.Duration - TimeSpan.FromMilliseconds(180));
                await Wait(() => player.PendingRecoveryTrackId == second.Id);
                check("A natural song end retains the failed next-song target", player.Current?.Id == first.Id && player.WaitingForNetwork, null);
                offline = false; player.NetworkAvailable(); await Wait(() => player.Current?.Id == second.Id && player.IsPlaying);
                check("Network recovery advances the queue without another click", player.Queue.Count == 2 && !player.WaitingForNetwork, null);
                offline = true; await player.PlayAsync(third, [first, second, third]);
                player.Toggle(); var pausedRequests = requests; offline = false; player.NetworkAvailable(); await Task.Delay(180);
                check("Manual pause cancels pending network recovery", !player.WaitingForNetwork && !player.IsPlaying && player.Current?.Id == second.Id && requests == pausedRequests, null);
                offline = true; await player.PlayAsync(third, [third]); offline = false; await player.PlayAsync(first, [first]); await Task.Delay(100);
                check("Manual song selection cancels the stale recovery target", player.Current?.Id == first.Id && player.Queue.Count == 1 && player.PendingRecoveryTrackId == 0, null);
                var fourth = new Track(910004, "等待中的歌曲", "测试", "测试", "", 4000);
                heldId = fourth.Id; heldRequest = new(TaskCreationOptions.RunContinuationsAsynchronously); var beforeHeld = requests;
                var switching = player.PlayAsync(fourth, [first, fourth]); await Wait(() => requests > beforeHeld);
                await player.PlayAsync(first, [first]); await switching; heldRequest.TrySetResult(); heldId = 0; await Task.Delay(100);
                check("Reselecting the current song cancels a different in-flight load", player.Current?.Id == first.Id && player.Queue.Count == 1 && player.PendingRecoveryTrackId == 0, null);
                player.Restore(new()); offline = true; player.RecoveryDelayOverride = _ => TimeSpan.FromMilliseconds(20); var budgetStart = requests;
                await player.PlayAsync(third, [third]); await Wait(() => requests >= budgetStart + 6); await Task.Delay(180);
                check("Disconnected playback uses a bounded retry burst", requests == budgetStart + 6 && player.WaitingForNetwork, new { attempts = requests - budgetStart });
                player.Toggle();
                check("Permanent access failures are not network retries", !PlaybackService.IsTransientPlaybackFailure(new NetEaseNEXT.Api.MusicApiException(403, "denied")) && !PlaybackService.IsTransientPlaybackFailure(new InvalidOperationException("No playable source")), null);
                check("Timeout and transport failures remain recoverable", PlaybackService.IsTransientPlaybackFailure(new TimeoutException()) && PlaybackService.IsTransientPlaybackFailure(new HttpRequestException()), null);
            }

            var input = await StorageFile.GetFileFromPathAsync(wave.LocalPath);
            var directory = await StorageFolder.GetFolderFromPathAsync(folder);
            var flac = await directory.CreateFileAsync("embedded.flac", CreationCollisionOption.ReplaceExisting);
            var conversion = await new MediaTranscoder().PrepareFileTranscodeAsync(input, flac, MediaEncodingProfile.CreateFlac(AudioEncodingQuality.Auto));
            check("The metadata fixture is encoded as a real FLAC file", conversion.CanTranscode, null);
            await conversion.TranscodeAsync();
            AudioTags.Write(flac.Path, "文件里的歌名", "文件里的歌手", "文件里的专辑", "[00:00.00]原词", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")), 910050, "lossless", 301, 401, "[00:00.00]翻译");
            var online = new Track(910050, "远端歌名", "远端歌手", "远端专辑", "https://example.invalid/remote-cover.png", 4000);
            var library = new DownloadedLibrary(Path.Combine(folder, "downloaded.json"));
            library.Register(new DownloadJob { Track = online, Quality = "lossless", FilePath = flac.Path, ActualLevel = "lossless" });
            _downloadedLibrary = _playback.Downloads = library; _restoringPlayback = true;
            var apiRequests = _api.RequestCount; await _playback.PlayAsync(online, [online]); _restoringPlayback = false;
            var current = _playback.Current!;
            check("A streamed playlist plays the matching downloaded FLAC", current.Id == online.Id && !current.IsLocal && _playback.CurrentDownloadedPath == flac.Path && _playback.CurrentQuality == "lossless", null);
            check("Playback metadata and cover come from embedded file tags", current.HasDownloadedMetadata && current.Title == "文件里的歌名" && current.Artist == "文件里的歌手" && current.Album == "文件里的专辑" && Uri.TryCreate(current.Cover, UriKind.Absolute, out var cover) && cover.IsFile && File.Exists(cover.LocalPath), null);
            check("File metadata retains artist album and streaming identity", current.ArtistId == 301 && current.AlbumId == 401 && current.Id == online.Id, null);
            await LoadQualityAsync(current);
            check("Downloaded playback does not auto-fetch remote quality details", _api.RequestCount == apiRequests, null);
            await NavigateAsync("playing");
            check("Downloaded lyrics use the embedded original and translation", _lyrics.Any(l => l.Text.Contains("原词")) && _lyrics.Any(l => l.Translation.Contains("翻译")) && _api.RequestCount == apiRequests, null);
            _playback.Toggle();
        }
        finally
        {
            _restoringPlayback = false; _downloadedLibrary = oldDownloads; _playback.Downloads = oldPlayerDownloads; _playback.Restore(oldPlayback);
            if (!Path.GetFullPath(folder).StartsWith(Path.Combine(Path.GetTempPath(), "NetEaseNEXT-network-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path");
            await DeleteWorkerFixtureAsync(folder);
        }
    }

    private sealed class LoopbackWaveServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _audio;
        internal string Url { get; }
        internal LoopbackWaveServer(byte[] audio)
        {
            _audio = audio; _listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/audio.wav"; _ = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = ServeAsync(client); } }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync(_stop.Token) ?? ""; string? line; var start = 0; var end = _audio.Length - 1; var partial = false;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    { var range = line[13..].Split('-', 2); partial = int.TryParse(range[0], out start); if (range.Length > 1 && int.TryParse(range[1], out var last)) end = Math.Min(end, last); }
                start = Math.Clamp(start, 0, _audio.Length - 1); end = Math.Max(start, end); var length = end - start + 1;
                var header = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: {length}\r\n" + (partial ? $"Content-Range: bytes {start}-{end}/{_audio.Length}\r\n" : "") + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                if (!request.StartsWith("HEAD ", StringComparison.Ordinal)) await stream.WriteAsync(_audio.AsMemory(start, length), _stop.Token);
            }
            catch (Exception) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }
}
