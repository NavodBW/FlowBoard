using System.Data;
using FlowBoard.Domain;

namespace FlowBoard.Services;

internal static class OrderHelper
{
    /// <summary>Re-densifies positions 0..n-1 and writes every changed sibling. Cheap at
    /// this scale, and immune to the drift/rebalance bugs of fractional indices.</summary>
    public static void Repack(OpContext ctx, IDbTransaction tx, Board board)
    {
        for (var i = 0; i < board.Cards.Count; i++)
        {
            var card = board.Cards[i];
            if (card.Position == i && card.BoardId == board.Id) continue;
            card.Position = i;
            card.BoardId = board.Id;
            ctx.Store.Upsert(tx, card);
        }
    }

    public static void Repack(OpContext ctx, IDbTransaction tx, Workspace ws)
    {
        for (var i = 0; i < ws.Boards.Count; i++)
        {
            var b = ws.Boards[i];
            if (b.Position == i && b.WorkspaceId == ws.Id) continue;
            b.Position = i;
            b.WorkspaceId = ws.Id;
            ctx.Store.Upsert(tx, b);
        }
    }

    public static void Log(OpContext ctx, IDbTransaction tx, Card card, ActivityKind kind, string detail)
    {
        var a = new Activity { CardId = card.Id, Kind = kind, Detail = detail };
        card.Activities.Add(a);
        ctx.Store.Upsert(tx, a);
    }
}

/// <summary>Add a card at a given index in a board (quick-add, paste, redo of a delete).</summary>
public sealed class AddCardOp(Card card, string boardId, int index) : IOp
{
    public string Label => "Add card";

    public void Apply(OpContext ctx)
    {
        var board = ctx.Model.BoardsById[boardId];
        card.BoardId = boardId;
        board.Cards.Insert(Math.Clamp(index, 0, board.Cards.Count), card);
        ctx.Model.CardsById[card.Id] = card;

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, card);
            ctx.Store.SetCardLabels(tx, card);
            foreach (var i in card.Checklist) ctx.Store.Upsert(tx, i);
            foreach (var l in card.Links) ctx.Store.Upsert(tx, l);
            OrderHelper.Repack(ctx, tx, board);
            OrderHelper.Log(ctx, tx, card, ActivityKind.Created, card.Title);
        });
    }

    public void Revert(OpContext ctx)
    {
        var board = ctx.Model.BoardsById[card.BoardId];
        board.Cards.Remove(card);
        ctx.Model.CardsById.Remove(card.Id);

        // Hard delete is correct here: undoing a create should leave no trace. Cascades
        // clear labels/checklist/links/activities.
        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Delete(tx, "cards", card.Id);
            OrderHelper.Repack(ctx, tx, board);
        });
    }
}

/// <summary>
/// Move a card within a board, to another board, or to a board in another workspace.
/// This is the op every drop produces — which is why drag-and-drop is undoable for free.
/// </summary>
public sealed class MoveCardOp(string cardId, string toBoardId, int toIndex) : IOp
{
    private string _fromBoardId = "";
    private int _fromIndex;

    public string Label => "Move card";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        var from = ctx.Model.BoardsById[card.BoardId];
        var to = ctx.Model.BoardsById[toBoardId];

        _fromBoardId = from.Id;
        _fromIndex = from.Cards.IndexOf(card);

        var crossedWorkspace = from.WorkspaceId != to.WorkspaceId;

        from.Cards.Remove(card);
        to.Cards.Insert(Math.Clamp(toIndex, 0, to.Cards.Count), card);
        card.BoardId = to.Id;
        card.TouchedUtc = DateTime.UtcNow;
        card.Touch();

