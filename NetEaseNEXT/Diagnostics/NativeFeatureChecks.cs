using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private async Task RunNativeFeatureChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow(); _sessionReady = true; _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); _playback.SetVolume(0);
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(900 * scale)));
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Feature check did not settle");
        }
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-native-features-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        try
        {
            var wave = Path.Combine(fixture, "source.wav");
            using (var writer = new BinaryWriter(File.Create(wave)))
            {
                const int length = 44100 * 2 * 4;
                writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length); writer.Write(new byte[length]);
            }
            var cover = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            const string lyric = "[00:00.00]本地音乐\n[00:02.00]无需网络播放";
            AudioTags.Write(wave, "本地播放验证", "测试歌手", "离线专辑", lyric, cover);
            var metadata = AudioTags.Read(wave);
            check("Audio tags round-trip title, artist, album, lyrics and artwork", metadata.Title == "本地播放验证" && metadata.Artist == "测试歌手" && metadata.Album == "离线专辑" && metadata.Lyrics == lyric && metadata.Cover?.SequenceEqual(cover) == true, null);
            // Metadata-only FLAC fixture: tests Vorbis comments and PICTURE blocks;
            // playback is tested separately with the generated PCM audio above.
            var flac = Path.Combine(fixture, "tags.flac"); var info = new byte[34];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(0), 4096);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), 4096);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10), ((ulong)44100 << 44) | ((ulong)15 << 36) | 176400UL);
            await File.WriteAllBytesAsync(flac, "fLaC"u8.ToArray().Concat(new byte[] { 128, 0, 0, 34 }).Concat(info).ToArray());
            AudioTags.Write(flac, "FLAC 标签", "测试歌手", "无损专辑", lyric, cover); var flacTags = AudioTags.Read(flac);
            check("FLAC Vorbis comments and embedded cover round-trip", flacTags.Title == "FLAC 标签" && flacTags.Lyrics == lyric && flacTags.Cover?.SequenceEqual(cover) == true, null);
            var bytes = await File.ReadAllBytesAsync(wave); var digest = Convert.ToHexString(MD5.HashData(bytes));
            var partial = Path.Combine(fixture, "resume.part.wav"); var job = new DownloadJob { Track = new(123, "完整下载", "测试歌手", "专辑", "", 4000), Directory = fixture, Quality = "lossless", Status = "下载中" };
            using (var interrupted = new CancellationTokenSource())
            using (var client = new HttpClient(new DownloadFixtureHandler(bytes, interrupted)))
            {
                try { await TransferDownloadAsync(new Uri("https://example.invalid/audio"), partial, bytes.Length, digest, job, interrupted.Token, client); }
                catch (OperationCanceledException) { }
            }
            var retained = new FileInfo(partial).Length;
            check("Pausing a download keeps a bounded partial file", retained > 0 && retained < bytes.Length, new { retained });
            using (var client = new HttpClient(new DownloadFixtureHandler(bytes))) await TransferDownloadAsync(new Uri("https://example.invalid/audio"), partial, bytes.Length, digest, job, CancellationToken.None, client);
            check("Range resume reconstructs and verifies the exact source bytes", (await File.ReadAllBytesAsync(partial)).SequenceEqual(bytes), null);
            using var downloadApi = new ApiClient((method, p, ct) => Task.FromResult(JsonSerializer.SerializeToElement(method == "lyric" ? (object)new { code = 200, lrc = new { lyric } } : new { code = 200, data = new { url = "https://example.invalid/audio", type = "wav", level = "lossless", size = bytes.Length, md5 = digest } })), fixture);
            using (var client = new HttpClient(new DownloadFixtureHandler(bytes)))
            {
                job = new DownloadJob { Track = new(123, "完整下载", "测试歌手", "专辑", "", 4000), Directory = fixture, Quality = "lossless" };
                await RunDownloadAsync(job, new CancellationTokenSource(), downloadApi, client);
            }
            check("A download is bound to its song and becomes complete only after verification and tagging", job.Finished && File.Exists(job.FilePath) && AudioTags.Read(job.FilePath).Title == job.Track.Title && AudioTags.Read(job.FilePath).Lyrics == lyric, new { job.Status, job.Error });
            await NavigateAsync("downloads"); DownloadJobs.Clear(); DownloadJobs.Add(job);
            DownloadJobs.Add(new() { Track = new(456, "暂停的下载任务", "歌手", "专辑", "", 0), Status = "已暂停", Progress = 42, ActualQuality = "无损" });
            DownloadJobs.Add(new() { Track = new(789, "下载权限或网络失败时保留任务", "歌手", "专辑", "", 0), Status = "失败", Error = "无法获取下载地址，请重试" });
            Root.UpdateLayout(); await SaveLibraryImageAsync(Root, "native-downloads.png");
            check("Download management has a native page and independent quality preference", DownloadsPanel.Visibility == Visibility.Visible && PageTitle.Text == "下载管理" && _settings.DownloadQuality.Length > 0, null);
            _localLibraryPathOverride = Path.Combine(fixture, "library.bin"); _localLibrary = new() { StoragePath = _localLibraryPathOverride }; _localLibrary.Save(); _localPlaylistId = ""; await NavigateAsync("local-playlist");
            await ImportLocalPathsAsync([wave, wave]);
            check("Local import deduplicates source paths and uses local track identities", Tracks.Count == 1 && Tracks[0].IsLocal && Tracks[0].Id < 0 && Tracks[0].Cover.StartsWith("file:", StringComparison.Ordinal), null);
            var playlist = new LocalPlaylist { Name = "离线收藏", Paths = [wave] }; CommitLocal(() => _localLibrary.Playlists.Add(playlist));
            var reread = LocalLibraryStore.Load(_localLibrary.StoragePath);
            check("Local songs and named playlists persist independently of the online session", reread.Tracks.Count == 1 && reread.Playlists.Single().Name == playlist.Name, null);
            UpdateLocalPlaylists(); LoadLocalTracks(); Root.UpdateLayout(); await SaveLibraryImageAsync(Root, "native-local-library.png");
            var track = Tracks[0]; var requests = _api.RequestCount;
            await _playback.PlayAsync(track, Tracks); await Wait(() => _playback.IsPlaying && _playback.Position.TotalMilliseconds > 150);
            check("Local files play without an API request", _api.RequestCount == requests && _playback.CurrentQuality == "local", null);
            _playback.Toggle();
            await NavigateAsync("playing");
            check("Embedded local lyrics bind without an API request", _lyrics.Count == 2 && _api.RequestCount == requests, null);
            SuspendLyrics(); await Wait(() => _followLyrics);
            check("Lyrics resume automatically after five seconds without a return button", FollowLyricsButton.Visibility == Visibility.Collapsed, null);
            await NavigateAsync("local-playlist"); _localLibrary = reread; _localPlaylistId = playlist.Id; UpdateLocalPlaylists(); LoadLocalTracks(); RemoveLocalTracks([track]);
            check("Removing a local playlist entry preserves the source audio", Tracks.Count == 0 && File.Exists(wave) && _localLibrary.Tracks.Count == 1, null);
            var posted = false;
            using var commentsApi = new ApiClient((method, p, ct) =>
            {
                if (method == "comment") { posted = true; return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, comment = new { commentId = 99, content = p.GetProperty("content").GetString(), time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), user = new { nickname = "本机验证", avatarUrl = "" } } })); }
                return Task.FromResult(JsonSerializer.SerializeToElement(new { code = 200, data = new { totalCount = 1, hasMore = false, cursor = "0", comments = posted ? Array.Empty<object>() : new object[] { new { commentId = 1, content = "评论排序与回复验证", time = 1700000000000L, user = new { nickname = "本机验证", avatarUrl = "" } } } } }));
            }, fixture);
            var commentsTask = ShowCommentsAsync(new SocialItem { Kind = "song", ResourceType = 0, ResourceKey = "123", ThreadId = "R_SO_4_123" }, commentsApi);
            await Wait(() => _dialog is { IsLoaded: true } && FindDescendant<ListView>(_dialog)?.Items.Count == 1);
            var dialog = _dialog!; var input = Descendants<TextBox>(dialog).Single(t => t.PlaceholderText == "评论"); var send = Descendants<Button>(dialog).First(b => b.Content as string == "发送");
            var list = FindDescendant<ListView>(dialog)!; var sort = FindDescendant<ComboBox>(dialog)!;
            var close = Descendants<Button>(dialog).First(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b) == "关闭评论");
            Root.UpdateLayout();
            var closePoint = close.TransformToVisual(dialog).TransformPoint(new Windows.Foundation.Point(0, 0));
            var sendPoint = send.TransformToVisual(dialog).TransformPoint(new Windows.Foundation.Point(0, 0));
            check("Comment close and inline composer fit inside the rendered dialog", close.ActualWidth > 0 && send.ActualHeight > 0 && closePoint.X + close.ActualWidth <= dialog.ActualWidth && sendPoint.Y + send.ActualHeight <= dialog.ActualHeight, new { closePoint, sendPoint, dialog.ActualWidth, dialog.ActualHeight });
            check("Comment send has a 44 DIP touch target", send.ActualHeight >= 44, new { send.ActualHeight });
            check("Comments offer latest/popular sorting and an inline disabled send button", sort.Items.Count == 2 && !send.IsEnabled && dialog.PrimaryButtonText.Length == 0, null);
            var reads = commentsApi.RequestCount; sort.SelectedIndex = 1; await Wait(() => commentsApi.RequestCount > reads);
            input.Text = "发送成功后立即显示"; await Wait(() => send.IsEnabled); Invoke(send); await Wait(() => posted && input.Text.Length == 0 && send.IsEnabled == false);
            check("Successful comments stay visible during upstream read-after-write delay", list.Items.OfType<SocialItem>().Any(i => i.CommentId == 99 && i.Text == "发送成功后立即显示"), null);
            await SaveLibraryImageAsync(dialog, "native-comments-light.png"); _settings.Theme = "Dark"; ApplyAppearance(); await SaveLibraryImageAsync(dialog, "native-comments-dark.png");
            dialog.Hide(); await commentsTask;
            await NavigateAsync("settings"); await RefreshFontsAsync(); check("Installed fonts refresh through an updated DirectWrite collection", FontCombo.Items.Count > 3, null);
            var fontSource = await ImportFontSourceAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NetEaseNEXTIcons.ttf"));
            var probes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var importedFont = new TextBlock { Text = "\uF8BB", FontSize = 32, Width = 48, Height = 48, FontFamily = new(fontSource) };
            var referenceFont = new TextBlock { Text = importedFont.Text, FontSize = 32, Width = 48, Height = 48, FontFamily = IconFonts.Symbols };
            var missingFont = new TextBlock { Text = importedFont.Text, FontSize = 32, Width = 48, Height = 48, FontFamily = new("ms-appx:///missing.ttf#Missing Font") };
            probes.Children.Add(importedFont); probes.Children.Add(referenceFont); probes.Children.Add(missingFont); Grid.SetRowSpan(probes, 3); Root.Children.Add(probes);
            try
            {
                async Task<byte[]> Pixels(TextBlock text)
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap(); await bitmap.RenderAsync(text); var data = await bitmap.GetPixelsAsync(); var bytes = new byte[data.Length];
                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(data); reader.ReadBytes(bytes); return bytes;
                }
                await Wait(() => importedFont.IsLoaded && importedFont.ActualWidth > 0);
                var matched = false; var clock = Stopwatch.StartNew();
                while (!matched && clock.Elapsed < TimeSpan.FromSeconds(5)) { matched = (await Pixels(importedFont)).SequenceEqual(await Pixels(referenceFont)); if (!matched) await Task.Delay(50); }
                check("An imported font file renders its actual glyphs instead of silently falling back", matched && !(await Pixels(importedFont)).SequenceEqual(await Pixels(missingFont)), new { fontSource });
            }
            finally { Root.Children.Remove(probes); }
            _checkingAutoPaging = true;
            try
            {
                _query = "陈奕迅"; await NavigateAsync("search"); var firstPage = Tracks.Take(50).Select(t => t.Id).ToArray();
                Root.UpdateLayout(); TrackList.ScrollIntoView(Tracks.Last());
                await Wait(() => Tracks.Count >= 100 && _pageLoad == null);
                check("Scrolling to the end loads the next search page without a button", Tracks.Take(50).Select(t => t.Id).SequenceEqual(firstPage) && TrackList.Footer is not Button, new { count = Tracks.Count });
                _playlistId = 3778678; _playlistName = "热歌榜";
                await NavigateAsync("playlist");
                await Wait(() => _playlistTrackCount > 0 && Tracks.Count >= _playlistTrackCount && _pageLoad == null);
                check("A small playlist loads completely without manual paging", _playlistTrackCount <= 500 && !_tracksMore, new { count = Tracks.Count });
            }
            finally { _checkingAutoPaging = false; }
            check("No Node process is required for the native app or playback", !_api.IsWorkerRunning && !_playback.IsWorkerRunning, null);
        }
        finally
        {
            _dialog?.Hide(); _playback.Restore(new());
            // Delete only this generated fixture after the player releases it.
            _localLibraryPathOverride = null;
            if (Directory.Exists(fixture)) await DeleteWorkerFixtureAsync(fixture);
        }
    }
    private sealed class DownloadFixtureHandler(byte[] bytes, CancellationTokenSource? interrupt = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new DownloadFixtureStream(bytes[(int)from..], interrupt)) };
            response.Content.Headers.ContentLength = bytes.Length - from;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class DownloadFixtureStream(byte[] bytes, CancellationTokenSource? interrupt) : MemoryStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 1) interrupt?.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
