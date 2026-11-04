using System.Numerics;
using NetEaseNEXT.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private readonly HashSet<FrameworkElement> _feedbackCovers = [];
    private static readonly TimeSpan IndicatorMotionDuration = FluentMotion.Duration;
    private bool? _configuredMotionEnabled;
    private void ConfigureInterfaceMotion()
    {
        var enabled = PlayerAnimationEnabled;
        if (_configuredMotionEnabled == enabled) return;
        _configuredMotionEnabled = enabled;
        LocalCoverParallax.VerticalShift = PlaylistCoverParallax.VerticalShift = ArtistCoverParallax.VerticalShift = enabled ? 16 : 0;
        QueueGlyph.OpacityTransition = enabled ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } : null;
        ConfigureSurfaceTransitions(SettingsPanel, enabled);
        SetBackIconMotion(enabled);
        SearchAnimatedIcon.Source = enabled ? new AnimatedFindVisualSource() : null;
        foreach (var grid in new[] { LocalPlaylistGrid, PlaylistGrid, CatalogGrid })
            grid.ItemContainerTransitions = enabled
                ? new TransitionCollection { new RepositionThemeTransition { IsStaggeringEnabled = false }, new AddDeleteThemeTransition() }
                : new TransitionCollection();
        ElementCompositionPreview.SetIsTranslationEnabled(QueuePane, true);
        var visual = ElementCompositionPreview.GetElementVisual(QueuePane);
        visual.StopAnimation("Opacity"); visual.StopAnimation("Translation");
        visual.Opacity = 1; visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!enabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(QueuePane, null);
            ElementCompositionPreview.SetImplicitHideAnimation(QueuePane, null);
            return;
        }
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new(.1f, .9f), new(.2f, 1));
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity"; fade.Duration = TimeSpan.FromMilliseconds(160);
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.Target = "Translation"; move.Duration = fade.Duration;
        move.InsertKeyFrame(0, new(0, 12, 0)); move.InsertKeyFrame(1, Vector3.Zero, ease);
        var show = visual.Compositor.CreateAnimationGroup(); show.Add(fade); show.Add(move);
        ElementCompositionPreview.SetImplicitShowAnimation(QueuePane, show);
        var hide = visual.Compositor.CreateScalarKeyFrameAnimation();
        hide.Target = "Opacity"; hide.Duration = TimeSpan.FromMilliseconds(100);
        hide.InsertExpressionKeyFrame(0, "this.StartingValue"); hide.InsertKeyFrame(1, 0);
        ElementCompositionPreview.SetImplicitHideAnimation(QueuePane, hide);
    }
    private static void ConfigureSurfaceTransitions(DependencyObject root, bool enabled)
    {
        if (root is Border border)
            border.BackgroundTransition = enabled ? new BrushTransition { Duration = TimeSpan.FromMilliseconds(160) } : null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            ConfigureSurfaceTransitions(VisualTreeHelper.GetChild(root, i), enabled);
    }
    private void MotionSurface_Loaded(object sender, RoutedEventArgs args)
    {
        ConfigureSurfaceTransitions((DependencyObject)sender, PlayerAnimationEnabled);
        Typography_Loaded(sender, args);
    }
    private void SetBackIconMotion(bool enabled)
    {
        BackAnimatedIcon.Source = enabled ? new AnimatedBackVisualSource() : null;
        PlayerBackAnimatedIcon.Source = enabled ? new AnimatedBackVisualSource() : null;
        AnimatedIcon.SetState(BackAnimatedIcon, "Normal");
        AnimatedIcon.SetState(PlayerBackAnimatedIcon, "Normal");
    }
    private void Action_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        SetActionIconState(sender, "PointerOver");
    }
    private void SetActionIconState(object sender, string state)
    {
        if (sender is Button { Content: AnimatedIcon icon })
            AnimatedIcon.SetState(icon, PlayerAnimationEnabled ? state : "Normal");
    }
    private readonly HashSet<FrameworkElement> _jumpingIcons = [];
    private void BounceIcon(FrameworkElement icon, bool sideways)
    {
        if (!PlayerAnimationEnabled || _hidden) return;
        _jumpingIcons.Add(icon);
        FluentMotion.Bounce(icon, sideways ? new(6, 0, 0) : new(0, -6, 0));
    }
    private void ResetIconBounces()
    {
        foreach (var icon in _jumpingIcons)
        {
            var visual = ElementCompositionPreview.GetElementVisual(icon);
            visual.StopAnimation("Translation"); visual.StopAnimation("Scale"); visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        _jumpingIcons.Clear();
    }
    private readonly HashSet<FrameworkElement> _actionMotionButtons = [];
    private void Action_Loaded(object sender, RoutedEventArgs args)
    {
        var button = (FrameworkElement)sender;
        if (!_actionMotionButtons.Add(button)) return;
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Cover_PointerPressed), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Action_PointerReleased), true);
        button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Action_KeyDown), true);
        button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Action_KeyUp), true);
        if (button is Button action) action.Click += (_, _) =>
        {
            if (action != PreviousButton && action != NextButton) BounceActionIcon(action);
        };
    }
    private void BounceActionIcon(Button button)
    {
        if (button == BackButton || button == PlayerBackButton || button == AccountButton) return;
        if (ActionIcon(button) is not { } icon || !PlayerAnimationEnabled || _hidden) return;
        _jumpingIcons.Add(icon);
        if (button == PlayPauseButton) FluentMotion.Sink(icon);
        else FluentMotion.Bounce(icon, button == NextButton ? new(6, 0, 0) : button == PreviousButton ? new(-6, 0, 0) : new(0, -6, 0));
    }
    private FrameworkElement? ActionIcon(Button button) => button == QueueButton ? QueueIconHost :
        button == DownloadCurrentButton ? DownloadIconHost : button.Content as IconElement ??
        (button.Content is DependencyObject content ? FindDescendant<IconElement>(content) : null);
    private void Action_Unloaded(object sender, RoutedEventArgs args)
    {
        // The transport is reparented between the footer and player page.
        // Keep its one set of handlers through delayed Unloaded notifications.
        Cover_Unloaded(sender, args);
    }
    private void Action_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)
        {
            SetActionIconState(sender, "Pressed");
            SetCoverFeedback((FrameworkElement)sender, .98f);
        }
    }
    private void Action_KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter)
        {
            SetActionIconState(sender, "Normal");
            SetCoverFeedback((FrameworkElement)sender, 1);
        }
    }
    private void StopInterfaceMotion()
    {
        AnimatePageEntrance(false);
        foreach (var cover in _feedbackCovers) ResetCoverFeedback(cover);
        _feedbackCovers.Clear();
        ResetIconBounces();
        StopSelectionMotion();
        UpdateSelectionIndicators(false);
        QueueGlyph.Opacity = 1;
        CancelCoverTransitions();
        ElementCompositionPreview.SetImplicitShowAnimation(QueuePane, null);
        ElementCompositionPreview.SetImplicitHideAnimation(QueuePane, null);
        var queue = ElementCompositionPreview.GetElementVisual(QueuePane);
        queue.StopAnimation("Opacity"); queue.StopAnimation("Translation");
        queue.Opacity = 1; queue.Properties.InsertVector3("Translation", Vector3.Zero);
        _configuredMotionEnabled = null;
        StopDownloadCompletionMotion();
    }
    private void AnimatePageEntrance(bool allow, bool backward = false)
    {
        var visual = ElementCompositionPreview.GetElementVisual(PageBody);
        ElementCompositionPreview.SetIsTranslationEnabled(PageBody, true);
        visual.StopAnimation("Opacity"); visual.StopAnimation("Translation");
        visual.Opacity = 1; visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!allow || !PlayerAnimationEnabled) return;
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new(.1f, .9f), new(.2f, 1));
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(180);
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.Duration = fade.Duration;
        move.InsertKeyFrame(0, new(backward ? -18 : 18, 0, 0)); move.InsertKeyFrame(1, Vector3.Zero, ease);
        visual.StartAnimation("Opacity", fade); visual.StartAnimation("Translation", move);
    }
    private void Cover_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            SetCoverFeedback((FrameworkElement)sender, 1.025f);
    }
    private void Cover_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        SetActionIconState(sender, "Normal");
        SetCoverFeedback((FrameworkElement)sender, 1);
    }
    private void Cover_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        SetActionIconState(sender, "Pressed");
        SetCoverFeedback((FrameworkElement)sender, .98f);
    }
    private void Cover_PointerReleased(object sender, PointerRoutedEventArgs args) => SetCoverFeedback((FrameworkElement)sender,
        args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse ? 1.025f : 1);
    private void Action_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        SetActionIconState(sender,
            args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse ? "PointerOver" : "Normal");
        SetCoverFeedback((FrameworkElement)sender, 1);
    }
    private int _downloadCompletionGeneration;
    private async Task<bool> PlayDownloadCompletionMotionAsync()
    {
        StopDownloadCompletionMotion();
        if (!PlayerAnimationEnabled || _view != "downloads") return false;
        var generation = _downloadCompletionGeneration;
        var source = new AnimatedAcceptVisualSource();
        source.SetColorProperty("Foreground", _themeSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
        DownloadCompletion.Source = source;
        DownloadCompletion.PlaybackRate = 3;
        DownloadCompletion.Visibility = Visibility.Visible;
        Root.UpdateLayout();
        try
        {
            var markers = source.Markers;
            var from = markers.TryGetValue("NormalToPointerOver_Start", out var start) ? start : 0;
            var to = markers.TryGetValue("NormalToPointerOver_End", out var end) ? end : 1;
            await DownloadCompletion.PlayAsync(from, to, false);
            return DownloadCompletion.IsAnimatedVisualLoaded;
        }
        catch (Exception ex) { Services.Log.Write(ex); return false; }
        finally { if (generation == _downloadCompletionGeneration) DownloadCompletion.Visibility = Visibility.Collapsed; }
    }
    private void StopDownloadCompletionMotion()
    {
        _downloadCompletionGeneration++;
        DownloadCompletion.Stop(); DownloadCompletion.Visibility = Visibility.Collapsed;
    }
    private void Cover_Unloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        ResetCoverFeedback(element); _feedbackCovers.Remove(element);
    }
    private static void ResetCoverFeedback(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Scale"); visual.Scale = Vector3.One;
    }
    private void SetCoverFeedback(FrameworkElement element, float scale)
    {
        if (element == BackButton || element == PlayerBackButton || element == AccountButton || element == PlayPauseButton) return;
        if (!PlayerAnimationEnabled) { ResetCoverFeedback(element); return; }
        _feedbackCovers.Add(element);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        if (scale >= 1)
        {
            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.FinalValue = new(scale, scale, 1);
            spring.DampingRatio = .7f; spring.Period = TimeSpan.FromMilliseconds(60);
            visual.StartAnimation("Scale", spring);
            return;
        }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(scale < 1 ? 80 : 140);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new(scale, scale, 1),
            visual.Compositor.CreateCubicBezierEasingFunction(new(.1f, .9f), new(.2f, 1)));
        visual.StartAnimation("Scale", animation);
    }
}
