using System.Collections.Concurrent;
using System.Text.Json;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

internal static class ArtistSharingFixtures
{
    internal static readonly ConcurrentQueue<(string Method, JsonElement Query)> Calls = new();
    internal static string GateMethod = "";
    internal static TaskCompletionSource<bool>? Gate;
    internal static bool FailShare, AlbumSaved, RadioSaved;
    internal static void Hold(string method) { GateMethod = method; Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal static void Release() => Gate?.TrySetResult(true);
    internal static ApiClient Create(string directory)
    {
        Calls.Clear(); GateMethod = ""; Gate = null; FailShare = AlbumSaved = RadioSaved = false;
        var cover = new Uri(LocalMusicFiles.StoreCover(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")))).AbsoluteUri;
        object Song(long id) => new { id, name = "歌曲 " + id, ar = new[] { new { id = 301, name = "测试歌手" } }, al = new { id = 401, name = "测试专辑", picUrl = cover }, dt = 60000 };
        var songs = Enumerable.Range(1, 3).Select(i => Song(i)).ToArray();
        var playlist = new { id = 101, name = "测试歌单", picUrl = cover, coverImgUrl = cover, description = "", trackCount = 3 };
        return new ApiClient(async (method, p, ct) =>
        {
            Calls.Enqueue((method, p.Clone()));
            if (method == GateMethod && Gate != null) { GateMethod = ""; await Gate.Task.WaitAsync(ct); }
            await Task.Delay(35, ct);
            if (Environment.GetCommandLineArgs().Contains("--artist-sharing-preview") && p.Number("offset") > 0) await Task.Delay(1200, ct);
            if (method == "album_sub") AlbumSaved = p.Number("t") == 1;
            if (method == "dj_sub") RadioSaved = p.Number("t") == 1;
            var offset = (int)p.Number("offset");
            var artistIds = p.Text("order") == "time" ? Enumerable.Range(1, 165).Reverse() : Enumerable.Range(1, 165);
            var page = artistIds.Skip(offset).Take((int)(p.Number("limit") > 0 ? p.Number("limit") : 100)).Select(i => Song(i)).ToArray();
            object body = method switch
            {
                "login_status" => new { code = 200, data = new { profile = new { userId = 42, nickname = "测试用户", avatarUrl = "" } } },
                "personalized" => new { code = 200, result = new[] { playlist } },
                "user_playlist" => new { code = 200, playlist = new[] { playlist }, more = false },
                "top_artists" => new { code = 200, artists = new[] { new { id = 301, name = "测试歌手", picUrl = cover } } },
                "artists" => new { code = 200, artist = new { id = 301, name = "测试歌手", picUrl = cover, musicSize = 165, albumSize = 3, briefDesc = "测试歌手介绍" }, hotSongs = songs },
                "artist_songs" => new { code = 200, total = 165, more = offset + page.Length < 165, songs = page },
                "artist_album" => new { code = 200, more = false, hotAlbums = new[] { new { id = 401, name = "测试专辑", picUrl = cover } } },
                "artist_mv" => new { code = 200, more = false, mvs = new[] { new { id = 501, name = "测试 MV", imgurl = cover } } },
                "artist_desc" => new { code = 200, briefDesc = "测试歌手介绍", introduction = new[] { new { ti = "介绍", txt = "用于验证切换介绍时头像不跳动。" } } },
                "album" => new { code = 200, album = new { id = 401, name = "测试专辑", picUrl = cover, description = "专辑介绍", artists = new[] { new { id = 301, name = "测试歌手" } } }, songs },
                "album_detail_dynamic" => new { code = 200, isSub = AlbumSaved, commentCount = 54 },
                "playlist_detail" => new { code = 200, playlist = new { id = 101, name = "测试歌单", coverImgUrl = cover, description = "歌单介绍", trackCount = 3, trackIds = new[] { new { id = 1 }, new { id = 2 }, new { id = 3 } }, commentCount = 13, creator = new { userId = 7, nickname = "创建者" } } },
                "playlist_track_all" => new { code = 200, songs },
                "song_detail" => new { code = 200, songs = p.Text("ids").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Select(Song).ToArray() },
                "dj_sublist" => new { code = 200, more = false, djRadios = new[] { new { id = 201, name = "整播客", picUrl = cover } } },
                "dj_detail" => new { code = 200, djRadio = new { id = 201, name = "整播客", picUrl = cover, desc = "播客介绍", subed = RadioSaved, subCount = 12, commentCount = 37, dj = new { nickname = "播客作者" } } },
                "dj_program" => new { code = 200, more = false, programs = Enumerable.Range(1, 3).Select(i => new { id = 9000 + i, name = "单集 " + i, coverUrl = cover, duration = 60000, mainSong = Song(6000 + i), dj = new { nickname = "播客作者" } }).ToArray() },
                "comment_new" => new { code = 200, data = new { totalCount = 1, hasMore = false, comments = new[] { new { commentId = 1, content = "资源类型 " + p.Number("type") + " 的评论", time = 1704038400000L, user = new { nickname = "评论用户", avatarUrl = "" } } } } },
                "share_resource" => FailShare ? (object)new { code = 500, msg = "模拟发布失败" } : new { code = 200, success = true },
                "song_red_count" => new { code = 200, data = new { count = 42 } },
                "song_like_check" => new { code = 200, ids = Array.Empty<long>() },
                _ => new { code = 200 }
            };
            return JsonSerializer.SerializeToElement(body);
        }, directory, "MUSIC_U=synthetic-artist-sharing");
    }
}
