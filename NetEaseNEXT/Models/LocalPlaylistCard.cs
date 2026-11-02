namespace NetEaseNEXT.Models;

public sealed class LocalPlaylistCard(string id, string name, string cover, string description)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Cover { get; } = cover;
    public string Description { get; } = description;
}
