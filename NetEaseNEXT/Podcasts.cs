using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private static List<Track> ParseProgramTracks(JsonElement result, string name, long radioId) => result.GetProperty("programs").EnumerateArray().Select(p =>
        new Track(p.GetProperty("mainSong").Number("id"), p.Text("name"), Object(p, "dj").Text("nickname"), name, p.Text("coverUrl"), p.Number("duration")) { PodcastId = radioId }).ToList();
}
