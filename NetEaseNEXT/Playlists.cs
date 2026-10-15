using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _playlistSubscribed, _playlistSubscribeWriting;
    private long _playlistOwner, _playlistSubscribers, _playlistTrackCount;
    private long[] _playlistTrackIds = [];
    private async Task LoadPlaylistDetailAsync(CancellationToken ct)
    {
        var id = _playlistId;
        PlaylistSubscribeButton.IsEnabled = false; PlaylistCommentsButton.IsEnabled = false;
        var result = await _api.CallCachedAsync("playlist_detail", new { id, s = 0 }, ct); ct.ThrowIfCancellationRequested();
        if (_view != "playlist" || _playlistId != id) return;
        var playlist = result.GetProperty("playlist");
        _playlistTrackIds = playlist.TryGetProperty("trackIds", out var ids) ? ids.EnumerateArray().Select(t => t.Number("id")).Where(id => id > 0).Take(10000).ToArray() : [];
        _playlistTrackCount = playlist.Number("trackCount");
        PlaylistDetailTitle.Text = playlist.Text("name");
        PlaylistDescription.Text = playlist.Text("description"); PlaylistDescription.MaxLines = 3;
        ExpandDescriptionButton.Content = "展开简介"; ExpandDescriptionButton.Visibility = PlaylistDescription.Text.Length > 100 ? Visibility.Visible : Visibility.Collapsed;
        var creator = Object(playlist, "creator"); PlaylistCreator.Text = creator.Text("nickname"); _playlistOwner = creator.Number("userId");
        PlaylistTags.Text = playlist.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array ? string.Join("  ", tags.EnumerateArray().Select(t => t.GetString())) : "";
        Artwork.SetUri(PlaylistDetailCover, playlist.Text("coverImgUrl"));
        _playlistSubscribed = Flag(playlist, "subscribed"); _playlistSubscribers = playlist.Number("subscribedCount");
        ApplyPlaylistOwnership(playlist.Number("specialType") != 5);
        if (_api.HasSession && _accountId == 0) _ = ResolvePlaylistOwnershipAsync(id, _api.Cookie, playlist.Number("specialType") != 5, ct);
        UpdatePlaylistSubscribe(); PlaylistCommentsButton.IsEnabled = true;
        var comments = playlist.Number("commentCount"); PlaylistCommentsLabel.Text = comments > 0 ? "评论 (" + CountText(comments) + ")" : "评论";
        Root.UpdateLayout();
    }
    private void ApplyPlaylistOwnership(bool canDelete)
    {
        EditPlaylistButton.Visibility = DeletePlaylistButton.Visibility = _accountId > 0 && _playlistOwner == _accountId ? Visibility.Visible : Visibility.Collapsed;
        if (!canDelete) DeletePlaylistButton.Visibility = Visibility.Collapsed;
        PlaylistOwnerActions.Visibility = EditPlaylistButton.Visibility;
    }
    private async Task ResolvePlaylistOwnershipAsync(long id, string session, bool canDelete, CancellationToken token)
    {
        try
        {
            var profile = await GetProfileAsync(token);
            if (token.IsCancellationRequested || _view != "playlist" || _playlistId != id || _api.Cookie != session || _hidden) return;
            _accountId = profile?.Number("userId") ?? 0; ApplyPlaylistOwnership(canDelete); UpdatePlaylistSubscribe();
        }
        catch (OperationCanceledException) { }
        catch { }
    }
    private void UpdatePlaylistSubscribe()
    {
        PlaylistSubscribeLabel.Text = (_playlistSubscribed ? "已收藏" : "收藏") + (_playlistSubscribers > 0 ? " (" + CountText(_playlistSubscribers) + ")" : "");
        PlaylistSubscribeIcon.Glyph = _playlistSubscribed ? "\uE735" : "\uE734";
        PlaylistSubscribeButton.IsEnabled = !_playlistSubscribeWriting && (_accountId == 0 || _playlistOwner != _accountId);
    }
    private void ExpandDescription_Click(object sender, RoutedEventArgs args)
    {
        var expand = PlaylistDescription.MaxLines != 0;
        PlaylistDescription.MaxLines = expand ? 0 : 3;
        ExpandDescriptionButton.Content = expand ? "收起简介" : "展开简介";
        // Keep the songs reachable when a playlist has a very long description.
        if (expand)
        {
            var text = new TextBlock { Text = PlaylistDescription.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false, MaxWidth = 440 };
            var flyout = new Flyout { Content = new ScrollViewer { Content = text, MaxHeight = 360 } };
            flyout.Closed += (_, _) => { PlaylistDescription.MaxLines = 3; ExpandDescriptionButton.Content = "展开简介"; };
            PlaylistDescription.MaxLines = 3; flyout.ShowAt(ExpandDescriptionButton);
        }
    }
    private async void PlaylistSubscribe_Click(object sender, RoutedEventArgs args)
    {
        if (_playlistSubscribeWriting || _view != "playlist") return;
        var id = _playlistId;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession || _view != "playlist" || _playlistId != id) return; }
        _playlistSubscribeWriting = true; UpdatePlaylistSubscribe();
        try
        {
            var profile = await GetProfileAsync();
            if (profile?.Number("userId") == _playlistOwner) { ShowError("不能收藏自己创建的歌单"); return; }
            var subscribed = !_playlistSubscribed;
            await _api.CallAsync("playlist_subscribe", new { id, t = subscribed ? 1 : 2 });
            if (_view == "playlist" && _playlistId == id) { _playlistSubscribed = subscribed; _playlistSubscribers = Math.Max(0, _playlistSubscribers + (subscribed ? 1 : -1)); }
            ShowPlayerMessage(subscribed ? "已收藏歌单" : "已取消收藏", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _playlistSubscribeWriting = false; UpdatePlaylistSubscribe(); }
    }
    private async void PlaylistComments_Click(object sender, RoutedEventArgs args)
    {
        if (_view == "playlist") await ShowCommentsAsync(new SocialItem { Name = _playlistName, Kind = "playlist", ResourceId = _playlistId, ResourceKey = _playlistId.ToString(), ResourceType = 2, ThreadId = "A_PL_0_" + _playlistId });
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs args) => await CreatePlaylistAsync();
    private async Task CreatePlaylistAsync()
    {
        if (_dialog != null) return;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession) return; }
        var name = new TextBox { Header = "名称", MaxLength = 40 };
        var privacy = new ComboBox { Header = "可见性", ItemsSource = new[] { "公开", "仅自己" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 16, Width = 360 }; content.Children.Add(name); content.Children.Add(privacy); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "创建歌单", Content = content, PrimaryButtonText = "创建", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        long createdId = 0; var createdName = "";
        dialog.PrimaryButtonClick += async (_, eventArgs) =>
        {
            var deferral = eventArgs.GetDeferral();
            try
            {
                createdName = name.Text.Trim();
                var result = await _api.CallAsync("playlist_create", new { name = createdName, privacy = privacy.SelectedIndex == 1 ? 10 : 0, type = "NORMAL" });
                createdId = result.Number("id"); if (createdId == 0) createdId = Object(result, "playlist").Number("id");
                if (createdId <= 0) throw new InvalidOperationException("未取得新歌单，请刷新歌单列表确认");
            }
            catch (Exception ex) { error.Text = ex.Message; eventArgs.Cancel = true; }
            finally { deferral.Complete(); }
        };
        _dialog = dialog;
        try { await dialog.ShowAsync(); } finally { _dialog = null; }
        if (createdId > 0) { _playlistId = createdId; _playlistName = createdName; await NavigateAsync("playlist"); }
    }
}
