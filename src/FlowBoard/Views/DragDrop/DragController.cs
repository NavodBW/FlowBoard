using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FlowBoard.Domain;
using FlowBoard.Services;
using FlowBoard.ViewModels;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// Owns every drag in one window: pickup, targeting, the gap, auto-scroll, cancel, commit.
///
/// The whole thing funnels into exactly one line that changes data — <c>Undo.Execute(op)</c>.
/// Everything above it is presentation: ghosts, offsets and hit tests that can be thrown
/// away at any moment without the model ever knowing a drag happened. That's what makes
/// ESC-cancel trivial (drop the visuals, touch nothing) and undo automatic (the drop is
/// just another op).
/// </summary>
public sealed class DragController
{
    private readonly Window _window;
    private readonly AutoScroller _scroller = new();

    private readonly List<(FrameworkElement Element, Board Board, Panel Panel)> _cardZones = new();
    private readonly List<(FrameworkElement Element, Workspace Workspace)> _workspaceZones = new();
    private readonly List<(FrameworkElement Element, Panel Panel)> _laneZones = new();

    private DragSession? _session;
    private Point _pressOrigin;
    private FrameworkElement? _pressCandidate;
    private DragKind _pendingKind;

    public DragController(Window window)
    {
        _window = window;
        _window.PreviewKeyDown += OnWindowKeyDown;
    }

    private ShellViewModel Vm => (ShellViewModel)_window.DataContext;

    public bool IsDragging => _session is not null;

    // ------------------------------------------------------------ registration

    public void RegisterCardZone(FrameworkElement element, Board board, Panel panel)
    {
        _cardZones.RemoveAll(z => ReferenceEquals(z.Element, element));
        _cardZones.Add((element, board, panel));
    }

    public void RegisterWorkspaceZone(FrameworkElement element, Workspace ws)
    {
        _workspaceZones.RemoveAll(z => ReferenceEquals(z.Element, element));
        _workspaceZones.Add((element, ws));
    }

    public void RegisterLaneZone(FrameworkElement element, Panel panel)
    {
        _laneZones.RemoveAll(z => ReferenceEquals(z.Element, element));
        _laneZones.Add((element, panel));
    }

    public void Unregister(FrameworkElement element)
    {
        _cardZones.RemoveAll(z => ReferenceEquals(z.Element, element));
        _workspaceZones.RemoveAll(z => ReferenceEquals(z.Element, element));
        _laneZones.RemoveAll(z => ReferenceEquals(z.Element, element));
    }

    public void RegisterScroller(ScrollViewer view, Orientation axis) => _scroller.Register(view, axis);
    public void UnregisterScroller(ScrollViewer view) => _scroller.Unregister(view);

    // ------------------------------------------------------------ pickup

    /// <summary>Records a potential drag. Nothing happens until the pointer clears the
    /// system drag threshold — otherwise every click on a card would start a drag and
    /// selecting a card by clicking it would be impossible.</summary>
    public void NotePress(FrameworkElement source, DragKind kind, MouseButtonEventArgs e)
    {
        _pressCandidate = source;
        _pendingKind = kind;
        _pressOrigin = e.GetPosition(_window);
    }

    public void NoteRelease() => _pressCandidate = null;

