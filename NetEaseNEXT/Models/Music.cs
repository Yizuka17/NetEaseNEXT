using System.Text.Json;

namespace NetEaseNEXT.Models;

public sealed class Track(long id, string title, string artist, string album, string cover, long duration)
{
    public long Id { get; } = id;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public string Album { get; } = album;
    public string Cover { get; } = cover;
    public long Duration { get; } = duration;
    public string DurationText => TimeSpan.FromMilliseconds(Duration).ToString(@"m\:ss");
    public string Subtitle => $"{Artist} - {Album}";
    public string Thumbnail => Cover.Length == 0 ? "" : Cover + (Cover.Contains('?') ? "&" : "?") + "param=192y192";
    public long ArtistId { get; private set; }
    public long AlbumId { get; private set; }
    public override string ToString() => Title;
    public static Track FromJson(JsonElement song)
    {
        var album = song.TryGetProperty("al", out var al) ? al : song.GetProperty("album");
        var artists = song.TryGetProperty("ar", out var ar) ? ar : song.GetProperty("artists");
        return new(song.GetProperty("id").GetInt64(), song.Text("name"),
            string.Join(" / ", artists.EnumerateArray().Select(a => a.Text("name"))),
            album.Text("name"), album.Text("picUrl"),
            song.TryGetProperty("dt", out var dt) ? dt.GetInt64() : song.GetProperty("duration").GetInt64())
        { ArtistId = artists.GetArrayLength() > 0 ? artists[0].Number("id") : 0, AlbumId = album.Number("id") };
    }
}
public sealed class Playlist(long id, string name, string cover, string description)
{
    public long Id { get; } = id;
    public string Name { get; } = name;
    public string Cover { get; } = cover;
    public string Description { get; } = description;
    public override string ToString() => Name;
    public string Thumbnail => Cover.Length == 0 ? "" : Cover + (Cover.Contains('?') ? "&" : "?") + "param=320y320";
    public static Playlist FromJson(JsonElement p) => new(p.GetProperty("id").GetInt64(), p.Text("name"),
        p.TryGetProperty("picUrl", out var pic) ? pic.GetString() ?? "" : p.Text("coverImgUrl"), p.Text("description"));
}
public static class JsonExtensions
{
    public static string Text(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    public static long Number(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) ? number : property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out var textNumber) ? textNumber : 0;
    }
}
