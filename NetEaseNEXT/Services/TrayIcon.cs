using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NetEaseNEXT.Services;

// Shell_NotifyIcon and a native popup menu: no WinForms or WPF message pump.
public sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 42;
    private readonly nint _window;
    private readonly SubclassProc _proc;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private nint _icon;
    private string _iconPath = "";
    private readonly DispatcherQueue _dispatcher;
    private NativeTrayMenu? _menu;
    private bool _disposed;
    internal NativeTrayMenu? Menu => _menu;
    internal string IconPath => _iconPath;
    internal int LastCommand { get; private set; }
    public void SetIcon(string path)
    {
        if (_iconPath == path) return;
        var icon = LoadImage(0, path, 1, 32, 32, 0x10);
        if (icon == 0) return;
        var old = _icon; _icon = icon; _iconPath = path; _data.Icon = icon;
        if (IsAvailable) Shell_NotifyIcon(1, ref _data);
        if (old != 0) DestroyIcon(old);
    }
    public bool IsAvailable { get; private set; }
    public event Action? Restore;
    public event Action? TogglePlayback;
    public event Action? Next;
    public event Action? Exit;
    public event Action? ThemeChanged;
    public TrayIcon(nint window)
    {
        _window = window; _proc = WndProc; _dispatcher = DispatcherQueue.GetForCurrentThread();
        _iconPath = ShellAppearance.IconPath();
        _icon = LoadImage(0, _iconPath, 1, 32, 32, 0x10);
        if (!SetWindowSubclass(window, _proc, 42, 0)) return;
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = Callback,
            Icon = _icon != 0 ? _icon : LoadIcon(0, 32516), Tip = "NetEaseNEXT", Info = "", InfoTitle = ""
        };
        IsAvailable = Shell_NotifyIcon(0, ref _data);
    }
    private nint WndProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == _taskbarCreated) { IsAvailable = Shell_NotifyIcon(0, ref _data); ThemeChanged?.Invoke(); }
        if (message is 0x001A or 0x031A) ThemeChanged?.Invoke();
        if (message == Callback)
        {
            var action = (uint)(lParam.ToInt64() & 0xffff);
            if (action == 0x202 || action == 0x203) Restore?.Invoke();
            if (action == 0x205 || action == 0x7b)
            {
                GetCursorPos(out var point);
                _dispatcher.TryEnqueue(() => { if (!_disposed) ShowMenu(new(point.X, point.Y), ShellAppearance.Theme); });
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    internal void ShowMenu(PointInt32 point, ElementTheme theme)
    {
        if (_disposed) return;
        if (_menu != null) { _menu.Dismiss(); return; }
        LastCommand = 0;
        using var menu = new NativeTrayMenu();
        _menu = menu;
        try { LastCommand = menu.Show(_window, point, theme); }
        finally { _menu = null; }
        if (!_disposed)
        {
            switch (LastCommand) { case 1: Restore?.Invoke(); break; case 2: TogglePlayback?.Invoke(); break; case 3: Next?.Invoke(); break; case 4: Exit?.Invoke(); break; }
        }
    }
    internal void UpdateMenuTheme()
    {
        _menu?.Dismiss();
        NativeMenuTheme.Apply(ShellAppearance.Theme);
    }
    internal void DismissMenu() => _menu?.Dismiss();
    public void Dispose()
    {
        _disposed = true; DismissMenu();
        if (_data.Window != 0) Shell_NotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _proc, 42);
        if (_icon != 0) DestroyIcon(_icon);
        IsAvailable = false;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
