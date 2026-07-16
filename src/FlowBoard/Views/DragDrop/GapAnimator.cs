using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// Opens the insertion gap by sliding containers out of the way.
///
/// Two things make this cheap and smooth:
///
/// 1. It animates <see cref="TranslateTransform"/>, not Margin or Height. A Margin
///    animation re-runs measure+arrange on the whole lane every frame at 60fps; a render
///    transform is composited on the GPU and touches no layout at all. This is the whole
///    reason the gap can animate while the board is also auto-scrolling.
///
/// 2. Nothing is inserted into the ItemsControl. The gap is an illusion made of offsets,
///    so the bound collection stays untouched until the drop actually commits — which
///    matters because that collection is only ever allowed to change through an op.
/// </summary>
public static class GapAnimator
{
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(140));

    public static void Apply(DragSession session, double gap, Orientation axis)
    {
        var wanted = new HashSet<FrameworkElement>();

        for (var i = 0; i < session.Slots.Count; i++)
        {
            var container = session.Slots[i].Container;
            var offset = i >= session.TargetIndex ? gap : 0;

            if (offset != 0) wanted.Add(container);
            Slide(container, offset, axis);
        }

        // Anything that was shifted last frame but isn't wanted now slides home. Without
        // this, moving the pointer back up the lane leaves a trail of displaced cards.
        foreach (var stale in session.Shifted.Where(c => !wanted.Contains(c)).ToList())
            Slide(stale, 0, axis);

        session.Shifted.Clear();
        foreach (var c in wanted) session.Shifted.Add(c);
    }

    public static void ClearAll(DragSession session, Orientation axis)
    {
        foreach (var c in session.Shifted) Slide(c, 0, axis);
        session.Shifted.Clear();
    }

    private static void Slide(FrameworkElement element, double offset, Orientation axis)
    {
        var transform = EnsureTransform(element);
        var property = axis == Orientation.Vertical
            ? TranslateTransform.YProperty
            : TranslateTransform.XProperty;

        var current = axis == Orientation.Vertical ? transform.Y : transform.X;
        if (Math.Abs(current - offset) < 0.5) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            // Respect the OS "show window contents while dragging" / reduced-motion
            // setting. Someone who turned animations off does not want us to argue.
            transform.BeginAnimation(property, null);
            if (axis == Orientation.Vertical) transform.Y = offset; else transform.X = offset;
            return;
        }

        transform.BeginAnimation(property, new DoubleAnimation
        {
            To = offset,
            Duration = SlideDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private static TranslateTransform EnsureTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;

        var t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    /// <summary>Drops the transform entirely once a drop has committed and the real
    /// layout has caught up — a stale HoldEnd animation would fight the new arrange.</summary>
    public static void Reset(FrameworkElement element)
    {
        if (element.RenderTransform is not TranslateTransform t) return;
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        t.X = 0;
        t.Y = 0;
    }
}
