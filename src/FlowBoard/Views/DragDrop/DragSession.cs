using System.Windows;
using System.Windows.Controls;
using FlowBoard.Domain;

namespace FlowBoard.Views.DragDrop;

public enum DragKind { Card, Lane }

/// <summary>A container plus the rect it occupied when the drag began.</summary>
public sealed record SlotSnapshot(FrameworkElement Container, Rect Bounds);

/// <summary>
/// Everything one drag needs to know, including how to put the world back if it's cancelled.
///
/// The <see cref="Slots"/> cache is the important part. Index maths has to answer "which gap
/// is the pointer in", and the obvious way — asking each container for its current position —
/// feeds back on itself the moment we start translating those same containers to open the
/// gap: the container moves, the hit test changes, the gap moves, the container moves again.
/// So we snapshot the untranslated layout once, in the panel's own coordinate space (which
/// scrolling doesn't affect), and do all subsequent maths against that.
/// </summary>
public sealed class DragSession
{
    public required DragKind Kind { get; init; }

    /// <summary>The card being dragged (Kind == Card).</summary>
    public Card? Card { get; init; }

    /// <summary>The lane being dragged (Kind == Lane).</summary>
    public Board? Lane { get; init; }

    public required Board SourceBoard { get; init; }
    public required int SourceIndex { get; init; }

    /// <summary>The container we hid on pickup, restored on cancel.</summary>
    public required FrameworkElement SourceContainer { get; init; }

    /// <summary>Where in the grabbed element the pointer went down, so the ghost stays
    /// pinned under the same spot on the card rather than snapping its corner to the
    /// cursor.</summary>
    public required Point GrabOffset { get; init; }

    public required Size GhostSize { get; init; }

    public DragGhostAdorner? Ghost { get; set; }

    // ---- live target state ----

    public Board? TargetBoard { get; set; }
    public Workspace? TargetWorkspace { get; set; }
    public int TargetIndex { get; set; } = -1;

    /// <summary>The panel we're currently computing indices against.</summary>
    public Panel? TargetPanel { get; set; }

    /// <summary>Untranslated layout of the target panel's containers, minus the dragged
    /// one. Rebuilt only when the pointer enters a different panel.</summary>
    public List<SlotSnapshot> Slots { get; } = new();

    /// <summary>Containers we've translated to open a gap, so we can put them all back.</summary>
    public HashSet<FrameworkElement> Shifted { get; } = new();

    public bool Cancelled { get; set; }

    /// <summary>A drop is a no-op if nothing actually moved. Worth checking: pushing a
    /// no-op onto the undo stack means the user's next Ctrl+Z appears to do nothing,
    /// which reads as a bug.</summary>
    public bool IsNoOp =>
        TargetBoard is not null
        && ReferenceEquals(TargetBoard, SourceBoard)
        && (TargetIndex == SourceIndex || TargetIndex == SourceIndex + 1);
}
