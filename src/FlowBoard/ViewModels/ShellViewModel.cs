using System.Collections.ObjectModel;
using System.IO;
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
    private static readonly string[] AccentPalette =
        { "#4C8DFF", "#37C2A8", "#B586F0", "#F5A524", "#E5484D", "#8A94A6" };

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

    /// <summary>Set by the window; opens the modal editor. The VM shouldn't know what a
    /// Window is, so it asks.</summary>
    public Action<Card>? RequestOpenCard { get; set; }
    public Action? RequestShowShortcuts { get; set; }
    public Action? RequestShowArchive { get; set; }
    public Func<bool, string?>? RequestFilePath { get; set; }
    public Func<string, bool>? RequestConfirm { get; set; }
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
        FocusedBoard = board;
    }

    [RelayCommand]
    private void ArchiveBoard(Board? board)
    {
        var target = board ?? FocusedBoard;
        if (target is null) return;
        Undo.Execute(new ArchiveBoardOp(target.Id, true));
    }

    [RelayCommand]
    private void RenameBoard((Board Board, string Name) arg)
    {
        if (string.IsNullOrWhiteSpace(arg.Name) || arg.Name == arg.Board.Name) return;
        Undo.Execute(EditBoardOp.Set(arg.Board, nameof(Board.Name), "Rename board",
            b => b.Name, (b, v) => b.Name = v, arg.Name.Trim()));
    }

    [RelayCommand]
    private void SetBoardAccent((Board Board, string Accent) arg) =>
        Undo.Execute(EditBoardOp.Set(arg.Board, nameof(Board.Accent), "Change accent",
            b => b.Accent, (b, v) => b.Accent = v, arg.Accent));

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

    [RelayCommand] private void UndoLast() => Undo.Undo();
    [RelayCommand] private void RedoLast() => Undo.Redo();
    [RelayCommand] private void ShowShortcuts() => RequestShowShortcuts?.Invoke();
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
            RequestConfirm?.Invoke($"Couldn't write the export:\n{e.Message}");
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
            RequestConfirm?.Invoke($"That file couldn't be imported:\n{e.Message}");
            return;
        }

        // Everything on the undo stack points at objects that no longer exist. Rebuild the
        // service rather than trying to rewrite history.
        Undo = new UndoRedoService(new OpContext(Model, _store));

        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Undo));

        SelectedCard = null;
        ActiveWorkspace = Model.Workspaces.FirstOrDefault(w => !w.Archived);
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
