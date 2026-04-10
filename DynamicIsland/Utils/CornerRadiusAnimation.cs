using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland.Utils;

public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(CornerRadius);

    public CornerRadius From
    {
        get => (CornerRadius)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(CornerRadius), typeof(CornerRadiusAnimation));

    public CornerRadius To
    {
        get => (CornerRadius)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(CornerRadius), typeof(CornerRadiusAnimation));

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(CornerRadiusAnimation));

    protected override Freezable CreateInstanceCore()
    {
        return new CornerRadiusAnimation();
    }

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = From;
        var to = To;
        var progress = animationClock.CurrentProgress ?? 0d;
        if (EasingFunction is not null)
        {
            progress = EasingFunction.Ease(progress);
        }

        return new CornerRadius(
            Lerp(from.TopLeft, to.TopLeft, progress),
            Lerp(from.TopRight, to.TopRight, progress),
            Lerp(from.BottomRight, to.BottomRight, progress),
            Lerp(from.BottomLeft, to.BottomLeft, progress));
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }
}
