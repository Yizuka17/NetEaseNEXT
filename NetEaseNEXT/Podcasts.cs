using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private long ProgramIdentity(Track track)
    {
        if (track.ProgramId > 0) return track.ProgramId;
        var known = Tracks.FirstOrDefault(t => t.Id == track.Id && t.PodcastId == track.PodcastId && t.ProgramId > 0);
        if (known != null) track.ProgramId = known.ProgramId;
        return track.ProgramId;
    }
    private static List<Track> ParseProgramTracks(JsonElement result, string name, long radioId) => result.GetProperty("programs").EnumerateArray().Select(p =>
        new Track(p.GetProperty("mainSong").Number("id"), p.Text("name"), Object(p, "dj").Text("nickname"), name, p.Text("coverUrl"), p.Number("duration")) { PodcastId = radioId, ProgramId = p.Number("id") }).ToList();
}
