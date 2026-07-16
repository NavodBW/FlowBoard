using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowBoard.Domain;
using FlowBoard.Services;

namespace FlowBoard.ViewModels;

public sealed partial class LabelToggle : ObservableObject
{
    public required Label Label { get; init; }
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class ChecklistDraft : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _done;
}

public sealed partial class LinkDraft : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private LinkKind _kind = LinkKind.Url;
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _display = "";
}

/// <summary>
/// The card editor works on a **draft**, not on the card.
///
/// The brief says Ctrl+Enter saves and Esc closes, which only means anything if Esc throws
/// work away — so nothing here touches the model until <see cref="Save"/>. Save then diffs
/// the draft against the card and emits one <see cref="CompositeOp"/>, so an edit session
/// that changed a title, a due date and three checklist items is a single Ctrl+Z.
///
/// The alternative (edit live, undo per keystroke) is how this usually gets built, and it
/// makes Esc a lie.
/// </summary>
public sealed partial class CardEditorViewModel : ObservableObject
{
    private readonly UndoRedoService _undo;

    public Card Card { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Priority _priority;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private string _dueTime = "17:00";
    [ObservableProperty] private string _newChecklistText = "";
    [ObservableProperty] private string _newLinkTarget = "";
    [ObservableProperty] private bool _isPreviewingDescription;

    public ObservableCollection<LabelToggle> Labels { get; } = new();
    public ObservableCollection<ChecklistDraft> Checklist { get; } = new();
    public ObservableCollection<LinkDraft> Links { get; } = new();

    /// <summary>Newest first: the interesting entry is almost always the last thing that
    /// happened, and a log that grows downward buries it.</summary>
    public IEnumerable<Activity> Activities => Card.Activities.OrderByDescending(a => a.CreatedUtc);

    public IReadOnlyList<Priority> Priorities { get; } = Enum.GetValues<Priority>();

    /// <summary>Set by the window so Ctrl+Enter can close it after a successful save.</summary>
    public Action? RequestClose { get; set; }

    public CardEditorViewModel(Card card, BoardModel model, UndoRedoService undo)
    {
        Card = card;
        _undo = undo;

        Title = card.Title;
        Description = card.Description;
        Priority = card.Priority;

        if (card.DueUtc is { } due)
        {
            var local = due.ToLocalTime();
            DueDate = local.Date;
            DueTime = local.ToString("HH:mm");
        }

        foreach (var label in model.Labels.OrderBy(l => l.Position))
            Labels.Add(new LabelToggle { Label = label, IsChecked = card.LabelIds.Contains(label.Id) });

        foreach (var item in card.Checklist.OrderBy(i => i.Position))
            Checklist.Add(new ChecklistDraft { Id = item.Id, Text = item.Text, Done = item.Done });

        foreach (var link in card.Links.OrderBy(l => l.Position))
            Links.Add(new LinkDraft { Id = link.Id, Kind = link.Kind, Target = link.Target, Display = link.Display });
    }

    [RelayCommand]
    private void AddChecklistItem()
    {
        if (string.IsNullOrWhiteSpace(NewChecklistText)) return;
        Checklist.Add(new ChecklistDraft { Text = NewChecklistText.Trim() });
        NewChecklistText = "";   // stay focused for rapid entry
    }

    [RelayCommand]
    private void RemoveChecklistItem(ChecklistDraft? item)
    {
        if (item is not null) Checklist.Remove(item);
    }

    [RelayCommand]
    private void AddLink()
    {
        var target = NewLinkTarget?.Trim();
        if (string.IsNullOrWhiteSpace(target)) return;

        // A path is a file, anything URI-shaped is a URL. Guessing here beats making the
        // user pick from a dropdown they don't care about.
        var kind = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
            ? LinkKind.Url
            : LinkKind.File;

        Links.Add(new LinkDraft { Kind = kind, Target = target, Display = target });
        NewLinkTarget = "";
    }

    [RelayCommand]
    private void RemoveLink(LinkDraft? link)
    {
        if (link is not null) Links.Remove(link);
    }

    [RelayCommand]
    private void OpenLink(LinkDraft? link)
    {
        if (link is not null) Views.Markdown.Launcher.Open(link.Target);
    }

    [RelayCommand]
    private void TogglePreview() => IsPreviewingDescription = !IsPreviewingDescription;

    private DateTime? ComposeDue()
    {
        if (DueDate is not { } date) return null;

        var time = TimeSpan.TryParse(DueTime, out var parsed) ? parsed : new TimeSpan(17, 0, 0);
        return DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>Diff draft against card, emit one op. Returns false if nothing changed —
    /// the caller closes either way, but an empty edit must not land on the undo stack.</summary>
    [RelayCommand]
    public void Save()
    {
        var ops = new List<IOp>();
        var card = Card;

        if (Title.Trim() is { Length: > 0 } title && title != card.Title)
            ops.Add(EditCardOp.Set(card, nameof(Card.Title), "Rename card", ActivityKind.TitleChanged,
                c => c.Title, (c, v) => c.Title = v, title, title));

        if (Description != card.Description)
            ops.Add(EditCardOp.Set(card, nameof(Card.Description), "Edit description",
                ActivityKind.DescriptionChanged, c => c.Description, (c, v) => c.Description = v, Description));

        if (Priority != card.Priority)
            ops.Add(EditCardOp.Set(card, nameof(Card.Priority), "Change priority", ActivityKind.PriorityChanged,
                c => c.Priority, (c, v) => c.Priority = v, Priority, Priority.ToString()));

        var due = ComposeDue();
        if (due != card.DueUtc)
            ops.Add(EditCardOp.Set(card, nameof(Card.DueUtc), "Change due date", ActivityKind.DueChanged,
                c => c.DueUtc, (c, v) => c.DueUtc = v, due,
                due is null ? "cleared" : due.Value.ToLocalTime().ToString("g")));

        var wanted = Labels.Where(l => l.IsChecked).Select(l => l.Label.Id).ToList();
        if (!wanted.SequenceEqual(card.LabelIds))
            ops.Add(new SetCardLabelsOp(card.Id, wanted));

        ops.AddRange(DiffChecklist(card));
        ops.AddRange(DiffLinks(card));

        if (ops.Count == 0) { RequestClose?.Invoke(); return; }

        _undo.Barrier();
        _undo.Execute(ops.Count == 1 ? ops[0] : new CompositeOp("Edit card", ops));
        _undo.Barrier();

        RequestClose?.Invoke();
    }

    private IEnumerable<IOp> DiffChecklist(Card card)
    {
        var existing = card.Checklist.ToDictionary(i => i.Id);

        foreach (var gone in existing.Keys.Where(id => Checklist.All(d => d.Id != id)))
            yield return new RemoveChecklistItemOp(card.Id, gone);

        for (var i = 0; i < Checklist.Count; i++)
        {
            var draft = Checklist[i];
            if (!existing.TryGetValue(draft.Id, out var item))
            {
                yield return new AddChecklistItemOp(card.Id,
                    new ChecklistItem { Id = draft.Id, CardId = card.Id, Text = draft.Text, Done = draft.Done, Position = i },
                    i);
                continue;
            }

            if (item.Text != draft.Text)
                yield return EditChecklistItemOp.Set(card, item, nameof(ChecklistItem.Text), "Edit checklist item",
                    x => x.Text, (x, v) => x.Text = v, draft.Text);

            if (item.Done != draft.Done)
                yield return EditChecklistItemOp.Set(card, item, nameof(ChecklistItem.Done), "Tick checklist item",
                    x => x.Done, (x, v) => x.Done = v, draft.Done);
        }
    }

    private IEnumerable<IOp> DiffLinks(Card card)
    {
        var existing = card.Links.ToDictionary(l => l.Id);

        foreach (var gone in existing.Keys.Where(id => Links.All(d => d.Id != id)))
            yield return new RemoveLinkOp(card.Id, gone);

        foreach (var draft in Links.Where(d => !existing.ContainsKey(d.Id)))
            yield return new AddLinkOp(card.Id, new CardLink
            {
                Id = draft.Id, CardId = card.Id, Kind = draft.Kind,
                Target = draft.Target, Display = draft.Display
            });
    }
}
