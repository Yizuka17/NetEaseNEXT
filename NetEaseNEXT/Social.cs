using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private string _messageCategory = "notices";
    private bool _sendingPrivate;
    private readonly HashSet<string> _socialWrites = [];
    private static bool Flag(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static JsonElement Object(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
    private static string FirstText(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static (int Type, long Id, string Key) ResourceFromThread(string thread)
    {
        var prefixes = new[] { "R_SO_4_", "R_MV_5_", "A_PL_0_", "R_AL_3_", "A_DJ_1_", "R_VI_62_", "A_EV_2_", "A_DR_14_" };
        for (var i = 0; i < prefixes.Length; i++)
            if (thread.StartsWith(prefixes[i], StringComparison.Ordinal)) { var key = thread[prefixes[i].Length..]; return (i, long.TryParse(i == 6 ? key.Split('_')[0] : key, out var id) ? id : 0, key); }
        return (-1, 0, "");
    }
    private static string SharedDescription(JsonElement e)
    {
        foreach (var key in new[] { "song", "album", "playlist", "program", "resource", "event" })
        {
            var shared = Object(e, key);
            var text = FirstText(shared.Text("name"), shared.Text("title"), shared.Text("content"));
            if (text.Length > 0) return text;
        }
        return "";
    }
    private static string PictureFromPayload(JsonElement p) => FirstText(p.Text("picUrl"), p.Text("imageUrl"), Object(p, "picInfo").Text("picUrl"), Object(p, "pic").Text("url"));
    private static SocialItem ParseSocialItem(JsonElement item, string view, string category, long accountId)
    {
        var payload = Payload(item, view == "events" || (view == "messages" && category == "mentions") ? "json" : view == "messages" ? "notice" : view == "private" ? "lastMsg" : "msg");
        if (payload.ValueKind != JsonValueKind.Object) payload = item;
        var user = view == "events" ? Object(item, "user") : Object(item, "fromUser");
        if (view == "private") user = ConversationPeer(item, accountId);
        if (view == "messages") user = Object(payload, "user").ValueKind == JsonValueKind.Object ? Object(payload, "user") : FirstObject(Object(item, "user"), user);
        var info = Object(item, "info"); var comment = Object(payload, "comment"); var reply = FirstObject(Object(payload, "replyComment"), Object(item, "beRepliedComment"));
        var sourceEvent = FirstObject(Payload(payload, "event"), Payload(payload, "track"));
        var sourceResource = FirstObject(Payload(item, "resourceJson"), Object(payload, "resource"));
        sourceEvent = FirstObject(sourceEvent, Payload(sourceResource, "event"));
        if (view == "messages" && user.ValueKind != JsonValueKind.Object) user = Object(comment, "user");
        var thread = FirstText(item.Text("threadId"), info.Text("threadId"), payload.Text("threadId"), comment.Text("threadId"), Object(item, "resource").Text("threadId"), sourceResource.Text("threadId"), sourceEvent.Text("threadId"), Object(sourceEvent, "info").Text("threadId"));
        if (thread.Length == 0 && sourceEvent.Number("id") > 0)
        {
            var owner = Math.Max(sourceEvent.Number("userId"), Object(sourceEvent, "user").Number("userId"));
            if (owner == 0) owner = accountId;
            if (owner > 0) thread = $"A_EV_2_{sourceEvent.Number("id")}_{owner}";
        }
        if (view == "events" && thread.Length == 0 && item.Number("id") > 0 && user.Number("userId") > 0) thread = $"A_EV_2_{item.Number("id")}_{user.Number("userId")}";
        var resource = ResourceFromThread(thread);
        if (resource.Type == 6 && sourceEvent.ValueKind != JsonValueKind.Object) sourceEvent = sourceResource;
        var text = view == "messages" ? FirstText(payload.Text("content"), payload.Text("msg"), comment.Text("content"), item.Text("content"), SharedDescription(payload)) : MessageText(payload);
        var original = FirstText(item.Text("beRepliedContent"), reply.Text("content"), Object(payload, "originalComment").Text("content"), SharedDescription(payload), Payload(item, "resourceJson").Text("name"));
        if (original == text) original = "";
        var picture = PictureFromPayload(payload);
        if (view == "events" && item.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array && pics.GetArrayLength() > 0) picture = pics[0].Text("originUrl");
        var general = Object(payload, "generalNotice");
        if (text.Length == 0) text = general.Text("content");
        var action = view != "messages" ? "" : category == "comments" ? "回复了你的评论" : category == "mentions" ? "提到了你" : comment.ValueKind == JsonValueKind.Object ? "赞了你的评论" : Object(payload, "playlist").ValueKind == JsonValueKind.Object ? "收藏了你的歌单" : FirstText(general.Text("actionDesc"), payload.Text("title"), "通知");
        SocialItem? linkedEvent = null;
        if (view == "messages" && sourceEvent.ValueKind == JsonValueKind.Object)
        {
            linkedEvent = ParseSocialItem(sourceEvent, "events", category, accountId);
            if (linkedEvent.ThreadId.Length == 0) { linkedEvent.ThreadId = thread; linkedEvent.ResourceType = resource.Type; linkedEvent.ResourceId = resource.Id; linkedEvent.ResourceKey = resource.Key; }
            if (category == "notices" && comment.ValueKind != JsonValueKind.Object) action = FirstText(general.Text("actionDesc"), "赞了你的动态");
            original = FirstText(linkedEvent.Text == "[消息]" ? "" : linkedEvent.Text, original);
            if (text.Length == 0) { text = FirstText(original, "你的动态收到新的赞"); original = ""; }
        }
        if (view == "messages" && text.Length == 0) text = FirstText(general.Text("content"), action, "通知");
        var commentId = comment.Number("commentId"); if (commentId == 0) commentId = item.Number("commentId");
        return new SocialItem
        {
            Id = item.Number("id"), Name = user.Text("nickname"), Avatar = user.Text("avatarUrl"), Text = text.Length > 0 ? text : "[消息]", OriginalText = original,
            Picture = picture, Pictures = EventPictures(item), SharedTrack = SharedEventTrack(payload), LinkedEvent = linkedEvent,
            Time = DateText(view == "events" ? item.Number("eventTime") : item.Number(view == "private" ? "lastMsgTime" : "time")), Timestamp = view == "events" ? item.Number("eventTime") : item.Number(view == "private" ? "lastMsgTime" : "time"), PeerId = view == "private" ? user.Number("userId") : 0,
            Kind = view == "events" ? "event" : view == "messages" ? "notice" : "message", ThreadId = thread, ResourceId = resource.Id, ResourceKey = resource.Key, ResourceType = resource.Type,
            Unread = PayloadIsUnread(item), UnreadCount = Math.Max(item.Number("newMsgCount"), item.Number("unreadCount")), CommentId = commentId, Liked = Flag(info, "liked"), LikeCount = info.Number("likedCount"), CommentCount = info.Number("commentCount"), Action = action
        };
    }
    private static JsonElement FirstObject(JsonElement first, JsonElement second) => first.ValueKind == JsonValueKind.Object ? first : second;
    private async void MessageCategory_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _view != "messages") return;
        _messageCategory = Tag(MessageCategory); await RefreshSocialAsync();
    }
    private async Task RefreshSocialAsync()
    {
        _offset = 0; _socialCursor = -1; ResetInbox(); SocialItems.Clear(); await LoadPageAsync(false);
    }
    private async void SocialLike_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is SocialItem comment && comment.Kind == "comment") { if (comment.LikeAsync != null) await comment.LikeAsync(); return; }
        if ((sender as FrameworkElement)?.Tag is not SocialItem item || item.ThreadId.Length == 0 || !_socialWrites.Add(item.ThreadId)) return;
        try
        {
            await _api.CallAsync("resource_like", new { type = 6, threadId = item.ThreadId, t = item.Liked ? 0 : 1 });
            item.Liked = !item.Liked; item.LikeCount = Math.Max(0, item.LikeCount + (item.Liked ? 1 : -1)); item.NotifyInteraction();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _socialWrites.Remove(item.ThreadId); }
    }
    private async void SocialComment_Click(object sender, RoutedEventArgs args) { if ((sender as FrameworkElement)?.Tag is SocialItem item) { if (item.Kind == "comment") item.Reply?.Invoke(); else await ShowCommentsAsync(item); } }
    private void PrivateEmoji_Click(object sender, RoutedEventArgs args) => ShowEmojiPicker((FrameworkElement)sender, PrivateText);
    private void ShowEmojiPicker(FrameworkElement anchor, TextBox input)
    {
        var grid = new GridView { ItemsSource = Services.Emojis.Items.Select(e => e.Glyph).ToArray(), SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxWidth = 320, MaxHeight = 260 };
        grid.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='44' ItemHeight='44'/></ItemsPanelTemplate>");
        var flyout = new Flyout { Content = grid };
        grid.ItemClick += (_, e) =>
        {
            var emoji = Services.Emojis.Items.First(x => x.Glyph == (string)e.ClickedItem);
            var start = input.SelectionStart;
            input.Text = input.Text.Remove(start, input.SelectionLength).Insert(start, emoji.Token);
            input.SelectionStart = start + emoji.Token.Length; input.SelectionLength = 0;
            flyout.Hide(); input.Focus(FocusState.Programmatic);
        };
        flyout.ShowAt(anchor);
    }
    private async Task ShowMessageDetailAsync(SocialItem item)
    {
        if (_dialog != null) return;
        var text = string.Join("\n\n", new[] { item.Action, item.Text, item.OriginalText, item.Time }.Where(s => s.Length > 0));
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = item.Name.Length > 0 ? item.Name : "消息", Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false }, MaxHeight = 480 }, CloseButtonText = "关闭", SecondaryButtonText = SourceLinkLabel(item) };
        ContentDialogResult result;
        _dialog = dialog; try { result = await dialog.ShowAsync(); } finally { _dialog = null; }
        if (result == ContentDialogResult.Secondary) await OpenMessageSourceAsync(item);
    }
    private async Task ShowCommentsAsync(SocialItem source)
    {
        if (_dialog != null) return;
        if (source.ThreadId.Length == 0 || source.ResourceType < 0) { await ShowMessageDetailAsync(source); return; }
        var items = new ObservableCollection<SocialItem>();
        var list = new ListView { ItemsSource = items, ItemTemplate = SocialList.ItemTemplate, SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = Math.Clamp(Root.ActualHeight - 380, 200, 480) };
        AttachWheelFallback(list);
        var input = new TextBox { PlaceholderText = "评论", MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
        var hint = new TextBlock { Text = source.CommentId > 0 ? "回复评论" : "", Opacity = .6, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var more = new Button { Content = "加载更多", Style = (Style)Application.Current.Resources["MoreButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 12) };
        list.Footer = more;
        var cancelReply = new Button { Content = "取消回复", Style = (Style)Application.Current.Resources["BareButtonStyle"], Visibility = source.CommentId > 0 ? Visibility.Visible : Visibility.Collapsed };
        var emoji = new Button { Content = new FontIcon { Glyph = "\uE899", FontFamily = Services.IconFonts.Symbols }, Style = (Style)Application.Current.Resources["BareButtonStyle"] };
        emoji.Click += (_, _) => ShowEmojiPicker(emoji, input);
        var replyBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; replyBar.Children.Add(hint); replyBar.Children.Add(cancelReply); replyBar.Children.Add(emoji);
        var content = new StackPanel { MaxWidth = 500, Width = Math.Max(240, Math.Min(500, Root.ActualWidth - 160)), Spacing = 10 };
        if (source.Kind == "notice") content.Children.Add(new TextBlock { Text = Services.Emojis.Display(string.Join("\n", new[] { source.Action, source.Text, source.OriginalText }.Where(s => s.Length > 0))), TextWrapping = TextWrapping.Wrap, MaxHeight = 100 });
        content.Children.Add(list); content.Children.Add(replyBar); content.Children.Add(input); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "评论", Content = content, PrimaryButtonText = "发送", CloseButtonText = "关闭", IsPrimaryButtonEnabled = false, SecondaryButtonText = source.Kind == "notice" ? SourceLinkLabel(source) : "" };
        _dialog = dialog; using var load = new CancellationTokenSource(); _commentsLoad = load;
        var offset = 0; long replyId = source.CommentId; var loading = false;
        async Task Fetch()
        {
            if (loading || load.IsCancellationRequested) return; loading = true; more.IsEnabled = false;
            try
            {
                var method = source.ResourceType switch { 0 => "comment_music", 1 => "comment_mv", 2 => "comment_playlist", 3 => "comment_album", 4 => "comment_dj", 5 => "comment_video", _ => "comment_event" };
                var result = await _api.CallAsync(method, new { id = source.ResourceKey, threadId = source.ThreadId, limit = 20, offset }, load.Token);
                load.Token.ThrowIfCancellationRequested();
                dialog.Title = "评论 (" + result.Number("total").ToString("N0") + ")";
                if (result.TryGetProperty("comments", out var array))
                {
                    foreach (var c in array.EnumerateArray())
                    {
                        var user = Object(c, "user"); var original = "";
                        if (c.TryGetProperty("beReplied", out var replies) && replies.ValueKind == JsonValueKind.Array) original = string.Join("\n", replies.EnumerateArray().Select(r => Object(r, "user").Text("nickname") + ": " + r.Text("content")));
                        var comment = new SocialItem { Kind = "comment", Name = user.Text("nickname"), Avatar = user.Text("avatarUrl"), Text = c.Text("content"), OriginalText = original, Time = DateText(c.Number("time")), CommentId = c.Number("commentId"), Liked = Flag(c, "liked"), LikeCount = c.Number("likedCount") };
                        comment.Reply = () => { replyId = comment.CommentId; hint.Text = "回复 " + comment.Name; cancelReply.Visibility = Visibility.Visible; input.Focus(FocusState.Programmatic); };
                        comment.LikeAsync = async () =>
                        {
                            var key = source.ThreadId + ":" + comment.CommentId;
                            if (!_socialWrites.Add(key)) return;
                            try { await _api.CallAsync("comment_like", new { type = source.ResourceType, id = source.ResourceKey, threadId = source.ThreadId, cid = comment.CommentId, t = comment.Liked ? 0 : 1 }, load.Token); comment.Liked = !comment.Liked; comment.LikeCount = Math.Max(0, comment.LikeCount + (comment.Liked ? 1 : -1)); comment.NotifyInteraction(); }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) { error.Text = ex.Message; }
                            finally { _socialWrites.Remove(key); }
                        };
                        items.Add(comment);
                    }
                    offset += array.GetArrayLength();
                    more.Visibility = (Flag(result, "more") || offset < result.Number("total")) && items.Count < 500 ? Visibility.Visible : Visibility.Collapsed;
                }
                error.Text = "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { loading = false; more.IsEnabled = true; }
        }
        cancelReply.Click += (_, _) => { replyId = 0; hint.Text = ""; cancelReply.Visibility = Visibility.Collapsed; };
        more.Click += async (_, _) => await Fetch();
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral();
            try
            {
                await _api.CallAsync("comment", new { t = replyId > 0 ? 2 : 1, type = source.ResourceType, id = source.ResourceKey, threadId = source.ThreadId, commentId = replyId, content = input.Text.Trim() }, load.Token);
                input.Text = ""; replyId = 0; hint.Text = ""; cancelReply.Visibility = Visibility.Collapsed;
                source.CommentCount++; source.NotifyInteraction(); items.Clear(); offset = 0; await Fetch();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { deferral.Complete(); }
        };
        dialog.Opened += async (_, _) => await Fetch(); dialog.Closed += (_, _) => load.Cancel();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); } finally { load.Cancel(); if (ReferenceEquals(_commentsLoad, load)) _commentsLoad = null; _dialog = null; }
        if (result == ContentDialogResult.Secondary) await OpenMessageSourceAsync(source);
    }
    private CancellationTokenSource? _commentsLoad;
    private async void SendPrivate_Click(object sender, RoutedEventArgs args)
    {
        var text = PrivateText.Text.Trim(); var peer = _peerId;
        if (_sendingPrivate || text.Length == 0 || peer <= 0 || _view != "conversation") return;
        _sendingPrivate = true; SendPrivateButton.IsEnabled = false;
        try
        {
            await _api.CallAsync("send_text", new { user_ids = peer.ToString(), msg = text });
            if (_view == "conversation" && _peerId == peer) { if (PrivateText.Text.Trim() == text) PrivateText.Text = ""; await RefreshSocialAsync(); }
            ShowPlayerMessage("已发送", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _sendingPrivate = false; SendPrivateButton.IsEnabled = true; }
    }
    private async void SharePrivate_Click(object sender, RoutedEventArgs args)
    {
        if (_dialog != null || _peerId <= 0) return; var peer = _peerId;
        var type = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "歌曲", "专辑", "歌单" }, SelectedIndex = 0 };
        var id = new TextBox { Header = "ID", Text = _playback.Current?.Id.ToString() ?? "", InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } } } };
        var message = new TextBox { PlaceholderText = "消息", Text = PrivateText.Text, MaxLength = 2000 }; var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, Width = 360 }; panel.Children.Add(type); panel.Children.Add(id); panel.Children.Add(message); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "分享", Content = panel, PrimaryButtonText = "发送", CloseButtonText = "取消" };
        dialog.PrimaryButtonClick += async (_, eventArgs) =>
        {
            if (!long.TryParse(id.Text, out var resource) || resource <= 0) { error.Text = "请输入有效 ID"; eventArgs.Cancel = true; return; }
            var deferral = eventArgs.GetDeferral();
            try
            {
                var method = type.SelectedIndex == 2 ? "send_playlist" : type.SelectedIndex == 1 ? "send_album" : "send_song";
                await _api.CallAsync(method, new { user_ids = peer.ToString(), id = resource, playlist = resource, msg = message.Text.Trim() });
            }
            catch (Exception ex) { error.Text = ex.Message; eventArgs.Cancel = true; }
            finally { deferral.Complete(); }
        };
        _dialog = dialog;
        try { if (await dialog.ShowAsync() == ContentDialogResult.Primary && _view == "conversation" && _peerId == peer) await RefreshSocialAsync(); }
        finally { _dialog = null; }
    }
}
