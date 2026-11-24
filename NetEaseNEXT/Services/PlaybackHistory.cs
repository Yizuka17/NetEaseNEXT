namespace NetEaseNEXT.Services;

public sealed partial class PlaybackService
{
    private ApiClient _historyApi = null!;
    private PlaybackHistoryReporter? _history;
    private long _historySongId, _historyTrackId;
    private string _historyCookie = "";
    internal bool HistoryReportingEnabled { get; set; }
    internal int HistoryRecordsSent => _history?.Sent ?? 0;
    private void RecordHistoryStart()
    {
        if (!HistoryReportingEnabled || !IsPlaying || Current is not { } track) return;
        if (_historyTrackId != track.Id) { _historyTrackId = track.Id; _historySongId = 0; }
        if (!_historyApi.HasSession || track.ProgramId > 0 || track.PodcastId > 0) return;
        var id = track.IsLocal ? _settings.LocalOnlineInfo && track.HasValidLocalSource() ? track.SourceSongId : 0 : track.Id;
        if (id <= 0 || id == _historySongId && _historyCookie == _historyApi.Cookie) return;
        _historySongId = id; _historyCookie = _historyApi.Cookie;
        (_history ??= new(_historyApi)).Record(id);
    }
}
