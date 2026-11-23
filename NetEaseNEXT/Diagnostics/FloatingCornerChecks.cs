using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunFloatingCornerChecksAsync(Action<string, bool, object?> check)
    {
        var fallback = new CornerRadius(8); var tipFallback = new CornerRadius(4);
        check("Shared WinUI corners accept custom and square radii", FloatingCornerPolicy.Resolve(fallback, tipFallback, 20, "", -1).Overlay == new CornerRadius(20) && FloatingCornerPolicy.Resolve(fallback, tipFallback, 0, "", -1).Overlay == new CornerRadius(0), null);
        check("Per-corner settings preserve DWM corner order", FloatingCornerPolicy.Resolve(fallback, tipFallback, 12, "1,2,3,4", -1).Overlay == new CornerRadius(1, 2, 3, 4), null);
        check("Unset and malformed values preserve WinUI defaults", FloatingCornerPolicy.Resolve(fallback, tipFallback, -1, "bad", -1).Overlay == fallback && FloatingCornerPolicy.Resolve(fallback, tipFallback, double.NaN, "1,2", double.PositiveInfinity).ToolTip == tipFallback, null);
        check("Explicit tooltip radius is independent", FloatingCornerPolicy.Resolve(fallback, tipFallback, 20, "", 2).ToolTip == new CornerRadius(2), null);
        _floatingCornersReady = false;
        ApplyFloatingCorners(FloatingCornerPolicy.Resolve(fallback, tipFallback, 0, "", 0));
        check("An initial zero radius replaces the framework resource", (CornerRadius)Application.Current.Resources["OverlayCornerRadius"] == new CornerRadius(0) && (CornerRadius)Application.Current.Resources["ToolTipCornerRadius"] == new CornerRadius(0), null);
        RefreshFloatingCorners();
        RestoreWindow(); AppWindow.Move(new(0, 0)); _sessionReady = _authenticated = true; _accountId = 42;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds < 12) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Shared corner check did not settle");
        }
        async Task Record(string name)
        {
            if (!Environment.GetCommandLineArgs().Contains("--secondary-surfaces-capture")) return;
            await Task.Delay(250);
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "secondary-stage.json"), JsonSerializer.Serialize(new { name, pid = Environment.ProcessId }));
            await Task.Delay(600);
        }
        _settings.Theme = "Light"; _settings.Backdrop = "Mica"; ApplyAppearance();
        var selected = FloatingCornerPolicy.Read(_defaultFloatingCorner!.Value, _defaultTooltipCorner!.Value);
        check("Application overlay resource follows the current corner policy", (CornerRadius)Application.Current.Resources["OverlayCornerRadius"] == selected.Overlay, selected);
        if (Environment.GetCommandLineArgs().Contains("--require-windhawk")) check("Installed enabled Windhawk corner configuration is detected", selected.Source == "Windhawk", selected);
        if (Environment.GetCommandLineArgs().Contains("--require-default-corners")) check("Missing or disabled Windhawk corners preserve WinUI defaults", selected.Source == "WinUI" && selected.Overlay == _defaultFloatingCorner.Value, selected);
        _playlistId = 101; await NavigateAsync("playlist");
        var information = ShowSongDetailsAsync(Tracks[0], _api);
        await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
        var border = FindDescendant<Border>(_dialog!, b => b.Name == "BackgroundElement")!;
        check("Information keeps its original ContentDialog and XAML root", ReferenceEquals(_dialog!.XamlRoot, Root.XamlRoot), null);
        check("Information template receives the shared radius", border.CornerRadius == selected.Overlay && _dialog.CornerRadius == selected.Overlay, border.CornerRadius);
        await Record("shared-information"); _dialog.Hide(); await information;
        var comments = ShowCommentsAsync(SongCommentsSource(Tracks[0])!);
        await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true });
        border = FindDescendant<Border>(_dialog!, b => b.Name == "BackgroundElement")!;
        check("Comments preserve the original modal ContentDialog and shared radius", ReferenceEquals(_dialog!.XamlRoot, Root.XamlRoot) && border.CornerRadius == selected.Overlay, null);
        var selector = Descendants<ComboBox>(_dialog!).Single();
        await Wait(() => selector.IsLoaded && SelectorPopup(selector) != null);
        selector.StartBringIntoView(); await Task.Delay(150); Root.UpdateLayout(); ApplySelectorMaterial(selector);
        selector.IsDropDownOpen = true;
        try { await Wait(() => SelectorPopup(selector)?.SystemBackdrop is AdjustableBackdrop { NativeProfileReady: true }); }
        catch (TimeoutException)
        {
            await Record("shared-selector-failed");
            check("Selector controller is attached to the active popup", false, new { selector.IsDropDownOpen, popupOpen = SelectorPopup(selector)?.IsOpen, constrained = SelectorPopup(selector)?.ShouldConstrainToRootBounds, current = FindDescendant<Microsoft.UI.Xaml.Controls.Primitives.Popup>(selector)?.IsOpen, same = ReferenceEquals(SelectorPopup(selector), FindDescendant<Microsoft.UI.Xaml.Controls.Primitives.Popup>(selector)) });
            throw;
        }
        var dropdown = FindDescendant<Border>(SelectorPopup(selector)!.Child, b => b.Name == "PopupBorder")!;
        check("Nested selector uses native acrylic and the same radius", dropdown.CornerRadius == selected.Overlay && dropdown.Background is SolidColorBrush, dropdown.CornerRadius);
        await Record("shared-comments-selector"); selector.IsDropDownOpen = false;
        DismissDialogFromOutside(_dialog!, false); await Task.Delay(80);
        check("Comments still ignore outside dismissal", !comments.IsCompleted, null);
        _dialog!.Hide(); await comments;
        Account_Click(AccountButton, new());
        await Wait(() => _activeFlyout?.SystemBackdrop is AdjustableBackdrop { NativeProfileReady: true });
        var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<MenuFlyoutPresenter>().Single();
        check("Account menu uses the same radius and physical border width", presenter.CornerRadius == selected.Overlay && presenter.BorderThickness == FloatingStroke, presenter.CornerRadius);
        await Record("shared-account"); _activeFlyout!.Hide();
        Notice.Message = "有新消息"; Notice.IsOpen = true; ApplyFloatingMaterials();
        check("Notifications keep acrylic and use configured or default corners", Notice.Background is AcrylicBrush && (selected.HasCustomOverlay ? Notice.CornerRadius == selected.Overlay : ReferenceEquals(Notice.ReadLocalValue(Control.CornerRadiusProperty), DependencyProperty.UnsetValue)), Notice.CornerRadius);
        await Record("shared-notification"); Notice.IsOpen = false;
    }
}
