using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLightDismissChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; RestoreWindow(); _playback.SetVolume(0); _accountLoad?.Cancel(); _inboxCheck?.Cancel();
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < 12) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Light-dismiss check did not settle");
        }
        async Task Ready() => await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
        async Task Dismiss(Task task)
        {
            _dialogSurface!.Hide(); await task;
            await Wait(() => _dialog == null && _dialogSurface == null);
        }
        try
        {
            _settings.Backdrop = "Mica";
            _playlistId = 101; _playlistName = "在线歌单"; await NavigateAsync("playlist");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                _settings.Theme = theme; ApplyAppearance();
                var comments = ShowCommentsAsync(SongCommentsSource(Tracks[0])!); await Ready();
                var dialogBrush = (SolidColorBrush)_dialog!.BorderBrush;
                var menu = CreateTrackMenu(Tracks[0], TrackList);
                // Open a separate native menu after closing comments, so it has
                // the same input activation and theme rather than nesting surfaces.
                var dialogColor = dialogBrush.Color; var dialogStroke = _dialog.BorderThickness;
                var actualBorder = FindDescendant<Border>(_dialog, b => b.Name == "BackgroundElement")!;
                check(theme + " dialog template uses the configured single border", actualBorder.BorderThickness == dialogStroke && ((SolidColorBrush)actualBorder.BorderBrush).Color == dialogColor, null);
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Single(p => p.Child is FlyoutPresenter f && ReferenceEquals(f.Content, _dialogSurface!.Content));
                var scale = Root.XamlRoot.RasterizationScale;
                check(theme + " popup edges align to physical pixels and keep the system corner radius", Math.Abs(actualBorder.BorderThickness.Left * scale - 1) < .001 && Math.Abs(popup.HorizontalOffset * scale - Math.Round(popup.HorizontalOffset * scale)) < .001 && Math.Abs(popup.VerticalOffset * scale - Math.Round(popup.VerticalOffset * scale)) < .001 && actualBorder.CornerRadius == (CornerRadius)Application.Current.Resources["OverlayCornerRadius"] && ((FlyoutPresenter)popup.Child).BorderThickness.Left == 0, new { scale, stroke = actualBorder.BorderThickness.Left, popup.HorizontalOffset, popup.VerticalOffset, radius = actualBorder.CornerRadius.TopLeft });
                _dialogSurface!.Hide(); await Task.Delay(200);
                check(theme + " comments ignore native outside-dismiss requests", !comments.IsCompleted && _dialog is { IsLoaded: true } && _dialogSurfaceReady, null);
                _dialog!.Hide(); await comments; Root.UpdateLayout();
                TrackList.ScrollIntoView(Tracks[0]); await Task.Delay(100); Root.UpdateLayout();
                var row = (ListViewItem)TrackList.ContainerFromItem(Tracks[0]); ShowRowContextMenu(menu, row);
                await Wait(() => menu.Items.OfType<MenuFlyoutItem>().Any(i => i.IsLoaded));
                var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<MenuFlyoutPresenter>().Single();
                check(theme + " menu and comments share actual stroke width color and system corners", presenter.BorderThickness == dialogStroke && ((SolidColorBrush)presenter.BorderBrush).Color == dialogColor && presenter.CornerRadius == actualBorder.CornerRadius, new { dialogStroke.Left, color = dialogColor.ToString(), radius = presenter.CornerRadius.TopLeft });
                menu.Hide(); await _rowContextClosed;

                var info = ShowSongDetailsAsync(Tracks[0], _api); await Ready();
                var backdrop = (AdjustableBackdrop)_dialogSurface!.SystemBackdrop!;
                await Dismiss(info);
                check(theme + " information outside-dismiss releases the dialog and material", !backdrop.NativeProfileReady && _songDetailLoad == null, null);

                var input = new TextBox { Text = "尚未保存" };
                var primary = 0;
                var edit = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "编辑", Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消" };
                edit.PrimaryButtonClick += (_, _) => primary++;
                _dialog = edit;
                var showing = ShowThemedDialogAsync(edit); await Ready();
                _dialogSurface!.Hide(); var result = await showing; _dialog = null;
                check(theme + " outside dismiss cancels a form without invoking Save", result == ContentDialogResult.None && primary == 0 && _dialogSurface == null, null);
            }
            _settings.Theme = "Light"; ApplyAppearance();
            LocalLinkFixtures.Hold("song_detail"); var canceled = LocalLinkFixtures.Canceled;
            var pending = ShowSongDetailsAsync(new Track(999, "加载中", "", "", "", 1000), _api); await Ready();
            await Wait(() => LocalLinkFixtures.GateMethod.Length == 0); await Dismiss(pending);
            check("Outside-dismiss cancels unfinished information requests", LocalLinkFixtures.Canceled > canceled && _songDetailLoad == null, null); LocalLinkFixtures.Release();

            _localPlaylistId = "preview-a"; await NavigateAsync("local-playlist"); StopLocalMonitoring();
            var local = Tracks.First(); var removal = ConfirmRemoveLocalTracksAsync([local]); await Ready();
            Descendants<CheckBox>(_dialog!).Single().IsChecked = true;
            await Dismiss(removal);
            check("Outside dismiss never removes a song or deletes its checked audio file", File.Exists(local.LocalPath) && _localLibrary.Tracks.Any(t => t.LocalPath == local.LocalPath) && !_localRemovalBusy, null);

            _playlistId = 101; await NavigateAsync("playlist");
            var downloads = ConfirmDownloadsAsync([Tracks[0]]); await Ready();
            var count = DownloadJobs.Count;
            var pickerGate = new TaskCompletionSource<bool>();
            var choosing = PreserveDialogForPickerAsync(() => pickerGate.Task);
            _dialogSurface!.Hide(); await Task.Delay(150);
            check("A native child picker keeps its parent form open", !downloads.IsCompleted && _dialog is { IsLoaded: true }, null);
            pickerGate.SetResult(true); await choosing;
            check("Returning from a native child picker restores outside dismissal", _dialogPickerDepth == 0, null);
            var combo = Descendants<ComboBox>(_dialog!).Single(); combo.IsDropDownOpen = true; await Task.Delay(100);
            check("Opening a nested quality picker keeps its parent dialog open", _dialog is { IsLoaded: true } && _dialogSurfaceReady && !downloads.IsCompleted, null);
            combo.IsDropDownOpen = false; await Dismiss(downloads);
            check("Outside dismiss cancels download options without enqueuing", DownloadJobs.Count == count, null);
        }
        finally { LocalLinkFixtures.Release(); _dialog?.Hide(); StopLocalMonitoring(); }
    }
}
