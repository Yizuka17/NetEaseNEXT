using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private MenuFlyout? _rowContextMenu;
    private void RowContextCanceled(UIElement sender, RoutedEventArgs args) => CloseRowContextMenu();
    private void CloseRowContextMenu()
    {
        _rowContextMenu?.Hide(); _rowContextMenu = null;
    }
    private static SelectorItem? ContextContainer(DependencyObject? source)
    {
        while (source != null && source is not SelectorItem) source = VisualTreeHelper.GetParent(source);
        return source as SelectorItem;
    }
    private void TrackContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_hidden || sender is not ListView list || ContextContainer(args.OriginalSource as DependencyObject) is not { } container || list.ItemFromContainer(container) is not Track track) return;
        var anchor = container.ContentTemplateRoot as FrameworkElement ?? container;
        var menu = CreateTrackMenu(track, list);
        ShowRowContextMenu(menu, anchor, args.TryGetPosition(anchor, out var point) ? point : null); args.Handled = true;
    }
    private void ShowRowContextMenu(MenuFlyout menu, FrameworkElement anchor, Windows.Foundation.Point? position = null)
    {
        CloseRowContextMenu(); _rowContextMenu = menu;
        menu.SystemBackdrop = FloatingBackdrop(menu.SystemBackdrop);
        menu.Opened += Popup_Opened; menu.Closed += Closed;
        void Closed(object? sender, object args)
        {
            menu.Opened -= Popup_Opened; menu.Closed -= Closed;
            if (ReferenceEquals(_rowContextMenu, menu)) _rowContextMenu = null;
        }
        if (position is { } point) menu.ShowAt(anchor, point); else menu.ShowAt(anchor);
    }
    private MenuFlyout CreateTrackMenu(Track track, ListView list)
    {
        var menu = new MenuFlyout(); var route = _route;
        void Add(string text, string glyph, RoutedEventHandler action, Visibility visibility = Visibility.Visible)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = IconFonts.Action(glyph), DataContext = track, Visibility = visibility };
            item.Click += (sender, args) => { if (!_hidden && ReferenceEquals(route, _route)) action(sender, args); };
            menu.Items.Add(item);
        }
        Add("多选", "\uE739", ContextBatch_Click); Add("播放", "\uE768", ContextPlay_Click); Add("添加至下一首播放", "\uE710", ContextNext_Click);
        Add("歌手", "\uE77B", ContextArtist_Click); Add("专辑", "\uE93C", ContextAlbum_Click); Add("评论", "\uE90A", ContextComments_Click);
        Add("歌曲信息", "\uE946", ContextTrackInfo_Click); Add("打开文件位置", "\uE8B7", ContextOpenFile_Click); Add("分享", "\uE72D", ContextShare_Click);
        Add("收藏到歌单", "\uE8F4", ContextFavorite_Click); Add("下载", "\uE896", ContextDownload_Click);
        if (ReferenceEquals(list, QueueList)) Add("移出播放列表", "\uE74D", ContextRemoveQueue_Click);
        else
        {
            Add("从歌单移除", "\uE74D", ContextRemovePlaylist_Click, Visibility.Collapsed);
            Add("从本地列表移除", "\uE74D", ContextRemoveLocal_Click, Visibility.Collapsed);
        }
        menu.Opening += (_, _) =>
        {
            route = _route;
            // Build on demand from ItemFromContainer, not a detached template's
            // stale DataContext, and freeze that song for every menu action.
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.DataContext = track;
            PrepareBatchMenu(menu, list);
            if (ReferenceEquals(list, TrackList)) UpdateTrackContextActions(menu, track);
        };
        return menu;
    }
}
