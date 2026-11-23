using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace NetEaseNEXT.Services;

internal sealed class WinUITrayMenu(SettingsStore settings, Action<FrameworkElement> typography) : IDisposable
{
    private TrayMenuWindow? _window;
    private readonly TaskCompletionSource<int> _completion = new();
    private readonly List<MenuFlyoutItem> _commands = [];
    private bool _ready;
    internal nint Owner => _window?.Handle ?? 0;
    internal bool IsTracking => _ready && !_completion.Task.IsCompleted;
    internal MenuFlyoutPresenter? Presenter { get; private set; }
    internal AdjustableBackdrop? Backdrop => _window?.SystemBackdrop as AdjustableBackdrop;
    internal async Task<int> ShowAsync(nint parent, PointInt32 point, ElementTheme theme)
    {
        var window = _window = new(parent, theme) { Title = "NetEaseNEXT 托盘菜单" };
        window.SetCloaked(true);
        var presenter = Presenter = new MenuFlyoutPresenter { RequestedTheme = theme, Width = 256, CornerRadius = new(0), BorderThickness = new(0), IsDefaultShadowEnabled = false };
        var value = settings.UsesSystemMaterial("Acrylic") ? AdjustableBackdrop.DefaultTransparency : AdjustableBackdrop.Normalize(settings.AcrylicTransparency);
        var alpha = (byte)Math.Round(24 + 231 * Math.Max(0, 1 - value / AdjustableBackdrop.DefaultTransparency));
        var shade = (byte)(theme == ElementTheme.Dark ? 44 : 249);
        presenter.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, shade, shade, shade));
        void Add(string label, string glyph, int command)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = IconFonts.Create(glyph), MinHeight = 44 };
            item.Click += (_, _) => _completion.TrySetResult(command);
            presenter.Items.Add(item); _commands.Add(item);
        }
        Add("打开 NetEaseNEXT", "\uE80F", 1); Add("播放 / 暂停", "\uE768", 2); Add("下一首", "\uE893", 3);
        presenter.Items.Add(new MenuFlyoutSeparator()); Add("退出", "\uE8BB", 4);
        presenter.Loaded += (_, _) => typography(presenter);
        presenter.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { Dismiss(); args.Handled = true; return; }
            var index = _commands.FindIndex(item => item.FocusState != FocusState.Unfocused);
            if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
            {
                index = args.Key == VirtualKey.Home ? 0 : args.Key == VirtualKey.End ? _commands.Count - 1 : (index + (args.Key == VirtualKey.Up ? _commands.Count - 1 : 1)) % _commands.Count;
                _commands[index].Focus(FocusState.Keyboard); args.Handled = true;
            }
            else if (index >= 0 && args.Key is VirtualKey.Enter or VirtualKey.Space)
            { _completion.TrySetResult(index + 1); args.Handled = true; }
        };
        window.Content = presenter;
        window.SystemBackdrop = new AdjustableBackdrop("Acrylic", settings.AcrylicTransparency, settings.UsesSystemMaterial("Acrylic"), theme, transient: true);
        window.Activated += (_, args) => { if (_ready && args.WindowActivationState == WindowActivationState.Deactivated) Dismiss(); };
        window.Closed += (_, _) => Dismiss();
        window.Place(new(256, 210), point, true);
        window.Activate();
        var deadline = Environment.TickCount64 + 2000;
        while (!window.IsClosed && (!presenter.IsLoaded || Backdrop?.NativeProfileReady != true) && Environment.TickCount64 < deadline) await Task.Delay(20);
        if (!_completion.Task.IsCompleted)
        {
            presenter.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var scale = presenter.XamlRoot?.RasterizationScale ?? 1;
            window.Place(new((int)Math.Ceiling(256 * scale), (int)Math.Ceiling(presenter.DesiredSize.Height * scale)), point, true);
            window.RevealAndActivate(); _ready = true; _commands[0].Focus(FocusState.Programmatic);
        }
        return await _completion.Task;
    }
    internal void Dismiss() => _completion.TrySetResult(0);
    public void Dispose()
    {
        _ready = false; Dismiss();
        if (_window is { IsClosed: false } window) { window.SystemBackdrop = null; window.Content = null; window.Close(); }
        _window = null; Presenter = null;
    }
}
