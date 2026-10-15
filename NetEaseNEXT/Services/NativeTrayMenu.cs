using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace NetEaseNEXT.Services;

// USER32 owns drawing, keyboard navigation, capture, placement and dismissal.
internal sealed class NativeTrayMenu : IDisposable
{
    internal nint Handle { get; private set; }
    internal nint Owner { get; private set; }
    internal bool IsTracking { get; private set; }
    internal ElementTheme Theme { get; private set; }
    internal int ItemCount => Handle == 0 ? 0 : GetMenuItemCount(Handle);
    internal string ItemText(int position)
    {
        var text = new StringBuilder(128);
        GetMenuString(Handle, (uint)position, text, text.Capacity, 0x400);
        return text.ToString();
    }

    internal int Show(nint parent, PointInt32 point, ElementTheme theme)
    {
        Theme = theme;
        NativeMenuTheme.Apply(theme);
        try
        {
            Handle = CreatePopupMenu();
            if (Handle == 0) throw new Win32Exception();
            Add(1, "打开 NetEaseNEXT"); Add(2, "播放 / 暂停"); Add(3, "下一首");
            if (!AppendMenu(Handle, 0x800, 0, null)) throw new Win32Exception();
            Add(4, "退出");

            // A visible, zero-sized tool window can own foreground/capture even
            // while the WinUI main window is hidden. It has no painted surface.
            Owner = CreateWindowEx(0x80, "STATIC", "NetEaseNEXT 托盘菜单", 0x80000000,
                point.X, point.Y, 0, 0, parent, 0, 0, 0);
            if (Owner == 0) throw new Win32Exception();
            NativeMenuTheme.ApplyToWindow(Owner, theme);
            ShowWindow(Owner, 1);
            SetForegroundWindow(Owner);
            IsTracking = true;
            var align = GetSystemMetrics(40) != 0 ? 0x8u : 0u;
            return (int)TrackPopupMenuEx(Handle, 0x100 | 0x2 | align, point.X, point.Y, Owner, 0);
        }
        finally
        {
            IsTracking = false;
            if (Owner != 0) PostMessage(Owner, 0, 0, 0);
            Dispose();
        }
    }

    private void Add(nuint command, string text)
    {
        if (!AppendMenu(Handle, 0, command, text)) throw new Win32Exception();
    }
    internal void Dismiss() { if (IsTracking) EndMenu(); }
    public void Dispose()
    {
        Dismiss();
        if (Handle != 0) { DestroyMenu(Handle); Handle = 0; }
        if (Owner != 0) { DestroyWindow(Owner); Owner = 0; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(nint menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] private static extern bool EndMenu();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

internal static class NativeMenuTheme
{
    // Windows 10 1903+ ordinal exports, also used by Microsoft's PowerToys.
    // Resolve optionally so a future platform change falls back to system menus.
    private static readonly SetPreferredMode? SetMode;
    private static readonly FlushThemes? Flush;
    private static readonly AllowDarkWindow? AllowDark;
    internal static bool IsSupported => SetMode != null && Flush != null && AllowDark != null;
    static NativeMenuTheme()
    {
        if (!NativeLibrary.TryLoad("uxtheme.dll", typeof(NativeMenuTheme).Assembly, DllImportSearchPath.System32, out var library)) return;
        SetMode = Resolve<SetPreferredMode>(library, 135);
        Flush = Resolve<FlushThemes>(library, 136);
        AllowDark = Resolve<AllowDarkWindow>(library, 133);
    }
    private static T? Resolve<T>(nint library, int ordinal) where T : Delegate
    {
        var function = GetProcAddress(library, ordinal);
        return function == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(function);
    }
    internal static void Apply(ElementTheme theme)
    {
        SetMode?.Invoke(new AccessibilitySettings().HighContrast ? 0 : theme == ElementTheme.Dark ? 2 : 3);
        Flush?.Invoke();
    }
    internal static void ApplyToWindow(nint window, ElementTheme theme) => AllowDark?.Invoke(window, theme == ElementTheme.Dark && !new AccessibilitySettings().HighContrast);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetPreferredMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FlushThemes();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool AllowDarkWindow(nint window, [MarshalAs(UnmanagedType.I1)] bool allow);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetProcAddress(nint module, nint ordinal);
}
