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
    private async Task RunSelectorTypographyChecksAsync(Action<string, bool, object?> check)
    {
        RestoreWindow(); AppWindow.Move(new(0, 0)); _sessionReady = true; _authenticated = true;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        var capture = Environment.GetCommandLineArgs().Contains("--secondary-surfaces-capture");
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds < 12) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Selector typography check did not settle");
        }
        async Task Record(string name)
        {
            if (!capture) return;
            await Task.Delay(180);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "secondary-stage.json"), JsonSerializer.Serialize(new { name, pid = Environment.ProcessId }));
            await Task.Delay(600);
        }
        TextBlock[] Labels(DependencyObject host) => Descendants<TextBlock>(host).Where(t => t.Visibility == Visibility.Visible && t.ActualWidth > 0 && t.Text.Any(char.IsLetterOrDigit) && !t.Text.Any(c => c is >= '\uE000' and <= '\uF8FF')).ToArray();
        async Task Selector(ComboBox combo, string name, bool scroll = false)
        {
            combo.StartBringIntoView(); await Task.Delay(150); Root.UpdateLayout();
            ApplySelectorMaterial(combo);
            var preparedPopup = SelectorPopup(combo);
            combo.IsDropDownOpen = true;
            try { await Wait(() => SelectorPopup(combo) is { IsOpen: true, SystemBackdrop: AdjustableBackdrop { NativeProfileReady: true } }); }
            catch (TimeoutException) { check(name + " dropdown readiness", false, new { combo.IsLoaded, combo.IsDropDownOpen, popupFound = SelectorPopup(combo) != null, prepared = preparedPopup != null, isOpen = SelectorPopup(combo)?.IsOpen, constrained = SelectorPopup(combo)?.ShouldConstrainToRootBounds, material = SelectorPopup(combo)?.SystemBackdrop?.GetType().Name }); throw; }
            var popup = SelectorPopup(combo)!;
            var backdrop = (AdjustableBackdrop)popup.SystemBackdrop;
            try { await Wait(() => Labels(popup.Child).Length > 0); }
            catch (TimeoutException)
            {
                await Record(name + "-empty");
                check(name + " actual popup content is realized", false, new { combo.IsDropDownOpen, child = popup.Child.GetType().Name, texts = Descendants<TextBlock>(popup.Child).Select(t => new { t.Text, t.IsLoaded, t.ActualWidth, t.FontWeight.Weight, t.Visibility }), rows = Descendants<ComboBoxItem>(popup.Child).Select(t => new { t.IsLoaded, t.Content }) });
                throw;
            }
            var border = FindDescendant<Border>(popup.Child, b => b.Name == "PopupBorder")!;
            check(name + " owns native acrylic on its actual dropdown popup", !popup.ShouldConstrainToRootBounds && backdrop.Kind == "Acrylic" && border.Background is AcrylicBrush, null);
            check(name + " applies selected weight to the control and dropdown text", combo.FontWeight.Weight >= _settings.TextFontWeight && Labels(popup.Child).Length > 0 && Labels(popup.Child).All(t => t.FontWeight.Weight >= _settings.TextFontWeight), Labels(popup.Child).Select(t => new { t.Text, t.FontWeight.Weight }).ToArray());
            await Record(name);
            if (scroll && FindDescendant<ScrollViewer>(popup.Child) is { } scroller)
            {
                scroller.ChangeView(null, scroller.ScrollableHeight, null, true); await Task.Delay(350); Root.UpdateLayout();
                check(name + " newly realized dropdown rows retain the selected weight", Labels(popup.Child).Length > 0 && Labels(popup.Child).All(t => t.FontWeight.Weight >= _settings.TextFontWeight), Labels(popup.Child).Select(t => new { t.Text, t.FontWeight.Weight }).ToArray());
                await Record(name + "-scrolled");
            }
            combo.IsDropDownOpen = false;
            await Wait(() => !backdrop.NativeProfileReady);
            check(name + " releases its dropdown controller", !backdrop.NativeProfileReady, null);
        }
        try
        {
            _settings.Backdrop = "Mica"; _settings.TextFontWeight = 700; SelectTag(FontWeightCombo, "700");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                _settings.Theme = theme; ApplyAppearance(); await NavigateAsync("settings"); await LoadFontsAsync(CancellationToken.None);
                foreach (var combo in new[] { BackdropCombo, ThemeCombo, FontWeightCombo, DownloadSourceCombo, OutputCombo, FontCombo })
                    await Selector(combo, "settings-" + combo.Name + "-" + theme, combo == FontCombo);
                check(theme + " buttons and inputs use the selected weight", ResetMaterialButton.FontWeight.Weight == 700 && DownloadPath.FontWeight.Weight == 700 && FontSizeBox.FontWeight.Weight == 700 && LocalOnlineInfoToggle.FontWeight.Weight == 700, null);
                _playlistId = 101; await NavigateAsync("playlist");
                check(theme + " online playlist actions realize with the selected weight", Labels(PlaylistDetails).Length > 0 && Labels(PlaylistDetails).All(t => t.FontWeight.Weight >= 700), Labels(PlaylistDetails).Select(t => new { t.Text, t.FontWeight.Weight }));
                TrackList.ScrollIntoView(Tracks[40]); await Task.Delay(300); Root.UpdateLayout();
                var row = (ListViewItem)TrackList.ContainerFromItem(Tracks[40]);
                check(theme + " newly realized song rows and text use the selected weight", row.FontWeight.Weight == 700 && Labels(row).Length > 0 && Labels(row).All(t => t.FontWeight.Weight >= 700), null);
                var comments = ShowCommentsAsync(SongCommentsSource(Tracks[0])!);
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
                await Selector(Descendants<ComboBox>(_dialog!).Single(), "comment-sort-" + theme);
                check(theme + " comment input and dynamically loaded labels use the selected weight", Descendants<TextBox>(_dialog!).All(t => t.FontWeight.Weight == 700) && Labels(_dialog!).All(t => t.FontWeight.Weight >= 700), new { controls = Descendants<TextBox>(_dialog!).Select(t => new { t.Name, t.FontWeight.Weight }), text = Labels(_dialog!).Select(t => new { t.Name, t.Text, t.FontWeight.Weight }) });
                _dialog!.Hide(); await comments;
                var downloads = ConfirmDownloadsAsync([new Track(999, "下载设置测试", "测试歌手", "测试专辑", "", 4000)]);
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
                await Selector(Descendants<ComboBox>(_dialog!).Single(), "download-quality-" + theme);
                _dialog!.Hide(); await downloads;
                var info = ShowSongDetailsAsync(Tracks[0], _api);
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true } && CreditLinks(_dialog).Length > 0);
                check(theme + " late song information follows the selected weight", Labels(_dialog!).Length > 0 && Labels(_dialog!).All(t => t.FontWeight.Weight >= 700), new { controls = Descendants<TextBox>(_dialog!).Select(t => new { t.Name, t.FontWeight.Weight }), text = Labels(_dialog!).Select(t => new { t.Name, t.Text, t.FontWeight.Weight }) });
                _dialog!.Hide(); await info;
                var creation = CreatePlaylistAsync();
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
                await Selector(Descendants<ComboBox>(_dialog!).Single(), "playlist-privacy-" + theme);
                _dialog!.Hide(); await creation;
                _peerId = 42; SharePrivate_Click(SongShareButton, new());
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
                await Selector(Descendants<ComboBox>(_dialog!).Single(), "private-share-type-" + theme);
                _dialog!.Hide(); await Wait(() => _dialog == null);
                _localPlaylistId = "preview-a"; await NavigateAsync("local-playlist"); StopLocalMonitoring();
                check(theme + " local playlist actions realize with the selected weight", Labels(LocalToolbar).Length > 0 && Labels(LocalToolbar).All(t => t.FontWeight.Weight >= 700), Labels(LocalToolbar).Select(t => new { t.Text, t.FontWeight.Weight }));
                BeginTrackBatch(TrackList, Tracks[0]); Root.UpdateLayout();
                await Task.Delay(180); Root.UpdateLayout();
                check(theme + " batch action presenters use the selected weight", Descendants<ContentPresenter>(TrackBatchHost).Where(p => p.Content is string s && s.Length > 0).Any() && Descendants<ContentPresenter>(TrackBatchHost).Where(p => p.Content is string s && s.Length > 0).All(p => p.FontWeight.Weight >= 700), new { TrackBatchHost.Visibility, children = Descendants<ContentPresenter>(TrackBatchHost).Select(p => new { content = p.Content?.ToString(), p.FontWeight.Weight, p.IsLoaded, p.ActualWidth }), labels = Labels(TrackBatchHost).Select(t => new { t.Text, t.FontWeight.Weight }) });
                EndTrackBatch();
                var favorites = FavoriteLocalTracksAsync([Tracks[0]]);
                await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
                await Selector(Descendants<ComboBox>(_dialog!).Single(), "local-playlist-choice-" + theme);
                _dialog!.Hide(); await favorites;
                _artistId = 301; _artistTab = "songs"; await NavigateAsync("artist");
                await Selector(ArtistSongSort, "artist-sort-" + theme);
                await NavigateAsync("events");
                await Selector(EventScope, "event-scope-" + theme);
            }
            _settings.TextFontWeight = 400; SelectTag(FontWeightCombo, "400"); ApplyFont(); await NavigateAsync("settings");
            check("Returning to regular restores normal control weight", FontCombo.FontWeight.Weight == 400 && DownloadPath.FontWeight.Weight == 400 && ResetMaterialButton.FontWeight.Weight == 400, null);
            check("Changing text weight preserves symbol fonts", PlayPauseIcon.FontFamily.Source == IconFonts.Source && HeartIcon.FontFamily.Source == IconFonts.Source, null);
        }
        finally { _settings.TextFontWeight = 400; ApplyFont(); _dialog?.Hide(); StopLocalMonitoring(); }
    }
}
