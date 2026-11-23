using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private void CardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_hidden || sender is not GridView grid || ContextContainer(args.OriginalSource as DependencyObject) is not { } container) return;
        var item = grid.ItemFromContainer(container);
        if (item is not (LocalPlaylistCard or Playlist or CatalogItem)) return;
        var anchor = container.ContentTemplateRoot as FrameworkElement ?? container;
        var menu = CreateCardMenu(item);
        ShowRowContextMenu(menu, anchor, args.TryGetPosition(anchor, out var point) ? point : null); args.Handled = true;
    }
    private MenuFlyout CreateCardMenu(object card)
    {
        var menu = new MenuFlyout(); var route = _route;
        void Add(string text, string glyph, RoutedEventHandler action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = IconFonts.Action(glyph), DataContext = card };
            item.Click += (sender, args) => RunRowMenuAction(menu, sender, args, action, () => ReferenceEquals(route, _route)); menu.Items.Add(item);
        }
        if (card is LocalPlaylistCard)
        {
            Add("打开文件夹", "\uE8B7", OpenLocalPlaylistFolder_Click); Add("编辑歌单", "\uE70F", EditLocalPlaylist_Click); Add("删除歌单", "\uE74D", DeleteLocalPlaylist_Click);
            menu.Opening += (_, args) => LocalCardMenu_Opening(menu, args);
        }
        else Add("分享", "\uE72D", ContextShare_Click);
        menu.Opening += (_, _) => route = _route;
        return menu;
    }
}
