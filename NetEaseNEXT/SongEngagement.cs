using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using System.Text.Json;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _songStatsLoad;
    private long? _redCount;
    private bool _songLiked, _heartWriting, _songLikeKnown;
    private long _songStatsTrack;
    private static string CountText(long count) => count >= 100000000 ? (count / 100000000d).ToString("0.#") + "亿" : count >= 10000 ? (count / 10000d).ToString("0.#") + "万" : count.ToString("N0");
    private void UpdateSongActions()
    {
        HeartButton.IsEnabled = !_heartWriting && _playback.Current != null;
        SongCommentsButton.IsEnabled = _playback.Current != null;
        RedCountText.Text = _redCount is long count ? CountText(count) : "—";
        HeartIcon.Glyph = _songLiked ? "\uEB52" : "\uEB51";
        if (_songLiked) HeartIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 222, 65, 85)); else HeartIcon.ClearValue(FontIcon.ForegroundProperty);
        AutomationProperties.SetName(HeartButton, _songLiked ? "取消红心" : "红心");
        ToolTipService.SetToolTip(HeartButton, (_songLiked ? "取消红心" : "红心") + (_redCount is long total ? "\n" + total.ToString("N0") : ""));
    }
    private async Task LoadSongEngagementAsync(Track track)
    {
        if (_hidden || _exiting) return;
        _songStatsLoad?.Cancel(); using var load = new CancellationTokenSource(); _songStatsLoad = load;
        if (_songStatsTrack != track.Id) { _redCount = null; _songLiked = false; _songLikeKnown = false; _songStatsTrack = track.Id; }
        UpdateSongActions();
        try
        {
        try
        {
            var response = await _api.CallAsync("song_red_count", new { id = track.Id }, load.Token);
            if (_playback.Current?.Id != track.Id || load.IsCancellationRequested) return;
            if (response.TryGetProperty("data", out var data) && data.TryGetProperty("count", out var count) && count.TryGetInt64(out var value)) _redCount = value;
            UpdateSongActions();
        }
        catch (OperationCanceledException) { }
        catch { /* A missing public count is unknown, never zero. */ }
        if (_api.HasSession && !load.IsCancellationRequested)
        {
            try
            {
                var response = await _api.CallAsync("song_like_check", new { ids = "[" + track.Id + "]" }, load.Token);
                if (_playback.Current?.Id != track.Id || load.IsCancellationRequested) return;
                _songLiked = response.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.EnumerateArray().Any(id => id.ToString() == track.Id.ToString());
                _songLikeKnown = true;
                UpdateSongActions();
            }
            catch (OperationCanceledException) { }
            catch { }
        }
        }
        finally { if (ReferenceEquals(_songStatsLoad, load)) _songStatsLoad = null; }
    }
    private async void Heart_Click(object sender, RoutedEventArgs args)
    {
        if (_heartWriting || _playback.Current is not Track track) return;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession || _playback.Current?.Id != track.Id) return; }
        _heartWriting = true;
        try
        {
            // Resolve the account's state before choosing add vs. remove.
            await LoadSongEngagementAsync(track); _songStatsLoad?.Cancel();
            if (_playback.Current?.Id != track.Id) return;
            if (!_songLikeKnown) { ShowError("红心状态加载失败，请重试"); return; }
            var liked = !_songLiked; UpdateSongActions();
            await _api.CallAsync("like", new { id = track.Id, like = liked ? "true" : "false" });
            if (_playback.Current?.Id == track.Id) { _songLiked = liked; if (_redCount is long count) _redCount = Math.Max(0, count + (liked ? 1 : -1)); UpdateSongActions(); }
            ShowPlayerMessage(liked ? "已设为红心" : "已取消红心", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _heartWriting = false; UpdateSongActions(); }
    }
    private async void SongComments_Click(object sender, RoutedEventArgs args)
    {
        if (_playback.Current is not Track track) return;
        await ShowCommentsAsync(new SocialItem { Name = track.Title, Kind = "song", ResourceId = track.Id, ResourceKey = track.Id.ToString(), ResourceType = 0, ThreadId = "R_SO_4_" + track.Id });
    }
}
