using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
            var glyphs = (item.Tag as string) switch
            {
                "discover" => ("\uE80F", "\uEA8A"),
                "library" => ("\uE8D6", "\uF8B3"),
                "saved-albums" => ("\uE93C", "\uF8B1"),
                "radio" => ("\uF8BA", "\uF8BB"),
                "events" => ("\uE77D", "\uF8B4"),
                "queue" => ("\uE770", "\uF8B5"),
                _ => ("", "")
            };
            if (glyphs.Item1.Length > 0) BindSidebarIcon(item, glyphs.Item1, glyphs.Item2);
        }
        void BindSettings()
        {
            if (Nav.SettingsItem is NavigationViewItem settings) BindSidebarIcon(settings, "\uE713", "\uF8B0");
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
    private void BindSidebarIcon(NavigationViewItem item, string regular, string selected)
    {
        if (!_boundSidebarIcons.Add(item)) return;
        var icon = Services.IconFonts.Create(regular);
        icon.SetBinding(IconElement.ForegroundProperty, new Binding
        {
            Source = item, Path = new PropertyPath(nameof(item.Foreground)), Mode = BindingMode.OneWay
        });
        item.Icon = icon;
        // NavigationView can recreate its built-in Settings AnimatedIcon after
        // Loaded/template changes. Keep our bundled icon and its color binding.
        item.RegisterPropertyChangedCallback(NavigationViewItem.IconProperty, (_, _) =>
        {
            if (!ReferenceEquals(item.Icon, icon)) item.Icon = icon;
        });
        void Apply()
        {
            if (ReferenceEquals(item, Nav.SettingsItem)) StopSettingsIconRotation();
            icon.Glyph = item.IsSelected ? selected : regular;
        }
        item.RegisterPropertyChangedCallback(NavigationViewItem.IsSelectedProperty, (_, _) => Apply());
        Apply();
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
