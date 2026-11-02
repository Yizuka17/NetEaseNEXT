using System.Text.Json;

namespace NetEaseNEXT.Api;

public sealed record ApiRequest(string Path, Dictionary<string, object?> Data, string Crypto = "eapi", bool CheckToken = false);
public static class ApiEndpoints
{
    public static readonly HashSet<string> Writes = ["like", "playlist_tracks", "song_order_update", "playlist_delete", "playlist_subscribe", "playlist_create", "resource_like", "comment", "comment_like", "send_text", "send_song", "send_album", "send_playlist", "msg_read_all", "share_resource"];
    private static readonly string[] Prefixes = ["R_SO_4_", "R_MV_5_", "A_PL_0_", "R_AL_3_", "A_DJ_1_", "R_VI_62_", "A_EV_2_", "A_DR_14_"];
    public static ApiRequest Create(string method, JsonElement query)
    {
        object? V(string name, object? fallback = null) => query.ValueKind == JsonValueKind.Object && query.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : fallback;
        string S(string name, string fallback = "") => V(name)?.ToString() ?? fallback;
        int N(string name, int fallback = 0) => int.TryParse(S(name), out var value) ? value : fallback;
        string Thread() => N("type") == 6 ? S("threadId", Prefixes[6] + S("id")) : Prefixes[Math.Clamp(N("type"), 0, 7)] + S("id");
        ApiRequest R(string path, object? data = null, string crypto = "eapi", bool token = false) => new(path, data == null ? new() : JsonSerializer.Deserialize<Dictionary<string, object?>>(ApiEncryption.Json(data))!, crypto, token);
        object Page(int limit = 30) => new { limit = V("limit", limit), offset = V("offset", 0), total = true };
        switch (method)
        {
            case "cloudsearch": return R("/api/cloudsearch/pc", new { s = V("keywords"), type = N("type") == 0 ? 1 : N("type"), limit = V("limit", 30), offset = V("offset", 0), total = true });
            case "personalized": return R("/api/personalized/playlist", new { limit = V("limit", 30), total = true, n = 1000 }, "weapi");
            case "playlist_detail": return R("/api/v6/playlist/detail", new { id = V("id"), n = 100000, s = V("s", 8) });
            case "song_detail": return R("/api/v3/song/detail", new { c = ApiEncryption.Json(S("ids").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(id => new { id = long.Parse(id) })) }, "weapi");
            case "song_url_v1":
                var play = R("/api/song/enhance/player/url/v1", new { ids = "[" + S("id") + "]", level = V("level"), encodeType = S("level") == "vivid" ? "mp3" : "flac" });
                if (S("level") == "sky") play.Data["immerseType"] = "c51"; return play;
            case "song_download_url_v1": return R("/api/song/enhance/download/url/v1", new { id = V("id"), level = V("level"), immerseType = "c51" });
            case "lyric": return R("/api/song/lyric", new { id = V("id"), tv = -1, lv = -1, rv = -1, kv = -1, _nmclfl = 1 });
            case "login_qr_key": return R("/api/login/qrcode/unikey", new { type = 3 });
            case "login_qr_check": return R("/api/login/qrcode/client/login", new { key = V("key"), type = 3 });
            case "login_status": return R("/api/w/nuser/account/get", null, "weapi");
            case "user_playlist": return R("/api/user/playlist", new { uid = V("uid"), limit = V("limit", 30), offset = V("offset", 0), includeVideo = true }, "weapi");
            case "recommend_songs": return R("/api/v3/discovery/recommend/songs", null, "weapi");
            case "likelist": return R("/api/song/like/get", new { uid = V("uid") });
            case "like": return R("/api/radio/like", new { alg = "itembased", trackId = V("id"), like = S("like") != "false", time = "3" }, "weapi");
            case "top_artists": return R("/api/artist/top", Page(50), "weapi");
            case "artist_album": return R("/api/artist/albums/" + S("id"), Page(), "weapi");
            case "album": return R("/api/v1/album/" + S("id"), null, "weapi");
            case "album_sublist": return R("/api/album/sublist", Page(25), "weapi");
            case "song_music_detail": return R("/api/song/music/detail/get", new { songId = V("id") });
            case "vip_info": return R("/api/music-vip-membership/front/vip/info", new { userId = V("uid", "") }, "weapi");
            case "event": return R("/api/v1/event/get", new { pagesize = V("pagesize", 20), lasttime = V("lasttime", -1) }, "weapi");
            case "user_event": return R("/api/event/get/" + S("uid"), new { getcounts = true, time = V("lasttime", -1), limit = V("limit", 30), total = false, fromRN = "true" });
            case "msg_notices": return R("/api/msg/notices", new { limit = V("limit", 30), time = V("lasttime", -1) }, "weapi");
            case "msg_comments": return R("/api/v1/user/comments/" + S("uid"), new { beforeTime = V("before", "-1"), limit = V("limit", 30), total = "true", uid = V("uid") }, "weapi");
            case "msg_private": return R("/api/msg/private/users", new { offset = V("offset", 0), limit = V("limit", 30), total = "true" }, "weapi");
            case "msg_private_history": return R("/api/msg/private/history", new { userId = V("uid"), time = V("before", 0), limit = V("limit", 30), total = "true" }, "weapi");
            case "msg_forwards": return R("/api/forwards/get", new { offset = V("offset", 0), limit = V("limit", 30), total = "true" }, "weapi");
            case "msg_read_all": return R("/api/msg/count/clear", null, "weapi");
            case "pl_count": return R("/api/pl/count", null, "weapi");
            case "dj_recommend": return R("/api/djradio/recommend/v1", null, "weapi");
            case "dj_program": return R("/api/dj/program/byradio", new { radioId = V("rid"), limit = V("limit", 30), offset = V("offset", 0), asc = S("asc") is "true" or "1" }, "weapi");
            case "dj_sublist": return R("/api/djradio/get/subed", Page(), "weapi");
            case "playlist_tracks": return R("/api/playlist/manipulate/tracks", new { op = V("op"), pid = V("pid"), trackIds = ApiEncryption.Json(S("tracks").Split(',', StringSplitOptions.RemoveEmptyEntries)), imme = "true" });
            case "song_order_update": return R("/api/playlist/manipulate/tracks", new { pid = V("pid"), trackIds = V("ids"), op = "update" });
            case "playlist_create": return R("/api/playlist/create", new { name = V("name"), privacy = V("privacy", "0"), type = V("type", "NORMAL") }, "weapi");
            case "playlist_delete": return R("/api/playlist/remove", new { ids = "[" + S("id") + "]" }, "weapi");
            case "playlist_subscribe":
                var subscribe = R("/api/playlist/" + (N("t") == 1 ? "subscribe" : "unsubscribe"), new { id = V("id") }, "eapi", true);
                if (N("t") == 1) subscribe.Data["checkToken"] = "9ca17ae2e6ffcda170e2e6ee8af14fbabdb988f225b3868eb2c15a879b9a83d274a790ac8ff54a97b889d5d42af0feaec3b92af58cff99c470a7eafd88f75e839a9ea7c14e909da883e83fb692a3abdb6b92adee9e";
                return subscribe;
            case "resource_like": return R("/api/resource/" + (N("t") == 1 ? "like" : "unlike"), new { threadId = Thread() }, "weapi");
            case "comment_like": return R("/api/v1/comment/" + (N("t") == 1 ? "like" : "unlike"), new { threadId = Thread(), commentId = V("cid") }, "weapi");
            case "comment":
                var action = N("t", 1) switch { 0 => "delete", 2 => "reply", _ => "add" };
                var comment = R("/api/resource/comments/" + action, new { threadId = Thread() }, "eapi", true);
                if (action != "delete") comment.Data["content"] = V("content");
                if (action != "add") comment.Data["commentId"] = V("commentId"); return comment;
            case "comment_new":
                var sort = N("sortType", 3); var page = N("pageNo", 1); var size = N("pageSize", 20);
                return R("/api/v2/resource/comments", new { threadId = Thread(), pageNo = page, pageSize = size, showInner = true, sortType = sort, cursor = sort == 3 ? S("cursor", "0") : sort == 2 ? "normalHot#" + (page - 1) * size : ((page - 1) * size).ToString() });
            case "comment_music": case "comment_mv": case "comment_playlist": case "comment_album": case "comment_dj": case "comment_video": case "comment_event":
                var type = method switch { "comment_music" => 0, "comment_mv" => 1, "comment_playlist" => 2, "comment_album" => 3, "comment_dj" => 4, "comment_video" => 5, _ => 6 };
                var readComments = R("/api/v1/resource/comments/" + (type == 6 ? S("threadId") : Prefixes[type] + S("id")), new { limit = V("limit", 20), offset = V("offset", 0), beforeTime = V("before", 0) }, "weapi");
                if (type != 6) readComments.Data["rid"] = V("id"); return readComments;
            case "send_text": case "send_song": case "send_album": case "send_playlist":
                var send = R("/api/msg/private/send", new { type = method[5..], msg = V("msg", ""), userIds = "[" + S("user_ids") + "]" });
                if (method != "send_text") send.Data["id"] = V(method == "send_playlist" ? "playlist" : "id"); return send;
            case "song_red_count": return R("/api/song/red/count", new { songId = V("id") });
            case "song_like_check": return R("/api/song/like/check", new { trackIds = V("ids") });
            case "artists": return R("/api/v1/artist/" + S("id"), null, "weapi");
            case "artist_detail": return R("/api/artist/head/info/get", new { id = V("id") });
            case "artist_desc": return R("/api/artist/introduction", new { id = V("id") }, "weapi");
            case "artist_mv": return R("/api/artist/mvs", new { artistId = V("id"), limit = V("limit", 30), offset = V("offset", 0), total = true }, "weapi");
            case "mv_url": return R("/api/song/enhance/play/mv/url", new { id = V("id"), r = V("r", 1080) }, "weapi");
            case "share_resource": return R("/api/share/friends/resource", new { type = V("type", "song"), msg = V("msg", ""), id = V("id", "") }, "xeapi", true);
            default: throw new ArgumentException("不支持的 API 方法：" + method);
        }
    }
}
