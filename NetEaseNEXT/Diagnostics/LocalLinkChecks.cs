using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private static Hyperlink[] CreditLinks(DependencyObject host) => Descendants<TextBlock>(host).SelectMany(t => t.Inlines.OfType<Hyperlink>()).ToArray();
    private static string CreditText(Hyperlink link) => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
    private static void InvokeCredit(DependencyObject host, string name)
    {
        var text = Descendants<TextBlock>(host).Single(t => t.Inlines.OfType<Hyperlink>().Any(l => CreditText(l) == name));
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(text) ?? new TextBlockAutomationPeer(text);
        ((IInvokeProvider)peer.GetChildren().Single(p => p.GetName() == name).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task RunLocalLinkChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var oldCatalog = _downloadedLibrary; var oldPlaybackCatalog = _playback.Downloads; var oldInfo = SongInfoApiOverride;
        var oldOnline = _settings.LocalOnlineInfo; var oldTheme = _settings.Theme; var oldBackdrop = _settings.Backdrop;
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-local-link-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); _checkingLibraryWorkflow = true; _connectionOverride = true; _settings.LocalOnlineInfo = true;
        _localLibraryPathOverride = Path.Combine(fixture, "library.bin");
        _downloadedLibrary = new(Path.Combine(fixture, "catalog.json")); _playback.Downloads = _downloadedLibrary; SongInfoApiOverride = _api;
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew(); while (!condition() && clock.Elapsed.TotalSeconds < 15) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Local link checks did not settle");
        }
        void Current(Track track)
        {
            _restoringPlayback = true; _playback.Restore(new() { Queue = [track], CurrentId = track.Id }); _restoringPlayback = false; RefreshSongCredits(); UpdateSongActions();
        }
        static string LinkName(Hyperlink link) => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
        static void Click(TextBlock text, string name)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(text) ?? new TextBlockAutomationPeer(text);
            var link = peer.GetChildren().Single(child => child.GetName() == name);
            ((IInvokeProvider)link.GetPattern(PatternInterface.Invoke)).Invoke();
        }
        async Task Page(string view, long id = 101) { _playlistId = id; _playlistName = "在线歌单"; await NavigateAsync(view); Root.UpdateLayout(); }
        try
        {
            var a = LocalLinkFixtures.Audio(Path.Combine(fixture, "A"), "合作歌曲");
            var b = LocalLinkFixtures.Audio(Path.Combine(fixture, "B"), "合作歌曲", 137, "hires");
            var plain = LocalLinkFixtures.Audio(Path.Combine(fixture, "A"), "未关联的本地音乐", 0);
            _downloadedLibrary.RegisterLocalSources([a, b]);
            var one = new LocalPlaylist { Id = "a", Name = "本地歌单 A", Paths = [a.LocalPath, plain.LocalPath] };
            var two = new LocalPlaylist { Id = "b", Name = "本地歌单 B", Paths = [b.LocalPath], FolderPath = Path.GetDirectoryName(b.LocalPath)! };
            _localLibrary = new() { StoragePath = _localLibraryPathOverride, Tracks = [a, b, plain], Playlists = [one, two] }; _localLibrary.Save();
            _localPlaylistId = "a"; await NavigateAsync("local-playlist"); StopLocalMonitoring();
            var metadataReads = LocalLinkFixtures.Calls.Count(c => c.Method == "song_detail"); Current(a);
            await Task.Delay(250); RefreshSongCredits();
            check("Playback and idle credit rendering never fetch song metadata", LocalLinkFixtures.Calls.Count(c => c.Method == "song_detail") == metadataReads && _songCreditsLoad == null, null);
            await Wait(() => _songCreditsLoad == null && MiniArtist.Inlines.OfType<Hyperlink>().Count() == 3);
            check("Downloaded local playback resolves both artist names and its album", MiniArtist.Inlines.OfType<Hyperlink>().Select(LinkName).SequenceEqual(new[] { "歌手甲", "歌手乙", "测试专辑" }), null);
            check("Every inline credit disables native underline styling", MiniArtist.Inlines.OfType<Hyperlink>().All(l => l.UnderlineStyle == UnderlineStyle.None), null);
            check("The footer artist links are outside the open-player button", !Descendants<TextBlock>(OpenPlayerButton).Contains(MiniArtist), null);
            check("Local playlist actions all carry complete glyphs", new[] { ImportLocalButton, EditLocalPlaylistButton, DeleteLocalPlaylistButton }.All(button => Descendants<FontIcon>(button).Any(icon => icon.ActualWidth > 0 && icon.ActualHeight >= 16)), null);
            await SaveLibraryImageAsync(Root, "local-linked-actions.png");
            Click(MiniArtist, "歌手乙"); await Wait(() => _view == "artist" && _artistId == 302 && _pageLoad == null);
            check("First click resolves an uncached local singer directly without a chooser", _dialog == null && LocalLinkFixtures.Calls.Count(c => c.Method == "song_detail") > metadataReads && LocalLinkFixtures.Calls.First(c => c.Method == "song_detail").Query.Text("ids") == "137", null);
            _localPlaylistId = "a"; await NavigateAsync("local-playlist"); StopLocalMonitoring(); Current(a);

            await Page("playlist");
            check("Opening an online playlist does not load or jump to an unloaded local source", Tracks.Count == 100 && _offset == 100 && TrackLocateButton.Visibility == Visibility.Visible && _playback.Current?.Id == a.Id, null);
            await LocateCurrentAsync(_api);
            check("The online locator pages to the verified source ID of a local song", TrackList.SelectedItem is Track { Id: 137 } && _offset == 137 && _view == "playlist" && _playback.Current?.Id == a.Id, new { localId = a.Id, sourceId = SourceSongId(a), selectedId = (TrackList.SelectedItem as Track)?.Id, _offset });
            check("Source lookup never submits a negative local ID to song details", LocalLinkFixtures.Calls.Where(c => c.Method == "song_detail").All(c => !c.Query.Text("ids").Split(',').Any(v => v.StartsWith('-'))), null);
            await SaveLibraryImageAsync(Root, "local-source-online-locator.png");
            Current(b); UpdateTrackLocator(); await LocateCurrentAsync(_api);
            check("A second folder's copy locates the same online song", TrackList.SelectedItem is Track { Id: 137 } && _playback.Current?.Id == b.Id, null);
            _localPlaylistId = "b"; await NavigateAsync("local-playlist"); StopLocalMonitoring(); Current(a); UpdateTrackLocator();
            check("Local playlists retain exact-file identity across folders", CurrentTrackInView(a) == null && TrackLocateButton.Visibility == Visibility.Collapsed && Tracks.Any(t => t.LocalPath == b.LocalPath), null);
            Root.UpdateLayout();
            await Wait(() => OpenLocalPlaylistFolderButton.IsLoaded);
            check("A folder playlist has a reachable folder action with an icon", OpenLocalPlaylistFolderButton.Visibility == Visibility.Visible && OpenLocalPlaylistFolderButton.IsEnabled && Descendants<FontIcon>(OpenLocalPlaylistFolderButton).Any(), new { visibility = OpenLocalPlaylistFolderButton.Visibility.ToString(), OpenLocalPlaylistFolderButton.IsEnabled, glyphs = Descendants<FontIcon>(OpenLocalPlaylistFolderButton).Count(), _localPlaylistId });

            var localRow = Tracks.Single(t => t.LocalPath == b.LocalPath); TrackList.ScrollIntoView(localRow); Root.UpdateLayout();
            await Wait(() => TrackList.ContainerFromItem(localRow) is FrameworkElement);
            var row = (ListViewItem)TrackList.ContainerFromItem(localRow);
            var anchor = (FrameworkElement)row.ContentTemplateRoot;
            var menu = CreateTrackMenu(localRow, TrackList); ShowRowContextMenu(menu, anchor);
            await Wait(() => menu.Items.OfType<MenuFlyoutItem>().Any(item => item.IsLoaded)); Root.UpdateLayout();
            await Wait(() => menu.Items.OfType<MenuFlyoutItem>().All(item => item.DataContext is Track target && target.Id == localRow.Id));
            await SaveLibraryImageAsync(Root, "local-linked-row-actions.png");
            check("Linked local rows expose artist album information and file navigation", new[] { "歌手", "专辑", "歌曲信息", "打开文件位置" }.All(label => menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == label && i.IsEnabled && i.Visibility == Visibility.Visible && i.Icon != null)), new { source = SourceSongId(localRow), target = menu.Target?.DataContext is Track mt ? new { mt.Id, mt.IsLocal, mt.LocalPath, file = ShareFilePath(mt) } : null, anchor = anchor.DataContext?.GetType().Name, items = menu.Items.OfType<MenuFlyoutItem>().Select(i => new { i.Text, i.IsEnabled, visibility = i.Visibility.ToString(), icon = i.Icon != null, context = (i.DataContext as Track)?.Id }).ToArray() });
            var albumAction = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "专辑");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(albumAction).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => _view == "album" && _playlistId == 401 && _pageLoad == null); menu.Hide();
            check("A linked local row resolves its album even before album IDs are cached", _view == "album" && _playlistId == 401, null);

            _connectionOverride = false; // Profile detection must not disable an explicit linked-song jump.
            var info = ShowSongDetailsAsync(a, _api); await Wait(() => _dialog is { IsLoaded: true } && CreditLinks(_dialog).Length == 3);
            var close = Descendants<Button>(_dialog!).Single(button => AutomationProperties.GetName(button) == "关闭歌曲信息");
            check("Song information uses labeled name links without framed actions", Descendants<TextBlock>(_dialog!).Any(t => t.Inlines.OfType<Run>().Any(r => r.Text == "歌手：") && t.Inlines.OfType<Hyperlink>().Count() == 2) && Descendants<TextBlock>(_dialog!).Any(t => t.Inlines.OfType<Run>().Any(r => r.Text == "专辑：") && t.Inlines.OfType<Hyperlink>().Count() == 1), null);
            check("Song information closes with a bare top-right cross", close.Style == (Style)Application.Current.Resources["BareButtonStyle"] && !close.UseSystemFocusVisuals && close.ActualWidth >= 44 && _dialog!.CloseButtonText.Length == 0 && close.TransformToVisual(_dialog).TransformPoint(new()).X > 200, null);
            _settings.Backdrop = "Mica"; ApplyAppearance(); await Task.Delay(150); ApplyFloatingMaterials();
            await Wait(() => _dialogSurfaceReady);
            check("Main Mica choice is preserved while dialogs stay acrylic", _settings.Backdrop == "Mica" && SystemBackdrop is AdjustableBackdrop { Kind: "Mica" } && _dialog!.Background is AcrylicBrush, null);
            var originalTransparency = _settings.AcrylicTransparency; var originalDefault = _settings.AcrylicSystemMaterial;
            foreach (var value in new[] { 0d, 65d, 100d })
            {
                _settings.AcrylicTransparency = value; _settings.AcrylicSystemMaterial = false; ApplyMaterial(Root.ActualTheme);
                check("Open dialog follows acrylic transparency " + value, _dialog!.Background is AcrylicBrush acrylic && acrylic.TintOpacity is >= 0 and <= 1 && acrylic.TintColor.A == 255, null);
            }
            _settings.AcrylicTransparency = originalTransparency; _settings.AcrylicSystemMaterial = originalDefault; ApplyMaterial(Root.ActualTheme);
            await SaveLibraryImageAsync(_dialog!, "song-information-name-links.png");
            InvokeCredit(_dialog!, "歌手乙"); await info;
            check("Closing the native dialog releases its input handler and readiness", !_dialogSurfaceReady, null);
            check("An information-panel artist name opens that exact artist even with no detected internet profile", _view == "artist" && _artistId == 302 && _dialog == null, null); _connectionOverride = true;
            info = ShowSongDetailsAsync(a, _api); await Wait(() => _dialog is { IsLoaded: true } && CreditLinks(_dialog).Length == 3);
            InvokeCredit(_dialog!, "测试专辑"); await info;
            check("An information-panel album name opens that exact album", _view == "album" && _playlistId == 401, null);

            Current(a); await Page("playing"); await Wait(() => NowArtist.Inlines.OfType<Hyperlink>().Count() == 3);
            await SaveLibraryImageAsync(Root, "player-individual-name-links.png");
            Click(NowArtist, "歌手乙");
            await Wait(() => _view == "artist" && _artistId == 302 && _pageLoad == null);
            check("Clicking the playing second artist goes straight to its page", _dialog == null && _artistId == 302, null);
            Click(MiniArtist, "歌手甲");
            await Wait(() => _view == "artist" && _artistId == 301 && _pageLoad == null);
            check("Footer artist names also navigate directly without a chooser", _dialog == null && _artistId == 301, null);

            // Drop the in-memory catalog and library: an online-first startup
            // must recover this source from the saved binding only on demand.
            var loadedLibrary = _localLibrary;
            _localLibrary = new(); _downloadedLibrary = new(Path.Combine(fixture, "cold-index.json")); _playback.Downloads = _downloadedLibrary;
            var beforeShareReads = _api.RequestCount;
            var savedOnline = Track.FromJson(JsonSerializer.SerializeToElement(LocalLinkFixtures.Song(137)));
            check("An online song initially has no file when its saved binding is not loaded", ShareFilePath(savedOnline) == null, null);
            var recovered = await FindShareFileAsync(savedOnline, CancellationToken.None);
            check("Click-time sharing recovers an online song's saved local folder binding without requests", recovered == b.LocalPath && _api.RequestCount == beforeShareReads, null);
            var onlineChoices = ResourceShareMenu(ShareForTrack(savedOnline), recovered, savedOnline.Title);
            check("A bound downloaded song in an online playlist offers file sharing", onlineChoices.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == "分享文件"), null);
            _localLibrary = loadedLibrary;
            ShowQueue_Click(QueueButton, new()); Root.UpdateLayout();
            await Task.Delay(250);
            check("Queue uses independent native acrylic", _queueSurfaceOpen && QueueSurface.SystemBackdrop is AdjustableBackdrop { Kind: "Acrylic", NativeProfileReady: true } && QueuePane.Background is SolidColorBrush { Color.A: < 255 } && QueuePane.ActualWidth > 0, null);
            ShowQueue_Click(QueueButton, new()); await Wait(() => !_queueSurfaceOpen);
            var legacy = LocalLinkFixtures.Audio(Path.Combine(fixture, "legacy"), "旧下载", 138, "");
            _localLibrary.Tracks.Add(legacy); _localLibrary.Save();
            var legacyOnline = Track.FromJson(JsonSerializer.SerializeToElement(LocalLinkFixtures.Song(138)));
            var legacyReads = _api.RequestCount;
            check("Verified older downloads can share files without a known quality label", await FindShareFileAsync(legacyOnline, CancellationToken.None) == legacy.LocalPath && _api.RequestCount == legacyReads, null);
            var online = Track.FromJson(JsonSerializer.SerializeToElement(LocalLinkFixtures.Song(137)));
            check("Sharing a local song chooses its exact file rather than a higher-quality other copy", ShareFilePath(a) == a.LocalPath && ShareFilePath(online) == b.LocalPath, null);
            var package = ShareFilePackage(await StorageFile.GetFileFromPathAsync(ShareFilePath(a)!), a.Title);
            var fileItems = await package.GetView().GetStorageItemsAsync();
            var originalBytes = await File.ReadAllBytesAsync(a.LocalPath); var sharedBytes = await FileIO.ReadBufferAsync((StorageFile)fileItems.Single());
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(sharedBytes))
            {
                var bytes = new byte[sharedBytes.Length]; reader.ReadBytes(bytes);
                check("File sharing carries the original audio bytes as one copyable storage item", fileItems.Count == 1 && fileItems[0].Path == a.LocalPath && SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(originalBytes)) && package.RequestedOperation == DataPackageOperation.Copy, null);
            }
            check("The file payload does not turn a local path into a link or text share", !package.GetView().Contains(StandardDataFormats.WebLink) && !package.GetView().Contains(StandardDataFormats.Text), null);
            var choices = ResourceShareMenu(ShareForTrack(a), ShareFilePath(a), a.Title);
            check("A downloaded source offers both link sharing and file sharing", new[] { "复制链接", "系统分享", "分享文件", "分享到动态" }.All(label => choices.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == label)), null);
            Current(plain); _connectionOverride = false; RefreshSongCredits(true); UpdateSongActions();
            var plainMenu = ResourceShareMenu(ShareForTrack(plain), ShareFilePath(plain), plain.Title);
            check("Offline unlinked local audio can share its file without a guessed online source", SongShareButton.IsEnabled && plainMenu.Items.OfType<MenuFlyoutItem>().Select(i => i.Text).SequenceEqual(new[] { "分享文件" }), null);
            check("Constructing share choices performs no remote publication", !LocalLinkFixtures.Calls.Any(c => c.Method == "share_resource"), null);

            _connectionOverride = true; Current(a); await Wait(() => _songCreditsLoad == null);
            var count = _api.RequestCount; _settings.LocalOnlineInfo = false; RefreshLocalOnlineContext();
            check("Turning local online information off removes links without requesting metadata", !MiniArtist.Inlines.OfType<Hyperlink>().Any() && !NowArtist.Inlines.OfType<Hyperlink>().Any() && _api.RequestCount == count, null);
            _settings.LocalOnlineInfo = true; RefreshLocalOnlineContext(); await Wait(() => MiniArtist.Inlines.OfType<Hyperlink>().Count() == 3);
            check("Restoring local online information restores individual name links", MiniArtist.Inlines.OfType<Hyperlink>().Any(link => LinkName(link) == "歌手乙"), null);
            await File.WriteAllBytesAsync(a.LocalPath, originalBytes.Concat(new byte[] { 1 }).ToArray());
            RefreshSongCredits(true); await Page("playlist"); UpdateTrackLocator();
            check("Changed audio cannot retain an obsolete online locator or artist link", SourceSongId(a) == 0 && CurrentTrackInView(a) == null && TrackLocateButton.Visibility == Visibility.Collapsed && !MiniArtist.Inlines.OfType<Hyperlink>().Any(), null);
            var unresolved = LocalLinkFixtures.Audio(Path.Combine(fixture, "C"), "合作歌曲", 998);
            Current(unresolved); await Page("playing"); LocalLinkFixtures.Hold("song_detail");
            var lazy = OpenTrackCreditAsync(unresolved, new SongArtist(0, "歌手乙"));
            await Wait(() => LocalLinkFixtures.GateMethod.Length == 0);
            await Page("queue"); await lazy;
            check("Leaving the page cancels a click-time credit request without navigation", _view == "queue" && _songCreditsLoad == null && LocalLinkFixtures.Canceled > 0, null); LocalLinkFixtures.Release();
            Current(online); LocalLinkFixtures.Hold("song_detail");
            info = ShowSongDetailsAsync(new Track(999, "等待中的歌曲", "", "", "", 1000), _api);
            await Wait(() => _dialog is { IsLoaded: true } && LocalLinkFixtures.GateMethod.Length == 0);
            Invoke(Descendants<Button>(_dialog!).Single(button => AutomationProperties.GetName(button) == "关闭歌曲信息")); await info;
            check("Closing pending song information cancels its unused request", _dialog == null && _songDetailLoad == null, null); LocalLinkFixtures.Release();
        }
        finally
        {
            LocalLinkFixtures.Release(); _dialog?.Hide(); _songCreditsLoad?.Cancel(); StopLocalMonitoring(); _playback.Restore(new());
            _downloadedLibrary = oldCatalog; _playback.Downloads = oldPlaybackCatalog; SongInfoApiOverride = oldInfo; _settings.LocalOnlineInfo = oldOnline; _settings.Theme = oldTheme; _settings.Backdrop = oldBackdrop;
            _connectionOverride = null; _checkingLibraryWorkflow = false; _localLibraryPathOverride = null;
            Directory.Delete(fixture, true);
        }
    }
}
