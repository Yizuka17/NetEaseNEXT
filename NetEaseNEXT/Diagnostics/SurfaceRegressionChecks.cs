using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Services;
using NetEaseNEXT.Models;
using NetEaseNEXT.Controls;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSurfaceRegressionChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        var oldSize = AppWindow.Size; var oldFont = _settings.TextFontSize; var oldBackground = TrackSelectionIndicator.Background;
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < 12) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Surface regression did not settle");
        }
        async Task<Track> OpenRowComment()
        {
            var track = Tracks[0]; TrackList.ScrollIntoView(track); Root.UpdateLayout();
            await Wait(() => TrackList.ContainerFromItem(track) is ListViewItem { IsLoaded: true });
            var row = (ListViewItem)TrackList.ContainerFromItem(track);
            var menu = CreateTrackMenu(track, TrackList); ShowRowContextMenu(menu, row);
            await Wait(() => menu.Items.OfType<MenuFlyoutItem>().Any(i => i.IsLoaded));
            var action = menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == "评论");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(action).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => _dialog is { IsLoaded: true } && _dialogSurfaceReady);
            return track;
        }
        async Task CloseComment()
        {
            _dialog!.Hide(); await Wait(() => _dialog == null && !_dialogSurfaceReady && _commentsLoad == null);
        }
        async Task RowComment(string label)
        {
            var track = await OpenRowComment();
            await Wait(() => _dialog is { IsLoaded: true } && _dialogSurfaceReady && Descendants<TextBlock>(_dialog).Any(t => t.Text == "评论 (0)"));
            check(label + " opens and loads the captured song after its context menu closes", _rowContextMenu == null && LocalLinkFixtures.Calls.Last(c => c.Method == "comment_new").Query.Text("id") == SourceSongId(track).ToString(), null);
            check(label + " keeps acrylic and a pixel-aligned WinUI border", _dialog!.Background is AcrylicBrush && Math.Abs(_dialog!.BorderThickness.Left * Root.XamlRoot.RasterizationScale - 1) < .001 && _dialog.CornerRadius == (CornerRadius)Application.Current.Resources["OverlayCornerRadius"], null);
            await SaveLibraryImageAsync(Root, "context-comments-" + label + ".png");
            await CloseComment();
            check(label + " releases the dialog and input handler on close", !_dialogSurfaceReady && _dialog == null, null);
        }
        async Task Marker(string label, bool queue = false)
        {
            Root.UpdateLayout(); UpdateSelectionIndicators(false); await Task.Delay(550);
            var state = queue ? _queueSelectionMotion : _trackSelectionMotion;
            var list = queue ? QueueList : TrackList;
            FrameworkElement capture = queue ? QueuePane : Root;
            var selected = state.PlaybackItem ?? list.SelectedItem!;
            var row = (ListViewItem)list.ContainerFromItem(selected);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(capture);
            var buffer = await bitmap.GetPixelsAsync(); var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var top = bitmap.PixelHeight; var bottom = -1;
            for (var y = 0; y < bitmap.PixelHeight; y++)
                for (var x = 0; x < bitmap.PixelWidth; x++)
                {
                    var i = (y * bitmap.PixelWidth + x) * 4;
                    if (pixels[i] > 240 && pixels[i + 1] < 15 && pixels[i + 2] > 240) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                }
            var rootBounds = new FrameworkElementAutomationPeer(capture).GetBoundingRectangle();
            var rowBounds = new ListViewItemAutomationPeer(row).GetBoundingRectangle();
            // UIA clips accessible bounds at the viewport edge. Compare the
            // rendered marker against the full row in capture coordinates,
            // independently of the production content/scroll expression.
            var fullRowBounds = row.TransformToVisual(capture).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
            var expected = (fullRowBounds.Y + fullRowBounds.Height / 2) * bitmap.PixelHeight / capture.ActualHeight;
            var center = (top + bottom + 1) / 2d;
            await SaveLibraryImageAsync(capture, "indicator-" + label + ".png");
            check(label + " rendered marker stays centered on its full song row", bottom >= top && Math.Abs(center - expected) <= 2, new { top, bottom, center, expected, rootBounds, rowBounds, fullRowBounds, state.Y, scroll = state.Scroll?.VerticalOffset });
        }
        try
        {
            _settings.Backdrop = "Mica"; _settings.Theme = "Light"; ApplyAppearance();
            _playlistId = 101; _playlistName = "在线歌单"; await NavigateAsync("playlist"); Root.UpdateLayout();
            await RowComment("online-first"); await RowComment("online-repeat");
            LocalLinkFixtures.CommentFailures = 1; await OpenRowComment();
            var footer = (PageLoadingFooter)Descendants<ListView>(_dialog!).Single().Footer;
            await Wait(() => footer.Failed);
            check("A comment request failure remains inside a usable dialog", _dialog is { IsLoaded: true } && footer.Retry.IsEnabled, null);
            ((IInvokeProvider)new ButtonAutomationPeer(footer.Retry).GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => Descendants<TextBlock>(_dialog!).Any(t => t.Text == "评论 (0)"));
            check("Failed context comments can retry without reopening", !footer.Failed, null); await CloseComment();
            var canceled = LocalLinkFixtures.Canceled; LocalLinkFixtures.Hold("comment_new"); await OpenRowComment();
            await Wait(() => LocalLinkFixtures.GateMethod.Length == 0); await CloseComment();
            await Wait(() => LocalLinkFixtures.Canceled > canceled); LocalLinkFixtures.Release();
            check("Closing context comments cancels an unfinished request", _dialog == null && _commentsLoad == null, null);
            _localPlaylistId = "preview-a"; await NavigateAsync("local-playlist"); StopLocalMonitoring();
            await RowComment("linked-local");

            _playlistId = 101; await NavigateAsync("playlist");
            PlaylistDetailTitle.Text = "南国秋临，红叶翩舞，窗口大小改变之后仍然正确定位";
            PlaylistDescription.Text = "秋的日常歌单，不断成长中。真正的好歌要经过千锤百炼，并且百听不厌。";
            TrackSelectionIndicator.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 0, 255));
            foreach (var font in new[] { 14d, 22d })
            {
                _settings.TextFontSize = font; ApplyFont();
                foreach (var width in new[] { 1000, 1280, 900 })
                {
                    AppWindow.Resize(new(width, 900)); await Task.Delay(200); Root.UpdateLayout();
                    var scroll = FindDescendant<ScrollViewer>(TrackList)!;
                    scroll.ChangeView(null, 0, null, true); await Task.Delay(150); Root.UpdateLayout();
                    SelectPlayingTrack(TrackList, Tracks[0]);
                    if (TrackSelectionIndicator.Visibility != Visibility.Visible) { TrackList.ScrollIntoView(Tracks[0]); await Task.Delay(200); }
                    await Marker("font" + font + "-width" + width);
                }
            }
            _settings.TextFontSize = 14; ApplyFont(); AppWindow.Resize(new(1100, 900)); await Task.Delay(200);
            var scroller = FindDescendant<ScrollViewer>(TrackList)!;
            TrackList.ScrollIntoView(Tracks[8]); await Task.Delay(200); SelectPlayingTrack(TrackList, Tracks[8]);
            await Marker("scrolled-before");
            scroller.ChangeView(null, scroller.VerticalOffset + 27, null, true); await Task.Delay(150);
            await Marker("scrolled-after");
            foreach (var width in new[] { 900, 1280, 1000 })
            {
                AppWindow.Resize(new(width, 900)); await Task.Delay(100); Root.UpdateLayout();
                await Marker("scrolled-resize-" + width);
            }
            var oldQueueBackground = QueueSelectionIndicator.Background;
            QueueSelectionIndicator.Background = TrackSelectionIndicator.Background;
            try
            {
                _restoringPlayback = true; _playback.Restore(new() { Queue = Tracks.ToList(), CurrentId = Tracks[8].Id }); _restoringPlayback = false;
                ShowQueue_Click(QueueButton, new()); await Wait(() => QueuePane.IsLoaded && QueueList.Items.Count > 0);
                QueueList.ScrollIntoView(Tracks[8]); await Task.Delay(200); SelectPlayingTrack(QueueList, Tracks[8]);
                await Marker("queue-before", true);
                var queueScroll = FindDescendant<ScrollViewer>(QueueList)!; queueScroll.ChangeView(null, queueScroll.VerticalOffset + 21, null, true); await Task.Delay(150);
                await Marker("queue-after", true);
            }
            finally { QueueSelectionIndicator.Background = oldQueueBackground; ShowQueue_Click(QueueButton, new()); }
            check("The window leaves corner rounding to DWM", DwmGetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, out var preference, sizeof(int)) == 0 && preference == 0, null);
        }
        finally
        {
            LocalLinkFixtures.Release(); _dialog?.Hide(); StopLocalMonitoring();
            TrackSelectionIndicator.Background = oldBackground; _settings.TextFontSize = oldFont; ApplyFont(); AppWindow.Resize(oldSize);
        }
    }
}
