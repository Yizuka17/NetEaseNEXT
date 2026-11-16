using System.Text.Json;
using NetEaseNEXT.Services;
using NetEaseNEXT.Models;
using System.Collections.Concurrent;

namespace NetEaseNEXT;

internal static class AlbumPagingFixtures
{
    internal static readonly ConcurrentQueue<(string Method, long Offset)> Calls = new();
    internal static string FailNext = "", GateMethod = "";
    internal static TaskCompletionSource<bool>? Gate;
    internal static void Hold(string method) { GateMethod = method; Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal static void Release() => Gate?.TrySetResult(true);
    internal static ApiClient Create(string directory)
    {
        Calls.Clear(); Gate = null; GateMethod = FailNext = "";
        var cover = new Uri(LocalMusicFiles.StoreCover(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")))).AbsoluteUri;
        object Song(int i) => new { id = i + 1, name = "续页歌曲 " + (i + 1), ar = new[] { new { id = 301, name = "歌手甲" } }, al = new { id = 401, name = "完整专辑", picUrl = cover }, dt = 60000 };
        object[] Songs(int offset, int count) => Enumerable.Range(offset, count).Select(Song).ToArray();
        object[] Cards(int offset, int count) => Enumerable.Range(offset, count).Select(i => (object)new { id = 7000 + i, name = "专辑 " + (i + 1), picUrl = cover, artists = new[] { new { id = 301, name = "歌手甲" } } }).ToArray();
        return new ApiClient(async (method, p, ct) =>
        {
            var offset = (int)p.Number("offset"); Calls.Enqueue((method, offset));
            if (method == GateMethod && Gate != null) { GateMethod = ""; await Gate.Task.WaitAsync(ct); }
            await Task.Delay(40, ct);
            if (Environment.GetCommandLineArgs().Contains("--album-paging-preview") && offset > 0) await Task.Delay(2500, ct);
            if (method == FailNext) { FailNext = ""; throw new InvalidOperationException("测试续页失败"); }
            object body = method switch
            {
                "login_status" => new { code = 200, data = new { profile = new { userId = 42, nickname = "测试用户", avatarUrl = "" } } },
                "personalized" => new { code = 200, result = Array.Empty<object>() },
                "album" => new { code = 200, album = new { id = 401, name = "完整专辑", picUrl = cover, description = new string('曲', 160), publishTime = 1704038400000L, company = "测试唱片", type = "专辑", subType = "录音室版", artists = new[] { new { id = 301, name = "歌手甲" }, new { id = 302, name = "歌手乙" } } }, songs = Songs(0, 60) },
                "album_detail_dynamic" => new { code = 200, isSub = false },
                "artists" => new { code = 200, artist = new { id = p.Number("id"), name = p.Number("id") == 302 ? "歌手乙" : "歌手甲", picUrl = cover, musicSize = 3, albumSize = 50 }, hotSongs = Songs(0, 3) },
                "cloudsearch" => new { code = 200, result = new { songs = Songs(offset, offset < 100 ? 50 : 3) } },
                "playlist_detail" => new { code = 200, playlist = new { id = 101, name = "续页歌单", coverImgUrl = cover, trackCount = 115, trackIds = Enumerable.Range(1, 115).Select(id => new { id }).ToArray(), creator = new { userId = 42, nickname = "测试用户" } } },
                "playlist_track_all" => new { code = 200, songs = Songs(offset, offset == 0 ? 100 : 15) },
                "user_playlist" => new { code = 200, more = offset == 0, playlist = Enumerable.Range(offset, offset == 0 ? 100 : 2).Select(i => new { id = 9000 + i, name = "歌单 " + (i + 1), coverImgUrl = cover }).ToArray() },
                "album_sublist" => new { code = 200, hasMore = offset == 0, count = 50, data = Cards(offset, offset == 0 ? 48 : 2) },
                "artist_album" => new { code = 200, more = offset == 0, hotAlbums = Cards(offset, offset == 0 ? 48 : 2) },
                "top_artists" => new { code = 200, more = offset == 0, artists = Cards(offset, offset == 0 ? 48 : 2) },
                "dj_sublist" => new { code = 200, hasMore = offset == 0, djRadios = Cards(offset, offset == 0 ? 48 : 2) },
                "dj_program" => new { code = 200, more = offset == 0, programs = Enumerable.Range(offset, offset == 0 ? 50 : 3).Select(i => new { id = i, name = "节目 " + i, mainSong = Song(i), coverUrl = cover, duration = 60000 }).ToArray() },
                "comment_new" => new { code = 200, data = new { totalCount = 21, hasMore = p.Number("pageNo") == 1, cursor = "next", comments = Enumerable.Range(p.Number("pageNo") == 1 ? 1 : 21, p.Number("pageNo") == 1 ? 20 : 1).Select(i => new { commentId = i, content = "评论 " + i, time = 1704038400000L, user = new { nickname = "用户 " + i, avatarUrl = "" } }).ToArray() } },
                _ => new { code = 200, data = Array.Empty<object>() }
            };
            return JsonSerializer.SerializeToElement(body);
        }, directory, "MUSIC_U=synthetic-album-paging");
    }
}
