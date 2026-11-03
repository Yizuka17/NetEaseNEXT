using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunPreviousTrackChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-previous-track-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldCatalog = _downloadedLibrary; var oldPlaybackCatalog = _playback.Downloads;
        _downloadedLibrary = new(Path.Combine(fixture, "catalog.json")); _playback.Downloads = _downloadedLibrary; _connectionOverride = false;
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        using var api = new ApiClient((method, p, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { count = 0 }, ids = Array.Empty<long>() })), Path.Combine(fixture, "metadata")); SongInfoApiOverride = api;
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); await NavigateAsync("downloads");
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(30);
            if (!condition()) throw new TimeoutException("Previous-track check did not settle");
        }
        var local = new List<Track>(); var online = new List<Track>();
        async Task Prepare(int index, double seconds, bool pause = false)
        {
            _playback.Mode = "Sequential";
            _restoringPlayback = true; try { await _playback.PlayAsync(local[index], local); } finally { _restoringPlayback = false; }
            await Wait(() => _playback.Current?.Id == local[index].Id && _playback.IsPlaying);
            if (pause) { _playback.Toggle(); await Wait(() => !_playback.IsPlaying); }
            _playback.Seek(TimeSpan.FromSeconds(seconds)); await Wait(() => _playback.Position.TotalSeconds >= seconds - .5);
        }
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(fixture, $"track-{i}.wav");
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    const int length = 44100 * 2 * 30;
                    writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
                }
                online.Add(new(8101 + i, $"播放验证 {i + 1}", "测试歌手", "静音专辑", "", 30000));
                _downloadedLibrary.Register(new() { Track = online[i], FilePath = path, ActualLevel = "lossless" }); local.Add(LocalMusicFiles.Read(path, _downloadedLibrary));
            }
            var requests = _playback.StreamRequestCount;
            _restoringPlayback = true; _playback.Restore(new() { Queue = online, OriginalQueue = online, CurrentId = online[1].Id, PositionSeconds = 15, Mode = "Sequential" }); _restoringPlayback = false;
            await _playback.PreviousAsync();
            check("A restored song past three seconds switches to the previous download in one command", _playback.Current?.Id == online[0].Id && _playback.CurrentDownloadedPath == local[0].LocalPath && _playback.Position.TotalSeconds < 2, null);
            await Prepare(1, 12);
            var previous = Descendants<Button>(Root).Single(b => AutomationProperties.GetName(b) == "上一首"); Invoke(previous);
            await Wait(() => _playback.Current?.Id == local[0].Id);
            check("One actual previous-button click switches songs after twelve seconds of playback", _playback.Current?.Id == local[0].Id && _playback.Position.TotalSeconds < 2 && _playback.IsPlaying, null);
            await Prepare(2, 10, true); await _playback.PreviousAsync();
            check("A paused song past three seconds also switches directly to the previous song", _playback.Current?.Id == local[1].Id && _playback.Position.TotalSeconds < 2, null);
            await Prepare(1, 1); await _playback.PreviousAsync();
            check("Previous still switches directly near the start of a song", _playback.Current?.Id == local[0].Id, null);
            await Prepare(0, 8); await _playback.PreviousAsync();
            check("Previous on the first queue entry wraps to the last entry", _playback.Current?.Id == local[2].Id && _playback.Position.TotalSeconds < 2, null);
            await Prepare(1, 10); _playback.Mode = "Shuffle";
            var order = _playback.Queue.Select(t => t.Id).ToArray(); var generation = _playback.ShuffleGeneration;
            var index = Array.IndexOf(order, _playback.Current!.Id); var expected = order[(index - 1 + order.Length) % order.Length];
            await _playback.PreviousAsync();
            check("Shuffle previous follows the current queue order without reshuffling", _playback.Current?.Id == expected && _playback.Queue.Select(t => t.Id).SequenceEqual(order) && _playback.ShuffleGeneration == generation, null);
            await Prepare(1, 10); _playback.Mode = "One"; await _playback.PreviousAsync();
            check("Explicit previous changes tracks even in single-song repeat mode", _playback.Current?.Id == local[0].Id && _playback.Mode == "One", null);
            _playback.Mode = "Sequential"; _restoringPlayback = true; await _playback.PlayAsync(local[1], [local[1]]); _restoringPlayback = false;
            await Wait(() => _playback.IsPlaying); _playback.Seek(TimeSpan.FromSeconds(9)); await Wait(() => _playback.Position.TotalSeconds >= 8);
            await _playback.PreviousAsync();
            check("A one-song queue restarts its only song without requiring a second click", _playback.Current?.Id == local[1].Id && _playback.Queue.Count == 1 && _playback.Position.TotalSeconds < 2, null);
            var missing = new Track(-404, "缺失文件", "测试歌手", "静音专辑", "", 30000) { LocalPath = Path.Combine(fixture, "missing.wav") };
            _restoringPlayback = true; await _playback.PlayAsync(local[1], [missing, local[1]]); _restoringPlayback = false;
            _playback.Seek(TimeSpan.FromSeconds(10)); await Wait(() => _playback.Position.TotalSeconds >= 9);
            await _playback.PreviousAsync();
            check("A missing previous file leaves the current song playing at its existing position", _playback.Current?.Id == local[1].Id && _playback.Position.TotalSeconds >= 9 && _playback.IsPlaying, null);
            _restoringPlayback = true; _playback.Restore(new()); _restoringPlayback = false; await _playback.PreviousAsync();
            check("An empty queue ignores previous safely", _playback.Current == null && _playback.Queue.Count == 0, null);
            check("Previous regression fixtures never request a streaming audio URL", _playback.StreamRequestCount == requests, null);
        }
        finally
        {
            _playback.Restore(new()); _connectionOverride = null; SongInfoApiOverride = null;
            _downloadedLibrary = oldCatalog; _playback.Downloads = oldPlaybackCatalog; await DeleteWorkerFixtureAsync(fixture);
        }
    }
}
