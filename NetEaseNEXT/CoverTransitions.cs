using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private ConnectedAnimation? _pendingCoverAnimation, _activeCoverAnimation;
    private ImageSource? _navigationCoverSource;
    private string? _navigationCoverKey;
    private int _coverAnimationStarts;
    private void PrepareCoverTransition(string key, Image source)
    {
        CancelCoverTransitions();
        if (!PlayerAnimationEnabled || source.Source == null || source.XamlRoot == null) return;
        _navigationCoverSource = source.Source; _navigationCoverKey = key;
        _pendingCoverAnimation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(key, source);
        _pendingCoverAnimation.Configuration = new DirectConnectedAnimationConfiguration();
    }
    private void StartCoverTransition(Image target)
    {
        var animation = _pendingCoverAnimation;
        if (animation == null) return;
        target.Source = _navigationCoverSource; Root.UpdateLayout();
        _pendingCoverAnimation = null; _navigationCoverSource = null; _navigationCoverKey = null;
        if (animation.TryStart(target))
        {
            _activeCoverAnimation = animation; _coverAnimationStarts++;
            animation.Completed += (_, _) => { if (ReferenceEquals(_activeCoverAnimation, animation)) _activeCoverAnimation = null; };
        }
        else animation.Cancel();
    }
    private void CancelCoverTransitions()
    {
        _pendingCoverAnimation?.Cancel(); _activeCoverAnimation?.Cancel();
        _pendingCoverAnimation = _activeCoverAnimation = null;
        _navigationCoverSource = null; _navigationCoverKey = null;
    }
    private readonly DispatcherTimer _windowTransitionTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private int _previousWindowTransitions;
    private bool _suppressWindowTransitions;
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    private void SuppressPresenterTransition()
    {
        CancelCoverTransitions();
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!_suppressWindowTransitions) DwmGetWindowAttribute(window, 3, out _previousWindowTransitions, sizeof(int));
        var disabled = 1; DwmSetWindowAttribute(window, 3, ref disabled, sizeof(int));
        _suppressWindowTransitions = true; _windowTransitionTimer.Stop(); _windowTransitionTimer.Start();
    }
    private void RestorePresenterTransition()
    {
        _windowTransitionTimer.Stop();
        if (!_suppressWindowTransitions) return;
        _suppressWindowTransitions = false;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 3, ref _previousWindowTransitions, sizeof(int));
    }
}
