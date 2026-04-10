using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland.Utils;

public static class AnimationHelper
{
    public static Storyboard CreateExpandStoryboard(
        Border mainSurface,
        FrameworkElement expandedRegion,
        UIElement actionPanel,
        UIElement actionHeader,
        UIElement actionButtons,
        TranslateTransform expandedRegionTranslateTransform,
        ScaleTransform islandScaleTransform,
        CornerRadius mainSurfaceFromCornerRadius,
        CornerRadius mainSurfaceToCornerRadius,
        double targetWidth,
        double targetHeight,
        double targetExpandedRegionHeight,
        double targetPanelOpacity,
        double targetExpandedRegionOffset,
        TimeSpan duration,
        bool isExpanding)
    {
        IEasingFunction shellEasing = new CubicEase { EasingMode = EasingMode.EaseOut };
        IEasingFunction contentEasing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.HoldEnd
        };

        var widthAnimation = new DoubleAnimation
        {
            From = CoerceFinite(mainSurface.Width, mainSurface.ActualWidth),
            To = targetWidth,
            Duration = duration,
            EasingFunction = shellEasing
        };
        Storyboard.SetTarget(widthAnimation, mainSurface);
        Storyboard.SetTargetProperty(widthAnimation, new PropertyPath(FrameworkElement.WidthProperty));

        var heightAnimation = new DoubleAnimation
        {
            From = CoerceFinite(mainSurface.Height, mainSurface.ActualHeight),
            To = targetHeight,
            Duration = duration,
            EasingFunction = shellEasing
        };
        Storyboard.SetTarget(heightAnimation, mainSurface);
        Storyboard.SetTargetProperty(heightAnimation, new PropertyPath(FrameworkElement.HeightProperty));

