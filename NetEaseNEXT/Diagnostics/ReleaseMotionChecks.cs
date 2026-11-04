using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunReleaseMotionChecksAsync(Action<string, bool, object?> check)
    {
        async Task Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition() && DateTime.UtcNow < deadline) { Root.UpdateLayout(); await Task.Delay(25); }
            if (!condition())
            {
                var item = (object?)Playlists.FirstOrDefault() ?? Catalog.FirstOrDefault();
                var grid = Playlists.Count > 0 ? PlaylistGrid : CatalogGrid;
                var image = item != null && grid.ContainerFromItem(item) is DependencyObject container ? FindDescendant<Image>(container) : null;
                await SaveLibraryImageAsync(Root, "release-motion-timeout.png");
                throw new TimeoutException("Release motion did not settle: " + System.Text.Json.JsonSerializer.Serialize(new { _view, playlists = Playlists.Count, catalog = Catalog.Count, image = image == null ? null : Artwork.Inspect(image), active = _activeCoverAnimation != null, cover = Artwork.Inspect(PlaylistDetailCover) }));
            }
        }
        Vector3 Translation(FrameworkElement icon)
        {
            ElementCompositionPreview.GetElementVisual(icon).Properties.TryGetVector3("Translation", out var value); return value;
        }
        _settings.Animations = true; ConfigureInterfaceMotion(); ResetIconBounces();
        BounceActionIcon(BackButton); BounceActionIcon(PlayerBackButton); BounceActionIcon(AccountButton);
        BounceSidebarIcon((NavigationViewItem)Nav.SettingsItem);
        SetCoverFeedback(BackButton, .98f); SetCoverFeedback(AccountButton, .98f);
        check("Back, account and settings never acquire spring transforms", _jumpingIcons.Count == 0 && !_feedbackCovers.Contains(BackButton) && !_feedbackCovers.Contains(AccountButton), null);
        check("Search uses the native Find animated vector", SearchAnimatedIcon.Source is Microsoft.UI.Xaml.Controls.AnimatedVisuals.AnimatedFindVisualSource && ReferenceEquals(SearchBox.QueryIcon, SearchAnimatedIcon), null);
        BounceActionIcon(NextButton); BounceActionIcon(PreviousButton); BounceActionIcon(PlayPauseButton);
        await Task.Delay(155);
        var nextIcon = ActionIcon(NextButton)!; var previousIcon = ActionIcon(PreviousButton)!;
        check("Transport feedback runs only on the requested icons, with a scale animation for play", _jumpingIcons.SetEquals(new[] { nextIcon, previousIcon, PlayPauseIcon }) &&
            ElementCompositionPreview.GetElementVisual(nextIcon).TryGetAnimationController("Translation") != null &&
            ElementCompositionPreview.GetElementVisual(previousIcon).TryGetAnimationController("Translation") != null &&
            ElementCompositionPreview.GetElementVisual(PlayPauseIcon).TryGetAnimationController("Scale") != null, null);
        await Task.Delay(FluentMotion.Duration);
        check("All transport icons settle at the original position", Translation(nextIcon).Length() < .1 && Translation(previousIcon).Length() < .1 && Translation(PlayPauseIcon).Length() < .1, null);

        _accountId = 42; // Synthetic profile used only with MotionApiFixtures.
        foreach (var source in new[] { "discover", "library", "saved-albums", "radio", "artists" })
        {
            await NavigateAsync(source);
            var isPlaylist = source is "discover" or "library";
            var grid = isPlaylist ? PlaylistGrid : CatalogGrid;
            check(source + " fixture loads its cards", isPlaylist ? Playlists.Count > 0 : Catalog.Count > 0, null);
            object item = isPlaylist ? Playlists[0] : Catalog[0];
            grid.ScrollIntoView(item);
            await Wait(() => grid.ContainerFromItem(item) is DependencyObject container && FindDescendant<Image>(container) is { } image && Artwork.IsReady(image));
            var starts = _coverAnimationStarts;
            if (isPlaylist) await OpenOnlinePlaylistAsync((Models.Playlist)item);
            else await OpenCatalogCardAsync((Models.CatalogItem)item);
            var cover = source == "artists" ? ArtistCover : PlaylistDetailCover;
            await Wait(() => Artwork.IsReady(cover) && _activeCoverAnimation == null);
            check(source + " opens a matching detail cover with a connected animation", _coverAnimationStarts > starts && cover.Source != null && Artwork.GetUri(cover).Length > 0, _view);
            starts = _coverAnimationStarts; await GoBackAsync();
            await Wait(() => _activeCoverAnimation == null);
            check(source + " restores its card with the reverse connected animation", _view == source && _coverAnimationStarts > starts, null);
        }
        check("Online, local and radio cards share native collection transitions", new[] { LocalPlaylistGrid, PlaylistGrid, CatalogGrid }.All(g => g.ItemContainerTransitions.Count == 2), null);
        await NavigateAsync("queue"); Root.UpdateLayout();
        if (Tracks.Count >= 2)
        {
            TrackList.SelectedItem = Tracks[0]; UpdateSelectionIndicators(false);
            TrackList.SelectedItem = Tracks[1]; await Task.Delay(155);
            check("Song indicator runs both stretch and position animations", ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).TryGetAnimationController("Scale") != null &&
                ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).TryGetAnimationController("Translation") != null, null);
            await Task.Delay(FluentMotion.Duration);
            check("Song indicator finishes at normal height", ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).Scale == Vector3.One, null);
        }
        _settings.Animations = false; ApplyAppearance();
        check("Reduced motion removes every card collection transition", new[] { LocalPlaylistGrid, PlaylistGrid, CatalogGrid }.All(g => g.ItemContainerTransitions.Count == 0), null);
        _settings.Animations = true; ApplyAppearance();
    }
}
