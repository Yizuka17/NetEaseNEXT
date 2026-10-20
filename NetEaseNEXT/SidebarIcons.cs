using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly HashSet<NavigationViewItem> _boundSidebarIcons = [];
    private Storyboard? _settingsIconRotation;
    private RotateTransform? _settingsIconTransform;

    private void InitializeSidebarIcons()
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            var filled = (item.Tag as string) switch
            {
                "discover" => SolidSidebarGlyph("\uEA8A"),
                "saved-albums" => SolidSidebarGlyph("\uF8B1"),
                "library" => SolidSidebarPath("M2,2 L6,2 L6,22 L2,22 Z M8,2 L12,2 L12,22 L8,22 Z M14,3 L18,2 L23,21 L19,22 Z"),
                "radio" => SolidSidebarPath("M7,5 L21,2 L21,17 C21,20 15,22 15,19 C15,17 17,16 19,16 L19,7 L9,9 L9,20 C9,23 3,25 3,22 C3,20 5,19 7,19 Z"),
                "events" => SolidSidebarPath("M8,2 A4,4 0 1 1 8,10 A4,4 0 1 1 8,2 Z M18,4 A3,3 0 1 1 18,10 A3,3 0 1 1 18,4 Z M4,12 L12,12 C14,12 15,13 15,15 L15,17 C15,23 1,23 1,17 L1,15 C1,13 2,12 4,12 Z M17,12 L20,12 C22,12 23,13 23,15 L23,17 C23,20 19,22 16,20 C17,19 17,18 17,16 Z"),
                "queue" => SolidSidebarPath("M3,3 L21,3 A1.5,1.5 0 0 1 21,6 L3,6 A1.5,1.5 0 0 1 3,3 Z M3,10.5 L15,10.5 A1.5,1.5 0 0 1 15,13.5 L3,13.5 A1.5,1.5 0 0 1 3,10.5 Z M3,18 L21,18 A1.5,1.5 0 0 1 21,21 L3,21 A1.5,1.5 0 0 1 3,18 Z"),
                _ => null
            };
            if (filled != null) BindSidebarIcon(item, filled);
        }
        void BindSettings()
        {
            if (Nav.SettingsItem is NavigationViewItem settings) BindSidebarIcon(settings, SolidSidebarGlyph("\uF8B0"));
        }
        BindSettings();
        Nav.Loaded += (_, _) => BindSettings();
        Nav.ItemInvoked += (_, args) =>
        {
            if (args.IsSettingsInvoked) DispatcherQueue.TryEnqueue(() =>
            {
                RotateSettingsIcon();
                if (_runningSmokeTest) Diagnostics.StartupTrace.Mark($"Settings item invoked: rotation={_settingsIconRotation != null}");
            });
        };
        Nav.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (Nav.Visibility != Visibility.Visible) StopSettingsIconRotation();
        });
    }
    private void BindSidebarIcon(NavigationViewItem item, IconElement filled)
    {
        if (!_boundSidebarIcons.Add(item)) return;
        var outline = item.Icon ?? new SymbolIcon(Symbol.Setting);
        void Apply()
        {
            if (ReferenceEquals(item, Nav.SettingsItem)) StopSettingsIconRotation();
            item.Icon = item.IsSelected ? filled : outline;
        }
        item.RegisterPropertyChangedCallback(NavigationViewItem.IsSelectedProperty, (_, _) => Apply());
        Apply();
    }
    private static IconElement SolidSidebarGlyph(string glyph) => new FontIcon
    {
        Glyph = glyph, FontFamily = Services.IconFonts.Symbols, FontSize = 20, Width = 20, Height = 20,
        IsTextScaleFactorEnabled = false
    };
    private static IconElement SolidSidebarPath(string data)
    {
        var icon = (PathIcon)XamlReader.Load($"""
            <PathIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="20" Height="20" Data="{data}" />
            """);
        // PathIcon does not scale Data to Width/Height. Fit the actual geometry
        // into an 18-DIP drawing area within the standard 20-DIP icon slot.
        var bounds = icon.Data.Bounds;
        var scale = 18 / Math.Max(bounds.Width, bounds.Height);
        icon.Data.Transform = new MatrixTransform
        {
            Matrix = new Matrix(scale, 0, 0, scale,
                (20 - bounds.Width * scale) / 2 - bounds.X * scale,
                (20 - bounds.Height * scale) / 2 - bounds.Y * scale)
        };
        return icon;
    }

    private void RotateSettingsIcon()
    {
        StopSettingsIconRotation();
        if (!PlayerAnimationEnabled || Nav.Visibility != Visibility.Visible || Nav.SettingsItem is not NavigationViewItem { IsSelected: true, Icon: { } icon }) return;
        icon.RenderTransformOrigin = new Point(.5, .5);
        var transform = new RotateTransform();
        icon.RenderTransform = transform;
        var animation = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, transform); Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_settingsIconRotation, storyboard))
            {
                _settingsIconRotation = null; _settingsIconTransform = null;
                transform.Angle = 0;
            }
        };
        _settingsIconRotation = storyboard; _settingsIconTransform = transform;
        storyboard.Begin();
    }
    private void StopSettingsIconRotation()
    {
        _settingsIconRotation?.Stop(); _settingsIconRotation = null;
        if (_settingsIconTransform != null) _settingsIconTransform.Angle = 0;
        _settingsIconTransform = null;
    }
}
