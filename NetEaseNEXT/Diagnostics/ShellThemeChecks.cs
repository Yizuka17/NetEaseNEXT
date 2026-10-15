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
            check((dark ? "Dark" : "Light") + " shell icon updates window and tray with the appropriate identity", ApplicationIdentity.ReadWindowIconPath(handle) == path && _tray.IconPath == path && (ApplicationIdentity.IsPackaged || (string?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\AppUserModelId\" + ApplicationIdentity.Id, "IconUri", "") == path), null);
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
            NativeTrayMenu? opened = null;
            nint menu = 0, owner = 0;
            var seen = false; var native = false; var hidden = false; var foreground = false; var labels = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            var observed = false;
            timer.Tick += (_, _) =>
            {
                if (!observed)
                {
                    observed = true;
                    opened = _tray.Menu; menu = opened?.Handle ?? 0; owner = opened?.Owner ?? 0;
                    seen = opened?.IsTracking == true;
                    native = IsMenu(menu);
                    foreground = GetForegroundWindow() == owner;
                    hidden = !IsWindowVisible(main) && _hidden && !_timer.IsEnabled;
                    labels = opened?.ItemCount == 5 && opened.ItemText(0) == "打开 NetEaseNEXT" && opened.ItemText(4) == "退出";
                    Diagnostics.StartupTrace.Mark($"Native tray menu {theme}: open={seen}, ownerForeground={foreground}");
                    if (inspect) { timer.Interval = TimeSpan.FromSeconds(25); return; }
                }
                timer.Stop(); _tray.DismissMenu();
            };
            timer.Start();
            var point = inspect ? new PointInt32(AppWindow.Position.X + AppWindow.Size.Width / 2, AppWindow.Position.Y + AppWindow.Size.Height / 2) : new PointInt32(area.X + area.Width - 12, area.Y + area.Height - 12);
            try { _tray.ShowMenu(point, theme); }
            finally { timer.Stop(); }
            check(theme + " tray uses a real USER32 popup", seen && native, null);
            check(theme + " tray owner receives foreground for automatic dismissal", foreground, null);
            check(theme + " tray keeps expected WinUI visibility", inspect ? !hidden && IsWindowVisible(main) : hidden, new { inspect, hidden });
            check(theme + " native tray menu keeps all commands", labels, null);
            check(theme + " native menu theme support is available", NativeMenuTheme.IsSupported, null);
            check(theme + " native menu and owner are destroyed after dismissal", _tray.Menu == null && opened is { Handle: 0, Owner: 0, IsTracking: false } && !IsMenu(menu) && !IsWindow(owner), null);
            check(theme + " dismissal does not execute a playback or exit command", _tray.LastCommand == 0, null);
            await Task.Delay(100);
        }
        if (inspect) HideWindow();
        NativeMenuTheme.Apply(ShellAppearance.Theme);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
