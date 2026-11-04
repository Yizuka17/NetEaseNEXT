using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private ConnectedAnimation? _pendingCoverAnimation, _activeCoverAnimation;
    private ImageSource? _navigationCoverSource;
    private string? _navigationCoverKey;
    private string? _navigationCoverUri;
    private string? _returnLocalCoverId;
    private long? _returnOnlineCoverId;
    private int _coverAnimationStarts;
    private Image? _coverTransitionProxy;
    private void PrepareCoverTransition(string key, Image source)
    {
        CancelCoverTransitions();
        if (!PlayerAnimationEnabled || source.Source == null || source.XamlRoot == null) return;
        _navigationCoverSource = source.Source; _navigationCoverKey = key; _navigationCoverUri = Artwork.GetUri(source);
        var origin = source;
        if (source == ArtistCover && key == "OnlineCoverReturn")
        {
            // The artist header moves between scrolling hosts. Keep the
            // connected animation source independent of that reparenting.
            var point = source.TransformToVisual(Root).TransformPoint(new(0, 0));
            _coverTransitionProxy = new Image { Source = source.Source, Width = source.ActualWidth, Height = source.ActualHeight,
                Stretch = source.Stretch, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new(point.X, point.Y, 0, 0), IsHitTestVisible = false };
            Grid.SetRowSpan(_coverTransitionProxy, 3); Canvas.SetZIndex(_coverTransitionProxy, 20);
            Root.Children.Add(_coverTransitionProxy); Root.UpdateLayout(); origin = _coverTransitionProxy;
        }
        _pendingCoverAnimation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(key, origin);
        _pendingCoverAnimation.Configuration = new DirectConnectedAnimationConfiguration();
    }
    private void StartCoverTransition(Image target)
    {
        var animation = _pendingCoverAnimation;
        if (animation == null) return;
        if (!PlayerAnimationEnabled) { CancelCoverTransitions(); return; }
        if (_navigationCoverSource != null)
            Artwork.Seed(target, _navigationCoverUri ?? "", _navigationCoverSource);
        Root.UpdateLayout();
        _pendingCoverAnimation = null; _navigationCoverSource = null; _navigationCoverKey = null; _navigationCoverUri = null;
        if (animation.TryStart(target))
        {
            _activeCoverAnimation = animation; _coverAnimationStarts++;
            animation.Completed += (_, _) => { if (ReferenceEquals(_activeCoverAnimation, animation)) { _activeCoverAnimation = null; RemoveCoverProxy(); } };
        }
        else { animation.Cancel(); RemoveCoverProxy(); }
    }
    private void RemoveCoverProxy()
    {
        if (_coverTransitionProxy == null) return;
        Root.Children.Remove(_coverTransitionProxy); _coverTransitionProxy = null;
    }
    private void CancelCoverTransitions()
    {
        _pendingCoverAnimation?.Cancel(); _activeCoverAnimation?.Cancel();
        _pendingCoverAnimation = _activeCoverAnimation = null;
        RemoveCoverProxy();
        _navigationCoverSource = null; _navigationCoverKey = null;
        _navigationCoverUri = null; _returnLocalCoverId = null;
        _returnOnlineCoverId = null;
    }
    private void CompleteOnlineCoverReturn()
    {
        if (_returnOnlineCoverId is not long id) return;
        object? item = _view is "discover" or "library" ? Playlists.FirstOrDefault(p => p.Id == id) : Catalog.FirstOrDefault(c => c.Id == id);
        var grid = _view is "discover" or "library" ? PlaylistGrid : CatalogGrid;
        if (item == null) { CancelCoverTransitions(); return; }
        grid.ScrollIntoView(item); Root.UpdateLayout();
        if (grid.ContainerFromItem(item) is DependencyObject container && FindDescendant<Image>(container) is { } image) StartCoverTransition(image);
        else CancelCoverTransitions();
        _returnOnlineCoverId = null;
    }
    private void PrepareLocalPlaylistHeader(string id)
    {
        var card = LocalPlaylistCards.FirstOrDefault(c => c.Id == id);
        Artwork.SetUri(LocalPlaylistCover, card?.Cover ?? "");
        LocalPlaylistTitle.Text = card?.Name ?? (id.Length == 0 ? "全部歌曲" : "");
        LocalFolderPath.Text = LocalFolderStatus.Text = "";
        LocalFolderPath.Visibility = LocalFolderStatus.Visibility = Visibility.Collapsed;
    }
    private void CompleteLocalCoverReturn()
    {
        var id = _returnLocalCoverId;
        if (id == null || _navigationCoverKey != "LocalCoverReturn") return;
        var card = LocalPlaylistCards.FirstOrDefault(c => c.Id == id);
        if (card == null) { CancelCoverTransitions(); return; }
        LocalPlaylistGrid.ScrollIntoView(card);
        Root.UpdateLayout();
        if (LocalPlaylistGrid.ContainerFromItem(card) is DependencyObject container &&
            FindDescendant<Image>(container) is { } image) StartCoverTransition(image);
        else CancelCoverTransitions();
        _returnLocalCoverId = null;
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
