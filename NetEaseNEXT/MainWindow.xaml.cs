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
    private readonly SettingsStore _settings;
    private readonly StartupSession? _startup;
    private readonly ApiClient _api;
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
    private bool _hasBeenVisible;
    private Track? _displayedTrack;
    private sealed record Route(string View, long Id, long ArtistId, long PeerId, string Name, string Query, SocialItem? Source = null);
    private readonly Stack<Route> _history = new();
    private Route? _route;

    public MainWindow() : this(null) { }
    internal MainWindow(StartupSession? startup)
    {
        _startup = startup; _settings = startup?.Settings ?? SettingsStore.Load();
        Diagnostics.StartupTrace.Mark("InitializeComponent"); InitializeComponent(); Diagnostics.StartupTrace.Mark("XAML initialized");
        _api = startup?.Api ?? new(_settings);
        if (startup != null) { _profileTask = startup.Profile; _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddMinutes(5); }
        InitializeSidebarIcons();
        ToolTipService.SetToolTip(ReadAllButton, "清除消息和私信的全部未读提醒");
        foreach (var notice in new[] { Notice, PlayerNotice })
        {
            SetNoticeIcon(notice);
            notice.RegisterPropertyChangedCallback(InfoBar.SeverityProperty, (sender, _) => SetNoticeIcon((InfoBar)sender));
        }
        Artwork.CacheDirectory = _settings.CacheDirectory;
        ApplicationIdentity.BindWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _playback = new(_api, _settings, DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _playback.Changed += Playback_Changed; _playback.Error += ShowError;
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _tray.Restore += RestoreWindow; _tray.TogglePlayback += _playback.Toggle;
        _tray.Next += () => _ = _playback.NextAsync(); _tray.Exit += ExitApp;
        _tray.ThemeChanged += () => DispatcherQueue.TryEnqueue(() => { if (!_exiting) UpdateAppIcon(); });
        Diagnostics.StartupTrace.Mark("Configure window"); ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        AppWindow.SetIcon(ShellAppearance.IconPath());
        AppWindow.Resize(new SizeInt32(1280, 900));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 900;
        AppWindow.Closing += (_, args) =>
        {
            if (!_exiting && _settings.CloseToTray && _tray.IsAvailable) { args.Cancel = true; _dialog?.Hide(); HideWindow(); }
        };
        AppWindow.Changed += (_, args) => { if (args.DidVisibilityChange || args.DidPresenterChange) UpdateVisibility(); };
        Activated += (_, _) => UpdateVisibility(); Closed += (_, _) => Cleanup();
        Root.SizeChanged += (_, _) => UpdateLayout();
        CoverColumn.SizeChanged += (_, _) => { if (_view == "playing" && !_hidden) UpdateLayout(); };
        SettingsPanel.SizeChanged += (_, _) => SettingsContent.Width = Math.Max(0, Math.Min(760, SettingsPanel.ActualWidth - 24));
        Nav.PaneOpened += (_, _) => UpdateCompactAccount(); Nav.PaneClosed += (_, _) => UpdateCompactAccount();
        TrayToggle.IsOn = _settings.CloseToTray; AnimationToggle.IsOn = _settings.Animations;
        SelectTag(BackdropCombo, _settings.Backdrop); SelectTag(ThemeCombo, _settings.Theme);
        CachePath.Text = _settings.CacheDirectory; DownloadPath.Text = _settings.DownloadDirectory;
        Diagnostics.StartupTrace.Mark("Initialize fonts"); InitializeFonts(); Diagnostics.StartupTrace.Mark("Apply appearance"); ApplyAppearance(); Diagnostics.StartupTrace.Mark("Appearance applied");
        _timer.Tick += (_, _) => UpdateProgress();
        ConnectTrackGestures(TrackList); ConnectTrackGestures(QueueList);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Playback_KeyDown), true);
        _inboxTimer.Tick += async (_, _) => await CheckUnreadAsync();
        SettingsVolume.Value = _settings.Volume * 100;
        Nav.DisplayModeChanged += (_, _) => UpdateLayout();
        Root.ActualThemeChanged += (_, _) => { UpdateAppIcon(); RefreshLyricAppearance(); };
        _playerNoticeTimer.Tick += (_, _) => { _playerNoticeTimer.Stop(); PlayerNotice.IsOpen = false; };
        _windowTransitionTimer.Tick += (_, _) => RestorePresenterTransition();
        _playbackSaveTimer.Tick += (_, _) => SavePlaybackState();
        LyricsList.Loaded += (_, _) => QueueInitialLyricCenter();
        LyricsList.SizeChanged += (_, _) => { UpdateLyricPadding(); QueueInitialLyricCenter(); };
        LyricsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => SuspendLyrics()), true);
        LyricsList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => SuspendLyrics()), true);
        LyricsList.KeyDown += (_, args) => { if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown) SuspendLyrics(); };
        foreach (var host in new FrameworkElement[] { PlaylistGrid, CatalogGrid, TrackList, LyricsList, SocialList, QueueList, SettingsPanel }) AttachWheelFallback(host);
        _ready = true;
        Root.Loaded += async (_, _) =>
        {
            Diagnostics.StartupTrace.Mark("Root loaded");
            if (Environment.GetCommandLineArgs().Contains("--smoke-test")) await RunSmokeTestAsync();
            else await InitializeSessionAsync();
            _ = Artwork.TrimAsync();
        };
    }
    private void UpdateLayout()
    {
        SearchBox.Width = Root.ActualWidth < 1050 ? 220 : 280;
        ContentArea.Padding = Root.ActualWidth < 900 ? new Thickness(Nav.DisplayMode == NavigationViewDisplayMode.Minimal ? 64 : 16, 8, 16, 16) : new Thickness(24, 8, 24, 16);
        if (SettingsPanel.ActualWidth > 0) SettingsContent.Width = Math.Max(0, Math.Min(760, SettingsPanel.ActualWidth - 24));
        UpdateCompactAccount();
        var coverSize = Root.ActualWidth < 900 ? 120 : 164;
        PlaylistDetails.ColumnDefinitions[0].Width = new GridLength(coverSize);
        PlaylistCoverFrame.Width = PlaylistCoverFrame.Height = coverSize;
        var width = Math.Clamp(Math.Min(Root.ActualWidth * .34, Root.ActualHeight - 350), 80, 500);
        if (_view == "playing" && PlayerLayout.ActualHeight > 0 && CoverColumn.ActualHeight > 0)
        {
            var otherHeight = Math.Max(0, CoverColumn.ActualHeight - CoverFrame.ActualHeight);
            var available = PlayerLayout.ActualHeight - PlayerLayout.Padding.Top - PlayerLayout.Padding.Bottom - otherHeight - 4;
            width = Math.Max(48, Math.Min(width, available));
        }
        CoverFrame.Width = CoverFrame.Height = width;
        CoverColumn.Spacing = Root.ActualHeight < 650 ? 6 : 12;
        NowTitle.FontSize = Root.ActualHeight < 650 ? 22 : 26;
        PlayerLayout.ColumnSpacing = Root.ActualWidth < 1000 ? 28 : 64;
        PlayerLayout.Padding = Root.ActualWidth < 1000 ? new Thickness(32, _fullScreen ? 16 : 48, 32, 24) : new Thickness(64, _fullScreen ? 24 : 48, 64, 32);
        QueuePane.MaxHeight = Math.Max(100, Root.ActualHeight - 180);
        QueueList.MaxHeight = Math.Max(60, Root.ActualHeight - 260);
        RedCountText.Visibility = Root.ActualWidth < 1100 && _view != "playing" ? Visibility.Collapsed : Visibility.Visible;
        UpdateLyricPadding();
        if (_view == "playing" && _followLyrics) CenterCurrentLyric(false);
        var narrow = Root.ActualWidth < 1050;
        var tiny = Root.ActualWidth < 700;
        Grid.SetRow(FooterTransportHost, narrow ? 1 : 0); Grid.SetColumn(FooterTransportHost, narrow ? 0 : 1); Grid.SetColumnSpan(FooterTransportHost, narrow ? 3 : 1);
        Grid.SetColumnSpan(FooterSongInfo, tiny ? 3 : narrow ? 2 : 1);
        Grid.SetRow(FooterToolsHost, tiny ? 2 : 0); Grid.SetColumn(FooterToolsHost, tiny ? 0 : 2); Grid.SetColumnSpan(FooterToolsHost, tiny ? 3 : 1);
        FooterLayout.ColumnDefinitions[2].Width = narrow && !tiny ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        FooterToolsHost.HorizontalAlignment = tiny ? HorizontalAlignment.Center : HorizontalAlignment.Right;
    }
    private void UpdateCompactAccount()
    {
        var compact = Nav.DisplayMode != NavigationViewDisplayMode.Expanded && !Nav.IsPaneOpen;
        AccountName.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AccountFooter.Width = compact ? Nav.CompactPaneLength : double.NaN;
        AccountContent.ColumnSpacing = compact ? 0 : 10;
        AccountButton.Width = compact ? 56 : double.NaN;
        AccountButton.Padding = new Thickness(compact ? 8 : 10);
        AccountButton.Margin = new Thickness(compact ? 0 : 4, 0, compact ? 0 : 4, 10);
        AccountButton.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        AccountButton.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }
    private static void SetNoticeIcon(InfoBar notice)
    {
        notice.IconSource = new FontIconSource
        {
            FontFamily = IconFonts.Symbols,
            Glyph = notice.Severity switch { InfoBarSeverity.Warning => "\uE7BA", InfoBarSeverity.Error => "\uEA39", InfoBarSeverity.Success => "\uE73E", _ => "\uE946" }
        };
    }
    private void ShowError(string message) { if (_exiting) return; if (_view == "playing") { ShowPlayerMessage(message, InfoBarSeverity.Warning); return; } Notice.Message = message; Notice.Severity = InfoBarSeverity.Warning; Notice.IsOpen = true; }
    private void ShowEmpty(string text, string action = "重试") { EmptyText.Text = text; EmptyAction.Content = action; EmptyPanel.Visibility = Visibility.Visible; }
    private void SetBusy(bool busy) { Busy.IsActive = busy; Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; LoadMoreButton.IsEnabled = CatalogMore.IsEnabled = SocialMore.IsEnabled = !busy; }
    private void UpdateVisibility()
    {
        var hidden = !AppWindow.IsVisible || (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized);
        if (!hidden && Root.IsLoaded) _hasBeenVisible = true;
        if (_hidden == hidden) return;
        _hidden = hidden;
        _playback.SetBackgroundMode(hidden);
        if (hidden)
        {
            if (_hasBeenVisible) _startup?.Cancel();
            SavePlaybackState(); _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _metadataWarmLoad?.Cancel();
            _inboxTimer.Stop(); _inboxCheck?.Cancel(); _appearanceTimer.Stop(); _commentsLoad?.Cancel(); _playlistEditLoad?.Cancel(); _dialog?.Hide();
            ResetTrackGesture(); CancelCoverTransitions(); RestorePresenterTransition(); StopSettingsIconRotation();
            _playerNoticeTimer.Stop(); PlayerNotice.IsOpen = false; QueuePane.Visibility = Visibility.Collapsed; QueueList.ItemsSource = null;
            _songStatsLoad?.Cancel(); _qualityLoad?.Cancel();
            if (_pageLoad != null) _pageInterrupted = true;
            _pageLoad?.Cancel(); _lyricsLoad?.Cancel(); _loginLoad?.Cancel(); _coverLoad?.Cancel();
            // Keep completed UI data and decoded images. Hiding should stop work,
            // not force network requests, decoding and layout on every restore.
            if (_lyrics.Count == 0) _lyricId = 0;
            ++_lyricScrollGeneration;
            Artwork.SuspendTree(Root);
        }
        else
        {
            Artwork.ResumeTree(Root);
            Playback_Changed();
            if (_sessionReady || _runningSmokeTest)
            {
                if (_api.HasSession) _ = UpdateAccountAsync();
                if (_authenticated) { _inboxTimer.Start(); _ = CheckUnreadAsync(); }
                var resumePage = _pageInterrupted;
                if (resumePage) _ = LoadPageAsync(_pageWasAppend);
                if (_view == "playing") { UpdateNowPlaying(); _ = LoadLyricsAsync(); }
                else if (!resumePage && _view is "discover" or "library" && Playlists.Count == 0) _ = LoadPageAsync(false);
                else if (!resumePage && _view is "artists" or "albums" or "saved-albums" or "radio" && Catalog.Count == 0) _ = LoadPageAsync(false);
            }
            UpdateProgress();
            if (_playback.Current is Track track) { _qualityTrack = 0; _ = LoadQualityAsync(track); _ = LoadSongEngagementAsync(track); }
        }
        UpdateTimer();
        if (!hidden && Root.IsLoaded && !_runningSmokeTest && !_sessionReady && !_initializingSession) _ = InitializeSessionAsync();
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    private void HideWindow()
    {
        Diagnostics.StartupTrace.Mark("Hide: release backdrop"); SystemBackdrop = null;
        Diagnostics.StartupTrace.Mark("Hide: native window"); ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 0);
        Diagnostics.StartupTrace.Mark("Hide: update visibility"); UpdateVisibility();
    }
    internal void RestoreWindow() { ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 9); Activate(); UpdateVisibility(); ApplyAppearance(); if (!_sessionReady && !_runningSmokeTest) _ = InitializeSessionAsync(); }
    private void ExitApp() { _exiting = true; Close(); }
    private void Cleanup()
    {
        SavePlaybackState(); _playbackSaveTimer.Stop(); _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _metadataWarmLoad?.Cancel();
        _exiting = true; _windowTransitionTimer.Stop(); CancelCoverTransitions(); _timer.Stop(); _playerNoticeTimer.Stop(); _appearanceTimer.Stop(); _inboxTimer.Stop(); _inboxCheck?.Cancel(); _readAllLoad?.Cancel(); _commentsLoad?.Cancel(); _playlistEditLoad?.Cancel(); _playlistQueueLoad?.Cancel(); _pageLoad?.Cancel(); _lyricsLoad?.Cancel(); _loginLoad?.Cancel(); _coverLoad?.Cancel(); _downloads.Cancel();
        _playback.Changed -= Playback_Changed; _playback.Error -= ShowError;
        _songStatsLoad?.Cancel(); _qualityLoad?.Cancel();
        _startup?.Cancel(); _playback.Dispose(); _api.Dispose(); _tray.Dispose();
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
