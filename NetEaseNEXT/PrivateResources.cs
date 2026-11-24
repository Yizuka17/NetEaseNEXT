using System.Text.Json;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private sealed record PrivateResource(string Kind, long Id, string Name, string Cover, string Link);
    private static string PrivateShareText(JsonElement payload, int depth = 0)
    {
        if (depth > 8) return "";
        foreach (var key in new[] { "msg", "message", "content" })
        {
            if (!payload.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Object) return PrivateShareText(value, depth + 1);
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) continue;
            var text = value.GetString()!;
            if (text.TrimStart().StartsWith('{') && Payload(payload, key) is { ValueKind: JsonValueKind.Object } nested) return PrivateShareText(nested, depth + 1);
            return text;
        }
        return "";
    }
    private static PrivateResource? PrivateResourceLink(string text, string name, string cover, int depth = 0)
    {
        if (depth > 3 || text.Length > 16384 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is "http" or "https" && uri.Host is not ("music.163.com" or "y.music.163.com")) return null;
        if (uri.Scheme is not ("http" or "https" or "orpheus")) return null;
        if (uri.Fragment.StartsWith("#/", StringComparison.Ordinal)) return PrivateResourceLink(uri.GetLeftPart(UriPartial.Authority) + uri.Fragment[1..], name, cover, depth + 1);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Length == 2 ? Uri.UnescapeDataString(g.Last()[1]) : "", StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "url", "url1", "url2" })
            if (query.TryGetValue(key, out var target) && PrivateResourceLink(target, name, cover, depth + 1) is { } wrapped) return wrapped;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kind = uri.Scheme == "orpheus" ? uri.Host : path.LastOrDefault() ?? "";
        kind = kind.ToLowerInvariant() switch { "djradio" or "radio" => "radio", "song" => "song", "album" => "album", "playlist" => "playlist", "program" or "djprogram" => "program", _ => "" };
        if (kind.Length == 0) return null;
        var rawId = query.GetValueOrDefault("id", query.GetValueOrDefault("rid", ""));
        if (rawId.Length == 0 && uri.Scheme == "orpheus") rawId = path.FirstOrDefault() ?? "";
        return long.TryParse(rawId, out var id) && id > 0 ? new(kind, id, name, cover, text) : null;
    }
    private static PrivateResource? GeneralPrivateResource(JsonElement payload, int depth = 0)
    {
        if (depth > 8) return null;
        var general = FirstObject(Object(payload, "generalMsg"), Object(payload, "resource"));
        if (general.ValueKind == JsonValueKind.Object)
        {
            var name = FirstText(general.Text("resName"), general.Text("name"), general.Text("title"));
            var cover = FirstText(general.Text("cover"), general.Text("picUrl"), general.Text("coverUrl"));
            foreach (var key in new[] { "nativeUrl", "webUrl", "url" })
                if (PrivateResourceLink(general.Text(key), name, cover) is { } linked) return linked;
            var kind = FirstText(general.Text("resType"), payload.Text("resType"), general.Text("type")).ToLowerInvariant() switch
            { "album" => "album", "playlist" => "playlist", "djradio" or "radio" => "radio", "program" or "djprogram" => "program", "song" => "song", _ => "" };
            var id = Math.Max(general.Number("resId"), general.Number("id"));
            if (kind.Length > 0 && id > 0) return new(kind, id, name, cover, "");
        }
        var nested = Payload(payload, "msg");
        return nested.ValueKind == JsonValueKind.Object ? GeneralPrivateResource(nested, depth + 1) : null;
    }
    private static CatalogItem PrivateCatalog(PrivateResource resource) => new(resource.Id, resource.Name.Length > 0 ? resource.Name : resource.Kind switch { "album" => "专辑", "playlist" => "歌单", "program" => "播客单集", _ => "播客" }, resource.Cover, resource.Kind, resource.Kind switch { "album" => "专辑", "playlist" => "歌单", "program" => "播客单集", _ => "播客" });
}
