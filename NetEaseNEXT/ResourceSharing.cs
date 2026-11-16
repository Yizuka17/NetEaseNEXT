using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.ApplicationModel.DataTransfer;

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
    private ShareResource? _pendingShare;
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
    private void CopyResourceLink(ShareResource resource)
    {
        try { Clipboard.SetContent(SharePackage(resource)); Clipboard.Flush(); ShowPlayerMessage("已复制链接", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void RequestedShare(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pendingShare is { } resource) args.Request.Data = SharePackage(resource);
        else args.Request.FailWithDisplayText("请选择要分享的内容");
    }
    private void ShowWindowsShare(ShareResource resource)
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
            _pendingShare = resource; interop.ShowShareUIForWindow(window);
        }
        catch (Exception ex) { Log.Write(ex); ShowPlayerMessage("暂时无法打开系统分享，可使用复制链接", InfoBarSeverity.Warning); }
    }
    private void ShowResourceShare(FrameworkElement anchor, ShareResource? resource)
    {
        if (resource == null) { ShowPlayerMessage("暂无可分享的在线来源"); return; }
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "复制链接", Icon = new SymbolIcon(Symbol.Copy) }; copy.Click += (_, _) => CopyResourceLink(resource); menu.Items.Add(copy);
        var system = new MenuFlyoutItem { Text = "系统分享", Icon = new SymbolIcon(Symbol.Share) }; system.Click += (_, _) => ShowWindowsShare(resource); menu.Items.Add(system);
        if (resource.CanPublish)
        {
            var publish = new MenuFlyoutItem { Text = "分享到动态", Icon = new SymbolIcon(Symbol.Send) }; publish.Click += async (_, _) => await PublishResourceAsync(resource); menu.Items.Add(publish);
        }
        ShowThemedFlyout(menu, anchor);
    }
    private void ShareCurrent_Click(object sender, RoutedEventArgs args)
        => ShowResourceShare((FrameworkElement)sender, _playback.Current is { } track ? ShareForTrack(track) : null);
    private void ResourceShare_Click(object sender, RoutedEventArgs args) => ShowResourceShare((FrameworkElement)sender, ShareForPage());
    private void ContextShare_Click(object sender, RoutedEventArgs args)
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
        ShowResourceShare(anchor ?? Root, resource);
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
