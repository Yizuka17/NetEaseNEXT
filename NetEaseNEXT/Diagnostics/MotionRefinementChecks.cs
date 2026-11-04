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
    private async Task RunMotionRefinementChecksAsync(string fixture, Action<string, bool, object?> check)
    {
        var size = _settings.TextFontSize; var weight = _settings.TextFontWeight;
        try
        {
            ResetIconBounces();
            await NavigateAsync("settings"); Root.UpdateLayout(); ApplyFont();
            check("Programmatic navigation never bounces unrelated sidebar icons", _jumpingIcons.Count == 0, null);
            var titleSize = MiniTitle.FontSize; var iconSize = PlayPauseIcon.FontSize;
            _settings.TextFontSize = 18; _settings.TextFontWeight = 600; ApplyFont();
            var expected = titleSize * 18 / size;
            check("Font size and weight change live without scaling icon glyphs", Math.Abs(MiniTitle.FontSize - expected) < .01 && MiniTitle.FontWeight.Weight == 600 && PlayPauseIcon.FontSize == iconSize, new { MiniTitle.FontSize });
            ApplyFont(); ApplyFont();
            check("Repeated typography application cannot accumulate scaling", Math.Abs(MiniTitle.FontSize - expected) < .01, null);
            var late = new TextBlock { Text = "新加载的歌曲", FontSize = 12 };
            ApplyTextFont(late, new FontFamily(_settings.FontFamily));
            check("Newly created rows receive the selected text metrics", Math.Abs(late.FontSize - 12d * 18 / 14) < .01 && late.FontWeight.Weight == 600, null);
            var inherited = new TextBlock { Text = "新加载的默认文本" };
            ApplyTextFont(inherited, new FontFamily(_settings.FontFamily));
            _settings.TextFontSize = size; _settings.TextFontWeight = weight;
            ApplyTextFont(inherited, new FontFamily(_settings.FontFamily));
            check("New default text can return from a bold enlarged setting", Math.Abs(inherited.FontSize - size) < .01 && inherited.FontWeight.Weight == weight, null);
            _settings.TextFontSize = 18; _settings.TextFontWeight = 600;
            var path = Path.Combine(fixture, "font-settings.json"); _settings.SaveTo(path);
            var restored = SettingsStore.LoadFrom(path);
            check("Typography settings survive saving and loading", restored.TextFontSize == 18 && restored.TextFontWeight == 600, null);
            SetActionIconState(BackButton, "PointerOver");
            check("Back hover state reaches the animated icon itself", AnimatedIcon.GetState(BackAnimatedIcon) == "PointerOver", null);
            SetActionIconState(BackButton, "Normal");
            BounceIcon(PlayPauseIcon, false); ResetIconBounces();
            ElementCompositionPreview.GetElementVisual(PlayPauseIcon).Properties.TryGetVector3("Translation", out var offset);
            check("Interrupted icon bounces return to their resting position", offset == System.Numerics.Vector3.Zero, null);
        }
        finally { _settings.TextFontSize = size; _settings.TextFontWeight = weight; ApplyFont(); }

        var tracks = new List<Track>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(fixture, $"switch-{i}.wav");
            using var writer = new BinaryWriter(File.Create(path));
            const int bytes = 88200 * 30;
            writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
            tracks.Add(new(-12000 - i, "相邻歌曲 " + (i + 1), "本地测试", "", "", 30000) { LocalPath = path });
        }
        await NavigateAsync("queue");
        string? playbackError = null;
        void Failed(string message) => playbackError = message;
        _playback.Error += Failed;
        try { await _playback.PlayAsync(tracks[0], tracks); }
        finally { _playback.Error -= Failed; }
        check("The native decoder starts the local playback fixture", _playback.Current?.Id == tracks[0].Id, playbackError);
        await _playback.NextPreparationTask;
        check("Next local song is prepared without a network request", _playback.NextPreparationStatus == "ready local", _playback.NextPreparationStatus);
        var timer = Stopwatch.StartNew(); await ChangeTransportAsync(NextButton, false); timer.Stop();
        check("Adjacent local playback consumes the prepared decoder", _playback.Current?.Id == tracks[1].Id && _playback.LastPlayUsedPreparedDecoder, new { switchMilliseconds = timer.Elapsed.TotalMilliseconds });
        Root.UpdateLayout(); UpdateSelectionIndicators(false);
        check("The song list selection follows transport changes", (TrackList.SelectedItem as Track)?.Id == tracks[1].Id && TrackSelectionIndicator.Visibility == Visibility.Visible, null);
        TrackList.SelectedItem = Tracks.First(t => t.Id == tracks[0].Id);
        TrackList.SelectedItem = Tracks.First(t => t.Id == tracks[2].Id);
        await Task.Delay(IndicatorMotionDuration + TimeSpan.FromMilliseconds(80));
        ElementCompositionPreview.GetElementVisual(TrackSelectionIndicator).Properties.TryGetVector3("Translation", out var position);
        check("Rapid row changes leave one indicator on the latest selection", Math.Abs(position.Y - _trackSelectionMotion.Y) < .5 && _trackSelectionMotion.Item == TrackList.SelectedItem, new { position.Y, target = _trackSelectionMotion.Y });
        await _playback.PreviousAsync();
        check("Previous switches directly to the preceding song", _playback.Current?.Id == tracks[0].Id, null);
        _playback.Toggle();
    }
}
