using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// Scrolls a ScrollViewer when the pointer nears its edge during a drag.
///
/// Driven by CompositionTarget.Rendering rather than a DispatcherTimer: scroll offset is
/// applied on the same beat as the frame that draws it, so the board doesn't shear against
/// the ghost. A timer at some arbitrary interval will always beat against the refresh rate.
///
/// Velocity ramps with depth into the hot zone. A constant speed forces the user to hover
/// and wait; a ramp lets them ask for "a nudge" or "get me across the board" with the same
/// gesture, which is the thing that makes edge-scroll feel like it's reading your mind.
/// </summary>
public sealed class AutoScroller
{
    private const double HotZone = 56;      // px from the edge where scrolling engages
    private const double MaxPixelsPerSecond = 1400;
    private const double MinPixelsPerSecond = 90;

    private readonly List<(ScrollViewer View, Orientation Axis)> _targets = new();

    private Point _pointerScreen;
    private bool _running;
    private long _lastTicks;

    public void Register(ScrollViewer view, Orientation axis)
    {
        if (!_targets.Any(t => ReferenceEquals(t.View, view) && t.Axis == axis))
            _targets.Add((view, axis));
    }

    public void Unregister(ScrollViewer view) => _targets.RemoveAll(t => ReferenceEquals(t.View, view));

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastTicks = DateTime.UtcNow.Ticks;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Screen coordinates, because the pointer routinely leaves the element that
    /// captured it and element-relative positions stop meaning anything.</summary>
    public void UpdatePointer(Point screenPoint) => _pointerScreen = screenPoint;

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow.Ticks;
        var dt = Math.Min((now - _lastTicks) / (double)TimeSpan.TicksPerSecond, 0.05);
        _lastTicks = now;

        foreach (var (view, axis) in _targets)
        {
            if (!view.IsLoaded || !view.IsVisible) continue;

            Point local;
            try { local = view.PointFromScreen(_pointerScreen); }
            catch (InvalidOperationException) { continue; }   // not connected to a presentation source

            var size = axis == Orientation.Vertical ? view.ActualHeight : view.ActualWidth;
            var pos = axis == Orientation.Vertical ? local.Y : local.X;
            var cross = axis == Orientation.Vertical ? local.X : local.Y;
            var crossSize = axis == Orientation.Vertical ? view.ActualWidth : view.ActualHeight;

            // Ignore the axis entirely if the pointer isn't over this viewer at all,
            // with a little slack so a drag hugging the edge still counts.
            if (cross < -24 || cross > crossSize + 24) continue;
            if (pos < -HotZone || pos > size + HotZone) continue;

            var velocity = 0.0;
            if (pos < HotZone)
                velocity = -Ramp((HotZone - pos) / HotZone);
            else if (pos > size - HotZone)
                velocity = Ramp((pos - (size - HotZone)) / HotZone);

            if (velocity == 0) continue;

            var delta = velocity * dt;
            if (axis == Orientation.Vertical)
                view.ScrollToVerticalOffset(view.VerticalOffset + delta);
            else
                view.ScrollToHorizontalOffset(view.HorizontalOffset + delta);
        }
    }

    /// <summary>Quadratic ramp: gentle at the boundary, quick at the very edge.</summary>
    private static double Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return MinPixelsPerSecond + (MaxPixelsPerSecond - MinPixelsPerSecond) * (t * t);
    }
}
