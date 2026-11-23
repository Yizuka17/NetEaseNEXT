using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunSecondarySurfacesChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow(); AppWindow.Move(new(0, 0)); _sessionReady = true; _authenticated = true; _accountId = 42;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        var capture = Environment.GetCommandLineArgs().Contains("--secondary-surfaces-capture");
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < 12) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Secondary surface did not settle");
        }
        async Task Record(string name)
        {
            if (!capture) return;
            await Task.Delay(200);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "secondary-stage.json"), JsonSerializer.Serialize(new { name, pid = Environment.ProcessId }));
            await Task.Delay(650);
        }
        async Task NativePopup(string name, bool menu, Action open)
        {
            open();
            await Wait(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => menu ? p.Child is MenuFlyoutPresenter : p.Child is FlyoutPresenter));
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Single(p => menu ? p.Child is MenuFlyoutPresenter : p.Child is FlyoutPresenter);
            var presenter = (Control)popup.Child;
            SystemBackdrop? Backdrop() => _rowContextMenu is { IsOpen: true } rowMenu ? rowMenu.SystemBackdrop : _activeFlyout?.SystemBackdrop;
            await Wait(() => Backdrop() is AdjustableBackdrop { NativeProfileReady: true });
            var backdrop = Backdrop() as AdjustableBackdrop ?? throw new InvalidOperationException("No active native material");
            check(name + " has an attached native acrylic controller and translucent content", backdrop.Kind == "Acrylic" && presenter.Background is SolidColorBrush { Color.A: < 255 }, new { main = _settings.Backdrop, theme = Root.ActualTheme.ToString(), backdrop.Kind, backdrop.AppliedProfile, backdrop.AppliedColors });
            await Record(name);
            popup.IsOpen = false;
            await Wait(() => !presenter.IsLoaded && !backdrop.NativeProfileReady);
            check(name + " releases its material on close", !backdrop.NativeProfileReady, null);
        }
        try
        {
            await NavigateAsync("settings");
            foreach (var material in new[] { "Mica", "MicaAlt", "Acrylic", "Solid" })
            {
                SelectTag(BackdropCombo, material); ApplyAppearance();
                check("Main window still supports " + material, _settings.Backdrop == material && (material == "Solid" ? SystemBackdrop == null : SystemBackdrop is AdjustableBackdrop backdrop && backdrop.Kind == material), null);
            }
            SelectTag(BackdropCombo, "Mica"); ApplyAppearance();
            var preferenceFile = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-material-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await File.WriteAllTextAsync(preferenceFile, "{\"Backdrop\":\"MicaAlt\",\"MicaTransparency\":42,\"AcrylicTransparency\":78}");
                var saved = SettingsStore.LoadFrom(preferenceFile);
                check("Existing main material and both transparency preferences are not rewritten on load", saved.Backdrop == "MicaAlt" && saved.MicaTransparency == 42 && saved.AcrylicTransparency == 78, null);
            }
            finally { File.Delete(preferenceFile); }
            await LoadFontsAsync(CancellationToken.None);
            FontCombo.StartBringIntoView(); await Task.Delay(200); Root.UpdateLayout();
            FontCombo.IsDropDownOpen = true; await Task.Delay(250);
            var originalSource = FontCombo.ItemsSource;
            var enumeration = _fontEnumeration;
            check("Font selector remains open after opening", FontCombo.IsDropDownOpen, null);
            await RefreshFontsAsync();
            check("Refreshing fonts defers list replacement until the selector closes", FontCombo.IsDropDownOpen && ReferenceEquals(originalSource, FontCombo.ItemsSource) && _pendingFontFamilies != null, null);
            await Record("font-refresh-open");
            FontCombo.IsDropDownOpen = false; await Task.Delay(200);
            check("Closing applies the pending fonts and preserves the selected family", _pendingFontFamilies == null && Equals(FontCombo.SelectedItem, _settings.FontFamily) && FontCombo.Items.Count > 3, new { count = FontCombo.Items.Count });
            enumeration = _fontEnumeration;
            FontCombo.IsDropDownOpen = true; await Task.Delay(700);
            check("Opening again neither rescans fonts nor closes the selector", FontCombo.IsDropDownOpen && ReferenceEquals(enumeration, _fontEnumeration), null);
            await Record("font-reopen"); FontCombo.IsDropDownOpen = false;

            DownloadPath.StartBringIntoView(); await Task.Delay(200); Root.UpdateLayout();
            if (DownloadPath.ContextFlyout is { } textContext)
            {
                textContext.ShowAt(DownloadPath);
                await Wait(() => textContext.SystemBackdrop is AdjustableBackdrop { NativeProfileReady: true });
                check("Built-in text editing context menu has native acrylic", textContext.SystemBackdrop is AdjustableBackdrop { Kind: "Acrylic" }, null);
                await Record("text-edit-context"); textContext.Hide();
                await Wait(() => !textContext.IsOpen); await Task.Delay(200);
            }
            else check("Built-in text editing context menu is available", false, null);
            _playlistId = 101; _playlistName = "在线歌单"; await NavigateAsync("playlist");
            var track = Tracks[0];
            _restoringPlayback = true;
            try { _playback.Restore(new() { Queue = [track], CurrentId = track.Id }); }
            finally { _restoringPlayback = false; }
            _qualityTrack = track.Id; _maxQuality = 2;
            foreach (var theme in new[] { "Light", "Dark" })
            {
                _settings.Theme = theme; ApplyAppearance();
                await NativePopup("account-" + theme, true, () => Account_Click(AccountButton, new()));
                TrackList.ScrollIntoView(track); Root.UpdateLayout();
                await Wait(() => TrackList.ContainerFromItem(track) is ListViewItem { IsLoaded: true });
                var row = (ListViewItem)TrackList.ContainerFromItem(track);
                await NativePopup("song-context-" + theme, true, () => ShowRowContextMenu(CreateTrackMenu(track, TrackList), row));
                await NativePopup("playlist-context-" + theme, true, () => ShowRowContextMenu(CreateCardMenu(new LocalPlaylistCard("preview-a", "本地歌单 A", "", "")), row));
                await NativePopup("quality-" + theme, true, () => Quality_Click(QualityButton, new()));
                await NativePopup("share-" + theme, true, () => ShowResourceShare(SongShareButton, ShareForTrack(track)));
                await NativePopup("volume-" + theme, false, () => Sound_Click(SoundButton, new()));
                await NativePopup("emoji-" + theme, false, () => ShowEmojiPicker(SongCommentsButton, new TextBox()));
                PlaylistDescription.Text = "用于检查展开简介亚克力材质的测试文字。";
                PlaylistDescription.MaxLines = 3; ExpandDescriptionButton.Visibility = Visibility.Visible;
                await NativePopup("description-" + theme, false, () => ExpandDescription_Click(ExpandDescriptionButton, new()));
                check(theme + " secondary surfaces never change the main material selection", _settings.Backdrop == "Mica" && SystemBackdrop is AdjustableBackdrop { Kind: "Mica" }, null);
            }
        }
        finally
        {
            FontCombo.IsDropDownOpen = false; CloseRowContextMenu(); _dialog?.Hide(); StopLocalMonitoring();
        }
    }
}
