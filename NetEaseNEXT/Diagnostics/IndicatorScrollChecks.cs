using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunIndicatorScrollChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        var oldAnimations = _settings.Animations; var oldMode = Nav.PaneDisplayMode;
        var oldSize = AppWindow.Size; var oldSelection = Nav.SelectedItem;
        var oldFont = _settings.TextFontSize;
        _settings.Animations = false; _settings.TextFontSize = 14; ApplyFont();
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < 12) { await Task.Delay(25); }
            if (!condition()) throw new TimeoutException("Indicator scroll check did not settle");
        }
        async Task<int> SidebarPixels(string label, ScrollViewer scroll)
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
            var buffer = await bitmap.GetPixelsAsync(); var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var viewport = FindDescendant<ScrollContentPresenter>(scroll)!;
            var bounds = viewport.TransformToVisual(Root).TransformBounds(new(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            var scale = bitmap.PixelHeight / Root.ActualHeight;
            var count = 0; var outside = 0;
            for (var y = 0; y < bitmap.PixelHeight; y++)
                for (var x = 0; x < bitmap.PixelWidth; x++)
                {
                    var i = (y * bitmap.PixelWidth + x) * 4;
                    if (pixels[i] <= 240 || pixels[i + 1] >= 15 || pixels[i + 2] <= 240) continue;
                    count++;
                    if (x + .5 < bounds.X * scale || x + .5 > bounds.Right * scale || y + .5 < bounds.Y * scale || y + .5 > bounds.Bottom * scale) outside++;
                }
            check(label + " paints no indicator outside the native sidebar viewport", outside == 0, new { count, outside, bounds });
            return count;
        }
        async Task Follow(string name, FrameworkElement owner, FrameworkElement row, FrameworkElement marker, ScrollViewer scroll, SelectionMotionState state)
        {
            var content = (FrameworkElement)scroll.Content;
            var bounds = row.TransformToVisual(content).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
            var start = Math.Clamp(bounds.Y + bounds.Height / 2 - scroll.ViewportHeight / 2, 0, scroll.ScrollableHeight);
            scroll.ChangeView(null, start, null, true); await Task.Delay(180);
            var target = Math.Clamp(start + Math.Min(36, scroll.ViewportHeight / 5), 0, scroll.ScrollableHeight);
            if (target - start < 5) target = Math.Max(0, start - Math.Min(36, scroll.ViewportHeight / 5));
            check(name + " has enough viewport movement to test scrolling", Math.Abs(target - start) >= 5, new { start, target, scroll.ScrollableHeight, scroll.ViewportHeight });
            var samples = new List<object>(); var maxError = 0d;
            var reference = new Border { Width = 4, Height = 4, Background = new SolidColorBrush(Microsoft.UI.Colors.Cyan), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0), IsHitTestVisible = false };
            var rowRoot = FindDescendant<Grid>(row)!;
            Grid.SetRowSpan(reference, Math.Max(1, rowRoot.RowDefinitions.Count)); Grid.SetColumnSpan(reference, Math.Max(1, rowRoot.ColumnDefinitions.Count));
            var oldBrush = ((Border)marker).Background;
            rowRoot.Children.Add(reference); ((Border)marker).Background = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
            owner.UpdateLayout();
            var referenceBounds = reference.TransformToVisual(owner).TransformBounds(new(0, 0, reference.ActualWidth, reference.ActualHeight));
            var rowBounds = row.TransformToVisual(owner).TransformBounds(new(0, 0, row.ActualWidth, row.ActualHeight));
            var referenceOffset = rowBounds.Y + rowBounds.Height / 2 - referenceBounds.Y - referenceBounds.Height / 2;
            var bitmap = new RenderTargetBitmap();
            byte[]? pixels = null;
            try
            {
            if (Environment.GetCommandLineArgs().Contains("--indicator-scroll-inspect") && name == "LeftCompact sidebar")
            {
                var trigger = new Button { Content = "滚动检查", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 8, 12, 0) };
                var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                trigger.Click += (_, _) => pressed.TrySetResult();
                Grid.SetRow(trigger, 1); Canvas.SetZIndex(trigger, 20); Root.Children.Add(trigger);
                try { await pressed.Task.WaitAsync(TimeSpan.FromMinutes(2)); }
                finally { Root.Children.Remove(trigger); }
                owner.UpdateLayout(); await Task.Delay(120);
                scroll.ChangeView(null, target, null, false); await Task.Delay(60);
                // An opt-in native screen capture can observe independent
                // scrolling during this pause; no bitmap/layout capture here.
                Thread.Sleep(4000); await Task.Delay(200);
            }
            foreach (var destination in new[] { target, start, target, start })
            {
                scroll.ChangeView(null, destination, null, false);
                // Do not force a layout pass or manually reposition the marker:
                // scrolling must keep it attached between ordinary UI updates.
                for (var frame = 0; frame < 8; frame++)
                {
                    await Task.Delay(16);
                    // Read both colors from ONE rendered frame. Two successive
                    // TransformToVisual calls can observe different compositor
                    // times during inertia and report an artificial frame lag.
                    await bitmap.RenderAsync(owner);
                    var buffer = await bitmap.GetPixelsAsync(); pixels ??= new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                    var markerTop = bitmap.PixelHeight; var markerBottom = -1; var referenceTop = bitmap.PixelHeight; var referenceBottom = -1;
                    for (var y = 0; y < bitmap.PixelHeight; y++)
                        for (var x = 0; x < bitmap.PixelWidth; x++)
                        {
                            var i = (y * bitmap.PixelWidth + x) * 4;
                            if (pixels[i] > 240 && pixels[i + 1] < 15 && pixels[i + 2] > 240) { markerTop = Math.Min(markerTop, y); markerBottom = Math.Max(markerBottom, y); }
                            if (pixels[i] > 240 && pixels[i + 1] > 240 && pixels[i + 2] < 15) { referenceTop = Math.Min(referenceTop, y); referenceBottom = Math.Max(referenceBottom, y); }
                        }
                    if (markerBottom < markerTop || referenceBottom < referenceTop) throw new InvalidOperationException(name + " capture lost its visible marker or reference");
                    var scale = bitmap.PixelHeight / owner.ActualHeight;
                    var expected = (referenceTop + referenceBottom + 1) / 2d / scale + referenceOffset;
                    var actual = (markerTop + markerBottom + 1) / 2d / scale;
                    var error = Math.Abs(actual - expected); maxError = Math.Max(maxError, error);
                    samples.Add(new { expected, actual, error, offset = scroll.VerticalOffset });
                }
            }
            }
            finally { rowRoot.Children.Remove(reference); ((Border)marker).Background = oldBrush; }
            check(name + " follows animated scrolling in both directions", marker.Visibility == Visibility.Visible && maxError <= 1.5, new { maxError, samples });
            check(name + " is bound to the selected region's live compositor scroll", state.ScrollBound && ReferenceEquals(state.Scroll, scroll), null);
        }
        try
        {
            RestoreWindow(); AppWindow.Resize(new(1280, 1100)); await Task.Delay(200);
            foreach (var mode in new[] { NavigationViewPaneDisplayMode.LeftCompact, NavigationViewPaneDisplayMode.Left })
            {
                Nav.PaneDisplayMode = mode; Nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
                Nav.SelectedItem = Nav.MenuItems[2]; await Task.Delay(200); Root.UpdateLayout(); UpdateSidebarSelection(false);
                var item = (NavigationViewItem)Nav.SelectedItem;
                ScrollViewer? scroll = null;
                for (DependencyObject? parent = VisualTreeHelper.GetParent(item); parent != null && parent != Nav; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollViewer found) { scroll = found; break; }
                check(mode + " sidebar uses a scrollable menu in a short window", scroll is { ScrollableHeight: > 10 }, new { height = Nav.ActualHeight, scroll?.ScrollableHeight });
                await Follow(mode + " sidebar", Root, item, SidebarSelectionIndicator, scroll!, _sidebarSelectionMotion);
                var menuScroll = scroll!;
                Nav.SelectedItem = Nav.MenuItems[0]; await Task.Delay(150);
                var brush = SidebarSelectionIndicator.Background;
                SidebarSelectionIndicator.Background = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
                try
                {
                    var first = (NavigationViewItem)Nav.MenuItems[0];
                    var firstBounds = first.TransformToVisual((FrameworkElement)menuScroll.Content).TransformBounds(new(0, 0, first.ActualWidth, first.ActualHeight));
                    menuScroll.ChangeView(null, firstBounds.Y + firstBounds.Height / 2 + SidebarSelectionIndicator.Height / 4, null, true); await Task.Delay(150);
                    check(mode + " partially clipped selection stays within the scroll region", await SidebarPixels(mode + " partial selection", menuScroll) > 0, null);
                    menuScroll.ChangeView(null, menuScroll.ScrollableHeight, null, true); await Task.Delay(150);
                    check(mode + " scrolled-out selection is clipped without clearing selection", await SidebarPixels(mode + " hidden selection", menuScroll) == 0 && ReferenceEquals(Nav.SelectedItem, Nav.MenuItems[0]), null);
                    menuScroll.ChangeView(null, 0, null, true); await Task.Delay(150);
                    check(mode + " returning selection reappears without selecting it again", await SidebarPixels(mode + " returned selection", menuScroll) > 0 && ReferenceEquals(Nav.SelectedItem, Nav.MenuItems[0]), null);
                }
                finally { SidebarSelectionIndicator.Background = brush; }
                Nav.SelectedItem = Nav.SettingsItem; await Task.Delay(150);
                var settings = (NavigationViewItem)Nav.SettingsItem;
                var before = SidebarSelectionIndicator.TransformToVisual(Root).TransformPoint(new()).Y;
                menuScroll.ChangeView(null, menuScroll.ScrollableHeight, null, false); await Task.Delay(350);
                var after = SidebarSelectionIndicator.TransformToVisual(Root).TransformPoint(new()).Y;
                var settingsBounds = settings.TransformToVisual(Root).TransformBounds(new(0, 0, settings.ActualWidth, settings.ActualHeight));
                check(mode + " footer selection stays fixed when the main menu scrolls", Math.Abs(before - after) < 1 && Math.Abs(after + SidebarSelectionIndicator.Height / 2 - settingsBounds.Y - settingsBounds.Height / 2) < 1, new { before, after });
            }
            Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto; AppWindow.Resize(new(900, 1100));
            Nav.IsPaneOpen = false; await Task.Delay(200);
            check("Closed minimal sidebar stops its indicator and scroll binding", Nav.DisplayMode == NavigationViewDisplayMode.Minimal && SidebarSelectionIndicator.Visibility == Visibility.Collapsed && !_sidebarSelectionMotion.ScrollBound, null);
            Nav.SelectedItem = Nav.MenuItems[1]; await Task.Delay(150); Nav.IsPaneOpen = true; await Task.Delay(200);
            check("Opening the minimal sidebar rebinds the visible selection", SidebarSelectionIndicator.Visibility == Visibility.Visible && _sidebarSelectionMotion.ScrollBound, null);
            Nav.IsPaneOpen = false;
            _playlistId = 101; await NavigateAsync("playlist"); AppWindow.Resize(new(1280, 1400)); await Task.Delay(200);
            var track = Tracks[8]; TrackList.ScrollIntoView(track); await Wait(() => TrackList.ContainerFromItem(track) is ListViewItem { IsLoaded: true });
            SelectPlayingTrack(TrackList, track); await Task.Delay(150);
            await Follow("Song list", Root, (ListViewItem)TrackList.ContainerFromItem(track), TrackSelectionIndicator, FindDescendant<ScrollViewer>(TrackList)!, _trackSelectionMotion);
            _restoringPlayback = true; _playback.Restore(new() { Queue = Tracks.ToList(), CurrentId = track.Id }); _restoringPlayback = false;
            ShowQueue_Click(QueueButton, new()); await Wait(() => QueuePane.IsLoaded && QueueList.Items.Count > 0);
            QueueList.ScrollIntoView(track); await Wait(() => QueueList.ContainerFromItem(track) is ListViewItem { IsLoaded: true });
            SelectPlayingTrack(QueueList, track); await Task.Delay(150);
            await Follow("Playback queue", QueuePane, (ListViewItem)QueueList.ContainerFromItem(track), QueueSelectionIndicator, FindDescendant<ScrollViewer>(QueueList)!, _queueSelectionMotion);
            ShowQueue_Click(QueueButton, new());
            await Wait(() => !_queueSelectionMotion.ScrollBound);
            check("Closing the queue releases its compositor scroll binding", !_queueSelectionMotion.ScrollBound, null);
            await NavigateAsync("discover"); await Wait(() => !_trackSelectionMotion.ScrollBound);
            check("Leaving the song page releases its compositor scroll binding", !_trackSelectionMotion.ScrollBound, null);
            HideWindow();
            check("Hiding the window releases every indicator scroll binding", !_sidebarSelectionMotion.ScrollBound && !_trackSelectionMotion.ScrollBound && !_queueSelectionMotion.ScrollBound, null);
            RestoreWindow();
        }
        finally
        {
            _restoringPlayback = false; Nav.SelectedItem = oldSelection; Nav.PaneDisplayMode = oldMode;
            _settings.Animations = oldAnimations; _settings.TextFontSize = oldFont; ApplyFont(); AppWindow.Resize(oldSize);
        }
    }
}
