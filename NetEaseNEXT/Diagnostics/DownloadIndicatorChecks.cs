using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunDownloadIndicatorChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-download-indicators-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldCatalog = _downloadedLibrary; var oldPlaybackCatalog = _playback.Downloads;
        var oldOnlineInfo = _settings.LocalOnlineInfo; var oldQuality = _settings.DownloadQuality;
        _checkingLibraryWorkflow = true; _connectionOverride = true;
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        RestoreWindow(); _sessionReady = true; _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); _playback.SetVolume(0);
        var scale = Root.XamlRoot.RasterizationScale; AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(900 * scale)));
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(35); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Download indicator check did not settle");
        }
        async Task<bool> TransparentCorners(Button button)
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(button); var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            var padding = Math.Max(2, (int)(5 * scale));
            return new[] { (padding, padding), (bitmap.PixelWidth - padding - 1, padding), (padding, bitmap.PixelHeight - padding - 1), (bitmap.PixelWidth - padding - 1, bitmap.PixelHeight - padding - 1) }
                .All(p => p.Item1 >= 0 && p.Item2 >= 0 && bytes[(p.Item2 * bitmap.PixelWidth + p.Item1) * 4 + 3] == 0);
        }
        try
        {
            check("Local online information defaults to enabled for existing settings", new SettingsStore().LocalOnlineInfo && JsonSerializer.Deserialize<SettingsStore>("{}")!.LocalOnlineInfo, null);
            _settings.LocalOnlineInfo = true; LocalOnlineInfoToggle.IsOn = true;
            _downloadedLibrary = new(Path.Combine(fixture, "catalog.json")); _playback.Downloads = _downloadedLibrary;
            var plain = Path.Combine(fixture, "夜航.wav");
            using (var writer = new BinaryWriter(File.Create(plain)))
            {
                const int length = 44100 * 2 * 4;
                writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
            }
            var cover = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            AudioTags.Write(plain, "夜航", "晚风电台", "回家的路", "[00:00.00]夜色经过窗外\n[00:02.00]灯火落在远方", cover);
            var raw = await File.ReadAllBytesAsync(plain);
            var owned = Path.Combine(fixture, "已下载.wav"); File.Copy(plain, owned);
            AudioTags.Write(owned, "夜航", "晚风电台", "回家的路", "[00:00.00]夜色经过窗外\n[00:02.00]灯火落在远方", cover, 701, "lossless", 81, 91);
            check("WAV preserves original song identity and quality in custom metadata", AudioTags.Read(owned) is { SongId: 701, Level: "lossless", ArtistId: 81, AlbumId: 91 }, null);
            var online = new Track(701, "夜航", "晚风电台", "回家的路", "", 4000);
            _downloadedLibrary.Register(new() { Track = online, FilePath = owned, ActualLevel = "lossless" });
            var legacyPath = Path.Combine(fixture, "旧版下载.wav"); File.Copy(plain, legacyPath);
            _downloadedLibrary.Register(new() { Track = new(702, "旧版歌曲", "晚风电台", "回家的路", "", 4000), FilePath = legacyPath, ActualLevel = "exhigh" });
            check("Existing untagged downloads still map by their valid catalog path", LocalMusicFiles.Read(legacyPath, _downloadedLibrary) is { SourceSongId: 702, SourceLevel: "exhigh" }, null);
            var moved = Path.Combine(fixture, "新目录"); Directory.CreateDirectory(moved); var movedFile = Path.Combine(moved, "重命名的歌曲.wav"); File.Move(owned, movedFile);
            var recovered = LocalMusicFiles.Read(movedFile, _downloadedLibrary);
            check("Moving and renaming a newly downloaded file preserves its online identity", recovered.Id < 0 && recovered.SourceSongId == 701 && recovered.SourceLevel == "lossless" && recovered.ArtistId == 81 && recovered.AlbumId == 91, null);
            await RegisterLocalDownloadSourcesAsync([recovered]);
            check("Moved downloads can be matched from the online song again", _downloadedLibrary.Find(701)?.Path == movedFile, null);
            var flac = Path.Combine(fixture, "origin.flac"); var streamInfo = new byte[34];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(0), 4096); System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(2), 4096);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10), ((ulong)44100 << 44) | ((ulong)1 << 41) | ((ulong)15 << 36) | 44100);
            await File.WriteAllBytesAsync(flac, "fLaC"u8.ToArray().Concat(new byte[] { 0x80, 0, 0, 34 }).Concat(streamInfo).ToArray());
            AudioTags.Write(flac, "夜航", "晚风电台", "回家的路", "", cover, 703, "hires", 81, 91);
            check("FLAC custom fields preserve source identity without using ordinary comments", AudioTags.Read(flac) is { SongId: 703, Level: "hires" }, null);
            var requests = new ConcurrentQueue<(string Method, long Id)>();
            using var infoApi = new ApiClient((method, p, ct) =>
            {
                var id = p.Number("id"); requests.Enqueue((method, id));
                if (method == "song_red_count") return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { count = 1234 } }));
                if (method == "song_like_check") return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, ids = new[] { 701 } }));
                throw new InvalidOperationException("Unexpected social write in an isolated check");
            }, Path.Combine(fixture, "metadata"), "fixture-session");
            SongInfoApiOverride = infoApi;
            _localLibraryPathOverride = Path.Combine(fixture, "library.bin"); _localLibrary = new() { StoragePath = _localLibraryPathOverride, Tracks = [recovered] }; _localLibrary.Save(); _localPlaylistId = "";
            await NavigateAsync("local-playlist");
            _restoringPlayback = true; await _playback.PlayAsync(recovered, [recovered]); _restoringPlayback = false;
            await LoadSongEngagementAsync(recovered, infoApi); UpdateDownloadIndicator(); await Wait(() => DownloadCheck.ActualWidth > 0 && HeartButton.ActualWidth > 0);
            check("A local playlist song shows real linked heart state and public counts", HeartButton.Visibility == Visibility.Visible && HeartButton.IsEnabled && SongCommentsButton.IsEnabled && _songLiked && _redCount == 1234 && requests.Any(r => r.Method == "song_red_count" && r.Id == 701), null);
            check("Local comments target the original online song rather than the negative local ID", SongCommentsSource(recovered) is { ResourceId: 701, ThreadId: "R_SO_4_701" }, null);
            check("Downloaded state appears as a lower-right check on the download icon", DownloadCheck.Visibility == Visibility.Visible && DownloadCheck.ActualWidth == 12 && DownloadCheck.TransformToVisual(DownloadIconHost).TransformPoint(new Windows.Foundation.Point()).X >= 12 && DownloadCheck.TransformToVisual(DownloadIconHost).TransformPoint(new Windows.Foundation.Point()).Y >= 12, null);
            await SaveLibraryImageAsync(Root, "download-linked-light.png");
            _settings.Theme = "Dark"; ApplyAppearance(); await SaveLibraryImageAsync(Root, "download-linked-dark.png");
            check("Heart, comments and disabled quality controls have no grey backing", await TransparentCorners(HeartButton) && await TransparentCorners(SongCommentsButton) && await TransparentCorners(QualityButton), null);
            _heartWriting = true; UpdateSongActions(); Root.UpdateLayout();
            check("Disabled action buttons also stay transparent", await TransparentCorners(HeartButton), null); _heartWriting = false; UpdateSongActions();
            await NavigateAsync("playing"); await SaveLibraryImageAsync(Root, "download-linked-player.png");
            check("The same download badge follows the controls onto the playback page", DownloadCheck.Visibility == Visibility.Visible && DownloadCurrentButton.IsLoaded, null);
            var beforeOff = infoApi.RequestCount; LocalOnlineInfoToggle.IsOn = false;
            await LoadSongEngagementAsync(recovered, infoApi);
            check("Turning local online information off hides actions and makes no API request", !_settings.LocalOnlineInfo && HeartButton.Visibility == Visibility.Collapsed && SongCommentsButton.Visibility == Visibility.Collapsed && SongCommentsSource(recovered) == null && infoApi.RequestCount == beforeOff && DownloadCheck.Visibility == Visibility.Visible, null);
            await SaveLibraryImageAsync(Root, "download-local-info-off.png");
            LocalOnlineInfoToggle.IsOn = true; await Wait(() => HeartButton.Visibility == Visibility.Visible);
            _connectionOverride = false; RefreshLocalOnlineContext(); var beforeOffline = infoApi.RequestCount; await LoadSongEngagementAsync(recovered, infoApi);
            check("Offline mode hides local online controls while keeping local playback and the badge", HeartButton.Visibility == Visibility.Collapsed && SongCommentsButton.Visibility == Visibility.Collapsed && DownloadCheck.Visibility == Visibility.Visible && infoApi.RequestCount == beforeOffline, null);
            _connectionOverride = true; RefreshLocalOnlineContext(); await Wait(() => HeartButton.Visibility == Visibility.Visible);
            check("Online information resumes after reconnecting", _localOnlineReady && _redCount == 1234, null);
            using var slowApi = new ApiClient(async (method, p, ct) => { await Task.Delay(30000, ct); return JsonSerializer.SerializeToElement(new { code = 200, data = new { count = 999 } }); }, Path.Combine(fixture, "slow"));
            var pending = LoadSongEngagementAsync(recovered, slowApi); LocalOnlineInfoToggle.IsOn = false; await pending;
            check("Disabling the option cancels pending results and cannot restore hidden actions", !_localOnlineReady && HeartButton.Visibility == Visibility.Collapsed && _redCount == null, null);
            SongInfoApiOverride = infoApi; LocalOnlineInfoToggle.IsOn = true; await Wait(() => HeartButton.Visibility == Visibility.Visible);
            var confirmation = ConfirmDownloadsAsync([recovered]); await Wait(() => _dialog is { IsLoaded: true });
            var choices = Descendants<ComboBox>(_dialog!).First();
            check("Clicking an already downloaded song offers only higher quality upgrades", _dialog!.Title as string == "升级音质" && choices.Items.OfType<ComboBoxItem>().All(i => AudioQuality.Rank((string)i.Tag) > AudioQuality.Rank("lossless")), null);
            await SaveLibraryImageAsync(_dialog!, "download-upgrade-prompt.png"); _dialog!.Hide(); await confirmation;
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(movedFile));
            using var noUpgradeApi = new ApiClient((method, p, ct) => Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 701, url = "https://example.invalid/no-upgrade.wav", type = "wav", level = "lossless", size = raw.Length } })), fixture);
            using var transport = new HttpClient(new WorkflowAudioHandler(raw, false));
            var unchanged = new DownloadJob { Track = online, Quality = "hires", Directory = moved, Cancellation = new(), UpgradeFromLevel = "lossless" };
            await RunDownloadAsync(unchanged, unchanged.Cancellation, noUpgradeApi, transport);
            check("No higher available quality keeps the existing audio without downloading or rewriting it", unchanged.Status == "无需升级" && unchanged.Finished && unchanged.FilePath == movedFile && SHA256.HashData(await File.ReadAllBytesAsync(movedFile)).SequenceEqual(hash), null);
            using var upgradeApi = new ApiClient((method, p, ct) => Task.FromResult(method == "lyric" ? JsonSerializer.SerializeToElement(new { code = 200, lrc = new { lyric = "[00:00.00]夜色经过窗外" } }) : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = 701, url = "https://example.invalid/high.wav", type = "wav", level = "hires", size = raw.Length, md5 = Convert.ToHexString(MD5.HashData(raw)) } })), fixture);
            var upgraded = new DownloadJob { Track = online, Quality = "hires", Directory = moved, Cancellation = new(), UpgradeFromLevel = "lossless" };
            await RunDownloadAsync(upgraded, upgraded.Cancellation, upgradeApi, transport);
            check("Successful upgrades keep the old version and register the higher actual quality", upgraded.Finished && upgraded.ActualLevel == "hires" && File.Exists(movedFile) && _downloadedLibrary.Find(701)?.Path == upgraded.FilePath && AudioTags.Read(upgraded.FilePath) is { SongId: 701, Level: "hires" }, new { upgraded.Status, upgraded.Error });
            var audioRequests = 0;
            using var sharedTransport = new HttpClient(new WorkflowAudioHandler(raw, false, () => Interlocked.Increment(ref audioRequests)));
            using var sharedApi = new ApiClient((method, p, ct) => Task.FromResult(method == "lyric" ? JsonSerializer.SerializeToElement(new { code = 200, lrc = new { lyric = "" } }) : JsonSerializer.SerializeToElement(new { code = 200, data = new { id = p.Number("id"), url = "https://example.invalid/shared.wav", type = "wav", level = "lossless", size = raw.Length, md5 = Convert.ToHexString(MD5.HashData(raw)) } })), Path.Combine(fixture, "shared"));
            var sharedTrack = new Track(705, "同一首歌", "晚风电台", "回家的路", "", 4000);
            var listA = new DownloadJob { Track = sharedTrack, Quality = "hires", Directory = fixture, Cancellation = new() };
            var listB = new DownloadJob { Track = sharedTrack, Quality = "jymaster", Directory = fixture, Cancellation = new() };
            await Task.WhenAll(RunDownloadAsync(listA, listA.Cancellation, sharedApi, sharedTransport), RunDownloadAsync(listB, listB.Cancellation, sharedApi, sharedTransport));
            check("Concurrent playlist downloads with different ceilings fetch the same available audio only once", audioRequests == 1 && listA.Finished && listB.Finished && listA.FilePath == listB.FilePath && (listA.Status == "无需升级" || listB.Status == "无需升级"), null);
            var separateFolder = Path.Combine(fixture, "另一个歌单");
            var listC = new DownloadJob { Track = sharedTrack, Quality = "hires", Directory = separateFolder, Cancellation = new() };
            await RunDownloadAsync(listC, listC.Cancellation, sharedApi, sharedTransport);
            check("The same song in different target folders keeps independent playlist files", audioRequests == 2 && listC.Finished && listC.FilePath != listA.FilePath && File.Exists(listA.FilePath) && File.Exists(listC.FilePath) && _downloadedLibrary.Find(705, directory: fixture)?.Path == listA.FilePath && _downloadedLibrary.Find(705, directory: separateFolder)?.Path == listC.FilePath && DownloadForTrack(LocalMusicFiles.Read(listC.FilePath, _downloadedLibrary))?.Path == listC.FilePath, null);
            var master = Path.Combine(fixture, "master.wav"); File.Copy(upgraded.FilePath, master); _downloadedLibrary.Register(new() { Track = online, FilePath = master, ActualLevel = "jymaster" });
            var highest = ConfirmDownloadsAsync([online]); await Wait(() => _dialog is { IsLoaded: true });
            check("The highest downloaded tier does not offer a duplicate download", _dialog!.Title as string == "已下载" && _dialog.PrimaryButtonText.Length == 0, null); _dialog.Hide(); await highest;
            var oneSongBatch = ConfirmDownloadsAsync([online], upgradeExisting: false); await Wait(() => _dialog is { IsLoaded: true });
            check("A one-song playlist batch still offers target qualities for its destination folder", _dialog!.Title as string == "下载歌曲" && Descendants<ComboBox>(_dialog).First().Items.Count == Qualities.Length, null); _dialog.Hide(); await oneSongBatch;
            var plainTrack = LocalMusicFiles.Read(plain, _downloadedLibrary); var beforePlain = infoApi.RequestCount;
            _restoringPlayback = true; await _playback.PlayAsync(plainTrack, [plainTrack]); _restoringPlayback = false; await LoadSongEngagementAsync(plainTrack, infoApi);
            check("Unrelated local files never guess an online identity from their title", SourceSongId(plainTrack) == 0 && HeartButton.Visibility == Visibility.Collapsed && SongCommentsButton.Visibility == Visibility.Collapsed && infoApi.RequestCount == beforePlain && DownloadCheck.Visibility == Visibility.Collapsed, null);
            await SaveLibraryImageAsync(Root, "download-plain-local.png");
            await File.AppendAllTextAsync(movedFile, "changed");
            _downloadedLibrary.RegisterLocalSources([LocalMusicFiles.Read(movedFile, _downloadedLibrary)]);
            check("A changed local file cannot reuse an obsolete playback identity", SourceSongId(recovered) == 0, null);
            _playback.Restore(new()); _restoringPlayback = true; _playback.Restore(new() { Queue = [online], OriginalQueue = [online], CurrentId = online.Id }); _restoringPlayback = false; UpdateDownloadIndicator();
            check("Online song entries recognize the highest valid downloaded version", DownloadCheck.Visibility == Visibility.Visible, null);
            // Media Foundation may release the previous source after Restore returns.
            foreach (var path in new[] { master, upgraded.FilePath, movedFile })
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Delete(path); break; }
                    catch (IOException) when (attempt < 20) { await Task.Delay(50); }
                }
            }
            UpdateDownloadIndicator();
            check("Deleting all downloaded versions clears the check even if history says completed", DownloadCheck.Visibility == Visibility.Collapsed, null);
        }
        finally
        {
            _songStatsLoad?.Cancel(); _dialog?.Hide(); _dialog = null; _heartWriting = false; StopLocalMonitoring(); _playback.Restore(new());
            SongInfoApiOverride = null; _connectionOverride = null; _checkingLibraryWorkflow = false; _restoringPlayback = false;
            _settings.LocalOnlineInfo = oldOnlineInfo; LocalOnlineInfoToggle.IsOn = oldOnlineInfo; _settings.DownloadQuality = oldQuality;
            _downloadedLibrary = oldCatalog; _playback.Downloads = oldPlaybackCatalog; _localLibraryPathOverride = null;
            if (Directory.Exists(fixture)) await DeleteWorkerFixtureAsync(fixture);
        }
    }
}
