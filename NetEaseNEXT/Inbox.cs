using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using System.Text.Json;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private sealed class InboxPage { public long Cursor = -1; public int Offset; public bool More = true; }
    private readonly Dictionary<string, InboxPage> _inboxPages = new();
    private readonly HashSet<string> _inboxKeys = [];
    private readonly DispatcherTimer _inboxTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private CancellationTokenSource? _inboxCheck;
    private long _unreadCount, _messageUnreadCount, _privateUnreadCount;
    private readonly Dictionary<string, long> _unreadByCategory = new();
    private readonly Dictionary<string, (string Category, long Count)> _pendingUnread = new();
    private bool _markingAllRead;
    private CancellationTokenSource? _readAllLoad;
    private async void ReadAll_Click(object sender, RoutedEventArgs args)
    {
        if (_markingAllRead || !_api.HasSession) return;
        _markingAllRead = true; ReadAllButton.IsEnabled = false; _inboxCheck?.Cancel();
        using var load = new CancellationTokenSource(); _readAllLoad = load;
        try
        {
            if (_accountId == 0) _accountId = (await GetProfileAsync(load.Token))?.Number("userId") ?? 0;
            if (_accountId == 0) throw new InvalidOperationException("请重新登录。");
            var account = _accountId;
            var result = await _api.CallAsync("msg_read_all", cancellation: load.Token);
            if (result.Number("code") != 200) throw new InvalidOperationException("服务器未读同步失败，请重试。");
            if (_accountId != account || _exiting) return;
            ApplyServerReadAll();
            ShowPlayerMessage("已全部标记已读", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            _markingAllRead = false; ReadAllButton.IsEnabled = _api.HasSession;
            if (ReferenceEquals(_readAllLoad, load)) _readAllLoad = null;
            if (!_exiting && !_hidden) _ = CheckUnreadAsync();
        }
    }
    private void ApplyServerReadAll()
    {
        var keys = _pendingUnread.Keys.ToHashSet();
        foreach (var item in SocialItems.Where(i => i.InboxCategory is "notices" or "comments" or "mentions" or "private"))
        { if (item.Unread) keys.Add(ReadKey(item)); item.MarkRead(); }
        foreach (var key in keys)
        {
            if (!_settings.ReadInboxItems.Contains(key)) _settings.ReadInboxItems.Add(key);
            if (!_settings.AcknowledgedInboxItems.Contains(key)) _settings.AcknowledgedInboxItems.Add(key);
        }
        if (_settings.ReadInboxItems.Count > 1000) _settings.ReadInboxItems.RemoveRange(0, _settings.ReadInboxItems.Count - 1000);
        if (_settings.AcknowledgedInboxItems.Count > 1000) _settings.AcknowledgedInboxItems.RemoveRange(0, _settings.AcknowledgedInboxItems.Count - 1000);
        var prefix = CounterKey("private")[..CounterKey("private").IndexOf(':')] + ":";
        foreach (var key in _settings.ReadInboxOffsets.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _settings.ReadInboxOffsets[key] = 0;
        foreach (var key in _settings.InboxCounterSnapshots.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _settings.InboxCounterSnapshots[key] = 0;
        _pendingUnread.Clear(); _unreadByCategory.Clear(); UpdateUnreadCounts(); SaveSettings();
    }
    private string CounterKey(string category)
    {
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        return Hash(_accountId.ToString()) + ":" + (category.StartsWith("private:") ? "private:" + Hash(category) : category);
    }
    private void ReconcileUnread(string category, long reported)
    {
        reported = Math.Max(0, reported);
        var key = CounterKey(category);
        if (category == "private" && reported == 0)
        {
            foreach (var peerKey in _settings.ReadInboxOffsets.Keys.Where(k => k.StartsWith(key + ":")).ToArray())
            { _settings.ReadInboxOffsets[peerKey] = 0; _settings.InboxCounterSnapshots[peerKey] = 0; }
        }
        var previous = _settings.InboxCounterSnapshots.GetValueOrDefault(key);
        var acknowledged = _settings.ReadInboxOffsets.GetValueOrDefault(key);
        // A falling server counter acknowledges local reads; a repeated stale
        // counter must not resurrect them. New messages increase the remainder.
        acknowledged = reported == 0 ? 0 : Math.Max(0, acknowledged - Math.Max(0, previous - reported));
        _settings.InboxCounterSnapshots[key] = reported;
        _settings.ReadInboxOffsets[key] = acknowledged;
        _unreadByCategory[category] = Math.Max(0, reported - acknowledged);
    }
    private string ReadKey(SocialItem item) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{_accountId}:{item.InboxCategory}:{item.Id}:{item.Timestamp}:{item.CommentId}:{item.PeerId}:{item.ThreadId}")));
    private static bool PayloadIsUnread(JsonElement item) => Flag(item, "unread") || Flag(item, "new") || item.Number("newMsgCount") > 0 || item.Number("unreadCount") > 0 || item.ValueKind == JsonValueKind.Object && item.TryGetProperty("read", out var read) && read.ValueKind == JsonValueKind.False;
    private void ApplyUnread(SocialItem item, string category, bool inferred)
    {
        item.InboxCategory = category;
        var key = ReadKey(item);
        var reportedUnread = item.Unread || inferred;
        var alreadyRead = _settings.ReadInboxItems.Contains(key);
        if (alreadyRead && reportedUnread) AcknowledgeUnread(item, key);
        if (category == "private")
        {
            var peerCategory = "private:" + item.PeerId;
            ReconcileUnread(peerCategory, reportedUnread ? Math.Max(1, item.UnreadCount) : 0);
            item.UnreadCount = _unreadByCategory[peerCategory];
            reportedUnread &= item.UnreadCount > 0;
        }
        item.Unread = reportedUnread && !alreadyRead;
        if (item.Unread) _pendingUnread[ReadKey(item)] = (category, Math.Max(1, item.UnreadCount));
        else _pendingUnread.Remove(ReadKey(item));
    }
    private void MarkInboxRead(SocialItem item)
    {
        if (!item.Unread) return;
        var key = ReadKey(item); _pendingUnread.Remove(key);
        AcknowledgeUnread(item, key);
        if (!_settings.ReadInboxItems.Contains(key)) _settings.ReadInboxItems.Add(key);
        if (_settings.ReadInboxItems.Count > 1000) _settings.ReadInboxItems.RemoveRange(0, _settings.ReadInboxItems.Count - 1000);
        item.MarkRead(); UpdateUnreadCounts(); SaveSettings();
    }
    private void AcknowledgeUnread(SocialItem item, string key)
    {
        if (_settings.AcknowledgedInboxItems.Contains(key)) return;
        _settings.AcknowledgedInboxItems.Add(key);
        if (_settings.AcknowledgedInboxItems.Count > 1000) _settings.AcknowledgedInboxItems.RemoveRange(0, _settings.AcknowledgedInboxItems.Count - 1000);
        var counterKey = CounterKey(item.InboxCategory);
        _settings.ReadInboxOffsets[counterKey] = _settings.ReadInboxOffsets.GetValueOrDefault(counterKey) + Math.Max(1, item.UnreadCount);
        if (item.InboxCategory == "private")
        {
            var peerKey = CounterKey("private:" + item.PeerId);
            _settings.ReadInboxOffsets[peerKey] = _settings.ReadInboxOffsets.GetValueOrDefault(peerKey) + Math.Max(1, item.UnreadCount);
        }
        _unreadByCategory[item.InboxCategory] = Math.Max(0, _unreadByCategory.GetValueOrDefault(item.InboxCategory) - Math.Max(1, item.UnreadCount));
    }
    private void UpdateUnreadCounts()
    {
        long Count(string category) => Math.Max(_unreadByCategory.GetValueOrDefault(category), _pendingUnread.Values.Where(v => v.Category == category).Sum(v => v.Count));
        _messageUnreadCount = new[] { "notices", "comments", "mentions" }.Sum(Count);
        _privateUnreadCount = Count("private"); _unreadCount = _messageUnreadCount + _privateUnreadCount;
        UnreadBadge.Value = (int)Math.Clamp(_unreadCount, 0, 99); UnreadBadge.Visibility = _unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ResetInbox() { _inboxPages.Clear(); _inboxKeys.Clear(); }
    private async Task LoadInboxAsync(CancellationToken ct)
    {
        if (_accountId == 0) { var profile = await GetProfileAsync(ct); _accountId = profile?.Number("userId") ?? 0; }
        var batch = new List<SocialItem>();
        var failures = new List<string>();
        foreach (var category in new[] { "notices", "comments", "mentions" })
        {
            if (!_inboxPages.TryGetValue(category, out var page)) _inboxPages[category] = page = new();
            if (!page.More) continue;
            try
            {
                var result = category switch
                {
                    "comments" => await _api.CallAsync("msg_comments", new { uid = _accountId, limit = 30, before = page.Cursor }, ct),
                    "mentions" => await _api.CallAsync("msg_forwards", new { limit = 30, offset = page.Offset }, ct),
                    _ => await _api.CallAsync("msg_notices", new { limit = 30, lasttime = page.Cursor }, ct)
                };
                ct.ThrowIfCancellationRequested();
                var key = category == "mentions" ? "forwards" : category;
                if (!result.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) { page.More = false; continue; }
                foreach (var item in array.EnumerateArray())
                {
                    var social = ParseSocialItem(item, "messages", category, _accountId);
                    var budget = Math.Max(result.Number("newCount"), _unreadByCategory.GetValueOrDefault(category));
                    ApplyUnread(social, category, page.Offset + batch.Count(i => i.InboxCategory == category) < budget);
                    var identity = $"{category}:{social.Id}:{social.Timestamp}:{social.CommentId}:{social.PeerId}:{social.Text}";
                    if (_inboxKeys.Add(identity)) batch.Add(social);
                    if (social.Timestamp > 0) page.Cursor = page.Cursor <= 0 ? social.Timestamp : Math.Min(page.Cursor, social.Timestamp);
                }
                page.Offset += array.GetArrayLength();
                page.More = array.GetArrayLength() > 0 && (Flag(result, "more") || array.GetArrayLength() == 30);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        foreach (var item in batch.OrderByDescending(i => i.Timestamp))
        {
            var index = 0; while (index < SocialItems.Count && SocialItems[index].Timestamp >= item.Timestamp) index++;
            if (SocialItems.Count < 500) SocialItems.Insert(index, item);
        }
        SocialMore.Visibility = _inboxPages.Values.Any(p => p.More) && SocialItems.Count < 500 ? Visibility.Visible : Visibility.Collapsed;
        if (failures.Count > 0) ShowError(failures[0]);
        UpdateUnreadCounts();
    }
    private async Task CheckUnreadAsync()
    {
        if (!_authenticated || _hidden || _exiting || _inboxCheck != null || _dialog != null || _markingAllRead) return;
        using var check = new CancellationTokenSource(); _inboxCheck = check;
        try
        {
            var result = await _api.CallAsync("pl_count", cancellation: check.Token); check.Token.ThrowIfCancellationRequested();
            var counts = Object(result, "data"); if (counts.ValueKind != JsonValueKind.Object) counts = result;
            var previous = _unreadCount;
            ReconcileUnread("notices", counts.Number("notice")); ReconcileUnread("comments", counts.Number("comment"));
            ReconcileUnread("mentions", counts.Number("forward")); ReconcileUnread("private", counts.Number("msg"));
            UpdateUnreadCounts();
            SaveSettings();
            if (_unreadCount > previous && _view is not ("messages" or "private" or "conversation"))
            {
                ShowPlayerMessage(_messageUnreadCount > 0 ? "有新消息" : "有新私信");
                var button = new Button { Content = "查看" }; button.Click += async (_, _) => { PlayerNotice.IsOpen = false; await NavigateAsync(_messageUnreadCount > 0 ? "messages" : "private"); };
                PlayerNotice.ActionButton = button;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (ReferenceEquals(_inboxCheck, check)) _inboxCheck = null; }
    }
}
