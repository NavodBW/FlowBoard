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

// ---------------------------------------------------------------- labels

public sealed class AddLabelOp(Label label) : IOp
{
    public string Label => "Add label";

    public void Apply(OpContext ctx)
    {
        label.Position = ctx.Model.Labels.Count;
        ctx.Model.Labels.Add(label);
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, label));
    }

    public void Revert(OpContext ctx)
    {
        ctx.Model.Labels.Remove(label);
        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "labels", label.Id));
    }
}

/// <summary>Rename or recolour a label. Merges per label + field so dragging a colour or
/// typing a name is one undo step.</summary>
public sealed class EditLabelOp : IOp
{
    private readonly string _labelId;
    private readonly string _field;
    private readonly Action<Domain.Label> _apply;
    private readonly Action<Domain.Label> _revert;

    public string Label { get; }

    private EditLabelOp(string labelId, string field, string label,
                        Action<Domain.Label> apply, Action<Domain.Label> revert)
        => (_labelId, _field, Label, _apply, _revert) = (labelId, field, label, apply, revert);

    public static EditLabelOp Set<T>(Domain.Label label, string field, string description,
                                     Func<Domain.Label, T> get, Action<Domain.Label, T> set, T value)
    {
        var old = get(label);
        return new EditLabelOp(label.Id, field, description, l => set(l, value), l => set(l, old));
    }

    public void Apply(OpContext ctx) => Run(ctx, _apply);
    public void Revert(OpContext ctx) => Run(ctx, _revert);

    private void Run(OpContext ctx, Action<Domain.Label> mutate)
    {
        var label = ctx.Model.Labels.FirstOrDefault(l => l.Id == _labelId);
        if (label is null) return;

        mutate(label);
        label.Touch();
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, label));
    }

    public bool TryMerge(IOp next) => next is EditLabelOp e && e._labelId == _labelId && e._field == _field;
}

/// <summary>
/// Delete a label everywhere.
///
/// A label isn't only a row in `labels` — it's also every card that wears it. ON DELETE
/// CASCADE takes care of the card_labels rows in SQLite, but the in-memory cards would keep
/// a dangling id and the label chip would vanish with no way back. So we record exactly
/// which cards had it, and undo restores both the label and its wearers.
/// </summary>
public sealed class DeleteLabelOp(string labelId) : IOp
{
    private Domain.Label? _removed;
    private int _index;
    private List<string> _wornBy = new();

    public string Label => "Delete label";

    public void Apply(OpContext ctx)
    {
        _removed = ctx.Model.Labels.FirstOrDefault(l => l.Id == labelId);
        if (_removed is null) return;

        _index = ctx.Model.Labels.IndexOf(_removed);
        _wornBy = ctx.Model.CardsById.Values.Where(c => c.LabelIds.Contains(labelId))
                                            .Select(c => c.Id).ToList();

        foreach (var id in _wornBy) ctx.Model.CardsById[id].LabelIds.Remove(labelId);
        ctx.Model.Labels.Remove(_removed);

        ctx.Store.InTransaction(tx =>
        {
            foreach (var id in _wornBy) ctx.Store.SetCardLabels(tx, ctx.Model.CardsById[id]);
            ctx.Store.Delete(tx, "labels", labelId);
        });
    }

    public void Revert(OpContext ctx)
    {
        if (_removed is null) return;

        ctx.Model.Labels.Insert(Math.Clamp(_index, 0, ctx.Model.Labels.Count), _removed);
        foreach (var id in _wornBy)
            if (ctx.Model.CardsById.TryGetValue(id, out var card) && !card.LabelIds.Contains(labelId))
                card.LabelIds.Add(labelId);

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, _removed);
            foreach (var id in _wornBy) ctx.Store.SetCardLabels(tx, ctx.Model.CardsById[id]);
        });
    }
}

// ---------------------------------------------------------------- workspaces

public sealed class AddWorkspaceOp(Workspace workspace) : IOp
{
    public string Label => "Add workspace";

    public void Apply(OpContext ctx)
    {
        workspace.Position = ctx.Model.Workspaces.Count;
        ctx.Model.Workspaces.Add(workspace);
        ctx.Model.Index(workspace);

        ctx.Store.InTransaction(tx =>
        {
            ctx.Store.Upsert(tx, workspace);
            foreach (var b in workspace.Boards) ctx.Store.Upsert(tx, b);
        });
    }

    public void Revert(OpContext ctx)
    {
        ctx.Model.Workspaces.Remove(workspace);
        ctx.Model.WorkspacesById.Remove(workspace.Id);
        foreach (var b in workspace.Boards) ctx.Model.BoardsById.Remove(b.Id);

        ctx.Store.InTransaction(tx => ctx.Store.Delete(tx, "workspaces", workspace.Id));
    }
}

public sealed class EditWorkspaceOp : IOp
{
    private readonly string _wsId;
    private readonly string _field;
    private readonly Action<Workspace> _apply;
    private readonly Action<Workspace> _revert;

    public string Label { get; }

    private EditWorkspaceOp(string wsId, string field, string label,
                            Action<Workspace> apply, Action<Workspace> revert)
        => (_wsId, _field, Label, _apply, _revert) = (wsId, field, label, apply, revert);

    public static EditWorkspaceOp Set<T>(Workspace ws, string field, string label,
                                         Func<Workspace, T> get, Action<Workspace, T> set, T value)
    {
        var old = get(ws);
        return new EditWorkspaceOp(ws.Id, field, label, w => set(w, value), w => set(w, old));
    }

    public void Apply(OpContext ctx) => Run(ctx, _apply);
    public void Revert(OpContext ctx) => Run(ctx, _revert);

    private void Run(OpContext ctx, Action<Workspace> mutate)
    {
        if (!ctx.Model.WorkspacesById.TryGetValue(_wsId, out var ws)) return;

        mutate(ws);
        ws.Touch();
        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, ws));
    }

    public bool TryMerge(IOp next) => next is EditWorkspaceOp e && e._wsId == _wsId && e._field == _field;
}

/// <summary>
/// Archive a whole workspace — boards, cards and all.
///
/// Soft, like every other delete in the app: the rows stay, the flag flips, and Ctrl+Z puts
/// the entire tree back exactly where it was. Removing it from the Workspaces collection is
/// what makes it vanish from the sidebar; the flag is what keeps it out on the next load.
/// </summary>
public sealed class ArchiveWorkspaceOp(string workspaceId, bool archived) : IOp
{
    private int _index;

    public string Label => archived ? "Delete workspace" : "Restore workspace";

    public void Apply(OpContext ctx)
    {
        var ws = ctx.Model.WorkspacesById[workspaceId];
        _index = ctx.Model.Workspaces.IndexOf(ws);

        ws.Archived = archived;
        ws.Touch();

        if (archived) ctx.Model.Workspaces.Remove(ws);
        else ctx.Model.Workspaces.Insert(Math.Clamp(_index < 0 ? ws.Position : _index, 0, ctx.Model.Workspaces.Count), ws);

        ctx.Store.InTransaction(tx => ctx.Store.Upsert(tx, ws));
    }

    public void Revert(OpContext ctx) => new ArchiveWorkspaceOp(workspaceId, !archived).Apply(ctx);
}
