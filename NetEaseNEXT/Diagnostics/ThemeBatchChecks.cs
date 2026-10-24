using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunThemeBatchChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _settings.Backdrop = "Solid"; _playback.SetVolume(0); RestoreWindow();
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(800 * scale)));
        await NavigateAsync("queue");
        using var personalize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var previous = personalize.GetValue("AppsUseLightTheme");
        var previousKind = previous == null ? RegistryValueKind.DWord : personalize.GetValueKind("AppsUseLightTheme");
        async Task WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(6)) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Theme/batch UI did not settle");
        }
        async Task ExternalTheme(bool dark)
        {
            personalize.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x001A, 0, 0);
            await WaitFor(() => Root.ActualTheme == (dark ? ElementTheme.Dark : ElementTheme.Light));
        }
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-theme-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var script = Path.Combine(fixture, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import {createInterface} from 'node:readline';
                createInterface({input:process.stdin}).on('line',async line=>{
                  const r=JSON.parse(line),p=r.params??{};
                  await new Promise(resolve=>setTimeout(resolve,40));
                  let data={code:200};
                  if(r.method==='comment_music') data={code:200,total:1,more:false,comments:[{commentId:1,content:'Theme check comment',time:1700000000000,user:{nickname:'Test user',avatarUrl:''}}]};
                  if(r.method==='playlist_tracks') data=Number(p.pid)===901?{code:403,msg:'fixture batch denied'}:{code:200,op:p.op,pid:p.pid,tracks:p.tracks};
                  process.stdout.write(JSON.stringify({id:r.id,data})+'\n');
                }).on('close',()=>process.exit(0));
                """);
            using var api = new ApiClient(script, fixture);
            _settings.Theme = "Default"; ApplyAppearance();
            var comments = ShowCommentsAsync(new SocialItem { Kind = "song", ThreadId = "R_SO_4_123", ResourceType = 0, ResourceKey = "123" }, api);
            try
            {
                await WaitFor(() => _dialog is { IsLoaded: true } && FindDescendant<ListView>(_dialog)?.Items.Count == 1);
                var dialog = _dialog!; var list = FindDescendant<ListView>(dialog)!;
                var reads = api.RequestCount;
                foreach (var dark in new[] { true, false, true })
                {
                    await ExternalTheme(dark);
                    await WaitFor(() => dialog.ActualTheme == Root.ActualTheme);
                    Root.UpdateLayout(); await Task.Delay(100);
                    var row = (DependencyObject)list.ContainerFromIndex(0);
                    var text = FindDescendant<TextBlock>(row)!;
                    var color = ((SolidColorBrush)text.Foreground).Color;
                    var brightness = (color.R + color.G + color.B) / 3;
                    check($"Open comments follow external {(dark ? "dark" : "light")} theme", dialog.ActualTheme == Root.ActualTheme && (dark ? brightness > 180 : brightness < 100), new { theme = dialog.ActualTheme.ToString(), foreground = color.ToString() });
                    check("Theme changes preserve the same comments and input", ReferenceEquals(dialog, _dialog) && list.Items.Count == 1 && api.RequestCount == reads, null);
                    check("Caption buttons follow app theme", AppWindow.TitleBar.ButtonForegroundColor == (dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black), null);
                    await SaveLibraryImageAsync(dialog, dark ? "theme-batch-comments-dark.png" : "theme-batch-comments-light.png");
                }
            }
            finally { _dialog?.Hide(); await comments; }
            var artworkDirectory = Artwork.CacheDirectory;
            try
            {
                Artwork.CacheDirectory = fixture;
                const string coverUrl = "https://theme-check.invalid/cover.png";
                var cache = Path.Combine(fixture, "Artwork"); Directory.CreateDirectory(cache);
                var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Artwork.CacheIdentity(coverUrl, 256)))) + ".img";
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"), Path.Combine(cache, name));
                _restoringPlayback = true;
                try { _playback.Restore(new() { Queue = [new Track(123, "Theme song", "Artist", "Album", coverUrl, 60000)], CurrentId = 123 }); }
                finally { _restoringPlayback = false; }
                _view = "playing";
                var downloads = Artwork.Downloads;
                foreach (var dark in new[] { false, true })
                {
                    await ExternalTheme(dark);
                    await WaitFor(() => _coverBackgroundKey?.EndsWith(Root.ActualTheme.ToString()) == true && CoverTint.Background is LinearGradientBrush);
                    var tint = ((LinearGradientBrush)CoverTint.Background).GradientStops.Last().Color;
                    check("Playing cover tint refreshes with external " + (dark ? "dark" : "light") + " theme", (dark ? tint.R < 50 : tint.R > 200) && Artwork.Downloads == downloads, new { color = tint.ToString() });
                }
            }
            finally { _view = "queue"; _coverLoad?.Cancel(); BlurredCover.Source = null; Artwork.CacheDirectory = artworkDirectory; }
            var sound = new Flyout { Content = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 240 } };
            ShowThemedFlyout(sound, SoundButton); await WaitFor(() => sound.IsOpen);
            foreach (var dark in new[] { false, true })
            {
                await ExternalTheme(dark); await Task.Delay(80);
                var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<FlyoutPresenter>().First();
                check("Open flyout follows external " + (dark ? "dark" : "light") + " theme", presenter.ActualTheme == Root.ActualTheme, null);
            }
            sound.Hide();
            _settings.Theme = "Light"; ApplyAppearance();
            personalize.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
            SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x001A, 0, 0); await Task.Delay(100);
            check("Explicit app theme survives external system changes", Root.ActualTheme == ElementTheme.Light, null);
            var first = new Track(123, "First song", "Artist", "Album", "", 60000);
            var second = new Track(456, "Second song", "Artist", "Album", "", 60000);
            var third = new Track(789, "Third song", "Artist", "Album", "", 60000);
            var fourth = new Track(987, "Fourth song", "Artist", "Album", "", 60000);
            _playback.Restore(new() { Queue = [first, fourth], CurrentId = first.Id });
            _view = "playlist"; _playlistId = 900; _playlistOwner = _accountId = 42;
            _playlistTrackIds = [789, 456, 123]; _playlistTrackCount = _offset = 3;
            Tracks.Clear(); foreach (var t in new[] { third, second, first }) Tracks.Add(t);
            EmptyPanel.Visibility = Visibility.Collapsed; TrackCount.Text = "3 首"; PageTitle.Text = "测试歌单";
            Root.UpdateLayout(); TrackList.ScrollIntoView(third);
            await WaitFor(() => TrackList.ContainerFromItem(third) != null);
            var host = FindTrackContextHost((DependencyObject)TrackList.ContainerFromItem(third))!;
            var menu = (MenuFlyout)host.ContextFlyout;
            menu.ShowAt(host); await WaitFor(() => menu.IsOpen);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                _settings.Theme = theme; ApplyAppearance(); await Task.Delay(80);
                var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<MenuFlyoutPresenter>().First();
                check("Open context menu follows " + theme + " theme", presenter.ActualTheme == Root.ActualTheme, null);
            }
            var batch = menu.Items.OfType<MenuFlyoutItem>().First(i => i.Text == "多选");
            check("Native right-click/holding menu exposes multiselect", host.IsHoldingEnabled && batch.IsEnabled && ReferenceEquals(batch.Tag, TrackList), null);
            ContextBatch_Click(batch, new()); menu.Hide();
            check("Menu enters checkbox selection without playing", TrackList.SelectionMode == ListViewSelectionMode.Multiple && !TrackList.IsItemClickEnabled && TrackList.SelectedItems.Contains(third) && _playback.Current?.Id == 123, null);
            TrackList.SelectedItems.Add(second);
            var bar = (Panel)TrackBatchHost.Content;
            BatchAction_Click(bar.Children.OfType<Button>().First(b => b.Tag is "all"), new());
            check("Select-all includes loaded songs", TrackList.SelectedItems.Count == 3, null);
            BatchAction_Click(bar.Children.OfType<Button>().First(b => b.Tag is "clear"), new());
            check("Clear selection keeps multiselect active", TrackList.SelectedItems.Count == 0 && _batchList == TrackList, null);
            TrackList.SelectedItems.Add(third); TrackList.SelectedItems.Add(second);
            Track_DoubleTapped(TrackList, new());
            check("Double-tap in multiselect cannot start playback", _playback.Current?.Id == 123 && !_playback.IsPlaying && TrackList.SelectedItems.Count == 2, null);
            var selection = SelectedBatchTracks();
            check("Batch tracks follow displayed order", selection.Select(t => t.Id).SequenceEqual(new long[] { 789, 456 }), null);
            _playback.AddNext(selection);
            check("Batch next preserves order and existing queue", _playback.Queue.Select(t => t.Id).SequenceEqual(new long[] { 123, 789, 456, 987 }), null);
            check("Batch next is also applied to the original queue", _playback.Capture().OriginalQueue.Select(t => t.Id).SequenceEqual(new long[] { 123, 789, 456, 987 }), null);
            await SaveLibraryImageAsync(Root, "theme-batch-playlist.png");
            check("Stale playlist cannot be batch edited", !await RemovePlaylistTracksAsync(api, new(899, 789, _api.Cookie), [789, 456]) && Tracks.Count == 3, null);
            _playlistId = 901;
            check("Rejected batch removal preserves all selected rows", !await RemovePlaylistTracksAsync(api, new(901, 789, _api.Cookie), [789, 456]) && Tracks.Count == 3 && TrackList.SelectedItems.Count == 2, null);
            _playlistId = 900; Notice.IsOpen = false;
            check("Batch removal updates IDs, count and offset once", await RemovePlaylistTracksAsync(api, new(900, 789, _api.Cookie), [789, 456]) && Tracks.Count == 1 && Tracks[0].Id == 123 && _playlistTrackIds.SequenceEqual(new long[] { 123 }) && _playlistTrackCount == 1 && _offset == 1, null);
            check("Playlist batch removal preserves playback", _playback.Queue.Count == 4 && _playback.Current?.Id == 123, null);
            var requests = api.RequestCount;
            try { await api.RemovePlaylistTracksAsync(900, 99, 42, [123, 456]); check("Other owners cannot batch remove", false, null); }
            catch (InvalidOperationException) { check("Other owners cannot batch remove", api.RequestCount == requests, null); }
            var echo = await api.RemovePlaylistTracksAsync(900, 42, 42, [123, 456, 123]);
            check("Batch API deduplicates into a single request", echo.GetProperty("tracks").GetString() == "123,456", null);
            await NavigateAsync("queue");
            check("Navigation exits playlist multiselect", _batchList == null && TrackList.SelectionMode == ListViewSelectionMode.Single && TrackList.IsItemClickEnabled, null);
            BeginTrackBatch(TrackList, third); TrackList.SelectedItems.Add(second);
            _playback.Mode = "Shuffle";
            check("Queue page retains selection while shuffle reorders songs", SelectedBatchTracks().Length == 2, null);
            _playback.Mode = "Sequential"; EndTrackBatch();
            ShowQueue_Click(QueueButton, new()); Root.UpdateLayout();
            await WaitFor(() => QueueList.ContainerFromItem(third) != null);
            var queueHost = FindTrackContextHost((DependencyObject)QueueList.ContainerFromItem(third))!;
            var queueMenu = (MenuFlyout)queueHost.ContextFlyout; queueMenu.ShowAt(queueHost); await WaitFor(() => queueMenu.IsOpen);
            ContextBatch_Click(queueMenu.Items.OfType<MenuFlyoutItem>().First(i => i.Text == "多选"), new()); queueMenu.Hide();
            check("Queue holding menu enters native multiselect", queueHost.IsHoldingEnabled && ReferenceEquals(_batchList, QueueList) && QueueList.SelectedItems.Contains(third), null);
            QueueList.SelectedItems.Add(second);
            _playback.Mode = "Repeat";
            check("Playback notifications preserve queue multiselection", SelectedBatchTracks().Length == 2 && !QueueList.IsItemClickEnabled, null);
            QueueLocate_Click(QueueLocateButton, new());
            check("Locate current song preserves multiselection", SelectedBatchTracks().Length == 2, null);
            await SaveLibraryImageAsync(QueuePane, "theme-batch-queue.png");
            QueueList.SelectAll();
            var removed = _playback.Remove(SelectedBatchTracks());
            check("Queue batch removal keeps the playing song", removed == 3 && _playback.Queue.Count == 1 && _playback.Current?.Id == 123, null);
            ShowQueue_Click(QueueButton, new());
            check("Closing queue restores single-click playback", _batchList == null && QueueList.SelectionMode == ListViewSelectionMode.Single && QueueList.IsItemClickEnabled && QueueList.ItemsSource == null, null);
            BeginTrackBatch(TrackList, first); TrackList.SelectedItems.Clear();
            check("Empty selection disables batch writes", ((Panel)TrackBatchHost.Content).Children.OfType<Button>().Where(b => b.Tag is "next" or "favorite" or "download" or "remove").All(b => !b.IsEnabled), null);
            HideWindow();
            check("Hiding clears multiselect without background UI work", _batchList == null && !_timer.IsEnabled, null);
        }
        finally
        {
            EndTrackBatch();
            if (previous == null) personalize.DeleteValue("AppsUseLightTheme", false); else personalize.SetValue("AppsUseLightTheme", previous, previousKind);
            SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x001A, 0, 0);
            await DeleteWorkerFixtureAsync(fixture);
        }
    }
}
