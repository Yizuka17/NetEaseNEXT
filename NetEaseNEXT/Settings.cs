using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NetEaseNEXT.Services;
using Windows.Storage.Pickers;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private void UpdateAppIcon()
    {
        if (_exiting) return;
        var path = ShellAppearance.IconPath();
        if (File.Exists(path))
        {
            ApplicationIdentity.UpdateIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), path);
            AppWindow.SetIcon(path); _tray?.SetIcon(path); _tray?.UpdateMenuTheme();
        }
    }
    private bool _loadingOutputs;
    private async Task LoadOutputsAsync(CancellationToken ct)
    {
        _loadingOutputs = true;
        try
        {
            var fonts = LoadFontsAsync(ct);
            SettingsVolume.Value = _settings.Volume * 100;
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Media.Devices.MediaDevice.GetAudioRenderSelector()).AsTask(ct);
            ct.ThrowIfCancellationRequested(); OutputCombo.Items.Clear();
            OutputCombo.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });
            foreach (var device in devices) OutputCombo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            SelectTag(OutputCombo, _settings.AudioDeviceId);
            await fonts;
        }
        finally { _loadingOutputs = false; }
    }
    private async void Output_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _loadingOutputs) return;
        try { var id = Tag(OutputCombo); await _playback.SetOutputAsync(id); _settings.AudioDeviceId = id; SaveSettings(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void SettingsVolume_Changed(object sender, RangeBaseValueChangedEventArgs args) { if (_ready && !_loadingOutputs) { _playback.SetVolume(args.NewValue / 100); _appearanceTimer.Stop(); _appearanceTimer.Start(); } }
    private async void SystemAudio_Click(object sender, RoutedEventArgs args) => await OpenSystemAudioAsync();
    private readonly DispatcherTimer _appearanceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string _appliedFont = "";
    private static void SelectTag(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value) ?? combo.Items[0];
    private static string Tag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private void InitializeFonts()
    {
        FontCombo.ItemsSource = new[] { _settings.FontFamily, "Segoe UI", "Microsoft YaHei UI" }.Distinct().ToArray();
        FontCombo.SelectedItem = _settings.FontFamily;
        _appearanceTimer.Tick += (_, _) => { _appearanceTimer.Stop(); SaveSettings(); ApplyAppearance(); };
    }
    private bool _fontsLoaded, _loadingFonts;
    private async Task LoadFontsAsync(CancellationToken ct)
    {
        if (_fontsLoaded) return;
        string[] families;
        try { families = await Task.Run(() => CanvasTextFormat.GetSystemFontFamilies().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray(), ct); }
        catch (OperationCanceledException) { throw; }
        catch { return; }
        ct.ThrowIfCancellationRequested();
        _loadingFonts = true;
        try { FontCombo.ItemsSource = families.Prepend(_settings.FontFamily).Distinct().ToArray(); FontCombo.SelectedItem = _settings.FontFamily; _fontsLoaded = true; }
        finally { _loadingFonts = false; }
    }
    private void Setting_Changed(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _settings.CloseToTray = TrayToggle.IsOn; _settings.Animations = AnimationToggle.IsOn;
        _settings.Backdrop = Tag(BackdropCombo); _settings.Theme = Tag(ThemeCombo);
        SaveSettings(); ApplyAppearance();
    }
    private void SaveSettings() { if (_runningSmokeTest) return; try { _settings.Save(); } catch (Exception ex) { ShowError(ex.Message); } }
    private void Font_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _loadingFonts || FontCombo.SelectedItem is not string family) return;
        _settings.FontFamily = family; ApplyFont(); SaveSettings();
    }
    private void ApplyFont()
    {
        if (_appliedFont == _settings.FontFamily) return;
        _appliedFont = _settings.FontFamily;
        var family = new FontFamily(_settings.FontFamily);
        Application.Current.Resources["ContentControlThemeFontFamily"] = family;
        FontHost.FontFamily = family;
        ApplyTextFont(FontHost, family);
    }
    private static void ApplyTextFont(DependencyObject element, FontFamily family)
    {
        // Icon controls render private-use glyphs; their internal text must keep
        // the symbol font even when the user's content font changes.
        if (element is IconElement) return;
        // InfoBar and other native templates also use private-use characters
        // directly in TextBlocks, without an IconElement ancestor.
        if (element is TextBlock symbol && symbol.Text.Any(c => c is >= '\uE000' and <= '\uF8FF'))
        {
            symbol.FontFamily = IconFonts.FrameworkSymbols;
            return;
        }
        if (element is TextBlock text) text.FontFamily = family;
        else if (element is Control control) control.FontFamily = family;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) ApplyTextFont(VisualTreeHelper.GetChild(element, index), family);
    }
    private void Transparency_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_ready || _updating) return;
        if (_settings.Backdrop == "Acrylic") _settings.AcrylicTransparency = args.NewValue; else _settings.MicaTransparency = args.NewValue;
        TransparencyValue.Text = $"{args.NewValue:0}%";
        _appearanceTimer.Stop(); _appearanceTimer.Start();
    }
    private void ApplyAppearance()
    {
        Root.RequestedTheme = PreferredAppTheme;
        ApplyFont();
        var supported = _settings.Backdrop == "Acrylic" ? DesktopAcrylicController.IsSupported() : _settings.Backdrop is "Mica" or "MicaAlt" && MicaController.IsSupported();
        var transparency = _settings.Backdrop == "Acrylic" ? _settings.AcrylicTransparency : _settings.MicaTransparency;
        try { SystemBackdrop = supported ? new AdjustableBackdrop(_settings.Backdrop, transparency) : null; }
        catch (Exception ex) { Log.Write(ex); SystemBackdrop = null; supported = false; }
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SolidLayer.Visibility = SystemBackdrop == null ? Visibility.Visible : Visibility.Collapsed;
        _updating = true; TransparencySlider.Value = transparency; _updating = false;
        TransparencySlider.IsEnabled = supported; TransparencyValue.Text = $"{transparency:0}%";
        MaterialHint.Text = !supported && _settings.Backdrop != "Solid" ? "当前系统不支持此材质" : _settings.Backdrop.StartsWith("Mica") ? "Mica 取色自桌面壁纸" : "";
        MaterialHint.Visibility = MaterialHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        UpdateAppIcon();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync(); if (folder == null) return;
            var probe = Path.Combine(folder.Path, ".NetEaseNEXT-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probe, ""); File.Delete(probe);
            if ((string)((Button)sender).Tag == "cache")
            {
                await _api.SetCacheDirectoryAsync(folder.Path); _settings.CacheDirectory = folder.Path;
                Artwork.CacheDirectory = folder.Path; CachePath.Text = folder.Path;
            }
            else { _settings.DownloadDirectory = folder.Path; DownloadPath.Text = folder.Path; }
            SaveSettings();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
