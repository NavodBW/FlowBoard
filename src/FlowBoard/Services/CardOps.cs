using FlowBoard.Domain;

namespace FlowBoard.Services;

/// <summary>
/// Several ops as one undo step.
///
/// The card editor commits a title change, a due date and three checklist edits together;
/// the user thinks of that as one edit and expects one Ctrl+Z to take all of it back.
/// Revert runs backwards, because ops are only guaranteed reversible against the state
/// they were applied to.
/// </summary>
public sealed class CompositeOp(string label, IReadOnlyList<IOp> ops) : IOp
{
    public string Label => label;

    public void Apply(OpContext ctx)
    {
        foreach (var op in ops) op.Apply(ctx);
    }

    public void Revert(OpContext ctx)
    {
        for (var i = ops.Count - 1; i >= 0; i--) ops[i].Revert(ctx);
    }
}

public sealed class SetCardLabelsOp(string cardId, IReadOnlyList<string> labelIds) : IOp
{
    private List<string> _previous = new();

    public string Label => "Change labels";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        _previous = card.LabelIds.ToList();
        Write(ctx, card, labelIds);
    }

    public void Revert(OpContext ctx) => Write(ctx, ctx.Model.CardsById[cardId], _previous);

    private static void Write(OpContext ctx, Card card, IReadOnlyList<string> ids)
    {
        card.LabelIds.Clear();
        foreach (var id in ids) card.LabelIds.Add(id);
        card.Touch();

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, card);
            ctx.Store.SetCardLabels(tx, card);
        });
    }
}

public sealed class AddChecklistItemOp(string cardId, ChecklistItem item, int index) : IOp
{
    public string Label => "Add checklist item";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        item.CardId = cardId;
        card.Checklist.Insert(Math.Clamp(index, 0, card.Checklist.Count), item);
        Repack(ctx, card);
    }

    public void Revert(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        card.Checklist.Remove(item);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "checklist_items", item.Id));
        Repack(ctx, card);
    }

    internal static void Repack(OpContext ctx, Card card) =>
        ctx.Store.InTransaction(tx =>
        {
            for (var i = 0; i < card.Checklist.Count; i++)
            {
                card.Checklist[i].Position = i;
                ctx.Store.Upsert(tx, card.Checklist[i]);
            }
        });
}

public sealed class RemoveChecklistItemOp(string cardId, string itemId) : IOp
{
    private ChecklistItem? _removed;
    private int _index;

    public string Label => "Remove checklist item";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        _removed = card.Checklist.FirstOrDefault(i => i.Id == itemId);
        if (_removed is null) return;

        _index = card.Checklist.IndexOf(_removed);
        card.Checklist.Remove(_removed);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "checklist_items", itemId));
        AddChecklistItemOp.Repack(ctx, card);
    }

    public void Revert(OpContext ctx)
    {
        if (_removed is null) return;
        var card = ctx.Model.CardsById[cardId];
        card.Checklist.Insert(Math.Clamp(_index, 0, card.Checklist.Count), _removed);
        AddChecklistItemOp.Repack(ctx, card);
    }
}

/// <summary>Edit a checklist item's text or done state. Merges per item + field, so typing
/// a checklist item is one undo step rather than thirty.</summary>
public sealed class EditChecklistItemOp : IOp
{
    private readonly string _cardId;
    private readonly string _itemId;
    private readonly string _field;
    private readonly Action<ChecklistItem> _apply;
    private readonly Action<ChecklistItem> _revert;

    public string Label { get; }

    private EditChecklistItemOp(string cardId, string itemId, string field, string label,
                                Action<ChecklistItem> apply, Action<ChecklistItem> revert)
        => (_cardId, _itemId, _field, Label, _apply, _revert)
         = (cardId, itemId, field, label, apply, revert);

    public static EditChecklistItemOp Set<T>(Card card, ChecklistItem item, string field, string label,
                                             Func<ChecklistItem, T> get, Action<ChecklistItem, T> set, T value)
    {
        var old = get(item);
        return new EditChecklistItemOp(card.Id, item.Id, field, label, i => set(i, value), i => set(i, old));
    }

    public void Apply(OpContext ctx) => Run(ctx, _apply);
    public void Revert(OpContext ctx) => Run(ctx, _revert);

    private void Run(OpContext ctx, Action<ChecklistItem> mutate)
    {
        var card = ctx.Model.CardsById[_cardId];
        var item = card.Checklist.FirstOrDefault(i => i.Id == _itemId);
        if (item is null) return;

        mutate(item);
        card.Touch();
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, item));
    }

    public bool TryMerge(IOp next) =>
        next is EditChecklistItemOp e && e._itemId == _itemId && e._field == _field;
}

public sealed class AddLinkOp(string cardId, CardLink link) : IOp
{
    public string Label => "Add link";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        link.CardId = cardId;
        link.Position = card.Links.Count;
        card.Links.Add(link);
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, link));
    }

    public void Revert(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        card.Links.Remove(link);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "card_links", link.Id));
    }
}

public sealed class RemoveLinkOp(string cardId, string linkId) : IOp
{
    private CardLink? _removed;
    private int _index;

    public string Label => "Remove link";

    public void Apply(OpContext ctx)
    {
        var card = ctx.Model.CardsById[cardId];
        _removed = card.Links.FirstOrDefault(l => l.Id == linkId);
        if (_removed is null) return;

        _index = card.Links.IndexOf(_removed);
        card.Links.Remove(_removed);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "card_links", linkId));
    }

    public void Revert(OpContext ctx)
    {
        if (_removed is null) return;
        var card = ctx.Model.CardsById[cardId];
        card.Links.Insert(Math.Clamp(_index, 0, card.Links.Count), _removed);
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, _removed));
    }
}

/// <summary>Permanent delete from the Archive view. The only op in the app that destroys
/// data, and deliberately the only one that is not undoable — the undo stack is in memory
/// and would hand back an object whose rows are gone. The Archive view confirms instead.</summary>
public sealed class PurgeCardOp(string cardId) : IOp
{
    public string Label => "Delete card permanently";

    public void Apply(OpContext ctx)
    {
        ctx.Model.CardsById.Remove(cardId);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "cards", cardId));
    }

    public void Revert(OpContext ctx) =>
        throw new NotSupportedException("Permanent deletion cannot be undone.");
}
