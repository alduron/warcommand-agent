using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace WarCommand.Agent.Overlay;

/// <summary>
/// Binds a <see cref="RangeBase.Value"/> that eases to each new number instead of jumping to it.
/// </summary>
/// <remarks>
/// The board is re-rendered on a one second tick, so a countdown bound straight to Value advances
/// in one second steps. On a 120 s row that is a visible twitch every second on every open row at
/// once, which is the opposite of ambient. Easing over the tick interval makes the same data read
/// as continuous drain.
/// </remarks>
public static class SmoothProgress
{
    /// <summary>How long a value change takes to play. One tick, so the motion never falls behind.</summary>
    private static readonly Duration Glide = new(TimeSpan.FromSeconds(1));

    /// <summary>
    /// A jump this large or larger is a different row, not the same row draining: a re-seed, a
    /// slot move, or a container reused for another ticket. Those snap, because easing a row's
    /// whole lifetime backwards reads as a bug.
    /// </summary>
    private const double SnapThreshold = 0.1;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value",
        typeof(double),
        typeof(SmoothProgress),
        new PropertyMetadata(0.0, OnValueChanged));

    public static void SetValue(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ValueProperty, value);
    }

    public static double GetValue(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(ValueProperty);
    }

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not RangeBase bar)
        {
            return;
        }

        var target = (double)e.NewValue;
        if (Math.Abs(target - bar.Value) >= SnapThreshold)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        bar.BeginAnimation(
            RangeBase.ValueProperty,
            new DoubleAnimation(target, Glide) { FillBehavior = FillBehavior.HoldEnd });
    }
}
