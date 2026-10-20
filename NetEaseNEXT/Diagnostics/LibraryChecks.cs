using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLibraryChecksAsync(Action<string, bool, object?> check, List<object> results)
    {
        RestoreWindow(); _sessionReady = true; _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance();
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(720 * scale)));
        await NavigateAsync("queue"); Root.UpdateLayout();
        check("Saved albums has its own native sidebar entry", Nav.MenuItems.OfType<NavigationViewItem>().Any(i => (string)i.Tag == "saved-albums" && (string)i.Content == "收藏的专辑"), null);
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var script = Path.Combine(fixture, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import {createInterface} from 'node:readline';
                createInterface({input:process.stdin}).on('line',async line=>{
                  const r=JSON.parse(line),p=r.params??{};
                  await new Promise(resolve=>setTimeout(resolve,80));
                  let data={code:200};
                  if(r.method==='album_sublist') {
                    const offset=Number(p.offset);
                    data=offset===999 ? {code:500,msg:'fixture album failure'} : {
                      code:200,count:offset===700?0:49,hasMore:offset===0,
                      data:offset===700?[]:Array.from({length:offset===0?48:1},(_,i)=>({id:7000+offset+i,name:'Album '+(offset+i),picUrl:'',artists:[{name:'Artist A'},{name:'Artist B'}]}))
                    };
                  }
                  if(r.method==='playlist_tracks') data=Number(p.pid)===901 ? {code:403,msg:'fixture removal denied'} : {code:200,op:p.op,pid:p.pid,tracks:p.tracks};
                  process.stdout.write(JSON.stringify({id:r.id,data})+'\n');
                }).on('close',()=>process.exit(0));
                """);
            using var api = new ApiClient(script, fixture);
            _view = "saved-albums"; Catalog.Clear(); _offset = 0;
            await LoadSavedAlbumsAsync(api, CancellationToken.None);
            check("Saved albums parse all artists and expose the next page", Catalog.Count == 48 && Catalog[0].Kind == "album" && Catalog[0].Subtitle == "Artist A / Artist B" && _offset == 48 && CatalogMore.Visibility == Visibility.Visible, null);
            await LoadSavedAlbumsAsync(api, CancellationToken.None);
            check("Saved album pagination appends without losing the first page", Catalog.Count == 49 && Catalog.Select(a => a.Id).Distinct().Count() == 49 && _offset == 49 && CatalogMore.Visibility == Visibility.Collapsed, null);
            var reads = api.RequestCount; Catalog.Clear(); _offset = 0;
            await LoadSavedAlbumsAsync(api, CancellationToken.None);
            check("Saved albums read back from session-scoped cache", Catalog.Count == 48 && api.RequestCount == reads, null);
            _offset = 999;
            try { await LoadSavedAlbumsAsync(api, CancellationToken.None); check("Album failures retain existing contents and paging", false, null); }
            catch (InvalidOperationException) { check("Album failures retain existing contents and paging", Catalog.Count == 48 && _offset == 999, null); }
            using (var canceled = new CancellationTokenSource())
            {
                _offset = 500; var pending = LoadSavedAlbumsAsync(api, canceled.Token); canceled.Cancel();
                try { await pending; } catch (OperationCanceledException) { }
                check("Canceled album requests cannot append stale rows", Catalog.Count == 48 && _offset == 500, null);
            }
            _offset = 700; Catalog.Clear(); await LoadSavedAlbumsAsync(api, CancellationToken.None);
            check("An empty saved album collection has no paging button", Catalog.Count == 0 && CatalogMore.Visibility == Visibility.Collapsed, null);

            await NavigateAsync("queue");
            var first = new Track(123, "First song", "Artist", "Album", "", 60000);
            var second = new Track(456, "Second song", "Artist", "Album", "", 60000);
            _playback.Restore(new() { Queue = [first, second], CurrentId = first.Id });
            _view = "playlist"; _playlistId = 900; _playlistOwner = _accountId = 42;
            _playlistTrackIds = [123, 456]; _playlistTrackCount = 2; _offset = 2;
            Tracks.Clear(); Tracks.Add(first); Tracks.Add(second); EmptyPanel.Visibility = Visibility.Collapsed;
            Root.UpdateLayout(); TrackList.ScrollIntoView(first);
            var watch = Stopwatch.StartNew();
            while (TrackList.ContainerFromItem(first) == null && watch.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(20); Root.UpdateLayout(); }
            var row = FindTrackContextHost((DependencyObject)TrackList.ContainerFromItem(first))!;
            var menu = (MenuFlyout)row.ContextFlyout;
            async Task SetMenuAsync(bool open)
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object> changed = (_, _) => completed.TrySetResult();
                if (open) menu.Opened += changed; else menu.Closed += changed;
                try { if (open) menu.ShowAt(row); else menu.Hide(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { if (open) menu.Opened -= changed; else menu.Closed -= changed; }
            }
            await SetMenuAsync(true);
            var remove = menu.Items.OfType<MenuFlyoutItem>().First(i => i.Text == "从歌单移除");
            check("Native track context menu supports holding and captures the selected playlist", row.IsHoldingEnabled && remove.Tag is PlaylistTrackRemoval { PlaylistId: 900, TrackId: 123 }, null);
            check("Playlist removal is only shown to the authenticated owner", remove.Visibility == (_api.HasSession ? Visibility.Visible : Visibility.Collapsed), null);
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).FirstOrDefault(p => p.Child is MenuFlyoutPresenter)?.Child is UIElement presenter)
                await SaveLibraryImageAsync(presenter, "library-remove-menu.png");
            await SetMenuAsync(false); _playlistOwner = 99; await SetMenuAsync(true);
            check("Other users' playlists never show removal", remove.Visibility == Visibility.Collapsed, null);
            await SetMenuAsync(false); _playlistOwner = 42;
            var requests = api.RequestCount;
            try { await api.RemovePlaylistTrackAsync(900, 99, 42, 123); check("Non-owners cannot send a removal request", false, null); }
            catch (InvalidOperationException) { check("Non-owners cannot send a removal request", api.RequestCount == requests, null); }
            check("A menu from another playlist cannot remove the current row", !await RemovePlaylistTrackAsync(api, new(899, 123, _api.Cookie)) && Tracks.Count == 2, null);
            _playlistId = 901;
            check("Server rejection preserves songs and displays the error", !await RemovePlaylistTrackAsync(api, new(901, 123, _api.Cookie)) && Tracks.Count == 2 && Notice.IsOpen && Notice.Message == "fixture removal denied", null);
            _playlistId = 900; Notice.IsOpen = false;
            check("Successful removal updates playlist rows, IDs, count and paging", await RemovePlaylistTrackAsync(api, new(900, 456, _api.Cookie)) && Tracks.Count == 1 && Tracks[0].Id == 123 && _playlistTrackIds.SequenceEqual(new long[] { 123 }) && _playlistTrackCount == 1 && _offset == 1, null);
            check("Removing a playlist song preserves the current playback queue", _playback.Queue.Count == 2 && _playback.Current?.Id == 123, null);
            ShowQueue_Click(QueueButton, new()); Root.UpdateLayout();
            check("The playback bar opens the native queue with all songs", QueuePane.Visibility == Visibility.Visible && QueueList.Items.Count == 2, null);
            check("Queue and favorite buttons have different glyphs", FindDescendant<FontIcon>(QueueButton)!.Glyph == "\uE8FD" && FindDescendant<FontIcon>(PlayerTools.Children.OfType<Button>().First())!.Glyph == "\uE8F4", null);
            await SaveLibraryImageAsync(QueuePane, "library-queue.png");
            ShowQueue_Click(QueueButton, new());
            check("The queue button closes and releases queue bindings", QueuePane.Visibility == Visibility.Collapsed && QueueList.ItemsSource == null, null);
            await CheckTransportInkAsync(check, results, "footer");
            await SaveLibraryImageAsync(PlayerFooter, "library-footer-light.png");
            PlayPauseIcon.Glyph = "\uE769";
            await CheckTransportInkAsync(check, results, "paused-icon");
            await SaveLibraryImageAsync(PlayerFooter, "library-footer-pause.png");
            PlayPauseIcon.Glyph = "\uE768";
            _settings.Theme = "Dark"; ApplyAppearance(); Root.UpdateLayout();
            await SaveLibraryImageAsync(Root, "library-footer-dark.png");
            _settings.Theme = "Light"; ApplyAppearance();
            await NavigateAsync("playing"); Root.UpdateLayout();
            await CheckTransportInkAsync(check, results, "player");
            await SaveLibraryImageAsync(TransportButtons, "library-player-light.png");
            ShowQueue_Click(QueueButton, new()); Root.UpdateLayout();
            check("The same queue button works on the player page", QueuePane.Visibility == Visibility.Visible && QueueList.Items.Count == 2, null);
            ShowQueue_Click(QueueButton, new());
            if (_api.HasSession)
            {
                await UpdateAccountAsync();
                await NavigateAsync("saved-albums"); Root.UpdateLayout();
                check("Real saved albums load from the sidebar route", CatalogPanel.Visibility == Visibility.Visible && !Notice.IsOpen && PageTitle.Text == "收藏的专辑", new { count = Catalog.Count });
                // Keep this evidence viewport in the native expanded desktop mode.
                Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left; Nav.IsPaneOpen = true; PlayerNotice.IsOpen = false; Root.UpdateLayout();
                var layoutWait = Stopwatch.StartNew();
                while (Catalog.Count > 0 && CatalogGrid.ContainerFromIndex(0) == null && layoutWait.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(20); Root.UpdateLayout(); }
                check("Saved album cards are realized in the native grid", Catalog.Count == 0 || CatalogGrid.ContainerFromIndex(0) != null, null);
                await SaveLibraryImageAsync(Root, "library-saved-albums.png");
                if (Catalog.FirstOrDefault() is { } album)
                {
                    await OpenCatalogItemAsync(album);
                    check("A saved album opens its actual songs", _view == "album" && _playlistId == album.Id && TracksPanel.Visibility == Visibility.Visible && Tracks.Count > 0 && !Notice.IsOpen, new { tracks = Tracks.Count });
                }
            }
        }
        finally { await DeleteWorkerFixtureAsync(fixture); }
    }

    private static FrameworkElement? FindTrackContextHost(DependencyObject? parent)
    {
        if (parent is FrameworkElement { ContextFlyout: MenuFlyout } element) return element;
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindTrackContextHost(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private async Task CheckTransportInkAsync(Action<string, bool, object?> check, List<object> results, string host)
    {
        var buttonCenters = TransportButtons.Children.OfType<Button>().Select(b => b.TransformToVisual(TransportButtons).TransformPoint(new(0, b.ActualHeight / 2)).Y).ToArray();
        check(host + " transport buttons share a vertical center", buttonCenters.Max() - buttonCenters.Min() < .5, null);
        foreach (var button in TransportButtons.Children.OfType<Button>())
        {
            var icon = FindDescendant<FontIcon>(button)!;
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(icon);
            var buffer = await bitmap.GetPixelsAsync(); var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var minX = bitmap.PixelWidth; var minY = bitmap.PixelHeight; var maxX = -1; var maxY = -1;
            double weight = 0, sumX = 0, sumY = 0;
            for (var y = 0; y < bitmap.PixelHeight; y++) for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var alpha = pixels[(y * bitmap.PixelWidth + x) * 4 + 3]; if (alpha < 40) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                weight += alpha; sumX += alpha * (x + .5); sumY += alpha * (y + .5);
            }
            var centerX = icon.Glyph == "\uE768" ? sumX / weight : (minX + maxX + 1) / 2d;
            var centerY = (minY + maxY + 1) / 2d;
            var dx = (centerX / bitmap.PixelWidth - .5) * icon.ActualWidth;
            var dy = (centerY / bitmap.PixelHeight - .5) * icon.ActualHeight;
            results.Add(new { name = host + " icon ink", glyph = $"U+{(int)icon.Glyph[0]:X4}", dx, dy });
            check(host + " visible glyph is centered " + ((int)icon.Glyph[0]).ToString("X4"), weight > 0 && Math.Abs(dx) <= 1.25 && Math.Abs(dy) <= 1.25, new { dx, dy });
        }
    }

    private static async Task SaveLibraryImageAsync(UIElement element, string name)
    {
        // XAML layout and ItemsSource may be ready before the composition tree.
        // Wait for rendered frames so captures include native virtualized rows.
        byte[]? previous = null; var stable = 0;
        var bitmap = new RenderTargetBitmap(); byte[] bytes = [];
        var watch = Stopwatch.StartNew();
        while (stable < 3 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> handler = (_, _) => rendered.TrySetResult();
            CompositionTarget.Rendering += handler;
            try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { CompositionTarget.Rendering -= handler; }
            await bitmap.RenderAsync(element);
            var buffer = await bitmap.GetPixelsAsync(); bytes = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            stable = previous != null && bytes.AsSpan().SequenceEqual(previous) ? stable + 1 : 0;
            previous = bytes;
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(SettingsStore.DataDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
