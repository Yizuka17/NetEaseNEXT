using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async void Playback_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_hidden || _dialog != null) return;
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        for (var element = focused; element != null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or RichEditBox or AutoSuggestBox) return;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;
        if (alt) return;
        if (args.Key == VirtualKey.Space && focused is not ButtonBase)
        { args.Handled = true; _playback.Toggle(); }
        else if (control && args.Key == VirtualKey.Right) { args.Handled = true; await _playback.NextAsync(); }
        else if (control && args.Key == VirtualKey.Left) { args.Handled = true; await _playback.PreviousAsync(); }
        else if (control && args.Key is VirtualKey.Up or VirtualKey.Down)
        { args.Handled = true; _playback.SetVolume(_settings.Volume + (args.Key == VirtualKey.Up ? .05 : -.05)); _appearanceTimer.Stop(); _appearanceTimer.Start(); }
        else if (_view == "playing" && focused is not Slider && args.Key is VirtualKey.Left or VirtualKey.Right)
        { args.Handled = true; _playback.Seek(_playback.Position + TimeSpan.FromSeconds(args.Key == VirtualKey.Right ? 5 : -5)); }
    }
}
