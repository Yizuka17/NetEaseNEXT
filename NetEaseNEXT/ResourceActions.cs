using Microsoft.UI.Xaml;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private long _playlistComments, _albumComments, _radioComments, _radioSubscribers;
    private bool _radioSubscribed, _radioSubscribeKnown, _radioSubscribeWriting;
    private void UpdateResourceActions()
    {
        var supported = _view is "playlist" or "album" or "programs";
        PlaylistSubscribeButton.Visibility = _view is "playlist" or "programs" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistCommentsButton.Visibility = ResourceShareButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        PlaylistCommentsButton.IsEnabled = supported && _playlistId > 0;
        var count = _view == "album" ? _albumComments : _view == "programs" ? _radioComments : _playlistComments;
        PlaylistCommentsLabel.Text = count > 0 ? "评论 (" + CountText(count) + ")" : "评论";
        UpdatePlaylistSubscribe(); UpdateAlbumSubscribe();
    }
    private SocialItem? PageCommentsSource()
    {
        var (type, prefix, kind) = _view switch
        {
            "playlist" => (2, "A_PL_0_", "playlist"),
            "album" => (3, "R_AL_3_", "album"),
            "programs" => (7, "A_DR_14_", "radio"),
            _ => (-1, "", "")
        };
        return type < 0 || _playlistId <= 0 ? null : new() { Name = FirstText(PlaylistDetailTitle.Text, _playlistName), Kind = kind, ResourceId = _playlistId, ResourceKey = _playlistId.ToString(), ResourceType = type, ThreadId = prefix + _playlistId };
    }
    private async void ContextComments_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is Track track && SongCommentsSource(track) is { } source) await ShowCommentsAsync(source);
    }
    private async Task LoadRadioDetailAsync(CancellationToken token)
    {
        var id = _playlistId; var result = await _api.CallAsync("dj_detail", new { rid = id }, token); token.ThrowIfCancellationRequested();
        if (_hidden || _view != "programs" || _playlistId != id) return;
        var radio = Object(result, "djRadio");
        if (radio.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        PlaylistDetailTitle.Text = FirstText(radio.Text("name"), _playlistName);
        PlaylistCreator.Text = Object(radio, "dj").Text("nickname");
        PlaylistDescription.Text = radio.Text("desc"); PlaylistDescription.MaxLines = 3;
        ExpandDescriptionButton.Visibility = PlaylistDescription.Text.Length > 100 ? Visibility.Visible : Visibility.Collapsed;
        if (radio.Text("picUrl").Length > 0) Artwork.SetUri(PlaylistDetailCover, radio.Text("picUrl"));
        _radioComments = radio.Number("commentCount"); _radioSubscribers = radio.Number("subCount");
        _radioSubscribeKnown = radio.TryGetProperty("subed", out var subscribed) && subscribed.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
        if (_radioSubscribeKnown) _radioSubscribed = subscribed.GetBoolean();
        UpdateResourceActions();
    }
    private async Task ToggleRadioSubscriptionAsync()
    {
        if (_view != "programs" || _radioSubscribeWriting || _pageLoad != null) return;
        var id = _playlistId; var session = _api.Cookie;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession || _view != "programs" || _playlistId != id) return; session = _api.Cookie; }
        _radioSubscribeWriting = true; UpdatePlaylistSubscribe();
        try
        {
            if (!_radioSubscribeKnown) await LoadRadioDetailAsync(CancellationToken.None);
            if (_view != "programs" || _playlistId != id || _api.Cookie != session) return;
            if (!_radioSubscribeKnown) { ShowError("暂时无法读取收藏状态，请重试"); return; }
            var subscribed = !_radioSubscribed;
            await _api.CallAsync("dj_sub", new { rid = id, t = subscribed ? 1 : 0 });
            if (_view == "programs" && _playlistId == id && _api.Cookie == session)
            { _radioSubscribed = subscribed; _radioSubscribers = Math.Max(0, _radioSubscribers + (subscribed ? 1 : -1)); }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _radioSubscribeWriting = false; UpdateResourceActions(); }
    }
}