        ctx.Store.InTransaction(tx =>
        {
            OrderHelper.Repack(ctx, tx, from);
            OrderHelper.Repack(ctx, tx, to);
            ctx.Store.Upsert(tx, card);

            if (crossedWorkspace)
            {
                var fw = ctx.Model.WorkspacesById[from.WorkspaceId];
                var tw = ctx.Model.WorkspacesById[to.WorkspaceId];
                OrderHelper.Log(ctx, tx, card, ActivityKind.MovedWorkspace,
                    $"{fw.Name}/{from.Name} → {tw.Name}/{to.Name}");
            }
            else if (from.Id != to.Id)
            {
                OrderHelper.Log(ctx, tx, card, ActivityKind.Moved, $"{from.Name} → {to.Name}");
            }
        });
    }

    public void Revert(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        var current = ctx.Model.BoardsById[card.BoardId];
        var back = ctx.Model.BoardsById[_fromBoardId];

        current.Cards.Remove(card);
        back.Cards.Insert(Math.Clamp(_fromIndex, 0, back.Cards.Count), card);
        card.BoardId = back.Id;
        card.Touch();

        ctx.Store.InTransaction(tx =>
        {
            OrderHelper.Repack(ctx, tx, current);
            OrderHelper.Repack(ctx, tx, back);
            ctx.Store.Upsert(tx, card);
        });
    }
}

/// <summary>Reorder a board (lane) within its workspace — the column drag.</summary>
public sealed class MoveBoardOp(string boardId, int toIndex) : IOp
{
    private int _fromIndex;

    public string Label => "Move board";

    public void Apply(OpContext ctx) => Move(ctx, toIndex, capture: true);
    public void Revert(OpContext ctx) => Move(ctx, _fromIndex, capture: false);

    private void Move(OpContext ctx, int index, bool capture)
    {
        var board = ctx.Model.BoardsById[boardId];
        var ws = ctx.Model.WorkspacesById[board.WorkspaceId];
        var current = ws.Boards.IndexOf(board);
        if (capture) _fromIndex = current;

        ws.Boards.Move(current, Math.Clamp(index, 0, ws.Boards.Count - 1));
        ctx.Store.InTransaction(tx => OrderHelper.Repack(ctx, tx, ws));
    }
}

/// <summary>Create a board (lane) in a workspace.</summary>
public sealed class AddBoardOp(Board board, string workspaceId, int index) : IOp
{
    public string Label => "Add board";

    public void Apply(OpContext ctx)
    {
        var ws = ctx.Model.WorkspacesById[workspaceId];
        board.WorkspaceId = workspaceId;
        ws.Boards.Insert(Math.Clamp(index, 0, ws.Boards.Count), board);
        ctx.Model.BoardsById[board.Id] = board;

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, board);
            OrderHelper.Repack(ctx, tx, ws);
        });
    }

    public void Revert(OpContext ctx)
    {
        var ws = ctx.Model.WorkspacesById[board.WorkspaceId];
        ws.Boards.Remove(board);
        ctx.Model.BoardsById.Remove(board.Id);

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Delete(tx, "boards", board.Id);
            OrderHelper.Repack(ctx, tx, ws);
        });
    }
}

/// <summary>Rename / recolour / re-limit a board. Snapshot-based, like card edits.</summary>
public sealed class EditBoardOp : IOp
{
    private readonly string _boardId;
    private readonly string _field;
    private readonly Action<Board> _apply;
    private readonly Action<Board> _revert;

    public string Label { get; }

    private EditBoardOp(string boardId, string field, string label,
                        Action<Board> apply, Action<Board> revert)
        => (_boardId, _field, Label, _apply, _revert) = (boardId, field, label, apply, revert);

    public static EditBoardOp Set<T>(Board board, string field, string label,
                                     Func<Board, T> get, Action<Board, T> set, T value)
    {
        var old = get(board);
        return new EditBoardOp(board.Id, field, label, b => set(b, value), b => set(b, old));
    }

    public void Apply(OpContext ctx) => Run(ctx, _apply);
    public void Revert(OpContext ctx) => Run(ctx, _revert);

    private void Run(OpContext ctx, Action<Board> mutate)
    {
        var board = ctx.Model.BoardsById[_boardId];
        mutate(board);
        board.Touch();
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, board));
    }

    public bool TryMerge(IOp next) =>
        next is EditBoardOp e && e._boardId == _boardId && e._field == _field;
}

