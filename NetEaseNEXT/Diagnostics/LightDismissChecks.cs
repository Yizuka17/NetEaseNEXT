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
            DismissDialogFromOutside(_dialog!, true); await task;
            await Wait(() => _dialog == null && !_dialogSurfaceReady);
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
                check(theme + " native outside input layer is present and hit-testable", VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => p.Child is FrameworkElement { Name: "SmokeLayerBackground", IsHitTestVisible: true, ActualWidth: > 0, ActualHeight: > 0 }), null);
                check(theme + " dialog template uses the configured single border", actualBorder.BorderThickness == dialogStroke && ((SolidColorBrush)actualBorder.BorderBrush).Color == dialogColor, null);
                var scale = Root.XamlRoot.RasterizationScale;
                check(theme + " dialog uses one native template and a physical-pixel border", Math.Abs(actualBorder.BorderThickness.Left * scale - 1) < .001 && actualBorder.CornerRadius == (CornerRadius)Application.Current.Resources["OverlayCornerRadius"] && !VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => p.Child is FlyoutPresenter), new { scale, stroke = actualBorder.BorderThickness.Left, radius = actualBorder.CornerRadius.TopLeft });
                DismissDialogFromOutside(_dialog!, false); await Task.Delay(200);
                check(theme + " comments ignore native outside-dismiss requests", !comments.IsCompleted && _dialog is { IsLoaded: true } && _dialogSurfaceReady, null);
                _dialog!.Hide(); await comments; Root.UpdateLayout();
                TrackList.ScrollIntoView(Tracks[0]); await Task.Delay(100); Root.UpdateLayout();
                var row = (ListViewItem)TrackList.ContainerFromItem(Tracks[0]); ShowRowContextMenu(menu, row);
                await Wait(() => menu.Items.OfType<MenuFlyoutItem>().Any(i => i.IsLoaded));
                var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<MenuFlyoutPresenter>().Single();
                check(theme + " menu and comments share actual stroke width color and system corners", presenter.BorderThickness == dialogStroke && ((SolidColorBrush)presenter.BorderBrush).Color == dialogColor && presenter.CornerRadius == actualBorder.CornerRadius, new { dialogStroke.Left, color = dialogColor.ToString(), radius = presenter.CornerRadius.TopLeft });
                menu.Hide(); await _rowContextClosed;

                var info = ShowSongDetailsAsync(Tracks[0], _api); await Ready();
                var material = _dialog!.Background;
                await Dismiss(info);
                check(theme + " information outside-dismiss releases the dialog and request", material is AcrylicBrush && !_dialogSurfaceReady && _songDetailLoad == null, null);

                var input = new TextBox { Text = "尚未保存" };
                var primary = 0;
                var edit = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "编辑", Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消" };
                edit.PrimaryButtonClick += (_, _) => primary++;
                _dialog = edit;
                var showing = ShowThemedDialogAsync(edit); await Ready();
                DismissDialogFromOutside(_dialog!, true); var result = await showing; _dialog = null;
                check(theme + " outside dismiss cancels a form without invoking Save", result == ContentDialogResult.None && primary == 0 && !_dialogSurfaceReady, null);
                await NavigateAsync("settings"); Root.UpdateLayout();
                foreach (var selector in new[] { ThemeCombo, FontWeightCombo, DownloadSourceCombo })
                {
                    check(theme + " selector material is ready before opening " + selector.Name, selector.Resources["ComboBoxDropDownBackground"] is AcrylicBrush, null);
                    selector.IsDropDownOpen = true; await Task.Delay(180); Root.UpdateLayout();
                    var selectorBorder = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).Where(c => c != null).Select(c => FindDescendant<Border>(c!, b => b.Name == "PopupBorder")).FirstOrDefault(b => b != null);
                    check(theme + " selector actual popup is acrylic " + selector.Name, selectorBorder?.Background is AcrylicBrush acrylic && (acrylic.TintColor.R < 128) == (theme == "Dark") && selectorBorder.BorderThickness == FloatingStroke, null);
                    if (selector == ThemeCombo) await SaveLibraryImageAsync(Root, "acrylic-selector-" + theme + ".png");
                    selector.IsDropDownOpen = false; await Task.Delay(150);
                }
                var opacity = new List<double>(); var savedTransparency = _settings.AcrylicTransparency; var savedDefault = _settings.AcrylicSystemMaterial;
                foreach (var percent in new[] { 0d, 25d, 50d, 65d, 75d, 100d })
                {
                    _settings.AcrylicTransparency = percent; _settings.AcrylicSystemMaterial = false; ApplyMaterial(Root.ActualTheme);
                    opacity.Add(((AcrylicBrush)ThemeCombo.Resources["ComboBoxDropDownBackground"]).TintOpacity);
                }
                check(theme + " selector transparency changes across the entire slider", opacity.Zip(opacity.Skip(1)).All(pair => pair.First > pair.Second), opacity);
                _settings.AcrylicTransparency = savedTransparency; _settings.AcrylicSystemMaterial = savedDefault; ApplyMaterial(Root.ActualTheme);
                await NavigateAsync("playlist");
                ShowQueue_Click(QueueButton, new()); await Task.Delay(200);
                check(theme + " playback queue uses the same acrylic material", QueuePane.Background is AcrylicBrush && QueuePane.BorderThickness == FloatingStroke && QueueSurface.SystemBackdrop == null, null);
                await SaveLibraryImageAsync(Root, "acrylic-queue-" + theme + ".png");
                ShowQueue_Click(QueueButton, new()); await Task.Delay(300);

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
            DismissDialogFromOutside(_dialog!, true); await Task.Delay(150);
            check("A native child picker keeps its parent form open", !downloads.IsCompleted && _dialog is { IsLoaded: true }, null);
            pickerGate.SetResult(true); await choosing;
            check("Returning from a native child picker restores outside dismissal", _dialogPickerDepth == 0, null);
            var combo = Descendants<ComboBox>(_dialog!).Single(); combo.IsDropDownOpen = true; await Task.Delay(100);
            check("Opening a nested quality picker keeps its parent dialog open", _dialog is { IsLoaded: true } && _dialogSurfaceReady && !downloads.IsCompleted, null);
            var popupBorder = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).Where(c => c != null).Select(c => FindDescendant<Border>(c!, b => b.Name == "PopupBorder")).FirstOrDefault(b => b != null);
            check("Nested quality selector uses acrylic and the same physical-pixel stroke", popupBorder?.Background is AcrylicBrush && popupBorder.BorderThickness == FloatingStroke, null);
            combo.IsDropDownOpen = false; await Dismiss(downloads);
            check("Outside dismiss cancels download options without enqueuing", DownloadJobs.Count == count, null);
        }
        finally { LocalLinkFixtures.Release(); _dialog?.Hide(); StopLocalMonitoring(); }
    }
}
