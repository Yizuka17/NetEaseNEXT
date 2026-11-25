using System.Collections.Concurrent;
using System.Text.Json;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

internal static class LocalLinkFixtures
{
    internal static readonly ConcurrentQueue<(string Method, JsonElement Query)> Calls = new();
    internal static string GateMethod = "";
    internal static TaskCompletionSource<bool>? Gate;
    internal static int Canceled;
    internal static int CommentFailures;
    internal static int EventFailures;
    internal static JsonElement? EventResponse, PrivateResponse;
    internal static void Hold(string method) { GateMethod = method; Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal static void Release() => Gate?.TrySetResult(true);
    internal static object Song(long id) => new { id, name = id == 137 ? "合作歌曲" : "歌曲 " + id, ar = id == 137 ? new[] { new { id = 301, name = "歌手甲" }, new { id = 302, name = "歌手乙" } } : new[] { new { id = 301, name = "歌手甲" } }, al = new { id = 401, name = "测试专辑", picUrl = "" }, dt = 4000 };
    internal static ApiClient Create(string directory)
    {
        Calls.Clear(); GateMethod = ""; Gate = null; Canceled = 0; CommentFailures = 0;
        EventFailures = 0; EventResponse = PrivateResponse = null;
        var cover = new Uri(LocalMusicFiles.StoreCover(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")))).AbsoluteUri;
        var playlist = new { id = 101, name = "在线歌单", picUrl = cover, coverImgUrl = cover, trackCount = 165, creator = new { userId = 42 } };
        return new ApiClient(async (method, p, ct) =>
        {
            Calls.Enqueue((method, p.Clone()));
            if (method == GateMethod && Gate != null)
            {
                GateMethod = "";
                try { await Gate.Task.WaitAsync(ct); } catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            }
            await Task.Delay(25, ct);
            if (method is "event" or "user_event" && Interlocked.CompareExchange(ref EventFailures, 0, 1) == 1) throw new InvalidOperationException("测试刷新失败");
            if (method == "comment_new" && Interlocked.CompareExchange(ref CommentFailures, 0, 1) == 1)
                throw new IOException("fixture comments unavailable");
            object body = method switch
            {
                "login_status" => new { code = 200, data = new { profile = new { userId = 42, nickname = "测试用户", avatarUrl = "" } } },
                "personalized" => new { code = 200, result = new[] { playlist } },
                "event" => EventResponse is { } eventPage ? eventPage : new { code = 200, @event = Array.Empty<object>(), more = false, lasttime = 0 },
                "user_event" => EventResponse is { } ownPage ? ownPage : new { code = 200, events = Array.Empty<object>(), more = false, lasttime = 0 },
                "msg_private_history" => PrivateResponse is { } privatePage ? privatePage : new { code = 200, msgs = Array.Empty<object>(), more = false },
                "user_playlist" => new { code = 200, playlist = new[] { playlist }, more = false },
                "playlist_detail" => new { code = 200, playlist = new { id = 101, name = "在线歌单", coverImgUrl = cover, description = "本地下载来源定位", trackCount = 165, trackIds = Enumerable.Range(1, 165).Select(i => new { id = i }).ToArray() } },
                "playlist_track_all" => new { code = 200, songs = Enumerable.Range(1, 165).Skip((int)p.Number("offset")).Take(100).Select(i => Song(i)).ToArray() },
                "song_detail" => new { code = 200, songs = p.Text("ids").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).Select(Song).ToArray() },
                "artists" => new { code = 200, artist = new { id = p.Number("id"), name = p.Number("id") == 302 ? "歌手乙" : "歌手甲", picUrl = cover, musicSize = 1, albumSize = 1 }, hotSongs = new[] { Song(137) } },
                "artist_songs" => new { code = 200, total = 1, more = false, songs = new[] { Song(137) } },
                "artist_album" => new { code = 200, more = false, hotAlbums = new[] { new { id = 401, name = "测试专辑", picUrl = cover } } },
                "album" => new { code = 200, album = new { id = 401, name = "测试专辑", picUrl = cover, artists = new[] { new { id = 301, name = "歌手甲" }, new { id = 302, name = "歌手乙" } } }, songs = new[] { Song(137) } },
                "album_detail_dynamic" => new { code = 200, isSub = false, commentCount = 3 },
                "dj_detail" => new { code = 200, djRadio = new { id = p.Number("rid"), name = "测试播客", picUrl = cover, desc = "播客介绍", programCount = 1, commentCount = 2, subCount = 3, subed = false, dj = new { userId = 42, nickname = "测试主播" } } },
                "dj_program" => new { code = 200, more = false, programs = new[] { new { id = 601, name = "测试单集", coverUrl = cover, duration = 4000, mainSong = Song(137), dj = new { nickname = "测试主播" } } } },
                "dj_program_detail" => new { code = 200, program = new { id = p.Number("id"), radio = new { id = 501, name = "测试播客", picUrl = cover } } },
                "song_red_count" => new { code = 200, data = new { count = 12 } },
                "song_like_check" => new { code = 200, ids = Array.Empty<long>() },
                "comment_new" => new { code = 200, data = new { totalCount = 0, hasMore = false, comments = Array.Empty<object>() } },
                _ => new { code = 200 }
            };
            return JsonSerializer.SerializeToElement(body);
        }, directory, "MUSIC_U=synthetic-local-links");
    }
    internal static Track Audio(string directory, string name, long sourceId = 137, string quality = "lossless")
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, name + ".wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int bytes = 88200 * 4; writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
        }
        AudioTags.Write(path, name, sourceId > 0 ? "歌手甲 / 歌手乙" : "本地歌手", sourceId > 0 ? "测试专辑" : "本地专辑", "", null, sourceId, sourceId > 0 ? quality : "");
        var info = new FileInfo(path); var track = LocalMusicFiles.Read(path, new DownloadedLibrary(Path.Combine(directory, "empty-catalog.json")));
        track.SetLocalSource(sourceId, sourceId > 0 ? quality : "", info.Length, info.LastWriteTimeUtc.Ticks); return track;
    }
    internal static PlaybackSnapshot PreparePreview(string directory)
    {
        var a = Audio(Path.Combine(directory, "歌单 A"), "合作歌曲");
        var b = Audio(Path.Combine(directory, "歌单 B"), "合作歌曲", 137, "hires");
        var plain = Audio(Path.Combine(directory, "歌单 A"), "未关联的本地音乐", 0);
        var library = new LocalLibraryStore { StoragePath = Path.Combine(directory, "preview-library.bin"), Tracks = [a, b, plain], Playlists = [new() { Id = "preview-a", Name = "本地歌单 A", Paths = [a.LocalPath, plain.LocalPath] }, new() { Id = "preview-b", Name = "本地歌单 B", FolderPath = Path.GetDirectoryName(b.LocalPath)!, Paths = [b.LocalPath] }] };
        library.Save(); new DownloadedLibrary(Path.Combine(directory, "preview-catalog.json")).RegisterLocalSources([a, b]);
        return new() { View = "local-playlist", LocalPlaylistId = "preview-a", Queue = [a], CurrentId = a.Id, Quality = "lossless" };
    }
}

public sealed partial class MainWindow
{
    internal void UseLocalLinkPreview()
    {
        _connectionOverride = true;
        _localLibraryPathOverride = Path.Combine(_settings.CacheDirectory, "preview-library.bin");
        _downloadedLibrary = new(Path.Combine(_settings.CacheDirectory, "preview-catalog.json")); _playback.Downloads = _downloadedLibrary;
    }
}