        var expandedRegionHeightAnimation = new DoubleAnimation
        {
            From = CoerceFinite(expandedRegion.Height, expandedRegion.ActualHeight),
            To = targetExpandedRegionHeight,
            BeginTime = isExpanding ? TimeSpan.FromMilliseconds(24) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.88 : duration.TotalMilliseconds * 0.72),
            EasingFunction = shellEasing
        };
        Storyboard.SetTarget(expandedRegionHeightAnimation, expandedRegion);
        Storyboard.SetTargetProperty(expandedRegionHeightAnimation, new PropertyPath(FrameworkElement.HeightProperty));

        var mainSurfaceCornerRadiusAnimation = new CornerRadiusAnimation
        {
            From = mainSurfaceFromCornerRadius,
            To = mainSurfaceToCornerRadius,
            Duration = duration,
            EasingFunction = shellEasing
        };
        Storyboard.SetTarget(mainSurfaceCornerRadiusAnimation, mainSurface);
        Storyboard.SetTargetProperty(mainSurfaceCornerRadiusAnimation, new PropertyPath(Border.CornerRadiusProperty));

        var panelOpacityAnimation = new DoubleAnimation
        {
            From = CoerceFinite(actionPanel.Opacity, 1.0),
            To = targetPanelOpacity,
            BeginTime = isExpanding ? TimeSpan.FromMilliseconds(36) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.64 : duration.TotalMilliseconds * 0.45),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(panelOpacityAnimation, actionPanel);
        Storyboard.SetTargetProperty(panelOpacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        var headerOpacityAnimation = new DoubleAnimation
        {
            From = CoerceFinite(actionHeader.Opacity, 1.0),
            To = targetPanelOpacity,
            BeginTime = isExpanding ? TimeSpan.FromMilliseconds(68) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.42 : duration.TotalMilliseconds * 0.3),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(headerOpacityAnimation, actionHeader);
        Storyboard.SetTargetProperty(headerOpacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        var buttonsOpacityAnimation = new DoubleAnimation
        {
            From = CoerceFinite(actionButtons.Opacity, 1.0),
            To = targetPanelOpacity,
            BeginTime = isExpanding ? TimeSpan.FromMilliseconds(124) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.34 : duration.TotalMilliseconds * 0.24),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(buttonsOpacityAnimation, actionButtons);
        Storyboard.SetTargetProperty(buttonsOpacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        var expandedRegionOffsetAnimation = new DoubleAnimation
        {
            From = CoerceFinite(expandedRegionTranslateTransform.Y, 0.0),
            To = targetExpandedRegionOffset,
            BeginTime = isExpanding ? TimeSpan.FromMilliseconds(28) : TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.7 : duration.TotalMilliseconds * 0.48),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(expandedRegionOffsetAnimation, expandedRegionTranslateTransform);
        Storyboard.SetTargetProperty(expandedRegionOffsetAnimation, new PropertyPath(TranslateTransform.YProperty));

        var bounceScaleXAnimation = new DoubleAnimation
        {
            From = CoerceFinite(islandScaleTransform.ScaleX, 1.0),
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.55 : duration.TotalMilliseconds * 0.45),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(bounceScaleXAnimation, islandScaleTransform);
        Storyboard.SetTargetProperty(bounceScaleXAnimation, new PropertyPath(ScaleTransform.ScaleXProperty));

        var bounceScaleYAnimation = new DoubleAnimation
        {
            From = CoerceFinite(islandScaleTransform.ScaleY, 1.0),
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(isExpanding ? duration.TotalMilliseconds * 0.55 : duration.TotalMilliseconds * 0.45),
            EasingFunction = contentEasing
        };
        Storyboard.SetTarget(bounceScaleYAnimation, islandScaleTransform);
        Storyboard.SetTargetProperty(bounceScaleYAnimation, new PropertyPath(ScaleTransform.ScaleYProperty));

        storyboard.Children.Add(widthAnimation);
        storyboard.Children.Add(heightAnimation);
        storyboard.Children.Add(expandedRegionHeightAnimation);
        storyboard.Children.Add(mainSurfaceCornerRadiusAnimation);
        storyboard.Children.Add(panelOpacityAnimation);
        storyboard.Children.Add(headerOpacityAnimation);
        storyboard.Children.Add(buttonsOpacityAnimation);
        storyboard.Children.Add(expandedRegionOffsetAnimation);
        storyboard.Children.Add(bounceScaleXAnimation);
        storyboard.Children.Add(bounceScaleYAnimation);
        return storyboard;
    }

    private static double CoerceFinite(double value, double fallback)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value))
        {
            return value;
        }

        if (!double.IsNaN(fallback) && !double.IsInfinity(fallback))
        {
            return fallback;
        }

        return 0.0;
    }

    public static Storyboard CreateStatusTransitionStoryboard(
        UIElement statusTextPanel,
        UIElement badgePanel,
        TranslateTransform translateTransform)
    {
        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop
        };

        var textOpacity = new DoubleAnimationUsingKeyFrames();
        textOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        textOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(textOpacity, statusTextPanel);
        Storyboard.SetTargetProperty(textOpacity, new PropertyPath(UIElement.OpacityProperty));

        var badgeOpacity = new DoubleAnimationUsingKeyFrames();
        badgeOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.78, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        badgeOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(badgeOpacity, badgePanel);
        Storyboard.SetTargetProperty(badgeOpacity, new PropertyPath(UIElement.OpacityProperty));

        var translateY = new DoubleAnimationUsingKeyFrames();
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(2.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        translateY.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(translateY, translateTransform);
        Storyboard.SetTargetProperty(translateY, new PropertyPath(TranslateTransform.YProperty));

        storyboard.Children.Add(textOpacity);
        storyboard.Children.Add(badgeOpacity);
        storyboard.Children.Add(translateY);
        return storyboard;
    }

    public static Storyboard CreateBounceStoryboard(ScaleTransform scaleTransform)
    {
        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop
        };

        var scaleXAnimation = new DoubleAnimationUsingKeyFrames();
        scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)))
        {
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
        });
        scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(scaleXAnimation, scaleTransform);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath(ScaleTransform.ScaleXProperty));

        var scaleYAnimation = new DoubleAnimationUsingKeyFrames();
        scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(scaleYAnimation, scaleTransform);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath(ScaleTransform.ScaleYProperty));

        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        return storyboard;
    }

    public static void TransitionBrush(
        SolidColorBrush brush,
        string targetColor,
        bool animate,
        int durationMs = 260)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(targetColor)!;

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation
            {
                To = color,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
