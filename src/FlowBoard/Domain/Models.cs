using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowBoard.Domain;

public enum Priority { None = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

public enum LinkKind { Url = 0, File = 1 }

public enum ActivityKind
{
    Created = 0, TitleChanged = 1, DescriptionChanged = 2, Moved = 3, MovedWorkspace = 4,
    PriorityChanged = 5, DueChanged = 6, LabelsChanged = 7, ChecklistChanged = 8,
    LinkChanged = 9, Archived = 10, Restored = 11
}

/// <summary>Base for every persisted entity. Ids are client-generated GUIDs so that
/// export/import and future sync never have to remap keys.</summary>
public abstract partial class Entity : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] private DateTime _createdUtc = DateTime.UtcNow;
    [ObservableProperty] private DateTime _modifiedUtc = DateTime.UtcNow;

    public void Touch() => ModifiedUtc = DateTime.UtcNow;
}

/// <summary>Sidebar entry. Owns an ordered set of boards and doubles as a drop target
/// for moving a card to another workspace.</summary>
public partial class Workspace : Entity
{
    [ObservableProperty] private string _name = "Workspace";
    [ObservableProperty] private int _position;
    [ObservableProperty] private bool _archived;

    public ObservableCollection<Board> Boards { get; } = new();
}

/// <summary>
/// A board is a column: one titled, accent-coloured vertical lane of cards in the
/// horizontally scrolling row. Seeded as Now / Next / Later / Parked.
/// </summary>
public partial class Board : Entity
{
    [ObservableProperty] private string _workspaceId = "";
    [ObservableProperty] private string _name = "Board";

    /// <summary>Hex accent, e.g. "#4C8DFF". Drives the lane header, the drag insertion
    /// indicator and focus rings.</summary>
    [ObservableProperty] private string _accent = "#4C8DFF";

    [ObservableProperty] private int _position;
    [ObservableProperty] private bool _archived;

    /// <summary>0 = no WIP limit. Reserved for the stage 2 header badge.</summary>
    [ObservableProperty] private int _wipLimit;

    public ObservableCollection<Card> Cards { get; } = new();
}

public partial class Card : Entity
{
    [ObservableProperty] private string _boardId = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Priority _priority = Priority.None;
    [ObservableProperty] private DateTime? _dueUtc;
    [ObservableProperty] private int _position;
    [ObservableProperty] private bool _archived;

    /// <summary>Last time a human did something to this card. Drives the ageing
    /// desaturation, and is deliberately distinct from <see cref="Entity.ModifiedUtc"/>,
    /// which also moves when we rewrite <see cref="Position"/> during a neighbour's reorder.</summary>
    [ObservableProperty] private DateTime _touchedUtc = DateTime.UtcNow;

    public ObservableCollection<string> LabelIds { get; } = new();
    public ObservableCollection<ChecklistItem> Checklist { get; } = new();
    public ObservableCollection<CardLink> Links { get; } = new();
    public ObservableCollection<Activity> Activities { get; } = new();

    public Card()
    {
        // The card face shows "3/7", which changes when an item is added, removed, or
        // ticked. Only the first two raise CollectionChanged, so we also have to watch
        // each item's Done — otherwise the counter silently lies until the card reloads.
        Checklist.CollectionChanged += (_, e) =>
        {
            foreach (var i in e.OldItems?.OfType<ChecklistItem>() ?? Enumerable.Empty<ChecklistItem>())
                i.PropertyChanged -= OnChecklistItemChanged;
            foreach (var i in e.NewItems?.OfType<ChecklistItem>() ?? Enumerable.Empty<ChecklistItem>())
                i.PropertyChanged += OnChecklistItemChanged;

            RaiseChecklistProgress();
        };
    }

    private void OnChecklistItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChecklistItem.Done)) RaiseChecklistProgress();
    }

    private void RaiseChecklistProgress()
    {
        OnPropertyChanged(nameof(ChecklistDone));
        OnPropertyChanged(nameof(ChecklistTotal));
    }

    public int ChecklistDone => Checklist.Count(i => i.Done);
    public int ChecklistTotal => Checklist.Count;
}

public partial class Label : Entity
{
    [ObservableProperty] private string _name = "Label";
    [ObservableProperty] private string _color = "#8B8B8B";
    [ObservableProperty] private int _position;
}

public partial class ChecklistItem : Entity
{
    [ObservableProperty] private string _cardId = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _done;
    [ObservableProperty] private int _position;
}

public partial class CardLink : Entity
{
    [ObservableProperty] private string _cardId = "";
    [ObservableProperty] private LinkKind _kind = LinkKind.Url;
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private int _position;
}

/// <summary>Append-only. Never edited, never undone — an undo of a move appends its own
/// entry rather than deleting history, because the log is a record of what happened.</summary>
public partial class Activity : Entity
{
    [ObservableProperty] private string _cardId = "";
    [ObservableProperty] private ActivityKind _kind;
    [ObservableProperty] private string _detail = "";
}

/// <summary>The whole in-memory graph. Loaded once at startup, mutated only by ops.</summary>
public sealed class BoardModel
{
    public ObservableCollection<Workspace> Workspaces { get; } = new();
    public ObservableCollection<Label> Labels { get; } = new();

    // Flat indices — O(1) lookup during drag/drop and undo, kept in sync by ops.
    public Dictionary<string, Workspace> WorkspacesById { get; } = new();
    public Dictionary<string, Board> BoardsById { get; } = new();
    public Dictionary<string, Card> CardsById { get; } = new();

    public void Index(Workspace ws)
    {
        WorkspacesById[ws.Id] = ws;
        foreach (var b in ws.Boards)
        {
            BoardsById[b.Id] = b;
            foreach (var card in b.Cards) CardsById[card.Id] = card;
        }
    }
}
