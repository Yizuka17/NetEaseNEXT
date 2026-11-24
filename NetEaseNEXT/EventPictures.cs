using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private Grid? _eventPictureOverlay;
    private Action? _closeEventPictures;
    private void CloseEventPictures() => _closeEventPictures?.Invoke();
    private async Task ShowEventPicturesAsync(IReadOnlyList<string> urls, int index)
    {
        if (_dialog != null || _eventPictureOverlay != null || urls.Count == 0) return;
        index = Math.Clamp(index, 0, urls.Count - 1);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var overlay = new Grid { Name = "EventPictureOverlay", Background = FloatingBackground(), TabFocusNavigation = KeyboardNavigationMode.Cycle };
        Grid.SetRow(overlay, 1); Grid.SetRowSpan(overlay, 2); Canvas.SetZIndex(overlay, 20);
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Artwork.SetSize(image, 1600);
        var surface = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = image };
        var scroll = new ScrollViewer { Content = surface, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 1, MaxZoomFactor = 6, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsHorizontalRailEnabled = false, IsVerticalRailEnabled = false, IsHorizontalScrollChainingEnabled = false, IsVerticalScrollChainingEnabled = false, IsZoomChainingEnabled = false, Margin = new Thickness(56, 48, 56, 76) };
        overlay.Children.Add(scroll);
        Button IconButton(string glyph, string name)
        {
            var button = new Button { Content = IconFonts.Create(glyph), Style = (Style)Application.Current.Resources["BareButtonStyle"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            return button;
        }
        var close = IconButton("\uE711", "关闭图片"); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Margin = new(8);
        var previous = IconButton("\uE72B", "上一张"); previous.HorizontalAlignment = HorizontalAlignment.Left; previous.VerticalAlignment = VerticalAlignment.Center;
        var next = IconButton("\uE72A", "下一张"); next.HorizontalAlignment = HorizontalAlignment.Right; next.VerticalAlignment = VerticalAlignment.Center;
        var count = new TextBlock { Name = "EventPictureCount", Margin = new(20), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12) };
        var smaller = new Button { Content = "−", Style = (Style)Application.Current.Resources["BareButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(smaller, "缩小"); ToolTipService.SetToolTip(smaller, "缩小");
        var larger = IconButton("\uE710", "放大");
        var reset = new Button { Content = "还原", Style = (Style)Application.Current.Resources["BareButtonStyle"] };
        foreach (var button in new[] { smaller, reset, larger }) controls.Children.Add(button);
        foreach (var element in new FrameworkElement[] { count, previous, next, close, controls }) overlay.Children.Add(element);
        void Resize()
        {
            scroll.Width = image.Width = Math.Max(1, Root.ActualWidth - 112);
            scroll.Height = image.Height = Math.Max(1, Root.ActualHeight - TitleRow.ActualHeight - 124);
        }
        void Resized(object sender, SizeChangedEventArgs args) => Resize();
        void Zoom(float factor, Windows.Foundation.Point? anchor = null)
        {
            factor = Math.Clamp(factor, 1, 6);
            var point = anchor ?? new Windows.Foundation.Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
            var ratio = factor / scroll.ZoomFactor;
            scroll.ChangeView(Math.Max(0, (scroll.HorizontalOffset + point.X) * ratio - point.X), Math.Max(0, (scroll.VerticalOffset + point.Y) * ratio - point.Y), factor, true);
        }
        void Show()
        {
            count.Text = $"{index + 1} / {urls.Count}"; previous.IsEnabled = index > 0; next.IsEnabled = index + 1 < urls.Count;
            scroll.ChangeView(0, 0, 1, true); Artwork.SetUri(image, ""); image.Source = null;
            if (Uri.TryCreate(urls[index], UriKind.Absolute, out var uri) && uri.IsFile) image.Source = new BitmapImage(uri);
            else Artwork.SetUri(image, urls[index]);
        }
        close.Click += (_, _) => CloseEventPictures();
        previous.Click += (_, _) => { if (index > 0) { index--; Show(); } };
        next.Click += (_, _) => { if (index + 1 < urls.Count) { index++; Show(); } };
        smaller.Click += (_, _) => Zoom(scroll.ZoomFactor / 1.5f); larger.Click += (_, _) => Zoom(scroll.ZoomFactor * 1.5f);
        reset.Click += (_, _) => scroll.ChangeView(0, 0, 1, true);
        surface.DoubleTapped += (_, args) => { Zoom(scroll.ZoomFactor > 1.05 ? 1 : 2, args.GetPosition(scroll)); args.Handled = true; };
        surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(scroll);
            if (point.Properties.IsHorizontalMouseWheel) return;
            Zoom(scroll.ZoomFactor * MathF.Pow(1.2f, point.Properties.MouseWheelDelta / 120f), point.Position); args.Handled = true;
        }), true);
        overlay.Tapped += (_, args) =>
        {
            for (var node = args.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, overlay); node = VisualTreeHelper.GetParent(node))
                if (node is Button) return;
            if (image.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
            {
                var fit = Math.Min(image.ActualWidth / bitmap.PixelWidth, image.ActualHeight / bitmap.PixelHeight);
                var bounds = new Windows.Foundation.Rect((image.ActualWidth - bitmap.PixelWidth * fit) / 2, (image.ActualHeight - bitmap.PixelHeight * fit) / 2, bitmap.PixelWidth * fit, bitmap.PixelHeight * fit);
                if (bounds.Contains(args.GetPosition(image))) return;
            }
            CloseEventPictures(); args.Handled = true;
        };
        overlay.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Left && index > 0) { index--; Show(); args.Handled = true; }
            else if (args.Key == Windows.System.VirtualKey.Right && index + 1 < urls.Count) { index++; Show(); args.Handled = true; }
        };
        overlay.Loaded += (_, _) => close.Focus(FocusState.Programmatic);
        _eventPictureOverlay = overlay; _closeEventPictures = () => finished.TrySetResult();
        Root.Children.Add(overlay); Root.SizeChanged += Resized;
        Resize(); Show(); Root.UpdateLayout(); ApplyPopupTheme(overlay); close.Focus(FocusState.Programmatic);
        try { await finished.Task; }
        finally
        {
            Root.SizeChanged -= Resized; Artwork.SetUri(image, ""); image.Source = null; scroll.Content = null;
            Root.Children.Remove(overlay); _eventPictureOverlay = null; _closeEventPictures = null;
            if (!_hidden && !_exiting && previousFocus?.IsLoaded == true) previousFocus.Focus(FocusState.Programmatic);
        }
    }
}
