using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// The card under the cursor: a frozen bitmap of the real card, scaled up slightly and
/// given a deeper shadow so it reads as lifted off the board.
///
/// It's a bitmap rather than a VisualBrush of the live element on purpose — we hide the
/// source container the instant the drag starts, and a VisualBrush of a hidden element
/// renders nothing. Snapshotting also cuts the ghost loose from the source entirely, so
/// nothing we do to the lane underneath can flicker the thing in the user's hand.
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    private readonly BitmapSource _bitmap;
    private readonly Size _size;
    private readonly TranslateTransform _translate = new();
    private readonly ScaleTransform _scale = new(1.0, 1.0);

    private const double LiftScale = 1.03;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        _size = new Size(source.ActualWidth, source.ActualHeight);
        _bitmap = Snapshot(source);

        IsHitTestVisible = false;   // the pointer must reach the lane *under* the ghost

        _scale.CenterX = _size.Width / 2;
        _scale.CenterY = _size.Height / 2;

        RenderTransform = new TransformGroup { Children = { _scale, _translate } };

        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 28,
            ShadowDepth = 8,
            Direction = 270,
            Opacity = 0.45,
            Color = Colors.Black
        };

        Lift();
    }

    private static BitmapSource Snapshot(FrameworkElement source)
    {
        // Render at the monitor's real DPI, or the ghost is visibly softer than the board
        // it's floating over on any scaled display — which is most of them.
        var dpi = VisualTreeHelper.GetDpi(source);

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        rtb.Render(source);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Grow into the lift over 90ms. Instant scale looks like a glitch; this
    /// reads as the card being picked up.</summary>
    private void Lift()
    {
        if (!SystemParameters.ClientAreaAnimation) { _scale.ScaleX = _scale.ScaleY = LiftScale; return; }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(1.0, LiftScale, TimeSpan.FromMilliseconds(90)) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Position in adorner-layer coordinates, already grab-offset corrected.</summary>
    public void MoveTo(Point topLeft)
    {
        _translate.X = topLeft.X;
        _translate.Y = topLeft.Y;
    }

    protected override void OnRender(DrawingContext dc)
        => dc.DrawImage(_bitmap, new Rect(new Point(0, 0), _size));

    protected override Size MeasureOverride(Size constraint) => _size;
}