/// <summary>
/// Generic card field edit. Snapshot-based so one op type covers title, description,
/// priority, due date and labels without a bespoke class per field.
/// </summary>
public sealed class EditCardOp : IOp
{
    private readonly string _cardId;
    private readonly string _field;
    private readonly Action<Card> _apply;
    private readonly Action<Card> _revert;
    private readonly ActivityKind _kind;
    private readonly string _detail;

    public string Label { get; }

    private EditCardOp(string cardId, string field, string label, ActivityKind kind, string detail,
                       Action<Card> apply, Action<Card> revert)
        => (_cardId, _field, Label, _kind, _detail, _apply, _revert)
         = (cardId, field, label, kind, detail, apply, revert);

    public static EditCardOp Set<T>(Card card, string field, string label, ActivityKind kind,
                                    Func<Card, T> get, Action<Card, T> set, T value, string detail = "")
    {
        var old = get(card);
        return new EditCardOp(card.Id, field, label, kind, detail,
            c => set(c, value), c => set(c, old));
    }

    public void Apply(OpContext ctx) => Run(ctx, _apply, log: true);
    public void Revert(OpContext ctx) => Run(ctx, _revert, log: false);

    private void Run(OpContext ctx, Action<Card> mutate, bool log)
    {
        var card = ctx.Model.CardsById[_cardId];
        mutate(card);
        card.TouchedUtc = DateTime.UtcNow;
        card.Touch();

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, card);
            ctx.Store.SetCardLabels(tx, card);
            if (log && _kind != ActivityKind.Created) OrderHelper.Log(ctx, tx, card, _kind, _detail);
        });
    }

    /// <summary>Coalesce consecutive edits to the same field of the same card — one undo
    /// step per editing session, not per keystroke.</summary>
    public bool TryMerge(IOp next) =>
        next is EditCardOp e && e._cardId == _cardId && e._field == _field;
}

/// <summary>Archive / restore a card. Soft by design: nothing leaves the DB until the
/// user permanently deletes it from the Archive view.</summary>
public sealed class ArchiveCardOp(string cardId, bool archived) : IOp
{
    private int _fromIndex;

    public string Label => archived ? "Archive card" : "Restore card";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        var board = ctx.Model.BoardsById[card.BoardId];
        _fromIndex = board.Cards.IndexOf(card);

        card.Archived = archived;
        card.Touch();
        if (archived) board.Cards.Remove(card);
        else board.Cards.Insert(Math.Clamp(_fromIndex < 0 ? card.Position : _fromIndex, 0, board.Cards.Count), card);

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, card);
            OrderHelper.Repack(ctx, tx, board);
            OrderHelper.Log(ctx, tx, card,
                archived ? ActivityKind.Archived : ActivityKind.Restored, board.Name);
        });
    }

    public void Revert(OpContext ctx) => new ArchiveCardOp(cardId, !archived).Apply(ctx);
}

/// <summary>Archive / restore a whole board. Cards ride along untouched — restoring the
/// board brings its cards back exactly as they were.</summary>
public sealed class ArchiveBoardOp(string boardId, bool archived) : IOp
{
    private int _fromIndex;

    public string Label => archived ? "Archive board" : "Restore board";

    public void Apply(OpContext ctx)
    {
        var board = ctx.Model.BoardsById[boardId];
        var ws = ctx.Model.WorkspacesById[board.WorkspaceId];
        _fromIndex = ws.Boards.IndexOf(board);

        board.Archived = archived;
        board.Touch();
        if (archived) ws.Boards.Remove(board);
        else ws.Boards.Insert(Math.Clamp(_fromIndex < 0 ? board.Position : _fromIndex, 0, ws.Boards.Count), board);

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, board);
            OrderHelper.Repack(ctx, tx, ws);
        });
    }

    public void Revert(OpContext ctx) => new ArchiveBoardOp(boardId, !archived).Apply(ctx);
}
