using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// The card under the cursor: a frozen bitmap of the real card, scaled up slightly and
/// given a deeper shadow so it reads as lifted off the board.
///
/// **Why a child visual instead of transforming the adorner itself.**
/// An AdornerLayer positions its adorners during arrange, and in doing so it owns the
/// adorner's transform — anything set on the Adorner's own RenderTransform is overwritten.
/// The symptom is precise and misleading: the ghost pins itself to the adorned element's
/// origin (top-left of the window) and refuses to follow the mouse, while the drop still
/// lands perfectly, because the model never cared where the bitmap was drawn. So the bitmap
/// lives in a child Image and *that* carries the transform. The layer doesn't touch a
/// child's RenderTransform, and moving it costs no layout pass — which the documented
/// alternative (GetDesiredTransform + AdornerLayer.Update on every move) does, at 60fps,
/// during a drag that is already animating a gap and auto-scrolling.
///
/// It's a bitmap rather than a VisualBrush because we hide the source container the instant
/// the drag starts, and a VisualBrush of a hidden element renders nothing. Snapshotting also
/// cuts the ghost loose from the source, so nothing in the lane below can flicker it.
/// </summary>
public sealed class DragGhostAdorner : Adorner
{
    private readonly VisualCollection _children;
    private readonly Image _image;
    private readonly Size _size;
    private readonly TranslateTransform _translate = new();
    private readonly ScaleTransform _scale = new(1.0, 1.0);

    private const double LiftScale = 1.03;

    public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
        : base(adornedElement)
    {
        _size = new Size(source.ActualWidth, source.ActualHeight);
        _children = new VisualCollection(this);

        _image = new Image
        {
            Source = Snapshot(source),
            Width = _size.Width,
            Height = _size.Height,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _scale, _translate } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black
            }
        };

        _children.Add(_image);

        IsHitTestVisible = false;   // the pointer must reach the lane *under* the ghost
        Lift();
    }

    private static BitmapSource Snapshot(FrameworkElement source)
    {
        // Render at the monitor's real DPI, or the ghost is visibly softer than the board
        // it floats over on any scaled display — which is most of them.
        var dpi = VisualTreeHelper.GetDpi(source);

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        rtb.Render(source);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Grow into the lift over 90ms. Instant scale looks like a glitch; this reads
    /// as the card being picked up.</summary>
    private void Lift()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _scale.ScaleX = _scale.ScaleY = LiftScale;
            return;
        }

        var anim = new DoubleAnimation(1.0, LiftScale, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Top-left of the ghost, in coordinates of the adorned element.</summary>
    public void MoveTo(Point topLeft)
    {
        _translate.X = topLeft.X;
        _translate.Y = topLeft.Y;
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _image.Measure(_size);
        return _size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Arrange at the origin; the translate does all the moving. Adorners aren't clipped
        // to the adorned element, so the ghost can roam the whole window from here.
        _image.Arrange(new Rect(new Point(0, 0), _size));
        return finalSize;
    }
}
