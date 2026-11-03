using System.Text.Json;
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunShellPreferencesChecksAsync(Action<string, bool, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-shell-preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var oldRemember = _settings.RememberWindowBounds; var oldPlacement = _settings.WindowPlacement; var oldAcrylic = _settings.AcrylicTransparency; var oldMica = _settings.MicaTransparency;
        var oldAcrylicSystem = _settings.AcrylicSystemMaterial; var oldMicaSystem = _settings.MicaSystemMaterial;
        var oldJobs = DownloadJobs.ToArray(); var oldScope = DownloadScope.SelectedIndex;
        _checkingWindowPreferences = true; _sessionReady = true; _playback.SetVolume(0); _playback.Restore(new());
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount(); _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(35); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Shell preferences did not settle");
        }
        async Task Size(double width)
        {
            var scale = Root.XamlRoot.RasterizationScale; AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(850 * scale))); await Wait(() => Math.Abs(Root.ActualWidth - width) < 30);
        }
        try
        {
            RestoreWindow(); _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance(); await NavigateAsync("downloads");
            check("Legacy settings enable window memory without inventing a saved position", new SettingsStore().RememberWindowBounds && JsonSerializer.Deserialize<SettingsStore>("{}") is { RememberWindowBounds: true, WindowPlacement: null }, null);
            var presenter = (OverlappedPresenter)AppWindow.Presenter; var minWidth = presenter.PreferredMinimumWidth;
            presenter.PreferredMinimumWidth = 1;
            try
            {
                await Size(1000); await Wait(() => Nav.DisplayMode == NavigationViewDisplayMode.Expanded); check("Sidebar remains expanded at a 1000-DIP window", Nav.IsPaneOpen, new { NavWidth = Nav.ActualWidth, WindowWidth = Root.ActualWidth });
                await Size(840); await Wait(() => Nav.DisplayMode == NavigationViewDisplayMode.Expanded); check("Sidebar remains expanded while the content still fits at 840 DIP", Nav.IsPaneOpen, null);
                await Size(760); await Wait(() => Nav.DisplayMode == NavigationViewDisplayMode.Compact); check("Narrow windows still switch to compact navigation", !Nav.IsPaneOpen, null);
                await Size(1000); await Wait(() => Nav.DisplayMode == NavigationViewDisplayMode.Expanded);
            }
            finally { presenter.PreferredMinimumWidth = minWidth; }
            Nav.IsPaneOpen = false; Root.UpdateLayout(); check("The user can still collapse the sidebar manually at a wide size", !Nav.IsPaneOpen, null); Nav.IsPaneOpen = true;
            DownloadJobs.Clear(); DownloadScope.SelectedIndex = 0;
            var failed = new DownloadJob { Track = new(3, "下载失败示例", "晚风电台", "回家的路", "", 1000), Status = "失败", Error = "下载地址暂不可用" };
            DownloadJobs.Add(new() { Track = new(1, "夜航", "晚风电台", "回家的路", "", 1000), Status = "已暂停" }); DownloadJobs.Add(new() { Track = new(2, "沿途灯火", "晚风电台", "回家的路", "", 1000), Status = "已完成" }); DownloadJobs.Add(failed);
            check("The normal queue summary includes the independent failed count", DownloadSummary.Text.Contains("1 项已失败") && QueueDownloads.Count == 2 && FailedDownloads.Count == 1, DownloadSummary.Text);
            await SaveLibraryImageAsync(Root, "shell-preferences-queue-light.png"); failed.Status = "已暂停";
            check("Failed totals update immediately when a task leaves the failure list", DownloadSummary.Text.Contains("0 项已失败") && FailedDownloads.Count == 0, null); failed.Status = "失败";
            DownloadScope.SelectedIndex = 1; check("The dedicated failure list retains its own rows and summary", DownloadSummary.Text == "1 项失败" && FailedDownloadsList.Visibility == Visibility.Visible && FailedDownloads.Count == 1, null); DownloadScope.SelectedIndex = 0;
            _settings.RememberWindowBounds = true; RememberWindowToggle.IsOn = true;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var normal = new Windows.Graphics.RectInt32(area.X + 60, area.Y + 60, Math.Min(1200, area.Width - 120), Math.Min(900, area.Height - 120));
            ((OverlappedPresenter)AppWindow.Presenter).Restore(); AppWindow.MoveAndResize(normal); await Wait(() => AppWindow.Size.Width == normal.Width && AppWindow.Position.X == normal.X); CaptureWindowPlacement();
            var saved = _settings.WindowPlacement!; var path = Path.Combine(fixture, "settings.json"); new SettingsStore { RememberWindowBounds = true, WindowPlacement = saved }.SaveTo(path);
            var loaded = SettingsStore.LoadFrom(path);
            check("Window position and size survive an actual settings-file round trip", loaded.RememberWindowBounds && loaded.WindowPlacement == saved && saved.X == normal.X && saved.Y == normal.Y && saved.Width == normal.Width && saved.Height == normal.Height, null);
            AppWindow.Move(new Windows.Graphics.PointInt32(area.X + 120, area.Y + 120)); await Task.Delay(50); _settings.WindowPlacement = loaded.WindowPlacement; RestoreWindowPlacement();
            await Wait(() => AppWindow.Position.X == saved.X && AppWindow.Position.Y == saved.Y); check("Applying saved preferences restores the actual native window bounds", AppWindow.Size.Width == saved.Width && AppWindow.Size.Height == saved.Height, null);
            ((OverlappedPresenter)AppWindow.Presenter).Maximize(); await Wait(() => ((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Maximized); CaptureWindowPlacement();
            check("Maximizing preserves the last normal rectangle and saves the maximized state", _settings.WindowPlacement is { Maximized: true } maximized && maximized.Bounds.Equals(saved.Bounds), null);
            var maximizedState = _settings.WindowPlacement;
            ((OverlappedPresenter)AppWindow.Presenter).Minimize(); await Task.Delay(100); CaptureWindowPlacement(); check("Minimizing cannot overwrite remembered geometry with minimized coordinates", _settings.WindowPlacement == maximizedState, null);
            RestoreWindow(); await Wait(() => ((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Maximized);
            check("Restoring a maximized window keeps its maximized state", ((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Maximized, null);
            ((OverlappedPresenter)AppWindow.Presenter).Restore(); await Wait(() => ((OverlappedPresenter)AppWindow.Presenter).State == OverlappedPresenterState.Restored); CaptureWindowPlacement(); var beforeFull = _settings.WindowPlacement;
            SetFullScreen(true); CaptureWindowPlacement(); check("Full screen cannot replace the saved normal window rectangle", _settings.WindowPlacement == beforeFull, null); SetFullScreen(false);
            var clamped = new WindowPlacement(90000, 90000, 10000, 10000).ClampTo(new(-1600, 0, 1600, 900));
            check("Off-screen or oversized saved rectangles fit a negative-coordinate monitor work area", clamped.X == -1600 && clamped.Y == 0 && clamped.Width == 1600 && clamped.Height == 900, null);
            _settings.WindowPlacement = new(0, 0, 0, 0); check("Malformed saved window sizes are ignored safely", !RestoreWindowPlacement(), null);
            RememberWindowToggle.IsOn = false; _settings.WindowPlacement = saved;
            check("Turning memory off disables restoration", !_settings.RememberWindowBounds && !RestoreWindowPlacement(), null); _settings.WindowPlacement = null;
            new SettingsStore { RememberWindowBounds = false }.SaveTo(path); check("The disabled preference persists independently of geometry", !SettingsStore.LoadFrom(path).RememberWindowBounds && SettingsStore.LoadFrom(path).WindowPlacement == null, null);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { new SettingsStore { RememberWindowBounds = true, WindowPlacement = saved }.SaveTo(path); check("A failed settings replacement preserves the prior saved preferences", false, null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { check("A failed settings replacement preserves the prior saved preferences", !SettingsStore.LoadFrom(path).RememberWindowBounds, null); }
            }
            RememberWindowToggle.IsOn = true; await Size(1000); await NavigateAsync("settings"); _settings.Backdrop = "Acrylic"; _settings.AcrylicTransparency = AdjustableBackdrop.DefaultTransparency; _settings.AcrylicSystemMaterial = null; ApplyAppearance();
            if (DesktopAcrylicController.IsSupported())
            {
                await Wait(() => SystemBackdrop is AdjustableBackdrop { AppliedProfile: not null });
                using var native = new DesktopAcrylicController(); var config = new SystemBackdropConfiguration { Theme = SystemBackdropTheme.Light, IsInputActive = true }; native.SetSystemBackdropConfiguration(config); native.ResetProperties();
                var current = ((AdjustableBackdrop)SystemBackdrop!).AppliedProfile!.Value;
                var colors = ((AdjustableBackdrop)SystemBackdrop!).AppliedColors!.Value;
                check("The default acrylic uses native tint and luminosity instead of a shared custom opacity", Math.Abs(current.Tint - native.TintOpacity) < .001 && Math.Abs(current.Luminosity - native.LuminosityOpacity) < .001 && colors.Tint.Equals(native.TintColor) && colors.Fallback.Equals(native.FallbackColor), new { current.Tint, current.Luminosity });
                var backdrop = (AdjustableBackdrop)SystemBackdrop!;
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    _settings.Theme = theme; ApplyAppearance();
                    await Wait(() => Root.ActualTheme.ToString() == theme);
                    var values = new List<(float Tint, float Luminosity)>();
                    var stableColors = true;
                    foreach (var step in new[] { 0, 25, 50, 65, 75, 100 })
                    {
                        TransparencySlider.Value = step;
                        values.Add(backdrop.AppliedProfile!.Value);
                        var palette = backdrop.AppliedColors!.Value;
                        stableColors &= (palette.Tint.R < 128) == (theme == "Dark") && (palette.Fallback.R < 128) == (theme == "Dark");
                    }
                    check(theme + " Acrylic changes across the full slider without replacing the native backdrop or mixing palettes", ReferenceEquals(backdrop, SystemBackdrop) && stableColors && values.Zip(values.Skip(1)).All(x => x.First.Tint > x.Second.Tint && x.First.Luminosity > x.Second.Luminosity) && !backdrop.UsesSystemDefaults, values.Select(p => new { p.Tint, p.Luminosity }).ToArray());
                }
                TransparencySlider.Value = 65;
                check("65 percent can be selected in custom mode", _settings.AcrylicTransparency == 65 && _settings.AcrylicSystemMaterial == false && TransparencyValue.Text == "65%" && !backdrop.UsesSystemDefaults, null);
                Invoke(ResetMaterialButton); await Wait(() => SystemBackdrop is AdjustableBackdrop { AppliedProfile: not null });
                check("The reset control restores native defaults without overwriting the custom value", _settings.AcrylicTransparency == 65 && TransparencySlider.Value == 65 && TransparencyValue.Text == "系统默认" && backdrop.UsesSystemDefaults && !ResetMaterialButton.IsEnabled, null);
                await SaveLibraryImageAsync(Root, "shell-preferences-settings-light.png"); _settings.Theme = "Dark"; ApplyAppearance(); await Wait(() => Root.ActualTheme == ElementTheme.Dark && SystemBackdrop is AdjustableBackdrop { AppliedProfile: not null });
                await Wait(() => FontHost.ActualTheme == ElementTheme.Dark && SystemBackdrop is AdjustableBackdrop attached && attached.AppliedColors is { } darkColors && darkColors.Tint.R + darkColors.Tint.G + darkColors.Tint.B < 384);
                current = ((AdjustableBackdrop)SystemBackdrop!).AppliedProfile!.Value;
                colors = ((AdjustableBackdrop)SystemBackdrop!).AppliedColors!.Value;
                check("Dark backdrop and fallback colors stay consistent with the window content theme", ((AdjustableBackdrop)SystemBackdrop!).UsesSystemDefaults && current.Tint is >= 0 and <= 1 && current.Luminosity is >= 0 and <= 1 && colors.Tint.R + colors.Tint.G + colors.Tint.B < 384 && colors.Fallback.R + colors.Fallback.G + colors.Fallback.B < 384 && FontHost.ActualTheme == Root.ActualTheme, new { current.Tint, current.Luminosity, colors.Tint.R, colors.Tint.G, colors.Tint.B });
                await SaveLibraryImageAsync(Root, "shell-preferences-settings-dark.png");
            }
            var legacy = JsonSerializer.Deserialize<SettingsStore>("{" + "\"AcrylicTransparency\":80,\"MicaTransparency\":65}")!;
            check("Legacy custom material settings migrate independently from the native preset", legacy.AcrylicTransparency == 80 && !legacy.UsesSystemMaterial("Acrylic") && legacy.UsesSystemMaterial("Mica"), null);
            legacy.AcrylicSystemMaterial = true; legacy.MicaSystemMaterial = false;
            var materialPath = Path.Combine(fixture, "material.json"); legacy.SaveTo(materialPath); var savedMaterial = SettingsStore.LoadFrom(materialPath);
            check("Native mode and the saved custom slider position survive a settings round trip", savedMaterial.UsesSystemMaterial("Acrylic") && savedMaterial.AcrylicTransparency == 80 && !savedMaterial.UsesSystemMaterial("Mica") && savedMaterial.MicaTransparency == 65, null);
            check("Out-of-range material values do not enter the renderer", AdjustableBackdrop.Normalize(-1) == 0 && AdjustableBackdrop.Normalize(101) == 100 && AdjustableBackdrop.Normalize(double.NaN) == 65, null);
            _settings.Backdrop = "Mica"; ApplyAppearance(); check("Unsupported material is clearly reported with a solid fallback", MicaController.IsSupported() || SystemBackdrop == null && SolidLayer.Visibility == Visibility.Visible && MaterialHint.Text.Contains("纯色"), new { Mica = MicaController.IsSupported(), Acrylic = DesktopAcrylicController.IsSupported() });
        }
        finally
        {
            _checkingWindowPreferences = false; _windowPlacementTimer.Stop(); _windowPlacementDirty = false; if (_fullScreen) SetFullScreen(false);
            var ready = _ready; _ready = false; _settings.RememberWindowBounds = oldRemember; RememberWindowToggle.IsOn = oldRemember; _settings.WindowPlacement = oldPlacement; _ready = ready;
            _settings.AcrylicTransparency = oldAcrylic; _settings.MicaTransparency = oldMica; _settings.AcrylicSystemMaterial = oldAcrylicSystem; _settings.MicaSystemMaterial = oldMicaSystem; DownloadJobs.Clear(); foreach (var job in oldJobs) DownloadJobs.Add(job); DownloadScope.SelectedIndex = oldScope;
            await DeleteWorkerFixtureAsync(fixture);
        }
    }
    private async Task RunShellPreferencesPreviewAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _playback.SetVolume(0); _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        var oldJobs = DownloadJobs.ToArray(); DownloadJobs.Clear();
        var args = Environment.GetCommandLineArgs(); var label = args.FirstOrDefault(a => a.StartsWith("--preview-label="))?.Split('=')[1] ?? "baseline";
        try
        {
            RestoreWindow(); _settings.Theme = args.Contains("--preview-dark") ? "Dark" : "Light"; _settings.Backdrop = "Acrylic"; _settings.AcrylicTransparency = 65; _settings.AcrylicSystemMaterial = true; ApplyAppearance();
            var scale = Root.XamlRoot.RasterizationScale; AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(850 * scale)));
            DownloadJobs.Add(new() { Track = new(1, "夜航", "晚风电台", "回家的路", "", 1000), Status = "已暂停" });
            DownloadJobs.Add(new() { Track = new(2, "雨后散步", "晚风电台", "回家的路", "", 1000), Status = "失败", Error = "下载地址暂不可用，请重试" });
            DownloadJobs.Add(new() { Track = new(3, "沿途灯火", "晚风电台", "回家的路", "", 1000), Status = "已完成" });
            await NavigateAsync(args.Contains("--preview-settings") ? "settings" : "downloads"); Root.UpdateLayout(); await Task.Delay(250);
            await SaveLibraryImageAsync(Root, "shell-preferences-" + label + ".png");
            var detail = new { MicaSupported = MicaController.IsSupported(), AcrylicSupported = DesktopAcrylicController.IsSupported(), NavWidth = Nav.ActualWidth, Mode = Nav.DisplayMode.ToString(), Summary = DownloadSummary.Text, Scale = scale, SystemBackdrop = SystemBackdrop?.GetType().Name, SolidVisible = SolidLayer.Visibility.ToString(), ProcessId = Environment.ProcessId };
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "shell-preferences-preview.json"), JsonSerializer.Serialize(detail)); check("Shell preview recorded", true, detail);
            await Task.Delay(TimeSpan.FromSeconds(90));
        }
        finally { DownloadJobs.Clear(); foreach (var job in oldJobs) DownloadJobs.Add(job); }
    }
}
