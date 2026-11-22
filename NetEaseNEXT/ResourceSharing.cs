using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private sealed record ShareResource(string Kind, long Id, string Name)
    {
        internal Uri Link => new("https://music.163.com/" + (Kind == "djprogram" ? "program" : Kind) + "?id=" + Id);
        internal bool CanPublish => Kind is "song" or "playlist" or "album" or "djradio" or "djprogram" or "mv";
    }
    private CancellationTokenSource? _resourceShareLoad;
    private DataTransferManager? _shareManager;
    private sealed record ShareSelection(ShareResource? Resource, StorageFile? File, string Name);
    private ShareSelection? _pendingShare;
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShareWindow
    {
        nint GetForWindow(nint window, ref Guid iid);
        void ShowShareUIForWindow(nint window);
    }
    private ShareResource? ShareForTrack(Track track)
    {
        if (track.PodcastId > 0) return ProgramIdentity(track) is > 0 and var programId ? new("djprogram", programId, track.Title) : null;
        var id = SourceSongId(track); return id > 0 ? new("song", id, track.Title) : null;
    }
    private ShareResource? ShareForPage() => _playlistId <= 0 ? null : _view switch
    {
        "playlist" => new("playlist", _playlistId, PlaylistDetailTitle.Text),
        "album" => new("album", _playlistId, PlaylistDetailTitle.Text),
        "programs" => new("djradio", _playlistId, PlaylistDetailTitle.Text),
        _ => null
    };
    private static DataPackage SharePackage(ShareResource resource)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.Properties.Title = resource.Name; package.SetWebLink(resource.Link); package.SetText(resource.Name + "\n" + resource.Link.AbsoluteUri);
        return package;
    }
    private string? ShareFilePath(Track track)
    {
        var path = track.IsLocal ? track.LocalPath : _playback.Current?.Id == track.Id && _playback.CurrentDownloadedPath is { Length: > 0 } playing && File.Exists(playing) ? playing : DownloadForTrack(track)?.Path;
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
    private async Task<string?> FindShareFileAsync(Track track, CancellationToken token)
    {
        var known = ShareFilePath(track);
        if (known != null || track.IsLocal || track.Id <= 0) return known;
        // A saved folder binding may predate the download index, or have been
        // retagged. Repair only this song's known paths when sharing is requested.
        var sources = await Task.Run(() =>
        {
            LocalLibraryStore saved;
            try { saved = LocalLibraryStore.Load(_localLibraryPathOverride); }
            catch (Exception ex) { Log.Write(ex); saved = new(); }
            var candidates = saved.Tracks.Concat(_localLibrary.Tracks).Where(t => t.SourceSongId == track.Id).Select(t => t.LocalPath)
                .Concat(_downloadedLibrary.KnownPaths(track.Id)).Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = new List<Track>();
            foreach (var path in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Read the embedded source again: a replaced file must not
                    // inherit a stale association based on its filename.
                    var local = LocalMusicFiles.Read(path, embeddedSourceOnly: true);
                    if (local.HasValidLocalSource() && local.SourceSongId == track.Id) matches.Add(local);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { }
            }
            return matches;
        }, token);
        token.ThrowIfCancellationRequested();
        if (sources.Count > 0) await Task.Run(() => _downloadedLibrary.RegisterLocalSources(sources), token);
        // File sharing does not require a known audio quality. Older tagged
        // files can have a verified song ID without an entry in the quality index.
        return ShareFilePath(track) ?? sources.Where(t => t.HasValidLocalSource()).OrderByDescending(t => Services.AudioQuality.Rank(t.SourceLevel)).FirstOrDefault()?.LocalPath;
    }
    private async Task ShowTrackShareAsync(FrameworkElement anchor, Track track)
    {
        _resourceShareLoad?.Cancel(); using var load = new CancellationTokenSource(); _resourceShareLoad = load; var route = _route;
        try
        {
            var path = await FindShareFileAsync(track, load.Token);
            if (!load.IsCancellationRequested && !_hidden && ReferenceEquals(route, _route)) ShowResourceShare(anchor, ShareForTrack(track), path, track.Title);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (ReferenceEquals(_resourceShareLoad, load)) _resourceShareLoad = null; }
    }
    private bool CanShareTrack(Track track) => ShareForTrack(track) != null || ShareFilePath(track) != null;
    private static DataPackage ShareFilePackage(StorageFile file, string title)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.Properties.Title = string.IsNullOrWhiteSpace(title) ? file.DisplayName : title; package.SetStorageItems(new IStorageItem[] { file }, true);
        return package;
    }
    private async Task ShowFileShareAsync(string path, string title)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!_hidden && !_exiting) ShowWindowsShare(new ShareSelection(null, file, title));
        }
        catch (FileNotFoundException) { ShowPlayerMessage("文件已不存在，请刷新后重试", InfoBarSeverity.Warning); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void CopyResourceLink(ShareResource resource)
    {
        try { Clipboard.SetContent(SharePackage(resource)); Clipboard.Flush(); ShowPlayerMessage("已复制链接", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void RequestedShare(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pendingShare?.File is { } file) args.Request.Data = ShareFilePackage(file, _pendingShare.Name);
        else if (_pendingShare?.Resource is { } resource) args.Request.Data = SharePackage(resource);
        else args.Request.FailWithDisplayText("请选择要分享的内容");
    }
    private void ShowWindowsShare(ShareResource resource) => ShowWindowsShare(new ShareSelection(resource, null, resource.Name));
    private void ShowWindowsShare(ShareSelection selection)
    {
        try
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this); var interop = DataTransferManager.As<IShareWindow>();
            if (_shareManager == null)
            {
                var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C"); var pointer = interop.GetForWindow(window, ref iid);
                try { _shareManager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer); }
                finally { Marshal.Release(pointer); }
                _shareManager.DataRequested += RequestedShare;
            }
            _pendingShare = selection; interop.ShowShareUIForWindow(window);
        }
        catch (Exception ex) { Log.Write(ex); ShowPlayerMessage(selection.File != null ? "暂时无法打开文件分享" : "暂时无法打开系统分享，可使用复制链接", InfoBarSeverity.Warning); }
    }
    private MenuFlyout ResourceShareMenu(ShareResource? resource, string? filePath = null, string? title = null)
    {
        var menu = new MenuFlyout();
        if (resource != null)
        {
            var copy = new MenuFlyoutItem { Text = "复制链接", Icon = new SymbolIcon(Symbol.Copy) }; copy.Click += (_, _) => CopyResourceLink(resource); menu.Items.Add(copy);
            var system = new MenuFlyoutItem { Text = "系统分享", Icon = IconFonts.Create("\uE72D") }; system.Click += (_, _) => ShowWindowsShare(resource); menu.Items.Add(system);
        }
        if (filePath != null)
        {
            var file = new MenuFlyoutItem { Text = "分享文件", Icon = IconFonts.Action("\uE72D") };
            ToolTipService.SetToolTip(file, Path.GetFileName(filePath));
            file.Click += async (_, _) => await ShowFileShareAsync(filePath, title ?? resource?.Name ?? Path.GetFileNameWithoutExtension(filePath)); menu.Items.Add(file);
        }
        if (resource?.CanPublish == true)
        {
            var publish = new MenuFlyoutItem { Text = "分享到动态", Icon = new SymbolIcon(Symbol.Send) }; publish.Click += async (_, _) => await PublishResourceAsync(resource); menu.Items.Add(publish);
        }
        return menu;
    }
    private void ShowResourceShare(FrameworkElement anchor, ShareResource? resource, string? filePath = null, string? title = null)
    {
        var menu = ResourceShareMenu(resource, filePath, title);
        if (menu.Items.Count == 0) { ShowPlayerMessage("暂无可分享的内容"); return; }
        ShowThemedFlyout(menu, anchor);
    }
    private async void ShareCurrent_Click(object sender, RoutedEventArgs args)
    {
        if (_playback.Current is { } track) await ShowTrackShareAsync((FrameworkElement)sender, track);
    }
    private void ResourceShare_Click(object sender, RoutedEventArgs args) => ShowResourceShare((FrameworkElement)sender, ShareForPage());
    private async void ContextShare_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        var resource = element.DataContext switch
        {
            Track track => ShareForTrack(track),
            Playlist playlist => new ShareResource("playlist", playlist.Id, playlist.Name),
            CatalogItem item => new ShareResource(item.Kind == "radio" ? "djradio" : item.Kind, item.Id, item.Name),
            _ => null
        };
        var anchor = element.DataContext switch
        {
            Track track => TrackList.ContainerFromItem(track) as FrameworkElement ?? QueueList.ContainerFromItem(track) as FrameworkElement,
            Playlist playlist => PlaylistGrid.ContainerFromItem(playlist) as FrameworkElement,
            CatalogItem item => CatalogGrid.ContainerFromItem(item) as FrameworkElement,
            _ => null
        };
        var sharedTrack = element.DataContext as Track;
        if (sharedTrack != null) await ShowTrackShareAsync(anchor ?? Root, sharedTrack);
        else ShowResourceShare(anchor ?? Root, resource);
    }
    private async Task PublishResourceAsync(ShareResource resource)
    {
        if (_dialog != null || _hidden || !resource.CanPublish) return;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession || _hidden) return; }
        var input = new TextBox { PlaceholderText = "说点什么", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 140, MinHeight = 100 };
        var preview = new TextBlock { Text = resource.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var link = new TextBlock { Text = resource.Link.AbsoluteUri, Opacity = .65, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, Width = Math.Max(220, Math.Min(440, Root.ActualWidth - 120)) };
        panel.Children.Add(preview); panel.Children.Add(link); panel.Children.Add(input); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "分享到动态", Content = panel, PrimaryButtonText = "发布", CloseButtonText = "取消" };
        using var load = new CancellationTokenSource(); _resourceShareLoad = load; var session = _api.Cookie; var sending = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (sending || load.IsCancellationRequested || _api.Cookie != session) { args.Cancel = true; return; }
            sending = true; dialog.IsPrimaryButtonEnabled = false; var deferral = args.GetDeferral();
            try { await _api.CallAsync("share_resource", new { type = resource.Kind, id = resource.Id.ToString(), msg = input.Text.Trim() }, load.Token); }
            catch (Exception ex) { args.Cancel = true; error.Text = ex is OperationCanceledException ? "发布已取消" : ex.Message; }
            finally { sending = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        _dialog = dialog;
        try { if (await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary) ShowPlayerMessage("已发布", InfoBarSeverity.Success); }
        finally { load.Cancel(); if (ReferenceEquals(_resourceShareLoad, load)) _resourceShareLoad = null; if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
}
