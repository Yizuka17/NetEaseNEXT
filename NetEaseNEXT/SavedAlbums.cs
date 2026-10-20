using Microsoft.UI.Xaml;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task LoadSavedAlbumsAsync(ApiClient api, CancellationToken token)
    {
        var session = api.Cookie;
        var result = await api.CallCachedAsync("album_sublist", new { limit = 48, offset = _offset }, token);
        token.ThrowIfCancellationRequested();
        if (_view != "saved-albums" || api.Cookie != session) return;
        var albums = result.GetProperty("data");
        foreach (var item in albums.EnumerateArray().Take(1000 - Catalog.Count)) Catalog.Add(CatalogItem.Album(item));
        _offset += albums.GetArrayLength();
        CatalogMore.Visibility = (Flag(result, "hasMore") || Flag(result, "more") || result.Number("count") > _offset) && albums.GetArrayLength() > 0 && Catalog.Count < 1000 ? Visibility.Visible : Visibility.Collapsed;
    }
}
