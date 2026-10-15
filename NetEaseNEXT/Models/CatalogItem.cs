using System.Text.Json;

namespace NetEaseNEXT.Models;

public sealed class CatalogItem(long id, string name, string cover, string kind, string subtitle = "")
{
    public long Id { get; } = id;
    public string Name { get; } = name;
    public string Cover { get; } = cover;
    public string Kind { get; } = kind;
    public string Subtitle { get; } = subtitle;
    public override string ToString() => Name;
    public static CatalogItem Artist(JsonElement item) => new(item.Number("id"), item.Text("name"), item.Text("picUrl"), "artist");
    public static CatalogItem Album(JsonElement item) => new(item.Number("id"), item.Text("name"), item.Text("picUrl"), "album",
        item.TryGetProperty("artist", out var artist) ? artist.Text("name") : "");
}
public sealed class SocialItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string Text { get; set; } = "";
    public string Picture { get; set; } = "";
    public List<string> Pictures { get; set; } = [];
    public Track? SharedTrack { get; set; }
    public SocialItem? LinkedEvent { get; set; }
    public Microsoft.UI.Xaml.Visibility PictureVisibility => Pictures.Count == 0 && Picture.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility PicturesVisibility => Pictures.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility SharedTrackVisibility => SharedTrack != null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string Time { get; set; } = "";
    public long Timestamp { get; set; }
    public bool Unread { get; set; }
    public long UnreadCount { get; set; }
    public string InboxCategory { get; set; } = "";
    public Microsoft.UI.Xaml.Visibility UnreadVisibility => Unread ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility LikedVisibility => Liked ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility UnlikedVisibility => Liked ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string DisplayText => Services.Emojis.Display(Text);
    public string DisplayOriginalText => Services.Emojis.Display(OriginalText);
    public Func<Task>? LikeAsync { get; set; }
    public Action? Reply { get; set; }
    public long PeerId { get; set; }
    public long Id { get; set; }
    public string ThreadId { get; set; } = "";
    public string Kind { get; set; } = "";
    public long ResourceId { get; set; }
    public string ResourceKey { get; set; } = "";
    public int ResourceType { get; set; }
    public long CommentId { get; set; }
    public string OriginalText { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Liked { get; set; }
    public long LikeCount { get; set; }
    public long CommentCount { get; set; }
    public Microsoft.UI.Xaml.Visibility ActionsVisibility => Kind == "comment" || Kind == "event" && ThreadId.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility OriginalVisibility => OriginalText.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ActionVisibility => Action.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string LikeLabel => $"{(Liked ? "已赞" : "赞")} {LikeCount}";
    public string CommentLabel => Kind == "comment" ? "回复" : $"评论 {CommentCount}";
    public void NotifyInteraction()
    {
        PropertyChanged?.Invoke(this, new(nameof(LikeLabel)));
        PropertyChanged?.Invoke(this, new(nameof(CommentLabel)));
        PropertyChanged?.Invoke(this, new(nameof(LikedVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(UnlikedVisibility)));
    }
    public void MarkRead() { Unread = false; PropertyChanged?.Invoke(this, new(nameof(UnreadVisibility))); }
    public override string ToString() => Name;
}
