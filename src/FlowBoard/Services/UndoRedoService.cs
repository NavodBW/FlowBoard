using CommunityToolkit.Mvvm.ComponentModel;
using FlowBoard.Data;
using FlowBoard.Domain;

namespace FlowBoard.Services;

public sealed record OpContext(BoardModel Model, FlowBoardStore Store);

/// <summary>
/// An undoable unit of work. Implementations must be symmetric: Revert must restore the
/// exact prior state of both the in-memory graph and the database.
/// </summary>
public interface IOp
{
    /// <summary>Shown in the undo tooltip, e.g. "Move card".</summary>
    string Label { get; }

    void Apply(OpContext ctx);
    void Revert(OpContext ctx);

    /// <summary>Rapid-fire edits of the same field on the same entity collapse into one
    /// undo step (e.g. typing in a title box), so Ctrl+Z doesn't rewind one keystroke.</summary>
    bool TryMerge(IOp next) => false;
}

/// <summary>
/// In-memory, 100 levels. Every mutation in the app goes through <see cref="Execute"/>;
/// nothing else is allowed to touch the graph or the store.
/// </summary>
public sealed partial class UndoRedoService : ObservableObject
{
    private const int MaxDepth = 100;   // brief asks for >= 50

    private readonly OpContext _ctx;
    private readonly LinkedList<IOp> _undo = new();
    private readonly Stack<IOp> _redo = new();

    /// <summary>Suppresses merging across an explicit boundary (e.g. focus loss, drag start).</summary>
    private bool _barrier;

    public UndoRedoService(OpContext ctx) => _ctx = ctx;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    public void Execute(IOp op)
    {
        op.Apply(_ctx);

        if (!_barrier && _undo.Last is { } last && last.Value.TryMerge(op))
        {
            // merged into the previous step; stack depth unchanged
        }
        else
        {
            _undo.AddLast(op);
            if (_undo.Count > MaxDepth) _undo.RemoveFirst();
        }

        _barrier = false;
        _redo.Clear();
        Notify();
    }

    /// <summary>Call when a logical editing session ends so the next op starts a fresh
    /// undo step instead of merging into the last one.</summary>
    public void Barrier() => _barrier = true;

    public void Undo()
    {
        if (_undo.Last is not { } node) return;
        _undo.RemoveLast();
        node.Value.Revert(_ctx);
        _redo.Push(node.Value);
        _barrier = true;
        Notify();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var op = _redo.Pop();
        op.Apply(_ctx);
        _undo.AddLast(op);
        _barrier = true;
        Notify();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Notify();
    }

    /// <summary>
    /// For the one action that cannot be taken back: permanent deletion from the Archive.
    ///
    /// It applies the op and then wipes the stack, because every entry in it may hold a
    /// reference to the object we just destroyed — undoing "move card" after the card's
    /// rows are gone would either resurrect a ghost or throw. Callers must confirm with
    /// the user first; this is the point of no return, and it should be the only one.
    /// </summary>
    public void ExecuteIrreversible(IOp op)
    {
        op.Apply(_ctx);
        Clear();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }
}
