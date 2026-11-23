using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NetEaseNEXT.Services;

// The tray needs a WinUI host even when the main window is hidden.
internal sealed class TrayMenuWindow : Window
{
    internal nint Handle { get; }
    internal bool IsClosed { get; private set; }
    internal SizeInt32 ClientSize
    {
        get { GetClientRect(Handle, out var rect); return new(rect.Right - rect.Left, rect.Bottom - rect.Top); }
    }
    internal TrayMenuWindow(nint owner, ElementTheme theme)
    {
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtr(Handle, -8, owner);
        var extended = GetWindowLongPtr(Handle, -20).ToInt64();
        SetWindowLongPtr(Handle, -20, (nint)((extended | 0x80) & ~0x40000L));
        var corner = 2; DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
        var dark = theme == ElementTheme.Dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        Closed += (_, _) => IsClosed = true;
    }
    internal void SetCloaked(bool value)
    { var flag = value ? 1 : 0; DwmSetWindowAttribute(Handle, 13, ref flag, sizeof(int)); }
    internal void RevealAndActivate()
    { SetCloaked(false); Activate(); SetForegroundWindow(Handle); }
    internal void Place(SizeInt32 size, PointInt32 anchor, bool above)
    {
        if (ClientSize != size) AppWindow.ResizeClient(size);
        var work = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest).WorkArea;
        var width = AppWindow.Size.Width; var height = AppWindow.Size.Height;
        var x = above ? anchor.X - width : anchor.X - width / 2;
        var y = above ? anchor.Y - height : anchor.Y - height / 2;
        AppWindow.Move(new(Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width)), Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height))));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
