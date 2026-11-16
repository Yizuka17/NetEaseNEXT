using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private void ClearAlbumDetails()
    {
        AlbumArtists.Children.Clear(); AlbumArtists.Visibility = AlbumFacts.Visibility = Visibility.Collapsed;
        AlbumFacts.Text = "";
        PlaylistCreator.Visibility = PlaylistTags.Visibility = Visibility.Visible;
    }
    private void ApplyAlbumDetails(JsonElement album)
    {
        ClearAlbumDetails();
        PlaylistCreator.Visibility = PlaylistTags.Visibility = Visibility.Collapsed;
        PlaylistDetailTitle.Text = FirstText(album.Text("name"), _playlistName);
        Artwork.SetUri(PlaylistDetailCover, album.Text("picUrl"));
        PlaylistDescription.Text = album.Text("description"); PlaylistDescription.MaxLines = 3;
        ExpandDescriptionButton.Content = "展开简介";
        ExpandDescriptionButton.Visibility = PlaylistDescription.Text.Length > 100 ? Visibility.Visible : Visibility.Collapsed;
        var artists = new List<SongArtist>();
        if (album.TryGetProperty("artists", out var array) && array.ValueKind == JsonValueKind.Array)
            artists.AddRange(array.EnumerateArray().Select(a => new SongArtist(a.Number("id"), a.Text("name"))));
        if (artists.Count == 0 && Object(album, "artist") is { ValueKind: JsonValueKind.Object } artist)
            artists.Add(new(artist.Number("id"), artist.Text("name")));
        foreach (var person in artists.Where(a => a.Name.Length > 0).DistinctBy(a => (a.Id, a.Name)))
        {
            if (AlbumArtists.Children.Count > 0) AlbumArtists.Children.Add(new TextBlock { Text = "/", Opacity = .6, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 4, 0) });
            if (person.Id > 0)
            {
                var link = new HyperlinkButton { Content = person.Name, Tag = person, Padding = new(0), MinHeight = 28 };
                link.Click += AlbumArtist_Click; AlbumArtists.Children.Add(link);
            }
            else AlbumArtists.Children.Add(new TextBlock { Text = person.Name, VerticalAlignment = VerticalAlignment.Center });
        }
        AlbumArtists.Visibility = AlbumArtists.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var facts = new List<string>();
        var published = album.Number("publishTime");
        if (published > 0 && published <= 253402271999999L)
            facts.Add("发行时间：" + DateTimeOffset.FromUnixTimeMilliseconds(published).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"));
        var kind = string.Join(" / ", new[] { album.Text("type"), album.Text("subType") }.Where(s => s.Length > 0).Distinct());
        if (kind.Length > 0) facts.Add("专辑类型：" + kind);
        if (album.Text("company") is { Length: > 0 } company) facts.Add("发行公司：" + company);
        if (album.Text("language") is { Length: > 0 } language) facts.Add("语种：" + language);
        AlbumFacts.Text = string.Join('\n', facts);
        AlbumFacts.Visibility = facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void AlbumArtist_Click(object sender, RoutedEventArgs args)
    {
        if (!_hidden && _view == "album" && sender is HyperlinkButton { Tag: SongArtist artist }) await NavigateToArtistAsync(artist);
    }
}
