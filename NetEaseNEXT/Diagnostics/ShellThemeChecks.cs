using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;
using Windows.Graphics;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunShellThemeChecksAsync(Action<string, bool, object?> check)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (Environment.GetCommandLineArgs().Contains("--shell-input-check"))
        {
            RestoreWindow();
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "secondary-stage.json"), System.Text.Json.JsonSerializer.Serialize(new { name = "shell-activate", pid = Environment.ProcessId }));
            var deadline = Environment.TickCount64 + 30000;
            while (GetForegroundWindow() != handle && Environment.TickCount64 < deadline) await Task.Delay(30);
            check("Shell check begins with real foreground interaction", GetForegroundWindow() == handle, null);
        }
        var expected = ShellAppearance.IconPath();
        Root.RequestedTheme = ShellAppearance.IsDark ? ElementTheme.Light : ElementTheme.Dark;
        await Task.Delay(100); UpdateAppIcon();
        check("Shell icon follows Windows even when app theme differs", _tray.IconPath == expected && ApplicationIdentity.RegisteredIconPath == expected, new { appTheme = Root.ActualTheme.ToString(), shellTheme = ShellAppearance.Theme.ToString(), icon = _tray.IconPath });
        check("Taskbar relaunch icon matches shell theme", ApplicationIdentity.ReadWindowIconPath(handle) == expected, ApplicationIdentity.ReadWindowIconPath(handle));
        check("SMTC identity survives icon update", ApplicationIdentity.ResolveDisplayName() == "NetEaseNEXT", null);
        check("Both Windows icon sizes are installed", SendMessage(handle, 0x007F, 0, 0) != 0 && SendMessage(handle, 0x007F, 1, 0) != 0, null);
        check("Both transparent icon variants ship", File.Exists(ShellAppearance.IconPath(true)) && File.Exists(ShellAppearance.IconPath(false)) && !File.ReadAllBytes(ShellAppearance.IconPath(true)).SequenceEqual(File.ReadAllBytes(ShellAppearance.IconPath(false))), null);
        foreach (var dark in new[] { false, true })
        {
            var path = ShellAppearance.IconPath(dark);
            ApplicationIdentity.UpdateIcon(handle, path); AppWindow.SetIcon(path); _tray.SetIcon(path);
            check((dark ? "Dark" : "Light") + " shell icon updates window and tray with the appropriate identity", ApplicationIdentity.ReadWindowIconPath(handle) == path && _tray.IconPath == path && (ApplicationIdentity.UsesPackageIdentity || (string?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\AppUserModelId\" + ApplicationIdentity.Id, "IconUri", "") == path), null);
        }
        UpdateAppIcon();
        await RunNativeMenuChecksAsync(check);
        var alternate = ShellAppearance.IconPath(!ShellAppearance.IsDark);
        ApplicationIdentity.UpdateIcon(handle, alternate); AppWindow.SetIcon(alternate); _tray.SetIcon(alternate);
        SendMessage(handle, 0x031A, 0, 0);
        await Task.Delay(150);
        check("Windows theme notification refreshes icons while hidden", _tray.IconPath == expected && ApplicationIdentity.ReadWindowIconPath(handle) == expected && _hidden && !_timer.IsEnabled, null);
        RestoreWindow(); UpdateAppIcon();
    }

    private async Task RunNativeMenuChecksAsync(Action<string, bool, object?> check)
    {
        var main = WinRT.Interop.WindowNative.GetWindowHandle(this);
        HideWindow(); await Task.Delay(100);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var inspect = Environment.GetCommandLineArgs().Contains("--inspect-layout");
        if (inspect) { AppWindow.Title = "NetEaseNEXT 原生菜单检查"; RestoreWindow(); await Task.Delay(100); }
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            var point = inspect ? new PointInt32(AppWindow.Position.X + AppWindow.Size.Width / 2, AppWindow.Position.Y + AppWindow.Size.Height / 2) : new PointInt32(area.X + area.Width - 12, area.Y + area.Height - 12);
            _tray.ShowMenu(point, theme);
            var deadline = Environment.TickCount64 + 12000;
            while (_tray.Menu?.IsTracking != true && Environment.TickCount64 < deadline) await Task.Delay(30);
            var opened = _tray.Menu;
            var owner = opened?.Owner ?? 0;
            check(theme + " tray uses WinUI menu controls with native acrylic", opened?.Presenter is { IsLoaded: true } && opened.Backdrop is { NativeProfileReady: true, Kind: "Acrylic" }, null);
            deadline = Environment.TickCount64 + 1000;
            while (GetForegroundWindow() != owner && _tray.Menu != null && Environment.TickCount64 < deadline) await Task.Delay(20);
            check(theme + " tray surface receives foreground for automatic dismissal", GetForegroundWindow() == owner, new { owner = owner.ToInt64(), foreground = GetForegroundWindow().ToInt64(), visible = IsWindowVisible(owner) });
            check(theme + " tray keeps expected main window visibility", inspect ? IsWindowVisible(main) : !IsWindowVisible(main) && _hidden && !_timer.IsEnabled, null);
            check(theme + " tray keeps every command and touch target", opened?.Presenter?.Items.Count == 5 && opened.Presenter.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().All(i => i.MinHeight >= 44), null);
            var corner = -1; if (owner != 0) DwmGetWindowAttribute(owner, 33, out corner, sizeof(int));
            check(theme + " tray outer corners are owned by DWM", corner == 2 && opened?.Presenter?.CornerRadius == new CornerRadius(0), new { corner });
            if (inspect) await Task.Delay(TimeSpan.FromSeconds(25));
            _tray.DismissMenu();
            deadline = Environment.TickCount64 + 3000;
            while (_tray.Menu != null && Environment.TickCount64 < deadline) await Task.Delay(20);
            check(theme + " tray releases its window and controller on dismissal", _tray.Menu == null && opened?.Owner == 0 && !IsWindow(owner), null);
            check(theme + " dismissal does not execute a command", _tray.LastCommand == 0, null);
            await Task.Delay(100);
        }
        _playback.Restore(new());
        foreach (var command in new[] { 2, 3 })
        {
            var invoked = 0;
            Action observed = () => invoked++;
            if (command == 2) _tray.TogglePlayback += observed; else _tray.Next += observed;
            try
            {
                _tray.ShowMenu(new(area.X + area.Width - 12, area.Y + area.Height - 12), ElementTheme.Light);
                var deadline = Environment.TickCount64 + 12000;
                while (_tray.Menu?.IsTracking != true && Environment.TickCount64 < deadline) await Task.Delay(30);
                var item = _tray.Menu!.Presenter!.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().ElementAt(command - 1);
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(item);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                deadline = Environment.TickCount64 + 3000;
                while (_tray.Menu != null && Environment.TickCount64 < deadline) await Task.Delay(20);
                check("WinUI tray command " + command + " executes exactly once and closes", invoked == 1 && _tray.LastCommand == command && _tray.Menu == null, new { invoked, _tray.LastCommand });
            }
            finally { if (command == 2) _tray.TogglePlayback -= observed; else _tray.Next -= observed; _tray.DismissMenu(); }
        }
        if (inspect) HideWindow();
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
