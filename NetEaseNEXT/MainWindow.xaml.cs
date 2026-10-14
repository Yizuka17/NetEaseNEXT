using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Graphics;

namespace NetEaseNEXT;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<Playlist> Playlists { get; } = [];
    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<CatalogItem> Catalog { get; } = [];
    public ObservableCollection<SocialItem> SocialItems { get; } = [];
    private readonly SettingsStore _settings = SettingsStore.Load();
    private readonly ApiClient _api = new();
    private readonly PlaybackService _playback;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _pageLoad, _lyricsLoad, _loginLoad;
    private ContentDialog? _dialog;
    private List<LyricLine> _lyrics = [];
    private string _view = "discover", _query = "", _playlistName = "";
    private long _playlistId, _lyricId, _artistId, _peerId;
    private int _offset;
    private bool _ready, _updating, _exiting, _hidden, _selectingNav, _fullScreen, _followLyrics = true;
    private Track? _displayedTrack;
    private sealed record Route(string View, long Id, long ArtistId, long PeerId, string Name, string Query);
    private readonly Stack<Route> _history = new();
    private Route? _route;

    public MainWindow()
    {
        Diagnostics.StartupTrace.Mark("InitializeComponent"); InitializeComponent(); Diagnostics.StartupTrace.Mark("XAML initialized");
        Artwork.CacheDirectory = _settings.CacheDirectory;
        _playback = new(_api, _settings, DispatcherQueue);
        _playback.Changed += Playback_Changed; _playback.Error += ShowError;
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _tray.Restore += RestoreWindow; _tray.TogglePlayback += _playback.Toggle;
        _tray.Next += () => _ = _playback.NextAsync(); _tray.Exit += ExitApp;
        Diagnostics.StartupTrace.Mark("Configure window"); ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1280, 900));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 900;
        AppWindow.Closing += (_, args) =>
        {
            if (!_exiting && _settings.CloseToTray && _tray.IsAvailable) { args.Cancel = true; _dialog?.Hide(); HideWindow(); }
        };
        AppWindow.Changed += (_, args) => { if (args.DidVisibilityChange || args.DidPresenterChange) UpdateVisibility(); };
        Activated += (_, _) => UpdateVisibility(); Closed += (_, _) => Cleanup();
        Root.SizeChanged += (_, _) => UpdateLayout();
        TrayToggle.IsOn = _settings.CloseToTray; AnimationToggle.IsOn = _settings.Animations;
        SelectTag(BackdropCombo, _settings.Backdrop); SelectTag(ThemeCombo, _settings.Theme);
        CachePath.Text = _settings.CacheDirectory; DownloadPath.Text = _settings.DownloadDirectory;
        Diagnostics.StartupTrace.Mark("Initialize fonts"); InitializeFonts(); Diagnostics.StartupTrace.Mark("Apply appearance"); ApplyAppearance(); Diagnostics.StartupTrace.Mark("Appearance applied");
        _timer.Tick += (_, _) => UpdateProgress();
        _playerNoticeTimer.Tick += (_, _) => { _playerNoticeTimer.Stop(); PlayerNotice.IsOpen = false; };
        LyricsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => SuspendLyrics()), true);
        LyricsList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => SuspendLyrics()), true);
        LyricsList.KeyDown += (_, args) => { if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown) SuspendLyrics(); };
        foreach (var host in new FrameworkElement[] { PlaylistGrid, CatalogGrid, TrackList, LyricsList, SocialList, QueueList, SettingsPanel }) AttachWheelFallback(host);
        _ready = true;
        Root.Loaded += async (_, _) =>
        {
            Diagnostics.StartupTrace.Mark("Root loaded");
            if (Environment.GetCommandLineArgs().Contains("--smoke-test")) await RunSmokeTestAsync();
            else { await NavigateAsync("discover"); if (_api.HasSession) await UpdateAccountAsync(); }
            _ = Artwork.TrimAsync();
        };
    }
    private void UpdateLayout()
    {
        SearchBox.Width = Root.ActualWidth < 1050 ? 220 : 280;
        Nav.PaneDisplayMode = Root.ActualWidth < 1100 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        var width = Math.Clamp(Math.Min(Root.ActualWidth * .34, Root.ActualHeight - 340), 160, 500);
        CoverFrame.Width = CoverFrame.Height = width;
        PlayerLayout.ColumnSpacing = Root.ActualWidth < 1000 ? 28 : 64;
        PlayerLayout.Padding = Root.ActualWidth < 1000 ? new Thickness(32, 16, 32, 24) : new Thickness(64, 24, 64, 32);
        QueuePane.MaxHeight = Math.Max(100, Root.ActualHeight - 180);
        QueueList.MaxHeight = Math.Max(60, Root.ActualHeight - 260);
        RedCountText.Visibility = Root.ActualWidth < 1100 && _view != "playing" ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ShowError(string message) { if (_exiting) return; if (_view == "playing") { ShowPlayerMessage(message, InfoBarSeverity.Warning); return; } Notice.Message = message; Notice.Severity = InfoBarSeverity.Warning; Notice.IsOpen = true; }
    private void ShowEmpty(string text, string action = "重试") { EmptyText.Text = text; EmptyAction.Content = action; EmptyPanel.Visibility = Visibility.Visible; }
    private void SetBusy(bool busy) { Busy.IsActive = busy; Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; LoadMoreButton.IsEnabled = CatalogMore.IsEnabled = SocialMore.IsEnabled = !busy; }
    private void UpdateVisibility()
    {
        var hidden = !AppWindow.IsVisible || (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized);
        if (_hidden == hidden) return;
        _hidden = hidden;
        if (hidden)
        {
            _playerNoticeTimer.Stop(); PlayerNotice.IsOpen = false; QueuePane.Visibility = Visibility.Collapsed; QueueList.ItemsSource = null;
            _songStatsLoad?.Cancel();
            _pageLoad?.Cancel(); _lyricsLoad?.Cancel(); _loginLoad?.Cancel(); _coverLoad?.Cancel();
            PlaylistGrid.ItemsSource = null; CatalogGrid.ItemsSource = null;
            LargeCover.Source = null; BlurredCover.Source = null; LyricsList.ItemsSource = null; _lyrics.Clear(); _lyricId = 0;
        }
        else
        {
            PlaylistGrid.ItemsSource = Playlists; CatalogGrid.ItemsSource = Catalog;
            if (_view == "playing") { UpdateNowPlaying(); _ = LoadLyricsAsync(); }
            else if (_view is "discover" or "library" && Playlists.Count == 0) _ = LoadPageAsync(false);
            else if (_view is "artists" or "albums" or "radio" && Catalog.Count == 0) _ = LoadPageAsync(false);
            UpdateProgress();
            if (_playback.Current is Track track) _ = LoadSongEngagementAsync(track);
        }
        UpdateTimer();
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    private void HideWindow()
    {
        Diagnostics.StartupTrace.Mark("Hide: release backdrop"); SystemBackdrop = null;
        Diagnostics.StartupTrace.Mark("Hide: native window"); ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 0);
        Diagnostics.StartupTrace.Mark("Hide: update visibility"); UpdateVisibility();
    }
    internal void RestoreWindow() { ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 9); Activate(); UpdateVisibility(); ApplyAppearance(); }
    private void ExitApp() { _exiting = true; Close(); }
    private void Cleanup()
    {
        _exiting = true; _timer.Stop(); _playerNoticeTimer.Stop(); _appearanceTimer.Stop(); _pageLoad?.Cancel(); _lyricsLoad?.Cancel(); _loginLoad?.Cancel(); _coverLoad?.Cancel(); _downloads.Cancel();
        _playback.Changed -= Playback_Changed; _playback.Error -= ShowError;
        _songStatsLoad?.Cancel();
        _playback.Dispose(); _api.Dispose(); _tray.Dispose();
        try { _settings.Save(); } catch (Exception ex) { Log.Write(ex); }
    }
    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindDescendant<T>(VisualTreeHelper.GetChild(parent, i)) is T child) return child;
        return null;
    }
    private static void AttachWheelFallback(FrameworkElement host)
    {
        host.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            if (args.Handled) return;
            var properties = args.GetCurrentPoint(host).Properties;
            if (properties.IsHorizontalMouseWheel) return;
            var scroll = FindDescendant<ScrollViewer>(host);
            if (scroll == null || scroll.ScrollableHeight <= 0) return;
            var target = Math.Clamp(scroll.VerticalOffset - properties.MouseWheelDelta / 120d * 96, 0, scroll.ScrollableHeight);
            if (Math.Abs(target - scroll.VerticalOffset) < .1) return;
            scroll.ChangeView(null, target, null, true); args.Handled = true;
        }), true);
    }
}
