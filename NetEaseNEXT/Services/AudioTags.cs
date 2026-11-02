namespace NetEaseNEXT.Services;

public sealed record AudioFileInfo(string Path, string Title, string Artist, string Album, long Duration, string Lyrics, byte[]? Cover, long SongId = 0, string Level = "", long ArtistId = 0, long AlbumId = 0);
public static class AudioTags
{
    public static AudioFileInfo Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var picture = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? file.Tag.Pictures.FirstOrDefault();
        var songId = long.TryParse(ReadField(file, "NETEASENEXT_SONG_ID"), out var id) && id > 0 ? id : 0;
        var level = ReadField(file, "NETEASENEXT_QUALITY");
        var artistId = long.TryParse(ReadField(file, "NETEASENEXT_ARTIST_ID"), out var artist) ? artist : 0;
        var albumId = long.TryParse(ReadField(file, "NETEASENEXT_ALBUM_ID"), out var album) ? album : 0;
        return new(System.IO.Path.GetFullPath(path), string.IsNullOrWhiteSpace(file.Tag.Title) ? System.IO.Path.GetFileNameWithoutExtension(path) : file.Tag.Title,
            string.Join(" / ", file.Tag.Performers), file.Tag.Album ?? "", (long)file.Properties.Duration.TotalMilliseconds, file.Tag.Lyrics ?? "",
            picture != null && picture.Data.Count <= 8 * 1024 * 1024 ? picture.Data.Data : null, songId, AudioQuality.Rank(level) >= 0 ? level : "", artistId, albumId);
    }
    public static void Write(string path, string title, string artist, string album, string lyrics, byte[]? cover, long songId = 0, string level = "", long artistId = 0, long albumId = 0)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title; file.Tag.Performers = artist.Split(" / ", StringSplitOptions.RemoveEmptyEntries); file.Tag.Album = album; file.Tag.Lyrics = lyrics;
        if (cover is { Length: > 0 }) file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, Description = "Cover" }];
        if (songId > 0)
        {
            WriteField(file, "NETEASENEXT_SONG_ID", songId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteField(file, "NETEASENEXT_QUALITY", level);
            WriteField(file, "NETEASENEXT_ARTIST_ID", artistId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteField(file, "NETEASENEXT_ALBUM_ID", albumId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        file.Save();
    }
    private static string ReadField(TagLib.File file, string name)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph) return xiph.GetField(name).FirstOrDefault() ?? "";
        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple) return apple.GetDashBox("com.neteasenext", name) ?? "";
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3) return TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, false)?.Text.FirstOrDefault() ?? "";
        return "";
    }
    private static void WriteField(TagLib.File file, string name, string value)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph) xiph.SetField(name, new[] { value });
        else if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple) apple.SetDashBox("com.neteasenext", name, value);
        else if (file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3) TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, TagLib.StringType.UTF8, true).Text = [value];
    }
}
