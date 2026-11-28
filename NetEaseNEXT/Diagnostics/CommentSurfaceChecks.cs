using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunCommentSurfaceChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = _authenticated = true; _accountId = 42;
        _accountLoad?.Cancel(); _inboxCheck?.Cancel(); _playback.SetVolume(0);
        var oldTheme = _settings.Theme; var oldSize = AppWindow.Size;
        var inspect = Environment.GetCommandLineArgs().Contains("--comment-surface-inspect");
        var directory = Path.Combine(Services.SettingsStore.DataDirectory, "comment-surface-probe");
        Directory.CreateDirectory(directory);
        async Task WaitForDialog()
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!_dialogSurfaceReady && DateTime.UtcNow < deadline) await Task.Delay(30);
            if (!_dialogSurfaceReady) throw new TimeoutException("Comment dialog did not open");
            await Task.Delay(500);
        }
        async Task Observe(string name)
        {
            var frame = FindDescendant<Border>(_dialog!, b => b.Name == "BackgroundElement")!;
            var command = FindDescendant<Grid>(_dialog!, g => g.Name == "CommandSpace")!;
            var content = FindDescendant<ScrollViewer>(_dialog!, s => s.Name == "ContentScrollViewer")!;
            check(name + " uses one themed native surface", frame.Background is AcrylicBrush && command.Visibility == Visibility.Collapsed, new { frame.ActualWidth, frame.ActualHeight, frame.Translation.Z, background = frame.Background.GetType().Name, commands = command.Visibility.ToString(), commandHeight = command.ActualHeight, contentHeight = content.ActualHeight, theme = _dialog!.ActualTheme.ToString() });
            check(name + " retains its native shadow and dark/light theme", frame.Shadow is ThemeShadow && frame.Translation.Z == 32 && _dialog!.ActualTheme == Root.ActualTheme, null);
            if (!inspect) return;
            await File.WriteAllTextAsync(Path.Combine(directory, "stage.txt"), name);
            var release = Path.Combine(directory, name + ".continue");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(release) && DateTime.UtcNow < deadline) await Task.Delay(100);
        }
        try
        {
            RestoreWindow(); AppWindow.Resize(new(1280, 1400));
            _playlistId = 101; await NavigateAsync("playlist"); var track = Tracks[0];
            foreach (var theme in new[] { "Dark", "Light" })
            {
                _settings.Theme = theme; ApplyAppearance(); AppWindow.Resize(new(1280, 1400)); await Task.Delay(200);
                var showing = ShowCommentsAsync(SongCommentsSource(track)!);
                await WaitForDialog();
                var composer = Descendants<TextBox>(_dialog!).Single(t => t.AcceptsReturn && t.PlaceholderText == "评论"); composer.Text = "未发送的评论";
                await Observe(theme + "-initial");
                AppWindow.Resize(new(1280, 1200)); await Task.Delay(500); await Observe(theme + "-shrunk");
                check(theme + " resizing preserves the comment draft and outside-click behavior", composer.Text == "未发送的评论" && !DismissDialogFromOutside(_dialog!, false) && !showing.IsCompleted, null);
                _dialog!.Hide(); await showing;
                check(theme + " closing releases the dialog and request", _dialog == null && _commentsLoad == null && !_dialogSurfaceReady, null);
                AppWindow.Resize(new(1280, 1400)); await Task.Delay(200);
                showing = ShowCommentsAsync(SongCommentsSource(track)!); await WaitForDialog();
                await Observe(theme + "-reopened"); _dialog!.Hide(); await showing;
            }
        }
        finally { _dialog?.Hide(); _settings.Theme = oldTheme; ApplyAppearance(); AppWindow.Resize(oldSize); }
    }
}
