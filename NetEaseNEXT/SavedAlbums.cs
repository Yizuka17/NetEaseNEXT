using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _albumSubscribed, _albumSubscribeKnown, _albumSubscribeWriting;
    private string _albumSubscriptionSession = "";
    private void UpdateAlbumSubscribe()
    {
        AlbumSubscribeButton.Visibility = _view == "album" ? Visibility.Visible : Visibility.Collapsed;
        AlbumSubscribeButton.IsEnabled = !_albumSubscribeWriting && _pageLoad == null;
        AlbumSubscribeLabel.Text = _albumSubscribed ? "已收藏" : "收藏专辑";
        AlbumSubscribeIcon.Glyph = _albumSubscribed ? "\uE735" : "\uE734";
    }
    private async Task LoadAlbumSubscriptionAsync(ApiClient api, long id, CancellationToken token)
    {
        var session = api.Cookie;
        _albumSubscribeKnown = false; _albumSubscribed = false; UpdateAlbumSubscribe();
        if (!api.HasSession) return;
        try
        {
            var state = await api.CallAsync("album_detail_dynamic", new { id }, token);
            token.ThrowIfCancellationRequested();
            if (_hidden || _view != "album" || _playlistId != id || api.Cookie != session) return;
            _albumSubscribed = Flag(state, "isSub"); _albumSubscriptionSession = session; _albumSubscribeKnown = true; UpdateAlbumSubscribe();
            _albumComments = state.Number("commentCount"); UpdateResourceActions();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { if (_view == "album" && _playlistId == id && api.Cookie == session) ShowError("专辑收藏状态加载失败：" + ex.Message); }
    }
    private async void AlbumSubscribe_Click(object sender, RoutedEventArgs args) => await ToggleAlbumSubscriptionAsync(_api);
    private async Task ToggleAlbumSubscriptionAsync(ApiClient api)
    {
        if (_view != "album" || _albumSubscribeWriting || _pageLoad != null || _hidden) return;
        var id = _playlistId;
        if (!api.HasSession) { await ShowLoginAsync(); if (!api.HasSession || _view != "album" || _playlistId != id) return; }
        var session = api.Cookie;
        _albumSubscribeWriting = true; UpdateAlbumSubscribe();
        try
        {
            if (!_albumSubscribeKnown || _albumSubscriptionSession != session) await LoadAlbumSubscriptionAsync(api, id, CancellationToken.None);
            if (!_albumSubscribeKnown || _hidden || _view != "album" || _playlistId != id || api.Cookie != session) return;
            var subscribed = !_albumSubscribed;
            await api.CallAsync("album_sub", new { id, t = subscribed ? 1 : 0 });
            if (_view == "album" && _playlistId == id && api.Cookie == session) { _albumSubscribed = subscribed; if (!_hidden) ShowPlayerMessage(subscribed ? "已收藏专辑" : "已取消收藏", InfoBarSeverity.Success); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _albumSubscribeWriting = false; UpdateAlbumSubscribe(); }
    }
    private async Task LoadSavedAlbumsAsync(ApiClient api, CancellationToken token)
    {
        var session = api.Cookie;
        var result = await api.CallCachedAsync("album_sublist", new { limit = 48, offset = _offset }, token);
        token.ThrowIfCancellationRequested();
        if (_view != "saved-albums" || api.Cookie != session) return;
        var albums = result.GetProperty("data");
        foreach (var item in albums.EnumerateArray().Take(1000 - Catalog.Count)) Catalog.Add(CatalogItem.Album(item));
        _offset += albums.GetArrayLength();
        _catalogMore = (Flag(result, "hasMore") || Flag(result, "more") || result.Number("count") > _offset) && albums.GetArrayLength() > 0 && Catalog.Count < 1000;
    }
}