    public void NoteMove(MouseEventArgs e)
    {
        if (_session is not null) { Update(e); return; }
        if (_pressCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(_window);
        if (Math.Abs(now.X - _pressOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pressOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        Begin(_pressCandidate, _pendingKind, e);
        _pressCandidate = null;
    }

    private void Begin(FrameworkElement source, DragKind kind, MouseEventArgs e)
    {
        var layer = AdornerLayer.GetAdornerLayer(_window.Content as Visual ?? _window);
        if (layer is null) return;

        Board sourceBoard;
        int sourceIndex;
        Card? card = null;
        Board? lane = null;

        if (kind == DragKind.Card)
        {
            if (source.DataContext is not Card c) return;
            card = c;
            if (!Vm.Model.BoardsById.TryGetValue(c.BoardId, out var b)) return;
            sourceBoard = b;
            sourceIndex = b.Cards.IndexOf(c);
        }
        else
        {
            if (source.DataContext is not Board b) return;
            lane = b;
            sourceBoard = b;
            sourceIndex = Vm.ActiveWorkspace?.Boards.IndexOf(b) ?? -1;
        }

        if (sourceIndex < 0) return;

        // Snapshot before hiding: the ghost is a bitmap of a visible element. Take it
        // from the template root, which is tight to the card's own bounds.
        var ghost = new DragGhostAdorner(_window.Content as UIElement ?? _window, source);

        // What we hide and exclude from the slot cache is the *layout* container — the
        // ContentPresenter the ItemsControl generated — not the Border the template put
        // inside it. Hiding the Border would leave its presenter sitting in the panel,
        // holding a card-shaped hole open and throwing every index off by one.
        var container = LayoutContainerOf(source);

        _session = new DragSession
        {
            Kind = kind,
            Card = card,
            Lane = lane,
            SourceBoard = sourceBoard,
            SourceIndex = sourceIndex,
            SourceContainer = container,
            GrabOffset = e.GetPosition(source),
            GhostSize = new Size(source.ActualWidth, source.ActualHeight),
            Ghost = ghost
        };

        layer.Add(ghost);

        // Collapse, not Hidden: the slot closes up immediately, which reads as the card
        // leaving the board. Hidden would leave a permanent hole where it used to be.
        container.Visibility = Visibility.Collapsed;

        Mouse.Capture(_window, CaptureMode.SubTree);
        _scroller.Start();

        Update(e);
    }

    // ------------------------------------------------------------ targeting

    private void Update(MouseEventArgs e)
    {
        if (_session is not { } s) return;

        var inWindow = e.GetPosition(_window);
        s.Ghost?.MoveTo(new Point(inWindow.X - s.GrabOffset.X, inWindow.Y - s.GrabOffset.Y));
        _scroller.UpdatePointer(_window.PointToScreen(inWindow));

        if (s.Kind == DragKind.Card) UpdateCardTarget(s, inWindow);
        else UpdateLaneTarget(s, inWindow);
    }

    private void UpdateCardTarget(DragSession s, Point inWindow)
    {
        // A workspace in the sidebar wins over a lane: it's a smaller, deliberate target,
        // and it's the gesture that replaced the old board-tab strip.
        var ws = _workspaceZones.FirstOrDefault(z => Contains(z.Element, inWindow));
        if (ws.Element is not null && !ReferenceEquals(ws.Workspace, Vm.ActiveWorkspace))
        {
            GapAnimator.ClearAll(s, Orientation.Vertical);
            s.TargetWorkspace = ws.Workspace;
            s.TargetBoard = null;
            s.TargetPanel = null;
            return;
        }

        s.TargetWorkspace = null;

        var zone = _cardZones.FirstOrDefault(z => Contains(z.Element, inWindow));
        if (zone.Element is null)
        {
            GapAnimator.ClearAll(s, Orientation.Vertical);
            s.TargetBoard = null;
            s.TargetPanel = null;
            s.TargetIndex = -1;
            return;
        }

        if (!ReferenceEquals(s.TargetPanel, zone.Panel))
        {
            GapAnimator.ClearAll(s, Orientation.Vertical);
            s.TargetPanel = zone.Panel;
            s.TargetBoard = zone.Board;
            Resnapshot(s, zone.Panel);
        }

        var local = zone.Panel.PointFromScreen(_window.PointToScreen(inWindow));
        s.TargetIndex = IndexFor(s, local.Y, vertical: true);

        GapAnimator.Apply(s, s.GhostSize.Height + 8, Orientation.Vertical);
    }

    private void UpdateLaneTarget(DragSession s, Point inWindow)
    {
        var zone = _laneZones.FirstOrDefault(z => Contains(z.Element, inWindow));
        if (zone.Element is null) return;

        if (!ReferenceEquals(s.TargetPanel, zone.Panel))
        {
            s.TargetPanel = zone.Panel;
            Resnapshot(s, zone.Panel);
        }

        var local = zone.Panel.PointFromScreen(_window.PointToScreen(inWindow));
        s.TargetIndex = IndexFor(s, local.X, vertical: false);

        GapAnimator.Apply(s, s.GhostSize.Width + 12, Orientation.Horizontal);
    }

    /// <summary>Cache the target panel's layout, excluding the dragged container. Panel
    /// coordinates, so scrolling the lane doesn't invalidate any of it.</summary>
    private static void Resnapshot(DragSession s, Panel panel)
    {
        s.Slots.Clear();

        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(child, s.SourceContainer)) continue;
            if (child.Visibility != Visibility.Visible) continue;

            var origin = child.TranslatePoint(new Point(0, 0), panel);

            // Subtract any transform we've applied ourselves — the cache must describe
            // where things *live*, not where we're currently pretending they are.
            if (child.RenderTransform is System.Windows.Media.TranslateTransform t)
                origin = new Point(origin.X - t.X, origin.Y - t.Y);

            s.Slots.Add(new SlotSnapshot(child, new Rect(origin, new Size(child.ActualWidth, child.ActualHeight))));
        }

        s.Slots.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top) != 0
            ? a.Bounds.Top.CompareTo(b.Bounds.Top)
            : a.Bounds.Left.CompareTo(b.Bounds.Left));
    }

    /// <summary>How many slots the pointer has passed the midpoint of. Midpoints, not
    /// edges: with edges the insertion point lags the pointer and the gap feels like it's
    /// resisting you.</summary>
    private static int IndexFor(DragSession s, double pos, bool vertical)
    {
        var index = 0;
        foreach (var slot in s.Slots)
        {
            var mid = vertical
                ? slot.Bounds.Top + slot.Bounds.Height / 2
                : slot.Bounds.Left + slot.Bounds.Width / 2;
            if (pos < mid) break;
            index++;
        }
        return index;
    }

    /// <summary>The nearest ancestor (or self) whose visual parent is a Panel — i.e. the
    /// element the items panel actually lays out.</summary>
    private static FrameworkElement LayoutContainerOf(FrameworkElement element)
    {
        var node = element;
        while (VisualTreeHelper.GetParent(node) is FrameworkElement parent)
        {
            if (parent is Panel) return node;
            node = parent;
        }
        return element;
    }

    private bool Contains(FrameworkElement element, Point inWindow)
    {
        if (!element.IsVisible) return false;
        var topLeft = element.TranslatePoint(new Point(0, 0), _window);
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight)).Contains(inWindow);
    }

    // ------------------------------------------------------------ finish

    public void NoteDrop(MouseButtonEventArgs e)
    {
        if (_session is null) { _pressCandidate = null; return; }
        Update(e);
        Commit();
    }

    private void Commit()
    {
        if (_session is not { } s) return;

        if (s.Cancelled) { Teardown(); return; }

        IOp? op = null;

        if (s.Kind == DragKind.Card && s.Card is { } card)
        {
            if (s.TargetWorkspace is { } ws)
            {
                // Dropped on a workspace: land in its first lane. Any other choice would
                // be guessing on the user's behalf.
                var landing = ws.Boards.FirstOrDefault(b => !b.Archived);
                if (landing is not null)
                    op = new MoveCardOp(card.Id, landing.Id, landing.Cards.Count);
            }
            else if (s.TargetBoard is { } board && s.TargetIndex >= 0 && !s.IsNoOp)
            {
                op = new MoveCardOp(card.Id, board.Id, s.TargetIndex);
            }
        }
        else if (s.Kind == DragKind.Lane && s.Lane is { } lane
                 && s.TargetIndex >= 0 && s.TargetIndex != s.SourceIndex)
        {
            // TargetIndex counts slots excluding the dragged lane, which is exactly what
            // MoveBoardOp wants — it removes before it inserts.
            op = new MoveBoardOp(lane.Id, s.TargetIndex);
        }

        Teardown();

        // Execute after teardown so the ItemsControl re-arranges against clean transforms
        // rather than fighting held animations.
        if (op is not null)
        {
            Vm.Undo.Execute(op);
            Vm.Undo.Barrier();
        }
    }

    private void Cancel()
    {
        if (_session is null) return;
        _session.Cancelled = true;
        Teardown();
    }

    private void Teardown()
    {
        if (_session is not { } s) return;

        var axis = s.Kind == DragKind.Card ? Orientation.Vertical : Orientation.Horizontal;
        GapAnimator.ClearAll(s, axis);
        foreach (var slot in s.Slots) GapAnimator.Reset(slot.Container);

        if (s.Ghost is not null)
            AdornerLayer.GetAdornerLayer(_window.Content as Visual ?? _window)?.Remove(s.Ghost);

        s.SourceContainer.Visibility = Visibility.Visible;
        GapAnimator.Reset(s.SourceContainer);

        _scroller.Stop();
        Mouse.Capture(null);
        _session = null;
        _pressCandidate = null;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _session is null) return;

        // ESC during a drag means abandon it, and nothing else. Handled, so it never
        // reaches the card editor or closes the window.
        Cancel();
        e.Handled = true;
    }
}
