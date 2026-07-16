using System.Collections;
using FlowBoard.Domain;

namespace FlowBoard.Services;

public enum CardSort { Manual, Label, StartDate, EndDate, Priority, Title }

/// <summary>
/// Sorts a lane's cards for *display only*.
///
/// This is applied as ListCollectionView.CustomSort, which reorders what's drawn without
/// touching the underlying collection — so Card.Position, the database, and the undo stack
/// all stay exactly as the user arranged them. Switch back to Manual and the hand-made order
/// is still there, untouched.
///
/// Nulls sort last in every mode. A card with no due date is not "due first" — it's not in
/// the running at all, and floating it to the top would bury the cards that are.
/// </summary>
public sealed class CardComparer(CardSort mode, BoardModel model) : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is not Card a || y is not Card b) return 0;

        var result = mode switch
        {
            CardSort.Label => string.Compare(LabelName(a), LabelName(b), StringComparison.CurrentCultureIgnoreCase),
            CardSort.StartDate => Nullable(a.StartUtc, b.StartUtc),
            CardSort.EndDate => Nullable(a.DueUtc, b.DueUtc),

            // Critical first: descending is the only ordering anyone means by "sort by priority".
            CardSort.Priority => b.Priority.CompareTo(a.Priority),

            CardSort.Title => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase),
            _ => 0
        };

        // Manual position as the tiebreak, so equal keys keep the user's arrangement
        // instead of shuffling on every refresh.
        return result != 0 ? result : a.Position.CompareTo(b.Position);
    }

    private static int Nullable(DateTime? a, DateTime? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        var (x, y) => x!.Value.CompareTo(y!.Value)
    };

    /// <summary>A card's first label, by name. Unlabelled cards sort to the end.</summary>
    private string LabelName(Card card)
    {
        var id = card.LabelIds.FirstOrDefault();
        if (id is null) return "\uFFFF";

        return model.Labels.FirstOrDefault(l => l.Id == id)?.Name ?? "\uFFFF";
    }
}
