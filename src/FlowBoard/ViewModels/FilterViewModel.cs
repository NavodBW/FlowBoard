using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowBoard.Domain;

namespace FlowBoard.ViewModels;

public enum DueFilter { Any, Overdue, Soon, HasDue, NoDue }

/// <summary>
/// Search and filter state.
///
/// Non-matching cards **dim rather than disappear**, per the brief, and that's not a
/// cosmetic choice — it's why this is a filter object the card face binds against rather
/// than an ICollectionView over the lanes. Filtering a CollectionView would physically
/// remove containers, which destroys the board's shape (a lane of 30 showing 2 looks like
/// a lane of 2), breaks the drag slot cache mid-drag, and makes "where does this card
/// live?" unanswerable — the exact question a search is usually asked to answer.
/// </summary>
public sealed partial class FilterViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _labelId;
    [ObservableProperty] private Priority? _priority;
    [ObservableProperty] private DueFilter _due = DueFilter.Any;

    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Text) || LabelId is not null || Priority is not null || Due != DueFilter.Any;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsActive));
    partial void OnLabelIdChanged(string? value) => OnPropertyChanged(nameof(IsActive));
    partial void OnPriorityChanged(Priority? value) => OnPropertyChanged(nameof(IsActive));
    partial void OnDueChanged(DueFilter value) => OnPropertyChanged(nameof(IsActive));

    [RelayCommand]
    private void Clear()
    {
        Text = "";
        LabelId = null;
        Priority = null;
        Due = DueFilter.Any;
    }

    /// <summary>Does this card survive the filter? Called from a converter on every card,
    /// so it stays allocation-free and does the cheap tests first.</summary>
    public bool Matches(Card card, DateTime nowUtc)
    {
        if (!IsActive) return true;

        if (Priority is { } p && card.Priority != p) return false;
        if (LabelId is { } id && !card.LabelIds.Contains(id)) return false;

        switch (Due)
        {
            case DueFilter.NoDue when card.DueUtc is not null: return false;
            case DueFilter.HasDue when card.DueUtc is null: return false;
            case DueFilter.Overdue when card.DueUtc is not { } d1 || d1 >= nowUtc: return false;
            case DueFilter.Soon when card.DueUtc is not { } d2
                                     || d2 < nowUtc || d2 - nowUtc >= TimeSpan.FromHours(48): return false;
        }

        if (string.IsNullOrWhiteSpace(Text)) return true;

        var needle = Text.Trim();
        return card.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || card.Checklist.Any(i => i.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
