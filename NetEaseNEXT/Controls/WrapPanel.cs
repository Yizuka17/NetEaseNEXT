using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NetEaseNEXT.Controls;

// Content-sized native WinUI rows; unlike VariableSizedWrapGrid this does not
// force longer action labels into the width of the first button.
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, y = 0, height = 0, maximum = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(width, double.PositiveInfinity)); var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width) { maximum = Math.Max(maximum, x - Spacing); y += height + Spacing; x = height = 0; }
            x += size.Width + Spacing; height = Math.Max(height, size.Height);
        }
        return new(Math.Max(maximum, Math.Max(0, x - Spacing)), y + height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += height + Spacing; x = height = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing; height = Math.Max(height, size.Height);
        }
        return finalSize;
    }
}
