using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using NetEaseNEXT.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunLocalMotionChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _playback.SetVolume(0);
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1);
        _profileTask = Task.FromResult<System.Text.Json.JsonElement?>(null);
        var oldPath = _localLibraryPathOverride; var oldLibrary = _localLibrary; var oldAnimations = _settings.Animations;
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        async Task Wait(Func<bool> test)
        {
            var clock = Stopwatch.StartNew();
            while (!test() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(25); Root.UpdateLayout(); }
            if (!test()) throw new TimeoutException("Local artwork motion did not settle.");
        }
        async Task<string> MakeCover(byte r, byte g, byte b)
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var pixels = new byte[64 * 64 * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 64, 64, 96, 96, pixels);
            await encoder.FlushAsync(); stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            return LocalMusicFiles.StoreCover(bytes);
        }
        try
        {
            RestoreWindow(); _settings.Theme = "Light"; _settings.Backdrop = "Solid"; _settings.Animations = true; ApplyAppearance();
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            AppWindow.MoveAndResize(new(area.X + 20, area.Y + 20, Math.Min(1050, area.Width - 40), Math.Min(850, area.Height - 40)));
            var first = new LocalPlaylist { Name = "晴昼", CoverPath = await MakeCover(35, 120, 215) };
            var second = new LocalPlaylist { Name = "夜航", CoverPath = await MakeCover(210, 65, 60) };
            var empty = new LocalPlaylist { Name = "未设置封面" };
            _localLibraryPathOverride = Path.Combine(fixture, "library.bin");
            _localLibrary = new() { StoragePath = _localLibraryPathOverride, Playlists = [first, second, empty] };
            _localLibrary.Save();
            if (Environment.GetCommandLineArgs().Contains("--motion-interactive"))
            {
                await RunMotionTourAsync(first, second, fixture);
                return;
            }
            await NavigateAsync("local");
            var card = LocalPlaylistCards.First(c => c.Id == first.Id);
            Image CardImage(object item) => FindDescendant<Image>((DependencyObject)LocalPlaylistGrid.ContainerFromItem(item))!;
            await Wait(() => LocalPlaylistGrid.ContainerFromItem(card) != null && CardImage(card) is { } i && Artwork.IsReady(i));
            var source = CardImage(card).Source;
            var starts = _coverAnimationStarts;
            await OpenLocalPlaylistAsync(card);
            check("Local card starts a native connected cover animation", !PlayerAnimationEnabled || _coverAnimationStarts > starts,
                new { enabled = PlayerAnimationEnabled, starts = _coverAnimationStarts - starts });
            check("The destination uses the clicked card's bitmap immediately", ReferenceEquals(LocalPlaylistCover.Source, source), null);
            await Wait(() => Artwork.IsReady(LocalPlaylistCover) && _activeCoverAnimation == null);
            starts = _coverAnimationStarts;
            await GoBackAsync();
            check("Returning connects the same cover back to its realized card", _view == "local" && (!PlayerAnimationEnabled || _coverAnimationStarts > starts), null);
            await Wait(() => _activeCoverAnimation == null);
            var secondCard = LocalPlaylistCards.First(c => c.Id == second.Id);
            await Wait(() => LocalPlaylistGrid.ContainerFromItem(secondCard) != null && Artwork.IsReady(CardImage(secondCard)));
            source = CardImage(secondCard).Source;
            await OpenLocalPlaylistAsync(secondCard);
            check("Opening another playlist immediately shows its own cover and title", LocalPlaylistTitle.Text == second.Name && ReferenceEquals(LocalPlaylistCover.Source, source), null);
            await Wait(() => Artwork.IsReady(LocalPlaylistCover) && _activeCoverAnimation == null);
            var prior = LocalPlaylistCover.Source;
            _localPlaylistId = first.Id;
            var opening = NavigateAsync("local-playlist");
            check("A direct route clears the previous image before asynchronous loading", LocalPlaylistTitle.Text == first.Name && LocalPlaylistCover.Source == null, null);
            await opening; await Wait(() => Artwork.IsReady(LocalPlaylistCover));
            check("The replaced cover cannot be restored by an old request", !ReferenceEquals(prior, LocalPlaylistCover.Source) && Artwork.GetUri(LocalPlaylistCover) == new Uri(first.CoverPath).AbsoluteUri, null);
            _localPlaylistId = empty.Id; opening = NavigateAsync("local-playlist");
            check("A playlist without artwork never flashes the previous cover", LocalPlaylistCover.Source == null && LocalPlaylistTitle.Text == empty.Name, null);
            await opening;
            check("Empty artwork stays empty after the queued loader", LocalPlaylistCover.Source == null, null);
            for (var i = 0; i < 3; i++)
            {
                _localPlaylistId = first.Id; var a = NavigateAsync("local-playlist");
                _localPlaylistId = second.Id; var b = NavigateAsync("local-playlist");
                await Task.WhenAll(a, b); await Wait(() => Artwork.IsReady(LocalPlaylistCover));
                check("Rapid navigation keeps the latest cover " + i, LocalPlaylistTitle.Text == second.Name && Artwork.GetUri(LocalPlaylistCover) == new Uri(second.CoverPath).AbsoluteUri, null);
            }
            Artwork.SetUri(LocalPlaylistCover, new Uri(first.CoverPath).AbsoluteUri);
            check("Changing Artwork.Uri retires stale pixels synchronously", LocalPlaylistCover.Source == null, null);
            Artwork.SetUri(LocalPlaylistCover, new Uri(second.CoverPath).AbsoluteUri);
            await Wait(() => Artwork.IsReady(LocalPlaylistCover));
            await NavigateAsync("local"); card = LocalPlaylistCards.First(c => c.Id == first.Id);
            await Wait(() => LocalPlaylistGrid.ContainerFromItem(card) != null && Artwork.IsReady(CardImage(card)));
            _settings.Animations = false;
            starts = _coverAnimationStarts;
            await OpenLocalPlaylistAsync(card); await Wait(() => Artwork.IsReady(LocalPlaylistCover));
            check("Disabled animations still navigate and display the correct cover", _coverAnimationStarts == starts && _activeCoverAnimation == null && LocalPlaylistTitle.Text == first.Name, null);
            check("Disabled animations remove native collection transitions", LocalPlaylistGrid.ItemContainerTransitions.Count == 0, null);
            check("Disabled animations stop cover parallax and retain static back icons", LocalCoverParallax.VerticalShift == 0 && PlaylistCoverParallax.VerticalShift == 0 && BackAnimatedIcon.FallbackIconSource != null, null);
            var visual = ElementCompositionPreview.GetElementVisual(PageBody);
            visual.Properties.TryGetVector3("Translation", out var translation);
            check("Disabled page motion ends fully visible at its resting position", visual.Opacity == 1 && translation == System.Numerics.Vector3.Zero, null);
            _settings.Animations = true;
            SetCoverFeedback(LocalPlaylistCover, .98f); StopInterfaceMotion();
            var coverVisual = ElementCompositionPreview.GetElementVisual(LocalPlaylistCover);
            check("Stopping motion clears feedback transforms and pending transitions", coverVisual.Scale == System.Numerics.Vector3.One && _feedbackCovers.Count == 0 && _pendingCoverAnimation == null && _activeCoverAnimation == null, null);
            check("Player buttons receive pointer and keyboard feedback without changing their templates", _actionMotionButtons.Contains(PlayPauseButton) && _actionMotionButtons.Contains(QueueButton), null);
            ConfigureInterfaceMotion();
            check("Local cards use native add, remove and reposition transitions", !PlayerAnimationEnabled || LocalPlaylistGrid.ItemContainerTransitions.Count == 2, null);
            ShowQueue_Click(QueueButton, new RoutedEventArgs());
            check("Queue opens with its content already bound", QueuePane.Visibility == Visibility.Visible && ReferenceEquals(QueueList.ItemsSource, _playback.Queue), null);
            ShowQueue_Click(QueueButton, new RoutedEventArgs());
            check("Queue keeps content through the exit animation", QueuePane.Visibility == Visibility.Collapsed && ReferenceEquals(QueueList.ItemsSource, _playback.Queue), null);
            SetCoverFeedback(PlayPauseButton, .98f);
            SetCoverFeedback(PlayPauseButton, 1);
            StopInterfaceMotion();
            check("A spring return can be interrupted without leaving a scaled player button", ElementCompositionPreview.GetElementVisual(PlayPauseButton).Scale == System.Numerics.Vector3.One, null);
            ConfigureInterfaceMotion();
            check("Both playlist covers track the real scrolling list for subtle parallax", !PlayerAnimationEnabled || LocalCoverParallax.Source == TrackList && PlaylistCoverParallax.Source == TrackList && LocalCoverParallax.VerticalShift == 16 && PlaylistCoverParallax.VerticalShift == 16, null);
            check("Back buttons use native animated vector sources", !PlayerAnimationEnabled || BackAnimatedIcon.Source != null && PlayerBackAnimatedIcon.Source != null, null);
            await NavigateAsync("settings");
            await Wait(() => !PlayerAnimationEnabled || Descendants<Border>(SettingsPanel).Any(b => b.BackgroundTransition != null));
            check("Settings surfaces use native brush transitions", !PlayerAnimationEnabled || Descendants<Border>(SettingsPanel).Any(b => b.BackgroundTransition != null), null);
            await NavigateAsync("downloads");
            var played = await PlayDownloadCompletionMotionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            check("A completion vector plays once and returns to its hidden resting state", !PlayerAnimationEnabled || played && DownloadCompletion.Visibility == Visibility.Collapsed, null);
            _settings.Animations = false; ConfigureInterfaceMotion();
            check("Disabled animations skip vector playback and queue opacity transitions", !await PlayDownloadCompletionMotionAsync() && QueueGlyph.OpacityTransition == null, null);
            _settings.Animations = true;
            await RunMotionRefinementChecksAsync(fixture, check);
            if (Environment.GetCommandLineArgs().Contains("--motion-release-check")) await RunReleaseMotionChecksAsync(check);
            _localPlaylistId = first.Id; await NavigateAsync("local-playlist");
            await Wait(() => Artwork.IsReady(LocalPlaylistCover));
            await SaveLibraryImageAsync(Root, "local-motion-detail.png");
            if (Environment.GetCommandLineArgs().Contains("--motion-tour"))
                await RunMotionTourAsync(first, second, fixture);
        }
        finally
        {
            StopInterfaceMotion(); StopLocalMonitoring();
            _settings.Animations = oldAnimations; _localLibraryPathOverride = oldPath; _localLibrary = oldLibrary;
        }
    }
}
