using System.Text.RegularExpressions;

namespace NetEaseNEXT.Services;

public static class Emojis
{
    public static readonly (string Token, string Glyph)[] Items =
    [
        ("[大笑]", "😄"), ("[可爱]", "😊"), ("[憨笑]", "😁"), ("[色]", "😍"), ("[亲亲]", "😘"), ("[惊恐]", "😱"),
        ("[流泪]", "😢"), ("[大哭]", "😭"), ("[呲牙]", "😬"), ("[哀伤]", "😔"), ("[害羞]", "☺️"), ("[吐舌]", "😛"),
        ("[奸笑]", "😏"), ("[调皮]", "😜"), ("[晕]", "😵"), ("[怒]", "😠"), ("[鬼脸]", "🤪"), ("[汗]", "😅"),
        ("[捂脸]", "🤦"), ("[强]", "👍"), ("[弱]", "👎"), ("[爱心]", "❤️"), ("[心碎]", "💔"), ("[拜]", "🙏"),
        ("[牵手]", "🤝"), ("[OK]", "👌"), ("[胜利]", "✌️"), ("[示爱]", "💗"), ("[钻石]", "💎"), ("[礼物]", "🎁"),
        ("[蛋糕]", "🎂"), ("[钟情]", "💕"), ("[星星]", "⭐"), ("[便便]", "💩"), ("[狗]", "🐶"), ("[猫]", "🐱")
    ];
    private static readonly Dictionary<string, string> Map = Items.ToDictionary(e => e.Token, e => e.Glyph);
    public static string Display(string text) => Regex.Replace(text, @"\[[^\[\]\r\n]{1,8}\]", m => Map.TryGetValue(m.Value, out var glyph) ? glyph : m.Value);
}
