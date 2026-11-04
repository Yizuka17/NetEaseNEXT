using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private bool _interactiveMotionPreview;
    private async Task RunMotionTourAsync(LocalPlaylist first, LocalPlaylist second, string fixture)
    {
        first = _localLibrary.Playlists.First(p => p.Id == first.Id);
        second = _localLibrary.Playlists.First(p => p.Id == second.Id);
        async Task<string> Cover(bool blue)
        {
            var bytes = new byte[256 * 256 * 4];
            for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
            {
                var i = (y * 256 + x) * 4;
                var light = .60 + .38 * (1 - (double)y / 255);
                var ring = Math.Abs(Math.Sqrt((x - 166) * (x - 166) + (y - 82) * (y - 82)) - 38) < 2;
                var landscape = y > 164 + 24 * Math.Sin((double)x / 55);
                if (landscape) light *= .6;
                bytes[i] = (byte)(ring ? 230 : (blue ? 225 : 100) * light);
                bytes[i + 1] = (byte)(ring ? 230 : (blue ? 145 : 105) * light);
                bytes[i + 2] = (byte)(ring ? 245 : (blue ? 58 : 225) * light);
                bytes[i + 3] = 255;
            }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 256, 256, 96, 96, bytes);
            await encoder.FlushAsync(); stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var png = new byte[(int)stream.Size]; reader.ReadBytes(png);
            return LocalMusicFiles.StoreCover(png);
        }
        first.CoverPath = await Cover(true);
        second.CoverPath = await Cover(false);
        async Task Stage(string name, int pause = 650)
        {
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "motion-tour-state.json"),
                JsonSerializer.Serialize(new { stage = name, run = Environment.GetEnvironmentVariable("NETEASENEXT_VM_RUN") }));
            await Task.Delay(pause);
        }
        var titles = new[] { "雨后散步", "沿途灯火", "远方来信", "风经过窗台", "山间", "回家的路" };
        for (var i = 0; i < 30; i++)
        {
            var file = Path.Combine(fixture, "track-" + i + ".wav");
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                const int bytes = 88200 * 30;
                writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            _localLibrary.Tracks.Add(new Track(-10000 - i, titles[i % titles.Length], "午后电台", first.Name,
                new Uri(first.CoverPath).AbsoluteUri, 30000) { LocalPath = file });
            first.Paths.Add(file);
            if (i < 12) second.Paths.Add(file);
        }
        _localLibrary.Save();
        _playback.Restore(new PlaybackSnapshot { Queue = _localLibrary.Tracks.ToList(), CurrentId = _localLibrary.Tracks[0].Id });
        _settings.Theme = "Light"; ApplyAppearance();
        await NavigateAsync("local");
        Root.UpdateLayout();
        if (Environment.GetCommandLineArgs().Contains("--motion-interactive"))
        {
            _interactiveMotionPreview = true;
            _settings.CloseToTray = false;
            await File.WriteAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "motion-preview-ready.json"),
                JsonSerializer.Serialize(new { ready = true, run = Environment.GetEnvironmentVariable("NETEASENEXT_VM_RUN") }));
            await Task.Delay(Timeout.Infinite);
            return;
        }
        await Stage("begin", 1300);
        var card = LocalPlaylistCards.First(c => c.Id == first.Id);
        var image = FindDescendant<Image>((DependencyObject)LocalPlaylistGrid.ContainerFromItem(card))!;
        var frame = (FrameworkElement)image.Parent;
        SetCoverFeedback(frame, 1.025f); await Stage("hover");
        SetCoverFeedback(frame, .98f); await Task.Delay(90);
        SetCoverFeedback(frame, 1); await Stage("spring");
        await OpenLocalPlaylistAsync(card); await Stage("connected-open", 950);
        if (Tracks.Count != 30) throw new InvalidOperationException("The motion preview playlist is incomplete.");
        var scroll = FindDescendant<ScrollViewer>(TrackList);
        scroll?.ChangeView(null, 120, null, false); await Stage("parallax-scroll", 950);
        scroll?.ChangeView(null, 0, null, false); await Task.Delay(650);
        await GoBackAsync(); await Stage("connected-return", 950);
        await OpenLocalPlaylistAsync(LocalPlaylistCards.First(c => c.Id == second.Id));
        await Stage("different-cover", 950);
        BounceActionIcon(PlayPauseButton); await Stage("play-sink");
        ShowQueue_Click(QueueButton, new RoutedEventArgs()); await Stage("queue-open", 950);
        ShowQueue_Click(QueueButton, new RoutedEventArgs()); await Stage("queue-close");
        await NavigateAsync("queue");
        await _playback.PlayAsync(_playback.Queue[0]); await _playback.NextPreparationTask;
        await Stage("selection-before");
        await ChangeTransportAsync(NextButton, false); await Stage("selection-next", 750);
        await ChangeTransportAsync(PreviousButton, true); await Stage("previous-left", 750);
        await ChangeTransportAsync(NextButton, false); await Stage("selection-following", 750);
        _playback.Toggle();
        await NavigateAsync("local"); await Task.Delay(650);
        await NavigateAsync("queue");
        BounceActionIcon(PlayPauseButton);
        SetActionIconState(BackButton, "PointerOver"); await Stage("directional-icons");
        SetActionIconState(BackButton, "Normal");
        await NavigateAsync("settings"); await Stage("page-forward");
        FontSizeBox.Value = 22; SelectTag(FontWeightCombo, "600");
        Root.UpdateLayout(); FontCombo.StartBringIntoView(); await Stage("font-large", 1000);
        FontSizeBox.Value = 14; SelectTag(FontWeightCombo, "400");
        _settings.Theme = "Dark"; ApplyAppearance(); await Stage("theme-transition", 950);
        await GoBackAsync(); await Stage("page-back");
        await NavigateAsync("downloads"); await Stage("downloads");
        await PlayDownloadCompletionMotionAsync(); await Stage("completion-vector", 700);
        if (Environment.GetCommandLineArgs().Contains("--motion-release-check"))
        {
            foreach (var source in new[] { "discover", "saved-albums", "radio", "artists" })
            {
                await NavigateAsync(source); await Stage(source + "-cards", 700);
                var grid = source == "discover" ? PlaylistGrid : CatalogGrid;
                object item = source == "discover" ? Playlists[0] : Catalog[0];
                grid.ScrollIntoView(item); Root.UpdateLayout(); await Task.Delay(300);
                if (source == "discover") await OpenOnlinePlaylistAsync((Models.Playlist)item);
                else await OpenCatalogCardAsync((Models.CatalogItem)item);
                await Stage(source + "-detail", 750); await GoBackAsync(); await Stage(source + "-return", 750);
            }
        }
        _settings.Theme = "Light"; ApplyAppearance(); await NavigateAsync("local");
        await Stage("end", 900);
    }
}
