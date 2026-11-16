using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NetEaseNEXT.Controls;

// This lives inside the list's scrollable content, after its final item.
public sealed class PageLoadingFooter : Grid
{
    internal ProgressRing Ring { get; } = new() { Width = 32, Height = 32, IsActive = false, IsIndeterminate = true };
    internal Button Retry { get; } = new() { Content = "加载失败，点击重试", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Background = null, BorderThickness = new(0) };
    internal bool Failed => Retry.Visibility == Visibility.Visible;

    public PageLoadingFooter()
    {
        Height = 64; HorizontalAlignment = HorizontalAlignment.Stretch; Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(Ring, "正在加载更多");
        Children.Add(Ring); Children.Add(Retry);
    }
    internal void ShowLoading()
    {
        Retry.Visibility = Visibility.Collapsed; Ring.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible; Ring.IsActive = true;
    }
    internal void ShowFailure()
    {
        Ring.IsActive = false; Ring.Visibility = Visibility.Collapsed;
        Retry.Visibility = Visibility.Visible; Visibility = Visibility.Visible;
    }
    internal void Clear()
    {
        Ring.IsActive = false; Retry.Visibility = Visibility.Collapsed; Visibility = Visibility.Collapsed;
    }
}
