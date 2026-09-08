using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace FantasyMontior;

/// <summary>Keeps the sensor target separate from the animated presentation value.</summary>
public sealed class AnimatedUsageBar : ProgressBar
{
    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue), typeof(double), typeof(AnimatedUsageBar),
        new PropertyMetadata(0d, (d, _) => ((AnimatedUsageBar)d).UpdateTarget()));
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(AnimatedUsageBar),
        new PropertyMetadata(false, (d, _) => ((AnimatedUsageBar)d).SnapToTarget()));

    public double TargetValue { get => (double)GetValue(TargetValueProperty); set => SetValue(TargetValueProperty, value); }
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    public AnimatedUsageBar()
    {
        Loaded += (_, _) => SnapToTarget();
        Unloaded += (_, _) => SnapToTarget();
        IsVisibleChanged += (_, _) => SnapToTarget();
    }

    private double Target => double.IsFinite(TargetValue) ? Math.Clamp(TargetValue, Minimum, Maximum) : Minimum;

    private void SnapToTarget()
    {
        SetValue(ValueProperty, Target);
        BeginAnimation(ValueProperty, null);
    }

    private void UpdateTarget()
    {
        if (!IsLoaded || !IsVisible || !IsLive)
        {
            SnapToTarget();
            return;
        }
        // Retarget from the currently displayed fill, including an interrupted transition.
        var from = Value;
        var to = Target;
        // Keep the base at the displayed value until WPF ticks the new clock.
        SetValue(ValueProperty, from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(750))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        animation.Completed += (_, _) => SnapToTarget();
        BeginAnimation(ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
