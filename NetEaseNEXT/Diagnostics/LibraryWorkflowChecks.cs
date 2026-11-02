using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLibraryWorkflowChecksAsync(Action<string, bool, object?> check)
    {
        _checkingLibraryWorkflow = true;
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        RestoreWindow(); _sessionReady = true;
        _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); _playback.SetVolume(0);
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(900 * scale)));
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-library-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldLibrary = _downloadedLibrary; var oldPlaybackLibrary = _playback.Downloads;
        var oldSource = _settings.DownloadSource; var oldDownloadQuality = _settings.DownloadQuality;
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(35); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Library workflow did not settle");
        }
        try
        {
            var folder = Path.Combine(fixture, "收藏目录"); Directory.CreateDirectory(folder);
            var wave = Path.Combine(folder, "雾里慢行.wav");
            using (var writer = new BinaryWriter(File.Create(wave)))
            {
                const int length = 44100 * 2 * 5;
                writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
            }
            var picture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            var cover = LocalMusicFiles.StoreCover(picture);
            AudioTags.Write(wave, "雾里慢行", "午后电台", "雨后散步", "[00:00.00]风吹过窗台\n[00:02.00]雨停在远方", picture);
            var sourceBytes = await File.ReadAllBytesAsync(wave); var sourceHash = SHA256.HashData(sourceBytes);
            _localLibraryPathOverride = Path.Combine(fixture, "library.bin");
            var bound = new LocalPlaylist { Name = "雨后散步", CoverPath = cover, FolderPath = folder };
            var manual = new LocalPlaylist { Name = "夜间收藏", CoverPath = cover, Paths = [wave] };
            _localLibrary = new() { StoragePath = _localLibraryPathOverride, Tracks = [LocalMusicFiles.Read(wave)], Playlists = [bound, manual] }; _localLibrary.Save();
            await NavigateAsync("local");
            check("Local music opens a playlist card grid rather than a selector list", LocalLibraryPanel.Visibility == Visibility.Visible && TracksPanel.Visibility == Visibility.Collapsed && LocalPlaylistCards.Count == 3, null);
            check("Folder playlists preserve cover, directory and existing manual playlists", _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 1 && LocalPlaylistCards.Single(p => p.Id == bound.Id).Cover.StartsWith("file:") && LocalPlaylistCards.Any(p => p.Id == manual.Id), null);
            await SaveLibraryImageAsync(Root, "library-home-light.png");
            _settings.Theme = "Dark"; ApplyAppearance(); await SaveLibraryImageAsync(Root, "library-home-dark.png"); _settings.Theme = "Light"; ApplyAppearance();
            var added = Path.Combine(folder, "月台.wav"); File.Copy(wave, added);
            await Wait(() => _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 2);
            check("A file-system event automatically adds a new song without manual refresh", LocalPlaylistCards.First(p => p.Id == bound.Id).Description.StartsWith("2 首"), null);
            var renamed = Path.Combine(folder, "山间.wav"); File.Move(added, renamed);
            await Wait(() => _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Contains(renamed));
            check("Renames replace the old folder playlist path", !_localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Contains(added), null);
            File.Delete(renamed); await Wait(() => _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 1);
            check("Deleting a file updates a bound playlist without deleting other files", File.Exists(wave) && _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Single() == wave, null);
            HideWindow(); File.Copy(wave, added); await Task.Delay(900);
            check("Hidden windows stop folder scans", _localFolderMonitor == null && _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 1, null);
            RestoreWindow(); await Wait(() => _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 2);
            check("Returning to the window reconciles folder changes", _localFolderMonitor != null, null);
            _localPlaylistId = bound.Id; await NavigateAsync("local-playlist");
            await SaveLibraryImageAsync(Root, "library-folder-detail.png");
            check("A local playlist has a cover header, folder path and song details", Tracks.Count == 2 && LocalToolbar.Visibility == Visibility.Visible && LocalFolderPath.Text == folder && LocalPlaylistCover.ActualWidth > 0 && ImportLocalButton.Visibility == Visibility.Collapsed, new { count = Tracks.Count, toolbar = LocalToolbar.Visibility.ToString(), folderMatched = LocalFolderPath.Text == folder, LocalPlaylistCover.ActualWidth, import = ImportLocalButton.Visibility.ToString() });
            var editTask = EditLocalPlaylistAsync(_localLibrary.Playlists.First(p => p.Id == bound.Id));
            await Wait(() => _dialog is { IsLoaded: true });
            var dialog = _dialog!;
            var nameBox = Descendants<TextBox>(dialog).First(t => t.Header as string == "歌单名称"); nameBox.Text = "雨后散步收藏";
            await SaveLibraryImageAsync(dialog, "library-playlist-editor.png");
            Invoke(Descendants<Button>(dialog).First(b => b.Content as string == "保存")); await editTask;
            var reread = LocalLibraryStore.Load(_localLibraryPathOverride);
            check("Editing a playlist persists its name, cover and folder binding", reread.Playlists.First(p => p.Id == bound.Id) is { Name: "雨后散步收藏" } saved && saved.CoverPath == cover && saved.FolderPath == folder, null);
            var validStore = _localLibrary.StoragePath; var blockedStore = Path.Combine(fixture, "blocked-store"); Directory.CreateDirectory(blockedStore); _localLibrary.StoragePath = blockedStore;
            try { CommitLocal(() => _localLibrary.Playlists.First(p => p.Id == bound.Id).Name = "unsaved"); check("A failed local save rolls back playlist edits", false, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { check("A failed local save rolls back playlist edits", _localLibrary.Playlists.First(p => p.Id == bound.Id).Name == "雨后散步收藏", null); }
            finally { _localLibrary.StoragePath = validStore; }
            StopLocalMonitoring(); File.Delete(added); await RefreshLocalFoldersAsync(CancellationToken.None);
            check("Manual refresh works without an active file watcher", _localLibrary.Playlists.First(p => p.Id == bound.Id).Paths.Count == 1, null);
            check("Folder scans never change source audio bytes", SHA256.HashData(await File.ReadAllBytesAsync(wave)).SequenceEqual(sourceHash), null);

            var requested = new ConcurrentQueue<string>();
            using var fallbackApi = new ApiClient((method, p, ct) =>
            {
                var level = p.Text("level"); requested.Enqueue(level);
                return Task.FromResult(level == "hires"
                    ? JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 701, url = "https://example.invalid/high.flac", type = "flac", level = "hires", size = 100 } })
                    : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 701, url = "https://example.invalid/low.flac", type = "flac", level = "lossless", size = 100 } }));
            }, fixture);
            var ceilingJob = new DownloadJob { Track = new(701, "雾里慢行", "午后电台", "雨后散步", "", 5000), Quality = "jymaster" };
            var best = await DownloadResolver.ResolveAsync(fallbackApi, ceilingJob, CancellationToken.None);
            check("Quality fallback chooses the highest available level below the selected ceiling", best.Text("level") == "hires" && requested.Contains("hires") && !requested.Contains("exhigh"), new { attempts = requested.ToArray() });
            var bitrates = new List<long>();
            using var legacyApi = new ApiClient((method, p, ct) =>
            {
                if (method != "song_download_legacy") throw new InvalidOperationException("Wrong source");
                bitrates.Add(p.Number("br"));
                return Task.FromResult(p.Number("br") == 999000 ? JsonSerializer.SerializeToElement(new { code = 200, data = new { url = (string?)null } }) : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 701, url = "https://example.invalid/track.mp3", type = "mp3", br = 320000 } }));
            }, fixture);
            ceilingJob.Source = "legacy"; var legacy = await DownloadResolver.ResolveAsync(legacyApi, ceilingJob, CancellationToken.None);
            check("MusicUnlock source uses official bitrate fallback and records actual quality", bitrates.SequenceEqual(new long[] { 999000, 320000 }) && legacy.Text("level") == "exhigh", null);
            _settings.DownloadQuality = "hires";
            var beforeConfirm = DownloadJobs.Count; var confirmation = ConfirmDownloadsAsync([ceilingJob.Track]); await Wait(() => _dialog is { IsLoaded: true });
            var qualityBox = Descendants<ComboBox>(_dialog!).First();
            check("Download asks for a maximum quality before queueing", qualityBox.Items.Count == Qualities.Length && (qualityBox.SelectedItem as ComboBoxItem)?.Tag as string == "hires" && DownloadJobs.Count == beforeConfirm, null);
            await SaveLibraryImageAsync(_dialog!, "download-quality-prompt.png");
            var dialogButtons = Descendants<Button>(_dialog!).Where(b => b.Content as string is "开始下载" or "取消").ToArray();
            check("Native download confirmation buttons have 44 DIP touch targets", dialogButtons.Length == 2 && dialogButtons.All(b => b.ActualHeight >= 44), dialogButtons.Select(b => new { b.Content, b.ActualHeight, b.MinHeight }).ToArray());
            _dialog!.Hide(); await confirmation;
            check("Canceling the quality prompt leaves the queue unchanged", DownloadJobs.Count == beforeConfirm, null);

            _downloadedLibrary = new(Path.Combine(fixture, "downloads-index.json")); _playback.Downloads = _downloadedLibrary;
            DownloadJobs.Clear();
            using var downloadApi = new ApiClient((method, p, ct) => Task.FromResult(method == "lyric"
                ? JsonSerializer.SerializeToElement(new { code = 200, lrc = new { lyric = "[00:00.00]风吹过窗台" } })
                : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = p.Number("id"), url = "https://example.invalid/source.wav", type = "wav", level = "lossless", size = sourceBytes.Length, md5 = Convert.ToHexString(MD5.HashData(sourceBytes)) } })), fixture);
            using var holdingTransport = new HttpClient(new WorkflowAudioHandler(sourceBytes, true));
            DownloadJob StartFixture(long id, HttpClient transport)
            {
                var job = new DownloadJob { Track = new(id, id == 701 ? "雾里慢行" : "月台", "午后电台", "雨后散步", "", 5000), Quality = "lossless", Directory = fixture, Cancellation = new() };
                DownloadJobs.Add(job); job.Completion = RunDownloadAsync(job, job.Cancellation, downloadApi, transport); return job;
            }
            var first = StartFixture(701, holdingTransport); var second = StartFixture(702, holdingTransport); var waiting = StartFixture(703, holdingTransport);
            await Wait(() => first.PartialPath.Length > 0 && second.PartialPath.Length > 0 && File.Exists(first.PartialPath) && new FileInfo(first.PartialPath).Length > 0 && new FileInfo(second.PartialPath).Length > 0);
            await ChangeDownloadsAsync([first, second, waiting], false, false);
            check("Pause all cancels both transfers and queued work while keeping partial files", DownloadJobs.All(j => j.Status == "已暂停") && File.Exists(first.PartialPath) && new FileInfo(first.PartialPath).Length > 0 && first.Cancellation == null && waiting.Cancellation == null, null);
            using var resumedTransport = new HttpClient(new WorkflowAudioHandler(sourceBytes, false));
            first.Status = "等待中"; first.Cancellation = new(); first.Completion = RunDownloadAsync(first, first.Cancellation, downloadApi, resumedTransport); await first.Completion;
            check("A resumed download finishes with its verified actual quality", first.Finished && first.ActualLevel == "lossless" && first.AudioReady && File.Exists(first.FilePath), new { first.Status, first.Error });
            var catalog = new DownloadedLibrary(Path.Combine(fixture, "downloads-index.json"));
            check("Completed song identity, file and actual quality survive a catalog restart", catalog.Find(701) is { Level: "lossless" } completed && completed.Path == first.FilePath, null);
            var failedCatalog = new DownloadedLibrary(blockedStore);
            try { failedCatalog.Register(first); check("A failed catalog save keeps audio and does not commit memory state", false, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { check("A failed catalog save keeps audio and does not commit memory state", File.Exists(first.FilePath) && failedCatalog.Find(701) == null, null); }
            var tagged = new DownloadJob { Track = new(710, "完成标签的歌曲", "午后电台", "雨后散步", "", 5000), Quality = "lossless", ActualLevel = "lossless", ActualQuality = "无损", Directory = fixture, PartialPath = Path.Combine(fixture, ".tagged-710-lossless.part.wav"), FilePath = Path.Combine(fixture, "tagged-complete.wav"), TagsReady = true, Cancellation = new() };
            using var taggedApi = new ApiClient((method, p, ct) => throw new InvalidOperationException("A tagged file must not request a new URL"), fixture);
            File.Copy(wave, tagged.PartialPath); DownloadJobs.Add(tagged); tagged.Completion = RunDownloadAsync(tagged, tagged.Cancellation, taggedApi, resumedTransport); await tagged.Completion;
            check("Resuming after tag writing finishes locally without rechecking the original network hash", tagged.Finished && _downloadedLibrary.Find(710)?.Path == tagged.FilePath, null);
            var secondPartial = second.PartialPath;
            await ChangeDownloadsAsync([second, waiting], true, false);
            check("Cancel all removes only unfinished partial files and keeps completed audio", second.Status == "已取消" && waiting.Status == "已取消" && !File.Exists(secondPartial) && File.Exists(first.FilePath), null);
            using var deniedApi = new ApiClient((method, p, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { url = (string?)null } })), fixture);
            var failed = new DownloadJob { Track = new(709, "未完成的旅途", "午后电台", "远方", "", 5000), Directory = fixture, Quality = "hires", Cancellation = new() }; DownloadJobs.Add(failed);
            failed.Completion = RunDownloadAsync(failed, failed.Cancellation, deniedApi, resumedTransport); await failed.Completion;
            await NavigateAsync("downloads"); DownloadScope.SelectedIndex = 0;
            check("Failed tasks are excluded from the normal download queue", failed.Status == "失败" && !VisibleDownloads.Contains(failed), null);
            await SaveLibraryImageAsync(Root, "download-queue.png"); DownloadScope.SelectedIndex = 1; Root.UpdateLayout();
            check("Failed downloads have a separate list with a retry action", VisibleDownloads.Count == 1 && ReferenceEquals(VisibleDownloads[0], failed) && RetryFailedDownloadsButton.Visibility == Visibility.Visible && RetryFailedDownloadsButton.IsEnabled, null);
            await Wait(() => FailedDownloadsList.ContainerFromIndex(0) is ListViewItem { ActualHeight: > 0 });
            var failedRow = (ListViewItem)FailedDownloadsList.ContainerFromIndex(0);
            check("The failed song, error and retry button are actually rendered", Descendants<TextBlock>(failedRow).Any(t => t.Text == failed.Track.Title && t.ActualWidth > 0) && Descendants<TextBlock>(failedRow).Any(t => t.Text.StartsWith("失败：") && t.ActualHeight > 0) && Descendants<Button>(failedRow).Any(b => b.Content as string == "重试" && b.ActualWidth > 0), null);
            await SaveLibraryImageAsync(Root, "download-failures.png");
            failed.Status = "等待中"; failed.Cancellation = new(); failed.Completion = RunDownloadAsync(failed, failed.Cancellation, downloadApi, resumedTransport); await failed.Completion;
            check("Successful retries leave the failure list automatically", failed.Finished && VisibleDownloads.Count == 0, null);
            await ChangeDownloadsAsync(DownloadJobs.ToArray(), true, true);
            check("Clearing the queue preserves music files and their durable playback mapping", DownloadJobs.Count == 0 && File.Exists(first.FilePath) && _downloadedLibrary.Find(701)?.Path == first.FilePath, null);
            var streamRequests = _playback.StreamRequestCount;
            await _playback.PlayAsync(first.Track, [first.Track]); await Wait(() => _playback.IsPlaying);
            check("An online song plays its matching download with the actual quality and no stream request", _playback.Current?.Id == 701 && _playback.CurrentDownloadedPath == first.FilePath && _playback.CurrentQuality == "lossless" && _playback.StreamRequestCount == streamRequests && !_playback.IsCaching && !_playback.LastPlayUsedCache, null);
            _playback.Toggle(); _playback.Restore(new());
            await File.AppendAllTextAsync(first.FilePath, "changed");
            check("A modified or missing downloaded file cannot be silently reused", _downloadedLibrary.Find(701) == null, null);
            await NavigateAsync("settings"); Root.UpdateLayout();
            check("Download source is switchable in native settings", DownloadSourceCombo.Items.Count == 2 && DownloadSourceCombo.ActualWidth > 0, null);
        }
        finally
        {
            StopLocalMonitoring(); _dialog?.Hide(); _dialog = null;
            foreach (var job in DownloadJobs) job.Cancellation?.Cancel();
            await Task.WhenAll(DownloadJobs.Where(j => j.Completion != null).Select(j => j.Completion!));
            DownloadJobs.Clear(); _playback.Restore(new());
            _downloadedLibrary = oldLibrary; _playback.Downloads = oldPlaybackLibrary;
            _settings.DownloadSource = oldSource; _settings.DownloadQuality = oldDownloadQuality; _localLibraryPathOverride = null; _checkingLibraryWorkflow = false;
            if (Directory.Exists(fixture)) await DeleteWorkerFixtureAsync(fixture);
        }
    }
    private sealed class WorkflowAudioHandler(byte[] bytes, bool hold, Action? onRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest?.Invoke();
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new WorkflowAudioStream(bytes[(int)from..], hold)) };
            response.Content.Headers.ContentLength = bytes.Length - from;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class WorkflowAudioStream(byte[] bytes, bool hold) : MemoryStream(bytes)
    {
        private int _reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (hold && _reads++ > 0) await Task.Delay(Timeout.Infinite, ct);
            return await base.ReadAsync(buffer, ct);
        }
    }
}
