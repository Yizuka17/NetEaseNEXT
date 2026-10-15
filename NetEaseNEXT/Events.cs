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
    private static Track? SharedEventTrack(JsonElement payload)
    {
        var song = Object(payload, "song");
        if (song.Number("id") <= 0) return null;
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
        var emoji = new Button { Content = new SymbolIcon(Symbol.Emoji2), Style = (Style)Application.Current.Resources["BareButtonStyle"], MinWidth = 44, MinHeight = 44 };
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
        try { result = await dialog.ShowAsync(); }
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
    private async Task ShowEventPicturesAsync(IReadOnlyList<string> urls, int index)
    {
        if (_dialog != null || urls.Count == 0) return;
        index = Math.Clamp(index, 0, urls.Count - 1);
        var image = new Image { Stretch = Stretch.Uniform, Width = Math.Max(220, Math.Min(700, Root.ActualWidth - 120)), Height = Math.Max(180, Math.Min(500, Root.ActualHeight - 230)) };
        Artwork.SetSize(image, 800);
        var surface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = image };
        var scroll = new ScrollViewer { Content = surface, Width = image.Width, Height = image.Height, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 6, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsHorizontalRailEnabled = false, IsVerticalRailEnabled = false, IsHorizontalScrollChainingEnabled = false, IsVerticalScrollChainingEnabled = false, IsZoomChainingEnabled = false };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, CloseButtonText = "关闭" };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(scroll);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = new Button { Content = new SymbolIcon(Symbol.Back), MinWidth = 44, MinHeight = 44 };
        var next = new Button { Content = new SymbolIcon(Symbol.Forward), MinWidth = 44, MinHeight = 44 };
        var smaller = new Button { Content = "−", MinWidth = 44, MinHeight = 44 };
        var larger = new Button { Content = "+", MinWidth = 44, MinHeight = 44 };
        var reset = new Button { Content = "还原", MinHeight = 44 };
        void Zoom(float factor, Windows.Foundation.Point? anchor = null)
        {
            factor = Math.Clamp(factor, 1, 6);
            var point = anchor ?? new Windows.Foundation.Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
            var ratio = factor / scroll.ZoomFactor;
            scroll.ChangeView(Math.Max(0, (scroll.HorizontalOffset + point.X) * ratio - point.X), Math.Max(0, (scroll.VerticalOffset + point.Y) * ratio - point.Y), factor, true);
        }
        void Show()
        {
            dialog.Title = $"图片 {index + 1}/{urls.Count}"; previous.IsEnabled = index > 0; next.IsEnabled = index + 1 < urls.Count;
            scroll.ChangeView(0, 0, 1, true); image.Source = null;
            if (Uri.TryCreate(urls[index], UriKind.Absolute, out var uri) && uri.IsFile) image.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            else Artwork.SetUri(image, urls[index]);
        }
        previous.Click += (_, _) => { if (index > 0) { index--; Show(); } };
        next.Click += (_, _) => { if (index + 1 < urls.Count) { index++; Show(); } };
        smaller.Click += (_, _) => Zoom(scroll.ZoomFactor / 1.5f); larger.Click += (_, _) => Zoom(scroll.ZoomFactor * 1.5f);
        reset.Click += (_, _) => scroll.ChangeView(0, 0, 1);
        surface.DoubleTapped += (_, args) => { Zoom(scroll.ZoomFactor > 1.05 ? 1 : 2, args.GetPosition(scroll)); args.Handled = true; };
        surface.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(scroll);
            if (point.Properties.IsHorizontalMouseWheel) return;
            Zoom(scroll.ZoomFactor * MathF.Pow(1.2f, point.Properties.MouseWheelDelta / 120f), point.Position); args.Handled = true;
            if (_runningSmokeTest) Diagnostics.StartupTrace.Mark("Image viewer wheel delta: " + point.Properties.MouseWheelDelta);
        }), true);
        if (_runningSmokeTest) scroll.ViewChanged += (_, _) => Diagnostics.StartupTrace.Mark("Image viewer zoom: " + scroll.ZoomFactor);
        foreach (var button in new[] { previous, smaller, reset, larger, next }) controls.Children.Add(button);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previous, "上一张"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(next, "下一张");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(smaller, "缩小"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(larger, "放大");
        panel.Children.Add(controls); dialog.Content = panel; Show(); _dialog = dialog;
        void ResizeViewer(object sender, SizeChangedEventArgs args)
        {
            scroll.Width = image.Width = Math.Max(220, Math.Min(700, Root.ActualWidth - 120));
            scroll.Height = image.Height = Math.Max(180, Math.Min(500, Root.ActualHeight - 230));
        }
        Root.SizeChanged += ResizeViewer;
        try { await dialog.ShowAsync(); }
        finally { Root.SizeChanged -= ResizeViewer; Artwork.SetUri(image, ""); image.Source = null; _dialog = null; }
    }
}
