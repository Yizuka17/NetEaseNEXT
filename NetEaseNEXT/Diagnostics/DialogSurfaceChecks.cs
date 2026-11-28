using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunDialogSurfaceChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = _authenticated = true; _accountId = 42;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        RestoreWindow(); AppWindow.Resize(new(1280, 900));
        var physicalInput = Environment.GetCommandLineArgs().Contains("--dialog-surfaces-input");
        var separatorInput = Environment.GetCommandLineArgs().Contains("--dialog-separator-input");
        async Task Wait(Func<bool> condition, int seconds = 12)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) { await Task.Delay(30); Root.UpdateLayout(); }
            if (!condition()) throw new TimeoutException("Dialog surface did not settle: " + _dialog?.Title + ", loaded=" + _dialog?.IsLoaded + ", ready=" + _dialogSurfaceReady + ", notice=" + Notice.Message + ", player=" + PlayerNotice.Message);
        }
        async Task Closed() => await Wait(() => _dialog == null && !_dialogSurfaceReady);
        async Task Inspect(string name, Func<Task> open, bool outside = true)
        {
            var showing = open();
            await Wait(() => _dialogSurfaceReady && _dialog is { IsLoaded: true } || showing.IsCompleted);
            if (_dialog == null) { await showing; throw new InvalidOperationException(name + " did not open: " + Notice.Message + " / " + PlayerNotice.Message); }
            await Task.Delay(180); Root.UpdateLayout();
            var dialog = _dialog!;
            var frame = FindDescendant<Border>(dialog, b => b.Name == "BackgroundElement")!;
            var layout = FindDescendant<Grid>(dialog, g => g.Name == "LayoutRoot")!;
            check(name + " uses native shadow, shared corners and one physical-pixel stroke",
                frame.Shadow is ThemeShadow && frame.Translation.Z == 32 && frame.CornerRadius == _floatingCorner && frame.BorderThickness == FloatingStroke, new { shadow = frame.Shadow?.GetType().Name, frame.Translation.Z, radius = frame.CornerRadius.TopLeft, expectedRadius = _floatingCorner.TopLeft, stroke = frame.BorderThickness.Left, expectedStroke = FloatingStroke.Left });
            check(name + " has a full-window transparent input surface",
                layout.Children[0] is Microsoft.UI.Xaml.Shapes.Rectangle { Name: "DialogOutsideInput", Fill: SolidColorBrush { Color.A: 0 }, IsHitTestVisible: true } input && input.ActualWidth >= Root.ActualWidth - 1 && input.ActualHeight >= Root.ActualHeight - TitleBar.ActualHeight - 1, new { layout.ActualWidth, layout.ActualHeight });
            // Reapplying theme/material must preserve elevation and input coverage.
            ApplyFloatingMaterials();
            check(name + " retains its shadow after a material refresh", frame.Shadow is ThemeShadow && frame.Translation.Z == 32, null);
            var hasButtons = dialog.PrimaryButtonText.Length > 0 || dialog.SecondaryButtonText.Length > 0 || dialog.CloseButtonText.Length > 0;
            var separator = (Grid)FindDescendant<ScrollViewer>(dialog, s => s.Name == "ContentScrollViewer")!.Content;
            check(name + " only separates a visible command area", hasButtons ? separator.BorderThickness.Bottom > 0 : separator.BorderThickness == new Thickness(0), separator.BorderThickness.Bottom);
            if (name.EndsWith("favorite"))
            {
                var list = Descendants<ListView>(dialog).Single(); list.SelectedIndex = 0;
                check(name + " allows selecting a playlist", dialog.IsPrimaryButtonEnabled, null);
            }
            if (physicalInput && (name == "Light-information" || name == "Light-favorite" || name == "Dark-information") || separatorInput && (name.EndsWith("-information") || name.EndsWith("-comments")))
            {
                await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "dialog-surface-stage.json"), JsonSerializer.Serialize(new { name, pid = Environment.ProcessId }));
                await Wait(() => !_dialogSurfaceReady, 180);
            }
            else if (outside) DismissDialogFromOutside(dialog, true);
            else
            {
                check(name + " preserves the comment composer on outside clicks", !DismissDialogFromOutside(dialog, false) && !showing.IsCompleted, null);
                dialog.Hide();
            }
            await showing; await Closed();
            check(name + " closes and releases the shared input surface", _dialog == null && !_dialogSurfaceReady, null);
        }
        try
        {
            _playlistId = 101; await NavigateAsync("playlist"); var track = Tracks[0];
            foreach (var theme in new[] { "Light", "Dark" })
            {
                _settings.Theme = theme; ApplyAppearance();
                await Inspect(theme + "-information", () => ShowSongDetailsAsync(track, _api));
                await Inspect(theme + "-favorite", () => FavoriteTracksAsync([track]));
                await Inspect(theme + "-comments", () => ShowCommentsAsync(SongCommentsSource(track)!), false);
                await Inspect(theme + "-artists", () => ShowTrackArtistsAsync(Track.FromJson(JsonSerializer.SerializeToElement(LocalLinkFixtures.Song(137)))));
                await Inspect(theme + "-create-playlist", CreatePlaylistAsync);
                await Inspect(theme + "-download", () => ConfirmDownloadsAsync([track]));
                await Inspect(theme + "-message", () => ShowMessageDetailAsync(new SocialItem { Name = "消息", Text = "测试消息" }));
                await Inspect(theme + "-publish", ShowPublishEventAsync);
                await Inspect(theme + "-share", () => PublishResourceAsync(new("song", track.Id, track.Title)));
                await Inspect(theme + "-login", ShowLoginAsync);
                await Inspect(theme + "-new-local-playlist", () => EditLocalPlaylistAsync(null));
                _localPlaylistId = "preview-a"; await NavigateAsync("local-playlist"); StopLocalMonitoring();
                var local = Tracks[0]; var playlist = _localLibrary.Playlists.First(p => p.Id == "preview-a");
                await Inspect(theme + "-edit-local-playlist", () => EditLocalPlaylistAsync(playlist));
                await Inspect(theme + "-favorite-local", () => FavoriteLocalTracksAsync([local]));
                await Inspect(theme + "-remove-local", () => ConfirmRemoveLocalTracksAsync([local]));
                check(theme + " cancellation keeps the local audio file", File.Exists(local.LocalPath), null);
                _playlistId = 101; await NavigateAsync("playlist");
                _playlistOwner = _accountId;
                async Task EditTracks() { EditPlaylist_Click(this, new()); await Closed(); }
                async Task DeletePlaylist() { DeletePlaylist_Click(this, new()); await Closed(); }
                await Inspect(theme + "-edit-tracks", EditTracks);
                await Inspect(theme + "-delete-playlist", DeletePlaylist);
                _peerId = 99;
                async Task SharePrivate() { SharePrivate_Click(this, new()); await Closed(); }
                await Inspect(theme + "-private-share", SharePrivate); _peerId = 0;
            }
            var writes = new[] { "playlist_tracks", "playlist_create", "playlist_delete", "playlist_order_update", "song_order_update", "share_resource", "send_song", "send_album", "send_playlist" };
            check("Canceling every dialog sends no save, delete, favorite or share request", !LocalLinkFixtures.Calls.Any(c => writes.Contains(c.Method)), null);
            check("Canceling dialogs releases song, comment, login and editor requests", _songDetailLoad == null && _commentsLoad == null && _loginLoad == null && _playlistEditLoad == null, null);
        }
        finally { _dialog?.Hide(); StopLocalMonitoring(); }
    }
}
