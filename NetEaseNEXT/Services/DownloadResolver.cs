using System.Text.Json;
using NetEaseNEXT.Api;
using NetEaseNEXT.Models;

namespace NetEaseNEXT.Services;

internal static class DownloadResolver
{
    internal static async Task<JsonElement> ResolveAsync(ApiClient api, DownloadJob job, CancellationToken ct)
    {
        if (AudioQuality.Rank(job.Quality) < 0) throw new InvalidDataException("未知的目标音质");
        JsonElement? best = null; var bestRank = -1; var requestedBitrates = new HashSet<int>();
        Exception? lastFailure = null;
        foreach (var level in AudioQuality.Descending(job.Quality))
        {
            var rank = AudioQuality.Rank(level); if (rank <= bestRank) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                JsonElement result;
                if (job.Source == "legacy")
                {
                    var bitrate = rank >= 3 ? 999000 : rank == 2 ? 320000 : rank == 1 ? 192000 : 128000;
                    if (!requestedBitrates.Add(bitrate)) continue;
                    result = await api.CallAsync("song_download_legacy", new { id = job.Track.Id, br = bitrate }, ct);
                }
                else result = await api.CallAsync("song_download_url_v1", new { id = job.Track.Id, level }, ct);
                if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                if (data.TryGetProperty("freeTrialInfo", out var trial) && trial.ValueKind == JsonValueKind.Object) continue;
                if (!Uri.TryCreate(data.Text("url"), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) continue;
                if (data.Number("id") > 0 && data.Number("id") != job.Track.Id) continue;
                var format = data.Text("type").ToLowerInvariant(); if (format is not ("mp3" or "flac" or "m4a" or "wav")) continue;
                var actual = job.Source != "legacy" && AudioQuality.Rank(data.Text("level")) >= 0 ? data.Text("level") : AudioQuality.Infer(format, data.Number("br"));
                var actualRank = AudioQuality.Rank(actual);
                if (actualRank > AudioQuality.Rank(job.Quality) || actualRank <= bestRank) continue;
                best = JsonSerializer.SerializeToElement(new { url = url.AbsoluteUri, type = format, level = actual, size = data.Number("size"), md5 = data.Text("md5") }); bestRank = actualRank;
                if (bestRank == AudioQuality.Rank(job.Quality)) break;
            }
            catch (MusicApiException ex) when (ex.Code is not (301 or 401)) { lastFailure = ex; }
        }
        return best ?? throw new InvalidOperationException("目标音质及更低音质均无完整下载地址，请检查账号权益或切换下载来源", lastFailure);
    }
}
