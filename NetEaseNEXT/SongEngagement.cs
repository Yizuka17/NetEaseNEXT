using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using System.Text.Json;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _songStatsLoad;
    private long? _redCount;
    private bool _songLiked, _heartWriting, _songLikeKnown;
    private long _songStatsTrack, _songStatsPlaybackId;
    private ApiClient? _songStatsApi;
    private string _songStatsSession = "";
    internal ApiClient? SongInfoApiOverride { get; set; }
    private Task _songStatsTask = Task.CompletedTask;
    private static string CountText(long count) => count >= 100000000 ? (count / 100000000d).ToString("0.#") + "亿" : count >= 10000 ? (count / 10000d).ToString("0.#") + "万" : count.ToString("N0");
    private void UpdateSongActions()
    {
        var track = _playback.Current;
        var localVisible = track?.IsLocal != true || OnlineSongId(track) > 0 && _localOnlineReady;
        HeartButton.Visibility = SongCommentsButton.Visibility = localVisible ? Visibility.Visible : Visibility.Collapsed;
        HeartButton.IsEnabled = !_heartWriting && track != null && OnlineSongId(track) > 0 && localVisible;
        SongCommentsButton.IsEnabled = track != null && OnlineSongId(track) > 0 && localVisible;
        RedCountText.Text = _redCount is long count ? CountText(count) : "—";
        HeartIcon.Glyph = _songLiked ? "\uEB52" : "\uEB51";
        if (_songLiked) HeartIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 222, 65, 85)); else HeartIcon.ClearValue(FontIcon.ForegroundProperty);
        AutomationProperties.SetName(HeartButton, _songLiked ? "取消红心" : "红心");
        ToolTipService.SetToolTip(HeartButton, (_songLiked ? "取消红心" : "红心") + (_redCount is long total ? "\n" + total.ToString("N0") : ""));
    }
    private Task LoadSongEngagementAsync(Track track, ApiClient? source = null)
    {
        var client = source ?? SongInfoApiOverride ?? _api; var id = OnlineSongId(track);
        if (!_songStatsTask.IsCompleted && _songStatsTrack == id && _songStatsPlaybackId == track.Id && ReferenceEquals(client, _songStatsApi) && _songStatsSession == client.Cookie) return _songStatsTask;
        _songStatsTask = LoadSongEngagementCoreAsync(track, client); return _songStatsTask;
    }
    private bool SongContextMatches(Track track, long id, ApiClient client, string session)
        => _playback.Current?.Id == track.Id && OnlineSongId(track) == id && client.Cookie == session && !_hidden && !_exiting;
    private async Task LoadSongEngagementCoreAsync(Track track, ApiClient client)
    {
        var id = OnlineSongId(track);
        _songStatsLoad?.Cancel();
        if (id <= 0)
        {
            _songStatsTrack = 0; _localOnlineReady = false; _redCount = null; _songLiked = _songLikeKnown = false; UpdateSongActions(); return;
        }
        if (_hidden || _exiting) return;
        using var load = new CancellationTokenSource(); _songStatsLoad = load; var session = client.Cookie;
        if (_songStatsTrack != id || _songStatsPlaybackId != track.Id || !ReferenceEquals(_songStatsApi, client) || _songStatsSession != session) { _redCount = null; _songLiked = _songLikeKnown = _localOnlineReady = false; }
        _songStatsTrack = id; _songStatsPlaybackId = track.Id; _songStatsApi = client; _songStatsSession = session; UpdateSongActions();
        try
        {
            try
            {
                var response = track.IsLocal ? await client.CallAsync("song_red_count", new { id }, load.Token) : await client.CallCachedAsync("song_red_count", new { id }, load.Token);
                if (!SongContextMatches(track, id, client, session) || load.IsCancellationRequested) return;
                if (response.TryGetProperty("data", out var data) && data.TryGetProperty("count", out var count) && count.TryGetInt64(out var value)) { _redCount = value; _localOnlineReady = true; }
                UpdateSongActions();
            }
            catch (OperationCanceledException) { }
            catch { /* Unknown stays unknown; local controls remain hidden until reachable. */ }
            if (client.HasSession && !load.IsCancellationRequested)
            {
                try
                {
                    var response = track.IsLocal ? await client.CallAsync("song_like_check", new { ids = "[" + id + "]" }, load.Token) : await client.CallCachedAsync("song_like_check", new { ids = "[" + id + "]" }, load.Token);
                    if (!SongContextMatches(track, id, client, session) || load.IsCancellationRequested) return;
                    _songLiked = response.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.EnumerateArray().Any(value => value.ToString() == id.ToString());
                    _songLikeKnown = _localOnlineReady = true; UpdateSongActions();
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
        var id = OnlineSongId(track); if (id <= 0) return;
        if (!_api.HasSession) { await ShowLoginAsync(); if (!_api.HasSession || _playback.Current?.Id != track.Id || OnlineSongId(track) != id) return; }
        _heartWriting = true; var session = _api.Cookie;
        try
        {
            await LoadSongEngagementAsync(track); _songStatsLoad?.Cancel();
            if (!SongContextMatches(track, id, _api, session)) return;
            if (!_songLikeKnown) { ShowError("红心状态加载失败，请重试"); return; }
            var liked = !_songLiked; UpdateSongActions();
            await _api.CallAsync("like", new { id, like = liked ? "true" : "false" });
            if (SongContextMatches(track, id, _api, session)) { _songLiked = liked; if (_redCount is long count) _redCount = Math.Max(0, count + (liked ? 1 : -1)); UpdateSongActions(); }
            ShowPlayerMessage(liked ? "已设为红心" : "已取消红心", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _heartWriting = false; UpdateSongActions(); }
    }
    private async void SongComments_Click(object sender, RoutedEventArgs args)
    {
        if (_playback.Current is Track track && SongCommentsSource(track) is { } source) await ShowCommentsAsync(source);
    }
}
