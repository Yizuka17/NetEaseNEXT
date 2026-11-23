using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunMaterialVmChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _playback.SetVolume(0);
        _accountLoad?.Cancel(); _accountAvatarLoad?.Cancel(); _inboxCheck?.Cancel(); ResetAccount();
        _profileSession = _api.Cookie; _profileExpires = DateTimeOffset.UtcNow.AddHours(1); _profileTask = Task.FromResult<JsonElement?>(null);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new(area.X + 24, area.Y + 24, Math.Min(area.Width - 48, 1080), Math.Min(area.Height - 48, 860)));
        await NavigateAsync("settings");
        var run = Environment.GetEnvironmentVariable("NETEASENEXT_VM_RUN");
        var stageFolder = Path.Combine(SettingsStore.DataDirectory, "material-vm-" + (Guid.TryParseExact(run, "N", out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stageFolder);
        var sequence = 0;
        async Task Record(string stage, AdjustableBackdrop? material = null)
        {
            // Publish after the composition changes have reached the window.
            await Task.Delay(500);
            var json = JsonSerializer.Serialize(new
            {
                stage, mica = MicaController.IsSupported(), acrylic = DesktopAcrylicController.IsSupported(),
                tint = material?.AppliedProfile?.Tint, luminosity = material?.AppliedProfile?.Luminosity,
                tintRed = material?.AppliedColors?.Tint.R, fallbackRed = material?.AppliedColors?.Fallback.R,
                system = material?.UsesSystemDefaults, solidBlend = SolidLayer.Opacity, theme = Root.ActualTheme.ToString()
            });
            var target = Path.Combine(stageFolder, (++sequence).ToString("D3") + ".json");
            await File.WriteAllTextAsync(target + ".tmp", json);
            File.Move(target + ".tmp", target);
            await Task.Delay(1500);
        }
        foreach (var kind in new[] { "Acrylic", "Mica", "MicaAlt" })
        {
            SelectTag(BackdropCombo, kind);
            if (SystemBackdrop is not AdjustableBackdrop backdrop)
            {
                check(kind + " is unsupported and falls back to a solid theme background", !TransparencySlider.IsEnabled && SolidLayer.Visibility == Visibility.Visible, null);
                await Record(kind + "-fallback");
                continue;
            }
            foreach (var theme in new[] { "Dark", "Light" })
            {
                SelectTag(ThemeCombo, theme); await Task.Yield();
                ResetMaterial_Click(ResetMaterialButton, new RoutedEventArgs());
                await Record(kind + "-" + theme + "-system", backdrop);
                var system = backdrop.AppliedColors!.Value;
                var nativeProfile = backdrop.AppliedProfile!.Value;
                var neutral = AdjustableBackdrop.DefaultTransparency;
                check(kind + " uses system defaults with the selected theme", backdrop.UsesSystemDefaults && TransparencySlider.Value == neutral && Root.ActualTheme.ToString() == theme
                    && (system.Tint.R < 128) == (theme == "Dark") && (system.Fallback.R < 128) == (theme == "Dark"),
                    new { system.Tint.R, Fallback = system.Fallback.R });
                var previous = (Tint: 2f, Luminosity: 2f, Blend: 2d);
                foreach (var percent in new[] { 0, 25, 50, 64, 65, 66, 75, 100 })
                {
                    TransparencySlider.Value = percent;
                    await Record(kind + "-" + theme + "-" + percent, backdrop);
                    var profile = backdrop.AppliedProfile!.Value;
                    var colors = backdrop.AppliedColors!.Value;
                    var blend = SolidLayer.Opacity;
                    check(kind + " " + theme + " " + percent, ReferenceEquals(SystemBackdrop, backdrop)
                        && backdrop.UsesSystemDefaults == (percent == neutral)
                        && profile.Tint <= previous.Tint && profile.Luminosity <= previous.Luminosity && blend <= previous.Blend
                        && (profile.Tint, profile.Luminosity, blend) != previous
                        && colors == system && (percent > neutral || profile == nativeProfile), null);
                    previous = (profile.Tint, profile.Luminosity, blend);
                }
                TransparencySlider.Value = neutral;
                await Record(kind + "-" + theme + "-returned", backdrop);
                check(kind + " " + theme + " returns to the original native preset after adjusting", backdrop.UsesSystemDefaults
                    && backdrop.AppliedProfile == nativeProfile && backdrop.AppliedColors == system && SolidLayer.Opacity == 0
                    && !ResetMaterialButton.IsEnabled, null);
            }
        }
    }
}
