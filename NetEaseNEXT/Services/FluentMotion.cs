using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NetEaseNEXT.Services;

internal static class FluentMotion
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(480);

    // The user-approved taskbar-style arc: lift, linger, overshoot, settle.
    // peak = (6,0,0) right; (-6,0,0) left; (0,-6,0) up.
    internal static void Bounce(UIElement icon, Vector3 peak)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(icon, true);
        var visual = ElementCompositionPreview.GetElementVisual(icon);
        var motion = visual.Compositor.CreateVector3KeyFrameAnimation();
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new(.85f, 0), new(.75f, 1));
        motion.Duration = Duration;
        motion.InsertExpressionKeyFrame(0, "this.StartingValue");
        motion.InsertKeyFrame(.22f, peak, ease);
        motion.InsertKeyFrame(.42f, peak);
        motion.InsertKeyFrame(.82f, -peak * .2f, ease);
        motion.InsertKeyFrame(1, Vector3.Zero, ease);
        visual.StartAnimation("Translation", motion);
    }

    internal static void Sink(FrameworkElement icon)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(icon, true);
        var visual = ElementCompositionPreview.GetElementVisual(icon);
        visual.CenterPoint = new((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0);
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new(.2f, .8f), new(.2f, 1));
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = Duration; scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        scale.InsertKeyFrame(.22f, new(.86f, .86f, 1), ease); scale.InsertKeyFrame(.42f, new(.86f, .86f, 1));
        scale.InsertKeyFrame(1, Vector3.One, ease);
        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.Duration = Duration; move.InsertExpressionKeyFrame(0, "this.StartingValue");
        move.InsertKeyFrame(.22f, new(0, 2, 0), ease); move.InsertKeyFrame(.42f, new(0, 2, 0));
        move.InsertKeyFrame(1, Vector3.Zero, ease);
        visual.StartAnimation("Scale", scale); visual.StartAnimation("Translation", move);
    }

    // One shared stretch-and-contract indicator for navigation and song rows.
    internal static void MoveIndicator(FrameworkElement marker, float previousY, Vector3 target, float height)
    {
        var visual = ElementCompositionPreview.GetElementVisual(marker);
        visual.CenterPoint = Vector3.Zero;
        var stretch = visual.Compositor.CreateCubicBezierEasingFunction(new(.9f, .1f), new(1, .2f));
        var settle = visual.Compositor.CreateCubicBezierEasingFunction(new(.1f, .9f), new(.2f, 1));
        var bridge = 1 + Math.Abs(target.Y - previousY) / height;
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = Duration; scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        scale.InsertKeyFrame(1f / 3, new(1, bridge, 1), stretch); scale.InsertKeyFrame(1, Vector3.One, settle);
        var move = visual.Compositor.CreateVector3KeyFrameAnimation();
        move.Duration = Duration; move.InsertExpressionKeyFrame(0, "this.StartingValue");
        move.InsertKeyFrame(1f / 3, new(target.X, Math.Min(previousY, target.Y), 0), stretch);
        move.InsertKeyFrame(1, target, settle);
        visual.StartAnimation("Scale", scale); visual.StartAnimation("Translation", move);
    }
}
