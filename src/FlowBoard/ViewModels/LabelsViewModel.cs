using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowBoard.Domain;
using FlowBoard.Services;

namespace FlowBoard.ViewModels;

/// <summary>
/// Label management: add, rename, recolour, delete.
///
/// Unlike the card editor this edits **live** rather than through a draft, because there's
/// no natural "commit" gesture for a list of labels — you add one, tweak a colour, close.
/// Every change is its own op, so undo still works; the merge in EditLabelOp keeps typing a
/// name from becoming thirty undo steps.
/// </summary>
public sealed partial class LabelsViewModel : ObservableObject
{
    private readonly UndoRedoService _undo;

    public BoardModel Model { get; }
    public IReadOnlyList<string> Palette => ShellViewModel.AccentPalette;

    [ObservableProperty] private string _newLabelName = "";
    [ObservableProperty] private Label? _selected;

    public LabelsViewModel(BoardModel model, UndoRedoService undo)
    {
        Model = model;
        _undo = undo;
    }

    [RelayCommand]
    private void Add()
    {
        var name = NewLabelName.Trim();
        if (name.Length == 0) return;

        // Cycle the palette so a run of new labels doesn't come out all the same colour.
        var color = ShellViewModel.AccentPalette[Model.Labels.Count % ShellViewModel.AccentPalette.Length];

        var label = new Label { Name = name, Color = color };
        _undo.Barrier();
        _undo.Execute(new AddLabelOp(label));
        _undo.Barrier();

        NewLabelName = "";
        Selected = label;
    }

    [RelayCommand]
    private void Rename(Label? label)
    {
        if (label is null) return;
        _undo.Execute(EditLabelOp.Set(label, nameof(Label.Name), "Rename label",
            l => l.Name, (l, v) => l.Name = v, label.Name));
    }

    [RelayCommand]
    private void SetColor(object? arg)
    {
        if (arg is not AccentArgs { Hex: { } hex } || Selected is not { } label) return;
        if (label.Color == hex) return;

        _undo.Execute(EditLabelOp.Set(label, nameof(Label.Color), "Recolour label",
            l => l.Color, (l, v) => l.Color = v, hex));
    }

    [RelayCommand]
    private void Delete(Label? label)
    {
        if (label is null) return;

        _undo.Barrier();
        _undo.Execute(new DeleteLabelOp(label.Id));
        _undo.Barrier();

        if (ReferenceEquals(Selected, label)) Selected = null;
    }

    /// <summary>How many cards wear this label — shown next to Delete, because deleting a
    /// label that's on 40 cards should not feel like deleting one that's on none.</summary>
    public int UsageOf(Label label) =>
        Model.CardsById.Values.Count(c => !c.Archived && c.LabelIds.Contains(label.Id));
}
