using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowBoard.Data;
using FlowBoard.Domain;
using FlowBoard.Services;

namespace FlowBoard.ViewModels;

public enum MoveDirection { Left, Right, Up, Down }

/// <summary>
/// The board surface's view model: which workspace is active, what's selected, what the
/// filter says, and the commands the menu and keyboard both route through.
///
/// It owns no mutation logic of its own — every command here builds an op and hands it to
/// <see cref="UndoRedoService"/>. That's the same path drag-and-drop takes, which is why
/// there is exactly one place in the app where data changes.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>Board accents. Public because the colour picker binds to it — one list,
    /// so a new board's default and the swatch grid can never drift apart.</summary>
    public static readonly string[] AccentPalette =
        { "#4C8DFF", "#37C2A8", "#B586F0", "#F5A524", "#E5484D", "#8A94A6" };

    public IReadOnlyList<string> Accents => AccentPalette;

    private readonly FlowBoardStore _store;

    public BoardModel Model { get; private set; }
    public UndoRedoService Undo { get; private set; }
    public AppSettings Settings { get; }
    public FilterViewModel Filter { get; } = new();

    [ObservableProperty] private Workspace? _activeWorkspace;
    [ObservableProperty] private Board? _focusedBoard;
    [ObservableProperty] private Card? _selectedCard;

    /// <summary>Which lane's quick-add box is open. One at a time: two open boxes means
    /// Enter is ambiguous.</summary>
    [ObservableProperty] private Board? _quickAddBoard;

    [ObservableProperty] private string _quickAddText = "";

    /// <summary>Which board's header is in rename mode, and the draft name.</summary>
    [ObservableProperty] private Board? _renamingBoard;
    [ObservableProperty] private string _renameText = "";

    /// <summary>Which workspace's sidebar row is in rename mode, and its draft name.
    /// Separate from the board rename state so the two can never fight over one text box.</summary>
    [ObservableProperty] private Workspace? _renamingWorkspace;
    [ObservableProperty] private string _workspaceRenameText = "";

    /// <summary>Display order. Manual means "however the user dragged them".</summary>
    [ObservableProperty] private CardSort _sort = CardSort.Manual;

    public IReadOnlyList<CardSort> SortModes { get; } = Enum.GetValues<CardSort>();

    /// <summary>Dragging a card while a sort is active would be a lie: the drop sets a
    /// position the view isn't showing, so the card appears to leap somewhere else. Rather
    /// than silently doing something surprising, card drag is off unless the order on screen
    /// is the order being changed. Lane drag stays live — lanes aren't sorted.</summary>
    public bool CanDragCards => Sort == CardSort.Manual;

    /// <summary>Set by the window; opens the modal editor. The VM shouldn't know what a
    /// Window is, so it asks.</summary>
    public Action<Card>? RequestOpenCard { get; set; }
    public Action? RequestShowShortcuts { get; set; }
    public Action? RequestShowArchive { get; set; }
    public Action? RequestShowLabels { get; set; }
    public Func<bool, string?>? RequestFilePath { get; set; }
    /// <summary>Ask a yes/no question. Returns the answer.</summary>
    public Func<string, bool>? RequestConfirm { get; set; }

    /// <summary>State a fact. Separate from RequestConfirm because a message with only one
    /// possible response must not be dressed up as a choice — an OK/Cancel box for "this
    /// can't be deleted" invites the user to cancel something that isn't happening.</summary>
    public Action<string>? RequestNotify { get; set; }
    public Action? RequestFocusSearch { get; set; }

    public ShellViewModel(BoardModel model, UndoRedoService undo, AppSettings settings, FlowBoardStore store)
    {
        Model = model;
        Undo = undo;
        Settings = settings;
        _store = store;

        ActiveWorkspace = model.Workspaces.FirstOrDefault(w => !w.Archived);
        FocusedBoard = ActiveWorkspace?.Boards.FirstOrDefault();
    }

    partial void OnSortChanged(CardSort value)
    {
        ApplySort();
        OnPropertyChanged(nameof(CanDragCards));
    }

    /// <summary>
    /// Push the sort onto every lane's default view.
    ///
    /// An ItemsControl bound to board.Cards renders through that collection's *default*
    /// view, so setting CustomSort there reorders the display without the ItemsSource
    /// binding knowing anything happened — and without touching Position, the database, or
    /// the undo stack. Manual clears it, and the hand-made order is simply still there.
    /// </summary>
    public void ApplySort()
    {
        foreach (var board in Model.BoardsById.Values)
        {
            if (CollectionViewSource.GetDefaultView(board.Cards) is not ListCollectionView view) continue;
            view.CustomSort = Sort == CardSort.Manual ? null : new CardComparer(Sort, Model);
        }
    }

    partial void OnActiveWorkspaceChanged(Workspace? value)
    {
        FocusedBoard = value?.Boards.FirstOrDefault();
        SelectedCard = null;
    }

    partial void OnSelectedCardChanged(Card? value)
    {
        if (value is not null && Model.BoardsById.TryGetValue(value.BoardId, out var board))
            FocusedBoard = board;
    }

    public IEnumerable<Board> VisibleBoards =>
        ActiveWorkspace?.Boards.Where(b => !b.Archived) ?? Enumerable.Empty<Board>();

    // ------------------------------------------------------------ cards

    [RelayCommand]
    private void BeginQuickAdd(Board? board)
    {
        QuickAddBoard = board ?? FocusedBoard;
        QuickAddText = "";
    }

    /// <summary>Commit one quick-add. Returns so the caller can keep focus in the box:
    /// people add cards in runs, and making them re-click "+ Add card" each time is the
    /// difference between a tool and a chore.</summary>
    public void CommitQuickAdd()
    {
        if (QuickAddBoard is not { } board) return;

        var title = QuickAddText.Trim();
        if (title.Length == 0) { QuickAddBoard = null; return; }

        var card = new Card { BoardId = board.Id, Title = title };
        Undo.Execute(new AddCardOp(card, board.Id, board.Cards.Count));
        Undo.Barrier();   // each card is its own undo step

        QuickAddText = "";
    }

    [RelayCommand]
    private void CancelQuickAdd()
    {
        QuickAddBoard = null;
        QuickAddText = "";
    }

    [RelayCommand]
    private void OpenCard(Card? card)
    {
        var target = card ?? SelectedCard;
        if (target is not null) RequestOpenCard?.Invoke(target);
    }

    [RelayCommand]
    private void ArchiveCard(Card? card)
    {
        var target = card ?? SelectedCard;
        if (target is null) return;

        // Pick the neighbour before the card leaves, or selection lands nowhere and the
        // keyboard user has to reach for the mouse.
        var board = Model.BoardsById[target.BoardId];
        var index = board.Cards.IndexOf(target);

        Undo.Execute(new ArchiveCardOp(target.Id, true));

        SelectedCard = board.Cards.ElementAtOrDefault(Math.Min(index, board.Cards.Count - 1));
    }

    [RelayCommand]
    private void MoveCardToBoard(Board? target)
    {
        if (SelectedCard is not { } card || target is null) return;
        Undo.Execute(new MoveCardOp(card.Id, target.Id, target.Cards.Count));
    }

    /// <summary>Every board in every workspace — the "Move to board…" menu source.</summary>
    public IEnumerable<Board> AllBoards =>
        Model.Workspaces.Where(w => !w.Archived).SelectMany(w => w.Boards.Where(b => !b.Archived));

    // ------------------------------------------------------------ boards

    [RelayCommand]
    private void AddBoard()
    {
        if (ActiveWorkspace is not { } ws) return;

        var board = new Board
        {
            WorkspaceId = ws.Id,
            Name = "New board",
            Accent = AccentPalette[ws.Boards.Count % AccentPalette.Length]
        };
        Undo.Execute(new AddBoardOp(board, ws.Id, ws.Boards.Count));
        ApplySort();   // the new lane has a fresh view that knows nothing about the sort
        FocusedBoard = board;
    }

    [RelayCommand]
    private void ArchiveBoard(Board? board)
    {
        var target = board ?? FocusedBoard;
        if (target is null) return;
        Undo.Execute(new ArchiveBoardOp(target.Id, true));
    }

    /// <summary>
    /// Inline rename, same shape as quick-add: which board is in edit mode, plus the draft
    /// text. The old design took a (Board, string) tuple as a command parameter, which XAML
    /// has no way to construct — so the command existed and was unreachable. That's why
    /// boards couldn't be renamed: not a broken feature, an unbuilt one.
    /// </summary>
    [RelayCommand]
    private void BeginRename(Board? board)
    {
        var target = board ?? FocusedBoard;
        if (target is null) return;

        RenamingBoard = target;
        RenameText = target.Name;
    }

    public void CommitRename()
    {
        if (RenamingBoard is not { } board) return;

        var name = RenameText.Trim();
        if (name.Length > 0 && name != board.Name)
            Undo.Execute(EditBoardOp.Set(board, nameof(Board.Name), "Rename board",
                b => b.Name, (b, v) => b.Name = v, name));

        RenamingBoard = null;
    }

    [RelayCommand]
    private void CancelRename() => RenamingBoard = null;

    /// <summary>Recolour a whole lane. The parameter is packed by AccentArgsConverter,
    /// because a command needs both the board and the colour and XAML can only hand a
    /// single CommandParameter across.</summary>
    [RelayCommand]
    private void SetBoardAccent(object? arg)
    {
        if (arg is not AccentArgs { Board: { } board, Hex: { } hex }) return;
        if (board.Accent == hex) return;

        Undo.Execute(EditBoardOp.Set(board, nameof(Board.Accent), "Change board colour",
            b => b.Accent, (b, v) => b.Accent = v, hex));
    }

    // ------------------------------------------------------------ keyboard

    public void FocusBoardAt(int index)
    {
        var boards = VisibleBoards.ToList();
        if (index < 0 || index >= boards.Count) return;

        FocusedBoard = boards[index];
        SelectedCard = boards[index].Cards.FirstOrDefault();
    }

    /// <summary>Arrow-key selection. Left/Right change lane and hold the row where they
    /// can, because that's what the eye expects when moving sideways across a board.</summary>
    public void MoveSelection(MoveDirection direction)
    {
        var boards = VisibleBoards.ToList();
        if (boards.Count == 0) return;

        if (SelectedCard is null)
        {
            SelectedCard = (FocusedBoard ?? boards[0]).Cards.FirstOrDefault();
            return;
        }

        var board = Model.BoardsById[SelectedCard.BoardId];
        var laneIndex = boards.IndexOf(board);
        var cardIndex = board.Cards.IndexOf(SelectedCard);

        switch (direction)
        {
            case MoveDirection.Up:
                if (cardIndex > 0) SelectedCard = board.Cards[cardIndex - 1];
                break;

            case MoveDirection.Down:
                if (cardIndex < board.Cards.Count - 1) SelectedCard = board.Cards[cardIndex + 1];
                break;

            case MoveDirection.Left:
            case MoveDirection.Right:
            {
                var step = direction == MoveDirection.Left ? -1 : 1;

                // Skip empty lanes rather than parking selection in the void.
                for (var i = laneIndex + step; i >= 0 && i < boards.Count; i += step)
                {
                    if (boards[i].Cards.Count == 0) continue;
                    FocusedBoard = boards[i];
                    SelectedCard = boards[i].Cards[Math.Min(cardIndex, boards[i].Cards.Count - 1)];
                    return;
                }
                break;
            }
        }
    }

    [RelayCommand]
    private void SelectWorkspace(Workspace? ws)
    {
        if (ws is not null) ActiveWorkspace = ws;
    }

    [RelayCommand]
    private void AddWorkspace()
    {
        var ws = new Workspace { Name = "New workspace" };

        // A workspace with no boards is a dead end — there's nowhere to put a card and no
        // obvious next move. Seed it the way the app seeds itself on first run.
        var names = new[] { "Now", "Next", "Later" };
        for (var i = 0; i < names.Length; i++)
            ws.Boards.Add(new Board
            {
                WorkspaceId = ws.Id,
                Name = names[i],
                Accent = AccentPalette[i % AccentPalette.Length],
                Position = i
            });

        Undo.Barrier();
        Undo.Execute(new AddWorkspaceOp(ws));
        Undo.Barrier();

        ApplySort();
        ActiveWorkspace = ws;
        BeginRenameWorkspaceCommand.Execute(ws);   // it's called "New workspace"; name it now
    }

    [RelayCommand]
    private void BeginRenameWorkspace(Workspace? ws)
    {
        var target = ws ?? ActiveWorkspace;
        if (target is null) return;

        RenamingWorkspace = target;
        WorkspaceRenameText = target.Name;
    }

    public void CommitRenameWorkspace()
    {
        if (RenamingWorkspace is not { } ws) return;

        var name = WorkspaceRenameText.Trim();
        if (name.Length > 0 && name != ws.Name)
            Undo.Execute(EditWorkspaceOp.Set(ws, nameof(Workspace.Name), "Rename workspace",
                w => w.Name, (w, v) => w.Name = v, name));

        RenamingWorkspace = null;
    }

    [RelayCommand]
    private void CancelRenameWorkspace() => RenamingWorkspace = null;

    [RelayCommand]
    private void DeleteWorkspace(Workspace? ws)
    {
        var target = ws ?? ActiveWorkspace;
        if (target is null) return;

        // The last live workspace can't go: an empty sidebar has no way back to a board.
        // Counts the collection itself, which now holds only live workspaces — the flag and
        // the collection agreeing is exactly the thing that was broken before.
        if (Model.Workspaces.Count <= 1)
        {
            RequestNotify?.Invoke(
                "This is the only workspace, so it can't be deleted." + Environment.NewLine + Environment.NewLine
                + "Make another one first if you want to move on from this.");
            return;
        }

        var cards = target.Boards.Sum(b => b.Cards.Count);
        var detail = cards == 0
            ? $"Delete the workspace “{target.Name}”?"
            : $"Delete the workspace “{target.Name}”?" + Environment.NewLine + Environment.NewLine
              + $"It holds {target.Boards.Count} board{(target.Boards.Count == 1 ? "" : "s")} "
              + $"and {cards} card{(cards == 1 ? "" : "s")}. Ctrl+Z puts it all back.";

        if (RequestConfirm?.Invoke(detail) != true) return;

        Undo.Barrier();
        Undo.Execute(new ArchiveWorkspaceOp(target.Id, true));
        Undo.Barrier();

        if (ReferenceEquals(ActiveWorkspace, target))
            ActiveWorkspace = Model.Workspaces.FirstOrDefault(w => !w.Archived);
    }

    /// <summary>Archive a card by dropping it on the sidebar's Archive button.</summary>
    public void ArchiveCardById(string cardId)
    {
        if (!Model.CardsById.TryGetValue(cardId, out var card)) return;
        Undo.Execute(new ArchiveCardOp(card.Id, true));
        Undo.Barrier();
        if (ReferenceEquals(SelectedCard, card)) SelectedCard = null;
    }

    [RelayCommand] private void UndoLast() => Undo.Undo();
    [RelayCommand] private void RedoLast() => Undo.Redo();
    [RelayCommand] private void ShowShortcuts() => RequestShowShortcuts?.Invoke();
    [RelayCommand] private void ShowLabels() => RequestShowLabels?.Invoke();
    [RelayCommand] private void ShowArchive() => RequestShowArchive?.Invoke();
    [RelayCommand] private void FocusSearch() => RequestFocusSearch?.Invoke();

    [RelayCommand]
    private void ToggleSidebar() => Settings.SidebarCollapsed = !Settings.SidebarCollapsed;

    [RelayCommand]
    private void ToggleAging() => Settings.CardAgingEnabled = !Settings.CardAgingEnabled;

    [RelayCommand]
    private void SetTheme(ThemePreference preference)
    {
        Settings.Theme = preference;
        Settings.Save();
    }

    // ------------------------------------------------------------ file

    [RelayCommand]
    private void Export()
    {
        if (RequestFilePath?.Invoke(true) is not { } path) return;

        try
        {
            JsonSnapshot.Export(Model, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RequestNotify?.Invoke($"Couldn't write the export:{Environment.NewLine}{e.Message}");
        }
    }

    [RelayCommand]
    private void Import()
    {
        if (RequestFilePath?.Invoke(false) is not { } path) return;

        if (RequestConfirm?.Invoke(
                "Importing replaces everything currently in FlowBoard.\n\n" +
                "Export first if you want to keep it. Continue?") != true)
            return;

        try
        {
            Model = JsonSnapshot.Import(_store, path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            RequestNotify?.Invoke($"That file couldn't be imported:{Environment.NewLine}{e.Message}");
            return;
        }

        // Everything on the undo stack points at objects that no longer exist. Rebuild the
        // service rather than trying to rewrite history.
        Undo = new UndoRedoService(new OpContext(Model, _store));

        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Undo));

        SelectedCard = null;
        ActiveWorkspace = Model.Workspaces.FirstOrDefault(w => !w.Archived);
        ApplySort();
    }

    // ------------------------------------------------------------ archive

    public ObservableCollection<Card> ArchivedCards()
    {
        var cards = Model.CardsById.Values
            .Where(c => c.Archived)
            .OrderByDescending(c => c.ModifiedUtc);
        return new ObservableCollection<Card>(cards);
    }

    public void RestoreCard(Card card) => Undo.Execute(new ArchiveCardOp(card.Id, false));

    public void PurgeCard(Card card) => Undo.ExecuteIrreversible(new PurgeCardOp(card.Id));
}

/// <summary>A board plus a colour, so a single CommandParameter can carry both.</summary>
public sealed record AccentArgs(Board? Board, string? Hex);
