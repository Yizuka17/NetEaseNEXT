using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using System.Text.Json;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task ShowCommentsAsync(SocialItem source, ApiClient? client = null)
    {
        var api = client ?? _api;
        if (_dialog != null) return;
        if (source.ThreadId.Length == 0 || source.ResourceType < 0) { await ShowMessageDetailAsync(source); return; }
        var items = new ObservableCollection<SocialItem>(); var posted = new List<SocialItem>();
        var list = new ListView { ItemsSource = items, ItemTemplate = SocialList.ItemTemplate, SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AttachWheelFallback(list);
        var input = new TextBox { PlaceholderText = "评论", MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 100 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { Text = source.CommentId > 0 ? "回复评论" : "", Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        var sort = new ComboBox { ItemsSource = new[] { "最新", "最热" }, SelectedIndex = 0, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left };
        var send = new Button { Content = "发送", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinHeight = 44, MinWidth = 64, IsEnabled = false, VerticalAlignment = VerticalAlignment.Bottom };
        var close = new Button { Content = IconFonts.Create("\uE711"), Style = (Style)Application.Current.Resources["BareButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "关闭评论");
        var title = new TextBlock { Text = "评论", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var heading = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.Children.Add(title); Grid.SetColumn(close, 1); heading.Children.Add(close);
        var cancelReply = new Button { Content = "取消回复", Style = (Style)Application.Current.Resources["BareButtonStyle"], Visibility = source.CommentId > 0 ? Visibility.Visible : Visibility.Collapsed };
        var emoji = new Button { Content = IconFonts.Create("\uE899"), Style = (Style)Application.Current.Resources["BareButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(emoji, "表情"); emoji.Click += (_, _) => ShowEmojiPicker(emoji, input);
        var replyBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; replyBar.Children.Add(emoji); replyBar.Children.Add(hint); replyBar.Children.Add(cancelReply);
        var composer = new Grid { ColumnSpacing = 8 }; composer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); composer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); composer.Children.Add(input); Grid.SetColumn(send, 1); composer.Children.Add(send);
        var content = new Grid { RowSpacing = 12 };
        for (var row = 0; row < 6; row++) content.RowDefinitions.Add(new() { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var toolbar = new Grid(); toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.Children.Add(sort);
        var elements = new FrameworkElement[] { heading, toolbar, list, replyBar, composer, error };
        for (var row = 0; row < elements.Length; row++) { Grid.SetRow(elements[row], row); content.Children.Add(elements[row]); }
        var progress = new ProgressRing { IsActive = false, Width = 24, Height = 24, Visibility = Visibility.Collapsed, Margin = new Thickness(8) };
        var retry = new Button { Content = "重试", HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        var footer = new StackPanel(); footer.Children.Add(progress); footer.Children.Add(retry); list.Footer = footer;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = content };
        var openSource = false;
        if (source.Kind == "notice" && SourceLinkLabel(source) is { Length: > 0 } sourceLabel)
        {
            var sourceButton = new Button { Content = sourceLabel, Style = (Style)Application.Current.Resources["BareButtonStyle"] };
            Grid.SetColumn(sourceButton, 1); toolbar.Children.Add(sourceButton); sourceButton.Click += (_, _) => { openSource = true; dialog.Hide(); };
        }
        void Resize()
        {
            content.Width = Math.Max(200, Math.Min(560, Root.ActualWidth - 112));
            content.Height = Math.Max(200, Math.Min(640, Root.ActualHeight - 160));
            dialog.Resources["ContentDialogMaxWidth"] = content.Width + 48;
            dialog.MaxHeight = Math.Max(248, Root.ActualHeight - 48);
        }
        void Resized(object sender, SizeChangedEventArgs args) => Resize();
        Resize(); Root.SizeChanged += Resized;
        close.Click += (_, _) => dialog.Hide(); _dialog = dialog;
        using var load = new CancellationTokenSource(); _commentsLoad = load;
        CancellationTokenSource? fetch = null; var generation = 0; var page = 1; var cursor = "0"; var more = true; var loading = false; var sending = false; long replyId = source.CommentId;
        SocialItem Parse(JsonElement c)
        {
            var user = Object(c, "user");
            var original = c.TryGetProperty("beReplied", out var replies) && replies.ValueKind == JsonValueKind.Array ? string.Join("\n", replies.EnumerateArray().Select(r => Object(r, "user").Text("nickname") + ": " + r.Text("content"))) : "";
            var comment = new SocialItem { Kind = "comment", Name = user.Text("nickname"), Avatar = user.Text("avatarUrl"), Text = c.Text("content"), OriginalText = original, Time = DateText(c.Number("time")), Timestamp = c.Number("time"), CommentId = c.Number("commentId"), Liked = Flag(c, "liked"), LikeCount = c.Number("likedCount") };
            comment.Reply = () => { if (comment.CommentId <= 0) return; replyId = comment.CommentId; hint.Text = "回复 " + comment.Name; cancelReply.Visibility = Visibility.Visible; input.Focus(FocusState.Programmatic); };
            comment.LikeAsync = async () =>
            {
                if (comment.CommentId <= 0) return;
                var key = source.ThreadId + ":" + comment.CommentId; if (!_socialWrites.Add(key)) return;
                try { await api.CallAsync("comment_like", new { type = source.ResourceType, id = source.ResourceKey, threadId = source.ThreadId, cid = comment.CommentId, t = comment.Liked ? 0 : 1 }, load.Token); comment.Liked = !comment.Liked; comment.LikeCount = Math.Max(0, comment.LikeCount + (comment.Liked ? 1 : -1)); comment.NotifyInteraction(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error.Text = ex.Message; }
                finally { _socialWrites.Remove(key); }
            };
            return comment;
        }
        async Task Fetch(bool reset = false)
        {
            if (load.IsCancellationRequested || loading && !reset || !more && !reset) return;
            if (reset) { generation++; fetch?.Cancel(); page = 1; cursor = "0"; more = true; }
            var version = generation; var requestPage = page;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(load.Token); fetch = request;
            loading = true; progress.IsActive = true; progress.Visibility = Visibility.Visible; retry.Visibility = Visibility.Collapsed;
            try
            {
                var result = await api.CallAsync("comment_new", new { id = source.ResourceKey, type = source.ResourceType, threadId = source.ThreadId, pageNo = requestPage, pageSize = 20, sortType = sort.SelectedIndex == 1 ? 2 : 3, cursor }, request.Token);
                request.Token.ThrowIfCancellationRequested(); if (version != generation) return;
                var data = Object(result, "data"); if (data.ValueKind != JsonValueKind.Object) data = result;
                var comments = data.GetProperty("comments").EnumerateArray().Select(Parse).ToArray();
                title.Text = "评论 (" + Math.Max(data.Number("totalCount"), data.Number("total")).ToString("N0") + ")";
                if (reset) items.Clear();
                foreach (var own in posted.ToArray()) if (comments.Any(c => c.CommentId == own.CommentId || own.CommentId <= 0 && c.Text == own.Text && c.Name == own.Name)) posted.Remove(own);
                if (requestPage == 1) foreach (var own in posted) if (!items.Contains(own)) items.Add(own);
                var ids = items.Select(c => c.CommentId).ToHashSet(); var added = 0; foreach (var comment in comments) if (ids.Add(comment.CommentId)) { items.Add(comment); added++; }
                cursor = data.TryGetProperty("cursor", out var nextCursor) ? nextCursor.ToString() : comments.LastOrDefault()?.Timestamp.ToString() ?? "0";
                page++; more = Flag(data, "hasMore") && comments.Length > 0 && (requestPage == 1 || added > 0); error.Text = "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == generation) { error.Text = ex.Message; retry.Visibility = Visibility.Visible; } }
            finally { if (version == generation) { loading = false; progress.IsActive = false; progress.Visibility = Visibility.Collapsed; } if (ReferenceEquals(fetch, request)) fetch = null; }
        }
        void AtBottom()
        {
            if (!loading && more && retry.Visibility != Visibility.Visible && FindDescendant<ScrollViewer>(list) is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset < 160) _ = Fetch();
        }
        list.Loaded += (_, _) => { if (FindDescendant<ScrollViewer>(list) is { } scroll) scroll.ViewChanged += (_, _) => AtBottom(); };
        list.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AtBottom()), true);
        retry.Click += async (_, _) => await Fetch(); sort.SelectionChanged += async (_, _) => await Fetch(true);
        cancelReply.Click += (_, _) => { replyId = 0; hint.Text = ""; cancelReply.Visibility = Visibility.Collapsed; };
        input.TextChanged += (_, _) => send.IsEnabled = !sending && !string.IsNullOrWhiteSpace(input.Text);
        send.Click += async (_, _) =>
        {
            if (sending || string.IsNullOrWhiteSpace(input.Text)) return;
            var text = input.Text.Trim(); var replying = replyId; sending = true; send.IsEnabled = false; sort.IsEnabled = false; input.IsReadOnly = true;
            try
            {
                var result = await api.CallAsync("comment", new { t = replying > 0 ? 2 : 1, type = source.ResourceType, id = source.ResourceKey, threadId = source.ThreadId, commentId = replying, content = text }, load.Token);
                var saved = Object(result, "comment"); if (saved.ValueKind != JsonValueKind.Object) saved = Object(Object(result, "data"), "comment");
                var own = saved.ValueKind == JsonValueKind.Object ? Parse(saved) : new SocialItem { Kind = "comment", Name = AccountName.Text, Text = text, Time = DateText(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), CommentId = -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                if (own.Name.Length == 0) own.Name = AccountName.Text;
                if (own.Text.Length == 0) own.Text = text;
                if (own.Time.Length == 0) own.Time = DateText(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                posted.Insert(0, own); items.Insert(0, own); list.ScrollIntoView(own);
                input.Text = ""; replyId = 0; hint.Text = ""; cancelReply.Visibility = Visibility.Collapsed; source.CommentCount++; source.NotifyInteraction(); error.Text = "";
                await Fetch(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { sending = false; send.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); sort.IsEnabled = true; input.IsReadOnly = false; }
        };
        dialog.Opened += async (_, _) => await Fetch(true); dialog.Closed += (_, _) => load.Cancel();
        try { await ShowThemedDialogAsync(dialog); }
        finally { Root.SizeChanged -= Resized; load.Cancel(); fetch?.Cancel(); if (ReferenceEquals(_commentsLoad, load)) _commentsLoad = null; _dialog = null; }
        if (openSource) await OpenMessageSourceAsync(source);
    }
}
