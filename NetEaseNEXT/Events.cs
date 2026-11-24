using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private SocialItem? _linkedResource;
    private bool _myEvents, _publishingEvent;
    private static List<string> EventPictures(JsonElement item)
    {
        if (!item.TryGetProperty("pics", out var pics) || pics.ValueKind != JsonValueKind.Array) return [];
        return pics.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : FirstText(p.Text("originUrl"), p.Text("originalUrl"), p.Text("pcSquareUrl"), p.Text("url")))
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").Distinct().Take(9).ToList();
    }
    private static string EventBody(JsonElement payload)
    {
        if (payload.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.Object) return EventBody(message);
        return FirstText(payload.Text("msg"), payload.Text("message"), payload.Text("content"));
    }
    private static CatalogItem? SharedEventCatalog(JsonElement payload)
    {
        var album = Object(payload, "album");
        if (album.Number("id") > 0) return new(album.Number("id"), FirstText(album.Text("name"), album.Text("title")), FirstText(album.Text("picUrl"), album.Text("coverImgUrl"), album.Text("blurPicUrl")), "album", "专辑");
        var playlist = Object(payload, "playlist");
        if (playlist.Number("id") > 0) return new(playlist.Number("id"), FirstText(playlist.Text("name"), playlist.Text("title")), FirstText(playlist.Text("coverImgUrl"), playlist.Text("picUrl")), "playlist", "歌单");
        var program = Object(payload, "program");
        if (program.Number("id") > 0) return new(program.Number("id"), FirstText(program.Text("name"), program.Text("title")), FirstText(program.Text("coverUrl"), program.Text("picUrl")), "program", "播客单集");
        var radio = FirstObject(Object(payload, "djRadio"), FirstObject(Object(payload, "djradio"), FirstObject(Object(payload, "radio"), Object(program, "radio"))));
        if (radio.Number("id") > 0) return new(radio.Number("id"), FirstText(radio.Text("name"), radio.Text("title")), FirstText(radio.Text("picUrl"), radio.Text("coverUrl")), "radio", "播客");
        var nested = Payload(payload, "msg");
        return nested.ValueKind == JsonValueKind.Object ? SharedEventCatalog(nested) : null;
    }
    private async void SocialCatalog_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not SocialItem { SharedCatalog: { } resource }) return;
        try { await OpenSocialCatalogAsync(resource); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private CancellationTokenSource? _socialResourceLoad;
    private async Task OpenSocialCatalogAsync(CatalogItem resource)
    {
        if (_hidden || _exiting) return;
        _socialResourceLoad?.Cancel();
        using var load = new CancellationTokenSource(); _socialResourceLoad = load;
        var route = _route; var session = _api.Cookie;
        try
        {
            var id = resource.Id; var name = resource.Name; var cover = resource.Cover;
            if (resource.Kind == "program")
            {
                var result = await _api.CallCachedAsync("dj_program_detail", new { id }, load.Token);
                load.Token.ThrowIfCancellationRequested();
                if (_hidden || !ReferenceEquals(route, _route) || session != _api.Cookie) return;
                var radio = Object(Object(result, "program"), "radio");
                id = radio.Number("id"); name = radio.Text("name"); cover = radio.Text("picUrl");
                if (id <= 0) throw new InvalidOperationException("播客暂不可用");
            }
            _playlistId = id; _playlistName = name;
            await NavigateAsync(resource.Kind == "album" ? "album" : resource.Kind is "radio" or "program" ? "programs" : "playlist", cover: cover);
        }
        finally { if (ReferenceEquals(_socialResourceLoad, load)) _socialResourceLoad = null; }
    }
    private static Track? SharedEventTrack(JsonElement payload)
    {
        var song = Object(payload, "song");
        if (song.Number("id") <= 0)
        {
            var program = Object(payload, "program"); var main = Object(program, "mainSong");
            var radio = Object(program, "radio");
            if (program.Number("id") > 0 && main.Number("id") > 0 && radio.Number("id") > 0)
                return new(main.Number("id"), FirstText(program.Text("name"), main.Text("name")), Object(program, "dj").Text("nickname"), radio.Text("name"), FirstText(program.Text("coverUrl"), radio.Text("picUrl")), Math.Max(program.Number("duration"), main.Number("duration"))) { PodcastId = radio.Number("id"), ProgramId = program.Number("id") };
            var nested = Payload(payload, "msg");
            return nested.ValueKind == JsonValueKind.Object ? SharedEventTrack(nested) : null;
        }
        try { return Track.FromJson(song); }
        catch
        {
            var album = FirstObject(Object(song, "album"), Object(song, "al"));
            var artists = song.TryGetProperty("artists", out var a) ? a : song.TryGetProperty("ar", out a) ? a : default;
            return new(song.Number("id"), song.Text("name"), artists.ValueKind == JsonValueKind.Array ? string.Join(" / ", artists.EnumerateArray().Select(p => p.Text("name"))) : "", album.Text("name"), album.Text("picUrl"), Math.Max(song.Number("duration"), song.Number("dt")));
        }
    }
    private static string SourceLinkLabel(SocialItem item) => item.ResourceId > 0 ? item.ResourceType switch { 0 => "查看歌曲", 6 => "查看动态", 2 => "查看歌单", 3 => "查看专辑", _ => "" } : "";
    private async Task OpenMessageSourceAsync(SocialItem item)
    {
        _linkedResource = item;
        if (item.ResourceType is 2 or 3)
        {
            _playlistId = item.ResourceId; _playlistName = item.OriginalText;
            await NavigateAsync(item.ResourceType == 2 ? "playlist" : "album");
        }
        else if (SourceLinkLabel(item).Length > 0) await NavigateAsync(item.ResourceType == 0 ? "song" : "event-detail");
    }
    private async Task LoadLinkedResourceAsync(CancellationToken token)
    {
        if (_linkedResource is not { } source) return;
        if (_view == "song")
        {
            var result = await _api.CallCachedAsync("song_detail", new { ids = source.ResourceId.ToString() }, token);
            token.ThrowIfCancellationRequested();
            foreach (var song in result.GetProperty("songs").EnumerateArray()) Tracks.Add(Track.FromJson(song));
            PageTitle.Text = Tracks.FirstOrDefault()?.Title ?? "歌曲"; TrackCount.Text = "";
            return;
        }
        if (source.LinkedEvent is { } embedded && embedded.Text != "[消息]" && embedded.ThreadId.Length > 0)
        { SocialItems.Add(embedded); return; }
        var key = source.ResourceKey.Split('_');
        var owner = key.Length > 1 && long.TryParse(key[^1], out var uid) ? uid : 0;
        if (owner == 0) { ShowEmpty("动态暂不可用"); return; }
        long cursor = -1;
        for (var page = 0; page < 10; page++)
        {
            var response = await _api.CallAsync("user_event", new { uid = owner, limit = 30, lasttime = cursor }, token);
            token.ThrowIfCancellationRequested();
            if (!response.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) break;
            foreach (var item in events.EnumerateArray())
                if (item.Number("id") == source.ResourceId) { SocialItems.Add(ParseSocialItem(item, "events", "", _accountId)); return; }
            var next = response.Number("lasttime");
            if (!Flag(response, "more") || next <= 0 || next == cursor) break;
            cursor = next;
        }
        ShowEmpty("动态已删除或暂不可用");
    }
    private async void EventScope_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        _myEvents = EventScope.SelectedIndex == 1;
        if (_view == "events") await RefreshSocialAsync();
    }
    private async void PublishEvent_Click(object sender, RoutedEventArgs args) => await ShowPublishEventAsync();
    private async Task ShowPublishEventAsync()
    {
        if (_dialog != null || !RequireLogin()) return;
        var track = _playback.Current;
        var input = new TextBox { PlaceholderText = "分享新鲜事", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 140, MinHeight = 140, MaxHeight = 240 };
        var share = new CheckBox { Content = track == null ? "分享正在播放的歌曲" : "分享歌曲：" + track.Title, IsEnabled = track != null };
        var emoji = new Button { Content = Services.IconFonts.Create("\uE899"), Style = (Style)Application.Current.Resources["BareButtonStyle"], MinWidth = 44, MinHeight = 44 };
        emoji.Click += (_, _) => ShowEmojiPicker(emoji, input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, Width = Math.Max(240, Math.Min(440, Root.ActualWidth - 120)) };
        panel.Children.Add(input); panel.Children.Add(emoji); panel.Children.Add(share); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "发布动态", Content = panel, PrimaryButtonText = "发布", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        void Update() => dialog.IsPrimaryButtonEnabled = !_publishingEvent && (input.Text.Trim().Length > 0 || share.IsChecked == true);
        input.TextChanged += (_, _) => Update(); share.Checked += (_, _) => Update(); share.Unchecked += (_, _) => Update();
        using var load = new CancellationTokenSource();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (_publishingEvent) { args.Cancel = true; return; }
            _publishingEvent = true; Update(); var deferral = args.GetDeferral();
            try
            {
                var response = await _api.CallAsync("share_resource", new { type = share.IsChecked == true ? "song" : "noresource", id = share.IsChecked == true ? track?.Id.ToString() ?? "" : "", msg = input.Text.Trim() }, load.Token);
                if (response.Number("code") != 200) throw new InvalidOperationException("发布失败，请重试。");
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex is OperationCanceledException ? "发布已取消" : ex.Message; }
            finally { _publishingEvent = false; Update(); deferral.Complete(); }
        };
        ContentDialogResult result;
        _dialog = dialog;
        try { result = await ShowThemedDialogAsync(dialog); }
        finally { load.Cancel(); _dialog = null; }
        if (result == ContentDialogResult.Primary)
        {
            var selectionRefresh = _view == "events" && EventScope.SelectedIndex != 1;
            EventScope.SelectedIndex = 1; _myEvents = true;
            if (_view != "events") await NavigateAsync("events");
            else if (!selectionRefresh) await RefreshSocialAsync();
            ShowPlayerMessage("已发布", InfoBarSeverity.Success);
        }
    }
    private async void SocialTrack_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not SocialItem { SharedTrack: { } track }) return;
        try { await _playback.PlayNextAsync(track); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void SocialPhotoButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: string url } button) return;
        for (var parent = VisualTreeHelper.GetParent(button); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement { DataContext: SocialItem item }) { await ShowEventPicturesAsync(item.Pictures, item.Pictures.IndexOf(url)); return; }
    }
}
