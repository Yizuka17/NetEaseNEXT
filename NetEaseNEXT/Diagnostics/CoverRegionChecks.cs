using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunCoverRegionChecksAsync(string fixture, LocalPlaylist first, LocalPlaylist second, Action<string, bool, object?> check)
    {
        async Task Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < 15) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Cover and viewport checks did not settle");
        }
        var a = new Uri(first.CoverPath).AbsoluteUri; var b = new Uri(second.CoverPath).AbsoluteUri;
        _settings.Animations = true; ConfigureInterfaceMotion();
        await NavigateAsync("local");
        var card = LocalPlaylistCards.First(c => c.Id == first.Id); LocalPlaylistGrid.ScrollIntoView(card);
        await Wait(() => LocalPlaylistGrid.ContainerFromItem(card) is DependencyObject c && FindDescendant<Image>(c) is { } i && Artwork.IsReady(i));
        var image = FindDescendant<Image>((DependencyObject)LocalPlaylistGrid.ContainerFromItem(card))!;
        check("Connected cover snapshots include the rounded frame", RoundedCoverHost(image) is Border { CornerRadius.TopLeft: > 0 }, null);
        Artwork.Seed(image, a, image.Source!);
        check("Returning snapshots preserve the card URI binding", image.GetBindingExpression(Artwork.UriProperty) != null, null);
        image.DataContext = new LocalPlaylistCard("recycled", "复用卡片", b, "");
        await Wait(() => Artwork.IsReady(image) && Artwork.TrySnapshot(image, b, out _));
        check("A recycled card updates to the new item's own bitmap", !Artwork.TrySnapshot(image, a, out _), null);
        image.ClearValue(FrameworkElement.DataContextProperty);
        await Wait(() => Artwork.IsReady(image) && Artwork.TrySnapshot(image, a, out _));
        PrepareCoverTransition("LocalPlaylistCover", image, a);
        var frozen = (_coverTransitionProxy?.Child as Image)?.Source;
        image.DataContext = new LocalPlaylistCard("snapshot", "过渡源被回收", b, "");
        check("Source recycling cannot clear the independent rounded animation snapshot", frozen != null && ReferenceEquals((_coverTransitionProxy?.Child as Image)?.Source, frozen) && _coverTransitionProxy!.CornerRadius.TopLeft > 0, null);
        CancelCoverTransitions();
        image.ClearValue(FrameworkElement.DataContextProperty);
        await Wait(() => Artwork.IsReady(image) && Artwork.TrySnapshot(image, a, out _));
        PrepareCoverTransition("LocalPlaylistCover", image, b);
        check("A recycled card with the wrong image identity cannot prepare a transition", _pendingCoverAnimation == null, null);
        PrepareCoverTransition("LocalPlaylistCover", image, a);
        Artwork.SetUri(LocalPlaylistCover, b); var starts = _coverAnimationStarts;
        StartCoverTransition(LocalPlaylistCover);
        check("A stale connected snapshot cannot overwrite another cover URI", _pendingCoverAnimation == null && _coverAnimationStarts == starts && Artwork.GetUri(LocalPlaylistCover) == b, null);
        await OpenLocalPlaylistAsync(card); await Wait(() => Artwork.IsReady(LocalPlaylistCover) && _activeCoverAnimation == null);
        await GoBackAsync(); await Wait(() => _activeCoverAnimation == null);
        card = LocalPlaylistCards.First(c => c.Id == first.Id);
        LocalPlaylistGrid.ScrollIntoView(card); await Task.Delay(60); Root.UpdateLayout();
        await OpenLocalPlaylistAsync(card);
        check("A flying local cover has no source proxy painted at the original position", _coverTransitionProxy == null || !Root.Children.Contains(_coverTransitionProxy), null);
        await Wait(() => _activeCoverAnimation == null);
        Artwork.SetUri(LocalPlaylistCover, b);
        check("Detail artwork retires old pixels in the URI-change turn", LocalPlaylistCover.Source == null && !Artwork.TrySnapshot(LocalPlaylistCover, a, out _), null);
        await Wait(() => Artwork.IsReady(LocalPlaylistCover));
        for (var i = 0; i < 8; i++) { Artwork.SetUri(LocalPlaylistCover, a); await Task.Delay(1); Artwork.SetUri(LocalPlaylistCover, b); }
        await Wait(() => Artwork.IsReady(LocalPlaylistCover));
        check("Rapid image loads retain only the latest URI and matching bitmap", Artwork.TrySnapshot(LocalPlaylistCover, b, out _) && !Artwork.TrySnapshot(LocalPlaylistCover, a, out _), null);

        _settings.Animations = false; ConfigureInterfaceMotion();
        await NavigateAsync("radio"); var radio = Catalog.First();
        await OpenCatalogItemAsync(radio); await Wait(() => Artwork.IsReady(PlaylistDetailCover));
        check("Podcast headers keep the podcast cover with animations disabled", Artwork.SameImage(Artwork.GetUri(PlaylistDetailCover), radio.Cover) && Tracks.Any(t => !Artwork.SameImage(t.Cover, radio.Cover)), null);
        await NavigateAsync("queue"); await GoBackAsync();
        check("History retains the podcast cover independently of a connected snapshot", _view == "programs" && Artwork.SameImage(Artwork.GetUri(PlaylistDetailCover), radio.Cover), null);
        _settings.Animations = true; ConfigureInterfaceMotion();
        await OpenCatalogCardAsync(new(202, "无容器播客", b, "radio"));
        await Wait(() => Artwork.IsReady(PlaylistDetailCover));
        check("An unrealized podcast card cannot inherit another card's pending cover", Artwork.TrySnapshot(PlaylistDetailCover, b, out _) && _pendingCoverAnimation == null, null);
        var old = OpenCatalogItemAsync(new(203, "旧播客", a, "radio"));
        var latest = OpenCatalogItemAsync(new(204, "新播客", b, "radio"));
        await Task.WhenAll(old, latest); await Wait(() => Artwork.IsReady(PlaylistDetailCover));
        check("Canceled podcast routes cannot restore a previous cover", _playlistId == 204 && Artwork.TrySnapshot(PlaylistDetailCover, b, out _), null);

        _playlistId = 101; _playlistName = "歌曲区域"; await NavigateAsync("playlist");
        var audio = Path.Combine(fixture, "switch-0.wav");
        var songs = Enumerable.Range(1, 160).Select(i => new Track(-90000 - i, "随机歌曲 " + i, "本地验证", "", a, 30000) { LocalPath = audio }).ToArray();
        _restoringPlayback = true;
        try
        {
            _playback.Mode = "Sequential"; Tracks.Clear(); foreach (var song in songs) Tracks.Add(song);
            _playlistTrackIds = songs.Select(s => s.Id).ToArray();
            Root.UpdateLayout(); var scroll = FindDescendant<ScrollViewer>(TrackList)!; scroll.ChangeView(null, 0, null, true); await Task.Delay(100);
            _trackSelectionMotion.Item = null; _trackSelectionMotion.Y = double.NaN;
            SelectPlayingTrack(TrackList, songs[0]); await Task.Delay(50); Root.UpdateLayout(); UpdateSelectionIndicators(true);
            var rows = SongRowBounds(TrackList, scroll.VerticalOffset);
            check("The moving indicator clip starts below cover and playlist controls", rows.Top > PlaylistCoverFrame.ActualHeight && TrackSelectionLayer.Clip is RectangleGeometry geometry && Math.Abs(geometry.Rect.Top - rows.Top) < 1, new { rows.Top });
            check("Cross-playlist entrance stays inside the song-row clip", TrackSelectionIndicator.Visibility == Visibility.Visible && TrackSelectionLayer.Clip is RectangleGeometry { Rect.Top: > 100 } && TrackSelectionClip.Clip is RectangleGeometry, null);
            await SaveLibraryImageAsync(Root, "indicator-song-region.png");
            var route = _route; var offset = scroll.VerticalOffset; double furthest = offset;
            void Scrolled(object? sender, ScrollViewerViewChangedEventArgs args) => furthest = Math.Max(furthest, scroll.VerticalOffset);
            scroll.ViewChanged += Scrolled;
            try
            {
                _playback.Mode = "Shuffle";
                for (var i = 0; i < 8; i++) { await PlayAllAsync(); await Task.Delay(75); }
                await Task.Delay(300); Root.UpdateLayout();
                check("Shuffle starts playback without changing route or scrolling to its row", ReferenceEquals(route, _route) && _view == "playlist" && Math.Abs(scroll.VerticalOffset - offset) < 1 && Math.Abs(furthest - offset) < 1, new { offset, scroll.VerticalOffset, furthest });
            }
            finally { scroll.ViewChanged -= Scrolled; }
            await SaveLibraryImageAsync(Root, "shuffle-keeps-view.png");
            _playback.Mode = "Sequential";
            var revealRequests = 0;
            void Reveal(UIElement sender, BringIntoViewRequestedEventArgs args) => revealRequests++;
            TrackList.BringIntoViewRequested += Reveal;
            try
            {
                await _playback.PlayAsync(songs[0], songs); await Task.Delay(100);
                check("Visible playback updates only its marker without native row selection", TrackList.SelectedItem == null && ReferenceEquals(_trackSelectionMotion.PlaybackItem, songs[0]) && revealRequests == 0, new { revealRequests });
                await _playback.PlayAsync(songs[120], songs); await Task.Delay(100);
                check("Offscreen playback never selects or requests reveal of its native row", TrackList.SelectedItem == null && _trackSelectionMotion.PlaybackSelection && ReferenceEquals(_trackSelectionMotion.PlaybackItem, songs[120]) && revealRequests == 0, new { revealRequests });
            }
            finally { TrackList.BringIntoViewRequested -= Reveal; }
            await LocateCurrentAsync(_api);
            check("Explicit locate still reveals the playing row", TrackList.ContainerFromItem(songs[120]) is FrameworkElement row && row.TransformToVisual(TrackList).TransformPoint(new()).Y >= -1 && row.TransformToVisual(TrackList).TransformPoint(new()).Y < TrackList.ActualHeight, null);
        }
        finally { _restoringPlayback = false; _playback.Restore(new()); }
    }
}
