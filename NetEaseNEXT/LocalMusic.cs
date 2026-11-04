using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    public ObservableCollection<LocalPlaylistCard> LocalPlaylistCards { get; } = [];
    private LocalLibraryStore _localLibrary = new();
    private string _localPlaylistId = "";
    private bool _importingLocal, _checkingLibraryWorkflow;
    private string? _localLibraryPathOverride;
    private LocalFolderMonitor? _localFolderMonitor;
    private CancellationTokenSource? _localMonitorLoad;
    private readonly SemaphoreSlim _localRefreshGate = new(1);
    private static readonly string[] AudioExtensions = LocalMusicFiles.Extensions;
    private bool IsLocalView => _view is "local" or "local-playlist";

    private async Task LoadLocalLibraryAsync(CancellationToken ct)
    {
        var library = await Task.Run(() => LocalLibraryStore.Load(_localLibraryPathOverride), ct);
        ct.ThrowIfCancellationRequested();
        _localLibrary = library;
        await RegisterLocalDownloadSourcesAsync(_localLibrary.Tracks); ct.ThrowIfCancellationRequested();
        LoadLocalTracks();
        if (!_runningSmokeTest || _checkingLibraryWorkflow) { StartLocalMonitoring(); await RefreshLocalFoldersAsync(ct); }
    }
    private void UpdateLocalPlaylists()
    {
        var cards = new List<LocalPlaylistCard>();
        var all = LocalTracks(null).ToArray();
        cards.Add(new("", "全部歌曲", all.FirstOrDefault(t => t.Cover.Length > 0)?.Cover ?? "", all.Length + " 首"));
        foreach (var playlist in _localLibrary.Playlists)
        {
            var tracks = LocalTracks(playlist).ToArray();
            var cover = playlist.CoverPath.Length > 0 && File.Exists(playlist.CoverPath) ? new Uri(playlist.CoverPath).AbsoluteUri : tracks.FirstOrDefault(t => t.Cover.Length > 0)?.Cover ?? "";
            cards.Add(new(playlist.Id, playlist.Name, cover, tracks.Length + " 首" + (playlist.FolderPath.Length > 0 ? "，文件夹同步" : "")));
        }
        var ids = cards.Select(c => c.Id).ToHashSet();
        foreach (var old in LocalPlaylistCards.Where(c => !ids.Contains(c.Id)).ToArray()) LocalPlaylistCards.Remove(old);
        for (var i = 0; i < cards.Count; i++)
        {
            var next = cards[i]; var old = LocalPlaylistCards.FirstOrDefault(c => c.Id == next.Id);
            if (old == null) LocalPlaylistCards.Insert(i, next);
            else
            {
                var position = LocalPlaylistCards.IndexOf(old); if (position != i) LocalPlaylistCards.Move(position, i);
                if (old.Name != next.Name || old.Cover != next.Cover || old.Description != next.Description) LocalPlaylistCards[i] = next;
            }
        }
    }
    private IEnumerable<Track> LocalTracks(LocalPlaylist? playlist)
    {
        var paths = playlist?.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _localLibrary.Tracks.Where(t => (paths == null || paths.Contains(t.LocalPath)) && !LocalMusicFiles.IsDownloadPartial(t.LocalPath) && File.Exists(t.LocalPath));
    }
    private void LoadLocalTracks()
    {
        if (!IsLocalView) return;
        UpdateLocalPlaylists();
        if (_view != "local-playlist") return;
        var playlist = _localLibrary.Playlists.FirstOrDefault(p => p.Id == _localPlaylistId);
        if (_localPlaylistId.Length > 0 && playlist == null) { _localPlaylistId = ""; }
        var visible = LocalTracks(playlist).ToArray();
        if (!Tracks.SequenceEqual(visible))
        {
            var paths = visible.Select(t => t.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Tracks.Count - 1; i >= 0; i--) if (!paths.Contains(Tracks[i].LocalPath)) Tracks.RemoveAt(i);
            for (var i = 0; i < visible.Length; i++)
            {
                var track = visible[i];
                if (i == Tracks.Count) { Tracks.Add(track); continue; }
                if (!Tracks[i].LocalPath.Equals(track.LocalPath, StringComparison.OrdinalIgnoreCase))
                {
                    var from = i + 1;
                    while (from < Tracks.Count && !Tracks[from].LocalPath.Equals(track.LocalPath, StringComparison.OrdinalIgnoreCase)) from++;
                    if (from == Tracks.Count) { Tracks.Insert(i, track); continue; }
                    Tracks.Move(from, i);
                }
                if (!ReferenceEquals(Tracks[i], track)) Tracks[i] = track;
            }
        }
        TrackCount.Text = Tracks.Count + " 首"; _tracksMore = false;
        PageTitle.Text = "本地音乐";
        LocalPlaylistTitle.Text = playlist?.Name ?? "全部歌曲";
        var card = LocalPlaylistCards.FirstOrDefault(c => c.Id == _localPlaylistId);
        Artwork.SetUri(LocalPlaylistCover, card?.Cover ?? "");
        LocalFolderPath.Text = playlist?.FolderPath ?? "";
        LocalFolderPath.Visibility = playlist?.FolderPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalFolderStatus.Text = playlist?.FolderStatus ?? "";
        LocalFolderStatus.Visibility = LocalFolderStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditLocalPlaylistButton.Visibility = DeleteLocalPlaylistButton.Visibility = playlist != null ? Visibility.Visible : Visibility.Collapsed;
        ImportLocalButton.IsEnabled = playlist?.FolderPath.Length is not > 0;
        ImportLocalButton.Visibility = playlist?.FolderPath.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateTrackLocator();
    }
    private async void LocalPlaylist_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LocalPlaylistCard playlist) return;
        await OpenLocalPlaylistAsync(playlist);
    }
    private async Task OpenLocalPlaylistAsync(LocalPlaylistCard playlist)
    {
        if (LocalPlaylistGrid.ContainerFromItem(playlist) is DependencyObject container &&
            FindDescendant<Image>(container) is { } image)
            PrepareCoverTransition("LocalPlaylistCover", image);
        else CancelCoverTransitions();
        _localPlaylistId = playlist.Id;
        await NavigateAsync("local-playlist");
    }
    private void LocalCardMenu_Opening(object sender, object args)
    {
        if (sender is MenuFlyout menu)
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.IsEnabled = item.DataContext is LocalPlaylistCard { Id.Length: > 0 };
    }
    private void CommitLocal(Action change)
    {
        var previous = JsonSerializer.Serialize(_localLibrary); var path = _localLibrary.StoragePath;
        try { change(); _localLibrary.Save(); }
        catch { _localLibrary = JsonSerializer.Deserialize<LocalLibraryStore>(previous)!; _localLibrary.StoragePath = path; throw; }
    }
    private void StopLocalMonitoring()
    {
        _localFolderMonitor?.Dispose(); _localFolderMonitor = null;
        _localMonitorLoad?.Cancel(); _localMonitorLoad?.Dispose(); _localMonitorLoad = null;
    }
    private void StartLocalMonitoring()
    {
        StopLocalMonitoring();
        if (_hidden || _exiting || !IsLocalView || (_runningSmokeTest && !_checkingLibraryWorkflow)) return;
        _localMonitorLoad = new(); var token = _localMonitorLoad.Token;
        var pending = new HashSet<string>();
        var scanning = false;
        _localFolderMonitor = new(_localLibrary.Playlists, ids => DispatcherQueue.TryEnqueue(async () =>
        {
            if (token.IsCancellationRequested || _hidden || _exiting || !IsLocalView) return;
            pending.UnionWith(ids);
            if (scanning) return;
            scanning = true;
            try
            {
                while (pending.Count > 0 && !token.IsCancellationRequested)
                {
                    var batch = new HashSet<string>(pending); pending.Clear();
                    await RefreshLocalFoldersAsync(token, batch);
                }
            }
            finally { scanning = false; }
        }));
    }
    private void ResumeLocalMonitoring()
    {
        StartLocalMonitoring();
        if (_localMonitorLoad != null) _ = RefreshLocalFoldersAsync(_localMonitorLoad.Token);
    }
    private async Task RefreshLocalFoldersAsync(CancellationToken ct, IReadOnlySet<string>? affected = null)
    {
        try
        {
            await _localRefreshGate.WaitAsync(ct);
            try
            {
                var changed = false;
                foreach (var item in _localLibrary.Playlists.Where(p => p.FolderPath.Length > 0 && (affected == null || affected.Contains(p.Id))).ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    var root = item.FolderPath; var recursive = item.IncludeSubfolders;
                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<LocalPlaylist>(JsonSerializer.Serialize(item))!;
                        var known = _localLibrary.Tracks.ToArray();
                        var scan = await Task.Run(() => LocalMusicFiles.Scan(snapshot, known, ct, _downloadedLibrary), ct);
                        ct.ThrowIfCancellationRequested();
                        var current = _localLibrary.Playlists.FirstOrDefault(p => p.Id == item.Id);
                        if (current == null || current.FolderPath != root || current.IncludeSubfolders != recursive || !current.ExcludedPaths.SequenceEqual(snapshot.ExcludedPaths)) continue;
                        var paths = scan.Tracks.Select(t => t.LocalPath).ToList();
                        var status = scan.Limited ? "仅显示前 10000 首，可缩小文件夹范围" : scan.Failed > 0 ? $"{scan.Failed} 个文件暂时无法读取，可稍后刷新" : "";
                        if (current.FolderStatus == status && current.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)
                            && current.FileStamps.Count == scan.Stamps.Count && current.FileStamps.All(p => scan.Stamps.TryGetValue(p.Key, out var stamp) && stamp == p.Value)) continue;
                        CommitLocal(() =>
                        {
                            var replacements = scan.Tracks.ToDictionary(t => t.LocalPath, StringComparer.OrdinalIgnoreCase);
                            _localLibrary.Tracks = _localLibrary.Tracks.Select(t => replacements.Remove(t.LocalPath, out var replacement) ? replacement : t).Concat(replacements.Values).ToList();
                            current.Paths = paths; current.FileStamps = scan.Stamps; current.FolderStatus = status;
                        });
                        changed = true;
                        await RegisterLocalDownloadSourcesAsync(scan.Tracks);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        var current = _localLibrary.Playlists.FirstOrDefault(p => p.Id == item.Id);
                        if (current?.FolderPath == root && current.FolderStatus != "文件夹暂不可用，保留上次列表") { current.FolderStatus = "文件夹暂不可用，保留上次列表"; changed = true; }
                        Log.Write(ex);
                    }
                }
                if (changed && !ct.IsCancellationRequested && IsLocalView) LoadLocalTracks();
            }
            finally { _localRefreshGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex); if (!_hidden && !_exiting) ShowError("本地歌单刷新失败：" + ex.Message); }
    }
    private async void RefreshLocal_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Control control) control.IsEnabled = false;
        try
        {
            var playlist = _view == "local-playlist" ? _localLibrary.Playlists.FirstOrDefault(p => p.Id == _localPlaylistId) : null;
            IReadOnlySet<string>? affected = playlist != null ? new HashSet<string> { playlist.Id } : null;
            await RefreshLocalFoldersAsync(_localMonitorLoad?.Token ?? CancellationToken.None, affected);
            LoadLocalTracks(); StartLocalMonitoring();
        }
        finally { if (sender is Control button) button.IsEnabled = true; }
    }
    private async void ImportLocalFiles_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(); foreach (var extension in AudioExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) await ImportLocalPathsAsync(files.Select(f => f.Path));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task ImportLocalPathsAsync(IEnumerable<string> paths)
    {
        if (_importingLocal) return; _importingLocal = true; SetBusy(true);
        try
        {
            var playlist = _view == "local-playlist" ? _localLibrary.Playlists.FirstOrDefault(p => p.Id == _localPlaylistId) : null;
            if (playlist?.FolderPath.Length > 0) throw new InvalidOperationException("请将歌曲放入绑定的文件夹，然后刷新");
            var known = _localLibrary.Tracks.Select(t => t.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = paths.Where(LocalMusicFiles.IsAudioFile).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(10000).ToArray();
            var selection = selected.Where(p => !known.Contains(p)).Take(Math.Max(0, 10000 - known.Count)).ToArray();
            var result = await Task.Run(() =>
            {
                var tracks = new List<Track>(); var failed = 0;
                foreach (var path in selection) try { tracks.Add(LocalMusicFiles.Read(path, _downloadedLibrary)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { failed++; }
                return (tracks, failed);
            });
            CommitLocal(() =>
            {
                _localLibrary.Tracks.AddRange(result.tracks);
                if (playlist != null) playlist.Paths = playlist.Paths.Concat(selected.Where(p => _localLibrary.Tracks.Any(t => string.Equals(t.LocalPath, p, StringComparison.OrdinalIgnoreCase)))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
            await RegisterLocalDownloadSourcesAsync(result.tracks);
            UpdateLocalPlaylists(); LoadLocalTracks(); ShowPlayerMessage(result.failed > 0 ? $"已导入 {result.tracks.Count} 首，{result.failed} 个文件无法读取" : $"已导入 {result.tracks.Count} 首");
        }
        finally { _importingLocal = false; SetBusy(false); }
    }
    private async Task<string?> PickLocalFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async Task EditLocalPlaylistAsync(LocalPlaylist? existing)
    {
        if (_dialog != null) return;
        var name = new TextBox { Header = "歌单名称", Text = existing?.Name ?? "", MaxLength = 80, MinHeight = 44 };
        var folder = existing?.FolderPath ?? ""; var cover = existing?.CoverPath ?? "";
        var preview = new Image { Width = 112, Height = 112, Stretch = Stretch.UniformToFill };
        var frame = new Border { Width = 112, Height = 112, CornerRadius = new CornerRadius(8), Child = preview, Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        if (cover.Length > 0) Artwork.SetUri(preview, new Uri(cover).AbsoluteUri);
        var coverButton = new Button { Content = "选择封面", MinHeight = 44 }; var clearCover = new Button { Content = "移除封面", MinHeight = 44 };
        var artworkActions = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; artworkActions.Children.Add(coverButton); artworkActions.Children.Add(clearCover);
        var artwork = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 }; artwork.Children.Add(frame); artwork.Children.Add(artworkActions);
        var folderText = new TextBox { Header = "文件夹路径（可直接输入，或点击选择文件夹）", Text = folder, PlaceholderText = @"C:\Users\SANSH\Music\NetEaseNEXT-Motion-Test" };
        var choose = new Button { Content = "选择文件夹", MinHeight = 44 }; var unbind = new Button { Content = "取消绑定", MinHeight = 44, IsEnabled = folder.Length > 0 };
        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; folders.Children.Add(choose); folders.Children.Add(unbind);
        var recursive = new CheckBox { Content = "包含子文件夹", IsChecked = existing?.IncludeSubfolders ?? true, IsEnabled = folder.Length > 0 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Width = 420, Spacing = 14 }; body.Children.Add(artwork); body.Children.Add(name); body.Children.Add(new TextBlock { Text = "同步文件夹" }); body.Children.Add(folderText); body.Children.Add(folders); body.Children.Add(recursive); body.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = existing == null ? "新建本地歌单" : "编辑本地歌单", Content = body, PrimaryButtonText = existing == null ? "创建" : "保存", CloseButtonText = "取消", IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        choose.Click += async (_, _) =>
        {
            try { if (await PickLocalFolderAsync() is { } path) { folder = path; folderText.Text = folder; unbind.IsEnabled = recursive.IsEnabled = true; if (name.Text.Length == 0) name.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)); } }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        };
        unbind.Click += (_, _) => { folder = ""; folderText.Text = ""; unbind.IsEnabled = recursive.IsEnabled = false; };
        coverButton.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(); foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }) picker.FileTypeFilter.Add(extension);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSingleFileAsync() is not { } file) return;
                if (new FileInfo(file.Path).Length > 16 * 1024 * 1024) throw new InvalidDataException("封面不能超过 16 MB");
                using var stream = await file.OpenReadAsync(); var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                if ((long)decoder.PixelWidth * decoder.PixelHeight > 64000000) throw new InvalidDataException("封面尺寸过大");
                cover = LocalMusicFiles.StoreCover(await File.ReadAllBytesAsync(file.Path)); Artwork.SetUri(preview, new Uri(cover).AbsoluteUri);
            }
            catch (Exception ex) { error.Text = "无法读取封面：" + ex.Message; error.Visibility = Visibility.Visible; }
        };
        clearCover.Click += (_, _) => { cover = ""; Artwork.SetUri(preview, ""); };
        _dialog = dialog;
        try
        {
            if (await ShowThemedDialogAsync(dialog) != ContentDialogResult.Primary) return;
            folder = folderText.Text.Trim().Trim('"');
            if (folder.Length > 0)
            {
                folder = Path.GetFullPath(folder);
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("文件夹不存在：" + folder);
                recursive.IsChecked ??= true;
            }
            var id = existing?.Id ?? Guid.NewGuid().ToString("N");
            CommitLocal(() =>
            {
                var playlist = _localLibrary.Playlists.FirstOrDefault(p => p.Id == id);
                if (playlist == null) { playlist = new() { Id = id }; _localLibrary.Playlists.Add(playlist); }
                if (!string.Equals(playlist.FolderPath, folder, StringComparison.OrdinalIgnoreCase)) { playlist.FileStamps.Clear(); playlist.ExcludedPaths.Clear(); if (folder.Length > 0) playlist.Paths.Clear(); }
                playlist.Name = name.Text.Trim(); playlist.CoverPath = cover; playlist.FolderPath = folder; playlist.IncludeSubfolders = recursive.IsChecked == true; playlist.FolderStatus = "";
            });
            await RefreshLocalFoldersAsync(CancellationToken.None, new HashSet<string> { id }); LoadLocalTracks(); StartLocalMonitoring();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialog = null; }
    }
    private async void CreateLocalPlaylist_Click(object sender, RoutedEventArgs args) => await EditLocalPlaylistAsync(null);
    private async void EditLocalPlaylist_Click(object sender, RoutedEventArgs args)
    {
        var id = (sender as FrameworkElement)?.DataContext is LocalPlaylistCard card ? card.Id : _localPlaylistId;
        if (_localLibrary.Playlists.FirstOrDefault(p => p.Id == id) is { } playlist) await EditLocalPlaylistAsync(playlist);
    }
    private async void DeleteLocalPlaylist_Click(object sender, RoutedEventArgs args)
    {
        var id = (sender as FrameworkElement)?.DataContext is LocalPlaylistCard card ? card.Id : _localPlaylistId;
        if (id.Length == 0) return;
        try { CommitLocal(() => _localLibrary.Playlists.RemoveAll(p => p.Id == id)); StartLocalMonitoring(); if (_view == "local-playlist" && id == _localPlaylistId) { _localPlaylistId = ""; await NavigateAsync("local"); } else UpdateLocalPlaylists(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task FavoriteLocalTracksAsync(IReadOnlyList<Track> tracks)
    {
        if (_dialog != null) return;
        if (!IsLocalView) _localLibrary = await Task.Run(() => LocalLibraryStore.Load());
        var choices = _localLibrary.Playlists.Where(p => p.FolderPath.Length == 0).ToArray();
        var list = new ComboBox { Header = "手工歌单", ItemsSource = choices, SelectedIndex = choices.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "或新建歌单", MaxLength = 80 };
        var content = new StackPanel { Width = 340, Spacing = 12 }; content.Children.Add(list); content.Children.Add(name);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "添加到本地歌单", Content = content, PrimaryButtonText = "添加", CloseButtonText = "取消", IsPrimaryButtonEnabled = list.SelectedItem != null };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem != null || name.Text.Trim().Length > 0;
        _dialog = dialog;
        try
        {
            if (await ShowThemedDialogAsync(dialog) != ContentDialogResult.Primary) return;
            CommitLocal(() =>
            {
                var playlist = name.Text.Trim().Length > 0 ? new LocalPlaylist { Name = name.Text.Trim() } : list.SelectedItem as LocalPlaylist ?? throw new InvalidOperationException("请选择歌单");
                if (!_localLibrary.Playlists.Contains(playlist)) _localLibrary.Playlists.Add(playlist);
                playlist.Paths = playlist.Paths.Concat(tracks.Select(t => t.LocalPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
            UpdateLocalPlaylists(); ShowPlayerMessage("已添加到本地歌单");
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialog = null; }
    }
    private async void ContextRemoveLocal_Click(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: Track track }) await ConfirmRemoveLocalTracksAsync([track]); }
    private void RemoveLocalTracks(IReadOnlyList<Track> tracks)
    {
        try
        {
            var paths = tracks.Select(t => t.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var playlist = _localLibrary.Playlists.FirstOrDefault(p => p.Id == _localPlaylistId);
            CommitLocal(() =>
            {
                if (playlist != null)
                {
                    playlist.Paths.RemoveAll(paths.Contains);
                    if (playlist.FolderPath.Length > 0) playlist.ExcludedPaths = playlist.ExcludedPaths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                else
                {
                    _localLibrary.Tracks.RemoveAll(t => paths.Contains(t.LocalPath));
                    foreach (var item in _localLibrary.Playlists)
                    {
                        var excluded = item.Paths.Where(paths.Contains).ToArray();
                        item.Paths.RemoveAll(paths.Contains);
                        if (item.FolderPath.Length > 0) item.ExcludedPaths = item.ExcludedPaths.Concat(excluded).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            });
            UpdateLocalPlaylists(); LoadLocalTracks(); ShowPlayerMessage("已从列表移除，原文件保留");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
