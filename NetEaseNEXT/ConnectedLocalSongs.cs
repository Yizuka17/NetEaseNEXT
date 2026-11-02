using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Networking.Connectivity;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _localOnlineReady;
    private bool? _connectionOverride;
    private bool InternetAvailable
    {
        get
        {
            if (_connectionOverride is bool connected) return connected;
            try { return NetworkInformation.GetInternetConnectionProfile() is { } profile && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None; }
            catch { return false; }
        }
    }
    private DownloadedFile? DownloadForTrack(Track track)
    {
        if (!track.IsLocal) return _downloadedLibrary.Find(track.Id);
        if (track.SourceSongId > 0 && !track.HasValidLocalSource()) return null;
        var registered = _downloadedLibrary.FindPath(track.LocalPath);
        var origin = registered ?? (track.HasValidLocalSource() && AudioQuality.Rank(track.SourceLevel) >= 0
            ? new DownloadedFile(track.SourceSongId, track.LocalPath, track.SourceLevel, track.SourceFileSize, track.SourceModifiedUtcTicks, track.ArtistId, track.AlbumId) : null);
        return origin == null ? null : _downloadedLibrary.Find(origin.SongId, directory: Path.GetDirectoryName(track.LocalPath)) ?? origin;
    }
    private long SourceSongId(Track track)
        => !track.IsLocal ? track.Id : DownloadForTrack(track)?.SongId ?? (track.HasValidLocalSource() ? track.SourceSongId : 0);
    private long OnlineSongId(Track track)
        => !track.IsLocal ? track.Id : _settings.LocalOnlineInfo && InternetAvailable ? SourceSongId(track) : 0;
    private void UpdateDownloadIndicator()
    {
        if (_hidden || DownloadCurrentButton == null) return;
        var track = _playback.Current; var downloaded = track == null ? null : DownloadForTrack(track);
        DownloadCheck.Visibility = downloaded == null ? Visibility.Collapsed : Visibility.Visible;
        DownloadCurrentButton.IsEnabled = track != null && (!track.IsLocal || SourceSongId(track) > 0);
        var label = downloaded == null ? "下载" : "已下载" + AudioQuality.Name(downloaded.Level) + "，点击升级音质";
        AutomationProperties.SetName(DownloadCurrentButton, label); ToolTipService.SetToolTip(DownloadCurrentButton, label);
    }
    private void LocalOnlineInfo_Changed(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _settings.LocalOnlineInfo = LocalOnlineInfoToggle.IsOn; SaveSettings(); RefreshLocalOnlineContext();
    }
    private void NetworkStatusChanged(object sender)
    {
        if (_connectionOverride != null) return;
        DispatcherQueue.TryEnqueue(RefreshLocalOnlineContext);
    }
    private void RefreshLocalOnlineContext()
    {
        if (_exiting || _playback.Current is not { IsLocal: true } track) return;
        _songStatsLoad?.Cancel(); _songStatsTask = Task.CompletedTask; _songStatsTrack = 0;
        _localOnlineReady = false; _redCount = null; _songLiked = _songLikeKnown = false;
        if (_hidden) return;
        UpdateSongActions(); UpdateDownloadIndicator();
        if (OnlineSongId(track) > 0) _ = LoadSongEngagementAsync(track);
    }
    private SocialItem? SongCommentsSource(Track track)
    {
        var id = OnlineSongId(track); if (id <= 0 || track.IsLocal && !_localOnlineReady) return null;
        return new() { Name = track.Title, Kind = "song", ResourceId = id, ResourceKey = id.ToString(), ResourceType = 0, ThreadId = "R_SO_4_" + id };
    }
    private async Task RegisterLocalDownloadSourcesAsync(IEnumerable<Track> tracks)
    {
        var sources = tracks.Where(t => t.SourceSongId > 0).ToArray(); if (sources.Length == 0) return;
        try { await Task.Run(() => _downloadedLibrary.RegisterLocalSources(sources)); }
        catch (Exception ex) { Log.Write(ex); }
    }
}
