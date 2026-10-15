using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Storage.Streams;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _authenticated;
    private long _accountId;
    private long _socialCursor = -1;
    private Task<JsonElement?>? _profileTask;
    private DateTimeOffset _profileExpires;
    private string _profileSession = "";
    private CancellationTokenSource? _accountLoad;
    private CancellationTokenSource? _accountAvatarLoad;
    private string _accountAvatarUrl = "", _accountAvatarSession = "";
    private async Task<JsonElement?> GetProfileAsync(CancellationToken ct = default)
    {
        if (_profileTask == null || _profileSession != _api.Cookie || _profileExpires <= DateTimeOffset.UtcNow || _profileTask.IsCanceled || _profileTask.IsFaulted)
        {
            _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            _profileTask = FetchProfileAsync(ct);
        }
        return await _profileTask.WaitAsync(ct);
    }
    private async Task<JsonElement?> FetchProfileAsync(CancellationToken ct)
    {
        var result = await _api.CallCachedAsync("login_status", cancellation: ct);
        return result.GetProperty("data").TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object ? profile : null;
    }
    private void ApplyAccountProfile(JsonElement user)
    {
        _authenticated = true; _accountId = user.Number("userId");
        AccountName.Text = user.Text("nickname"); AccountAvatar.DisplayName = AccountName.Text;
        _ = LoadAccountAvatarAsync(user.Text("avatarUrl"));
    }
    private async Task LoadAccountAvatarAsync(string url)
    {
        if (_hidden || _exiting || url.Length == 0) return;
        var session = _api.Cookie;
        if (_accountAvatarUrl == url && _accountAvatarSession == session && (_accountAvatarLoad is { IsCancellationRequested: false } || AccountAvatar.ProfilePicture != null)) return;
        _accountAvatarLoad?.Cancel();
        using var load = new CancellationTokenSource(); _accountAvatarLoad = load;
        _accountAvatarUrl = url; _accountAvatarSession = session;
        try
        {
            var file = await Task.Run(() => Artwork.GetFileAsync(url, 128, load.Token), load.Token);
            load.Token.ThrowIfCancellationRequested();
            if (!_hidden && !_exiting && session == _api.Cookie && _accountAvatarUrl == url) AccountAvatar.ProfilePicture = new BitmapImage(new Uri(file));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex); }
        finally { if (ReferenceEquals(_accountAvatarLoad, load)) _accountAvatarLoad = null; }
    }
    private async Task UpdateAccountAsync()
    {
        if (_hidden || _exiting) return;
        if (_accountLoad is { IsCancellationRequested: false }) return;
        using var load = new CancellationTokenSource(); _accountLoad = load; var session = _api.Cookie;
        try
        {
            var profile = await GetProfileAsync(load.Token);
            if (_hidden || _exiting || session != _api.Cookie) return;
            _authenticated = profile != null;
            if (profile is JsonElement user)
            {
                ApplyAccountProfile(user);
                if (!_runningSmokeTest) await Task.Run(() => AccountCache.Save(session, user), load.Token);
                if (!_restoringPlayback && _playback.Current is Track track) { _qualityTrack = 0; _ = LoadQualityAsync(track); _ = LoadSongEngagementAsync(track); }
                if (!_hidden) { _inboxTimer.Start(); _ = CheckUnreadAsync(); }
            }
            else { if (!_runningSmokeTest) AccountCache.Clear(); ResetAccount(); }
        }
        catch { if (!_authenticated) ResetAccount(); }
        finally { if (ReferenceEquals(_accountLoad, load)) _accountLoad = null; }
    }
    private void ResetAccount() { _accountAvatarLoad?.Cancel(); _accountAvatarUrl = _accountAvatarSession = ""; _authenticated = false; _accountId = 0; _inboxTimer.Stop(); _inboxCheck?.Cancel(); _readAllLoad?.Cancel(); _unreadCount = _messageUnreadCount = _privateUnreadCount = 0; _unreadByCategory.Clear(); _pendingUnread.Clear(); UnreadBadge.Visibility = Visibility.Collapsed; ResetInbox(); _songLiked = _songLikeKnown = false; UpdateSongActions(); AccountName.Text = "未登录"; AccountAvatar.ProfilePicture = null; AccountAvatar.DisplayName = ""; }
    private static JsonElement ConversationPeer(JsonElement item, long accountId)
    {
        if (!item.TryGetProperty("fromUser", out var from)) return default;
        return from.Number("userId") == accountId && item.TryGetProperty("toUser", out var to) ? to : from;
    }
    private async void Account_Click(object sender, RoutedEventArgs args)
    {
        if (!_authenticated && _api.HasSession) await UpdateAccountAsync();
        if (!_authenticated) { await ShowLoginAsync(); return; }
        var menu = new MenuFlyout();
        foreach (var (view, text) in new[] { ("messages", "消息"), ("private", "私信") })
        { var count = view == "messages" ? _messageUnreadCount : _privateUnreadCount; var item = new MenuFlyoutItem { Text = text + (count > 0 ? " (" + count + ")" : "") }; item.Click += async (_, _) => await NavigateAsync(view); menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator());
        var logout = new MenuFlyoutItem { Text = "退出登录" }; logout.Click += Logout_Click; menu.Items.Add(logout); menu.ShowAt(AccountButton);
    }
    private static JsonElement Payload(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return default;
        if (value.ValueKind == JsonValueKind.Object) return value;
        try { using var document = JsonDocument.Parse(value.GetString() ?? "{}"); return document.RootElement.Clone(); } catch { return default; }
    }
    private static string DateText(long value)
    {
        try { return value <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); } catch { return ""; }
    }
    private static string MessageText(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("msg", out var nested) && nested.ValueKind == JsonValueKind.Object) return MessageText(nested);
        var text = payload.Text("msg"); if (text.Length == 0) text = payload.Text("message");
        if (text.Length == 0) text = payload.Text("content");
        if (text.Length > 0) return text;
        foreach (var key in new[] { "song", "album", "playlist", "program" }) if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var shared)) return shared.Text("name");
        return "[消息]";
    }
    private async Task LoadSocialAsync(CancellationToken ct)
    {
        if (_view == "messages") { await LoadInboxAsync(ct); return; }
        if (_accountId == 0 && _view is "private" or "conversation") _accountId = (await GetProfileAsync(ct))?.Number("userId") ?? 0;
        JsonElement result; JsonElement array;
        if (_view == "events")
        {
            if (_myEvents && _accountId == 0) _accountId = (await GetProfileAsync(ct))?.Number("userId") ?? 0;
            if (_myEvents && _accountId == 0) throw new InvalidOperationException("请重新登录。");
            result = _myEvents ? await _api.CallAsync("user_event", new { uid = _accountId, limit = 20, lasttime = _socialCursor }, ct) : await _api.CallAsync("event", new { pagesize = 20, lasttime = _socialCursor }, ct);
            array = result.GetProperty(_myEvents ? "events" : "event");
        }
        else if (_view == "private") { result = await _api.CallAsync("msg_private", new { limit = 30, offset = _offset }, ct); array = result.GetProperty("msgs"); }
        else { result = await _api.CallAsync("msg_private_history", new { uid = _peerId, limit = 30, before = _socialCursor < 0 ? 0 : _socialCursor }, ct); array = result.GetProperty("msgs"); }
        ct.ThrowIfCancellationRequested();
        foreach (var item in array.EnumerateArray())
        {
            var social = ParseSocialItem(item, _view, _messageCategory, _accountId);
            if (_view == "private") ApplyUnread(social, "private", false);
            SocialItems.Add(social);
            var time = item.Number(_view == "private" ? "lastMsgTime" : "time");
            if (_view == "events" && time == 0) time = item.Number("eventTime");
            if (time > 0) _socialCursor = _socialCursor <= 0 ? time : Math.Min(_socialCursor, time);
        }
        if (_view == "events" && result.Number("lasttime") > 0) _socialCursor = result.Number("lasttime");
        _offset += array.GetArrayLength();
        if (_view == "private") UpdateUnreadCounts();
        var more = result.TryGetProperty("more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True;
        SocialMore.Visibility = (more || array.GetArrayLength() == (_view == "events" ? 20 : 30)) && SocialItems.Count < 500 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Social_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not SocialItem item) return;
        MarkInboxRead(item);
        if (item.PeerId > 0) { _peerId = item.PeerId; _playlistName = item.Name; await NavigateAsync("conversation"); }
        else if (item.Kind == "event" || item.ThreadId.Length > 0) await ShowCommentsAsync(item);
        else await ShowMessageDetailAsync(item);
    }
    private async Task ShowLoginAsync()
    {
        if (_dialog != null) return;
        var image = new Image { Width = 224, Height = 224, Stretch = Stretch.Uniform };
        var label = new TextBlock { Text = "正在获取二维码…", TextWrapping = TextWrapping.Wrap, HorizontalTextAlignment = TextAlignment.Center };
        var content = new StackPanel { Spacing = 18, Width = 300 };
        content.Children.Add(image); content.Children.Add(label);
        content.Children.Add(new TextBlock { Text = "请使用网易云音乐手机客户端扫码，并在手机上确认登录。", TextWrapping = TextWrapping.Wrap, Opacity = .6 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "扫码登录", Content = content, CloseButtonText = "关闭", PrimaryButtonText = "刷新二维码", DefaultButton = ContentDialogButton.Close };
        _dialog = dialog;
        var signedIn = false;
        async Task GenerateAsync()
        {
            _loginLoad?.Cancel();
            using var load = new CancellationTokenSource(TimeSpan.FromMinutes(3)); _loginLoad = load;
            image.Source = null; label.Text = "正在获取二维码…";
            try
            {
                var keyResponse = await _api.CallAsync("login_qr_key", cancellation: load.Token);
                var key = keyResponse.GetProperty("data").Text("unikey");
                var qr = await _api.CallAsync("login_qr_create", new { key, qrimg = true }, load.Token);
                var encoded = qr.GetProperty("data").Text("qrimg");
                var bytes = Convert.FromBase64String(encoded[(encoded.IndexOf(',') + 1)..]);
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
                stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                load.Token.ThrowIfCancellationRequested(); image.Source = bitmap; label.Text = "等待扫码…";
                while (!load.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), load.Token);
                    var check = await _api.CallAsync("login_qr_check", new { key }, load.Token);
                    load.Token.ThrowIfCancellationRequested();
                    var code = check.GetProperty("code").GetInt32();
                    if (code == 800) { label.Text = "二维码已过期，请点击刷新。"; break; }
                    if (code == 802) label.Text = "扫码成功，请在手机上确认。";
                    if (code == 803)
                    {
                        var cookie = check.Text("cookie");
                        if (cookie.Length == 0) throw new InvalidOperationException("登录未返回会话，请刷新二维码重试。");
                        _api.SetCookie(cookie); signedIn = true; dialog.Hide(); break;
                    }
                }
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_loginLoad, load)) label.Text = "二维码已超时，请刷新。"; }
            catch (Exception e) { if (!load.IsCancellationRequested) label.Text = e.Message; }
            finally { if (ReferenceEquals(_loginLoad, load)) _loginLoad = null; }
        }
        dialog.Opened += async (_, _) => await GenerateAsync();
        dialog.PrimaryButtonClick += (sender, args) => { args.Cancel = true; _ = GenerateAsync(); };
        dialog.Closed += (_, _) => _loginLoad?.Cancel();
        try { await dialog.ShowAsync(); }
        catch (Exception e) { ShowError(e.Message); }
        finally { _loginLoad?.Cancel(); _dialog = null; }
        if (signedIn) { await UpdateAccountAsync(); if (_view is "library" or "events" or "messages" or "private") await NavigateAsync(_view, false); }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try { _accountLoad?.Cancel(); _profileTask = null; AccountCache.Clear(); _api.SetCookie(""); ResetAccount(); _qualityTrack = 0; if (_playback.Current is Track track) await LoadQualityAsync(track); if (_view == "library") await NavigateAsync("library"); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
