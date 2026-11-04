using System.Text.Json;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

internal static class MotionApiFixtures
{
    internal static ApiClient Create(string directory)
    {
        var cover = new Uri(LocalMusicFiles.StoreCover(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")))).AbsoluteUri;
        var songs = Enumerable.Range(1, 3).Select(i => new { id = i, name = "测试歌曲 " + i, ar = new[] { new { id = 301, name = "测试歌手" } }, al = new { id = 401, name = "测试专辑", picUrl = cover }, dt = 60000 }).ToArray();
        var playlists = new[] { new { id = 101, name = "在线晴昼", picUrl = cover, coverImgUrl = cover, description = "", trackCount = 3 } };
        return new ApiClient(async (method, p, ct) =>
        {
            await Task.Delay(35, ct);
            object body = method switch
            {
                "personalized" => new { code = 200, result = playlists },
                "user_playlist" => new { code = 200, playlist = playlists },
                "playlist_detail" => new { code = 200, playlist = new { id = 101, name = "在线晴昼", coverImgUrl = cover, description = "", tags = Array.Empty<string>(), trackCount = 3, subscribedCount = 0, creator = new { userId = 42, nickname = "测试用户" }, trackIds = songs.Select(s => new { s.id }).ToArray() } },
                "playlist_track_all" or "song_detail" => new { code = 200, songs, privileges = Array.Empty<object>() },
                "dj_sublist" => new { code = 200, count = 1, hasMore = false, djRadios = new[] { new { id = 201, name = "午后电台", picUrl = cover, dj = new { nickname = "测试主播" } } } },
                "dj_program" => new { code = 200, programs = songs.Select(s => new { name = s.name, coverUrl = cover, mainSong = s, duration = 60000, dj = new { nickname = "测试主播" } }).ToArray() },
                "top_artists" => new { code = 200, artists = new[] { new { id = 301, name = "测试歌手", picUrl = cover } } },
                "artists" => new { code = 200, artist = new { id = 301, name = "测试歌手", picUrl = cover, briefDesc = "", musicSize = 3, albumSize = 1 }, hotSongs = songs },
                "artist_album" => new { code = 200, hotAlbums = new[] { new { id = 401, name = "测试专辑", picUrl = cover } } },
                "album_sublist" => new { code = 200, count = 1, hasMore = false, data = new[] { new { id = 401, name = "测试专辑", picUrl = cover, artists = new[] { new { name = "测试歌手" } } } } },
                "album" => new { code = 200, album = new { id = 401, name = "测试专辑", picUrl = cover, description = "" }, songs },
                "login_status" => new { code = 200, data = new { profile = new { userId = 42, nickname = "测试用户", avatarUrl = "" } } },
                "like_list" => new { code = 200, ids = Array.Empty<long>() },
                _ => new { code = 200, data = Array.Empty<object>() }
            };
            return JsonSerializer.SerializeToElement(body);
        }, directory, "MUSIC_U=local-motion-fixture");
    }
}
