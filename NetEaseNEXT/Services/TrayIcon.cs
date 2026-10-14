using System.Runtime.InteropServices;

namespace NetEaseNEXT.Services;

// Shell_NotifyIcon keeps the UI entirely WinUI: no WinForms message pump or WPF.
public sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 42;
    private readonly nint _window;
    private readonly SubclassProc _proc;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private readonly nint _icon;
    public bool IsAvailable { get; private set; }
    public event Action? Restore;
    public event Action? TogglePlayback;
    public event Action? Next;
    public event Action? Exit;
    public TrayIcon(nint window)
    {
        _window = window; _proc = WndProc;
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 32, 32, 0x10);
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
        if (message == _taskbarCreated) IsAvailable = Shell_NotifyIcon(0, ref _data);
        if (message == Callback)
        {
            var action = (uint)(lParam.ToInt64() & 0xffff);
            if (action == 0x202 || action == 0x203) Restore?.Invoke();
            if (action == 0x205 || action == 0x7b) ShowMenu();
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "打开 NetEaseNEXT");
            AppendMenu(menu, 0, 2, "播放 / 暂停");
            AppendMenu(menu, 0, 3, "下一首");
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 4, "退出");
            GetCursorPos(out var point); SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0);
            switch (selected) { case 1: Restore?.Invoke(); break; case 2: TogglePlayback?.Invoke(); break; case 3: Next?.Invoke(); break; case 4: Exit?.Invoke(); break; }
            PostMessage(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
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
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nuint wp, nint lp);
}
