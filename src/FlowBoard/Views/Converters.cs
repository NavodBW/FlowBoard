using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FlowBoard.Domain;
using FlowBoard.ViewModels;

namespace FlowBoard.Views;

/// <summary>
/// Converters here return *values and states*, never brushes.
///
/// A converter that resolved a brush would capture whichever theme dictionary was loaded
/// at the moment it ran, and go stale the instant the user (or the sun) flipped the
/// system theme. So state comes out here, and XAML DataTriggers map state to a
/// DynamicResource brush — which re-resolves on swap for free.
/// </summary>
public enum DueState { None, Upcoming, Soon, Overdue }

/// <summary>Priority -> fraction of the card edge the strip fills. This is the signature:
/// level is readable by strip *length* alone, so it survives colour-vision deficiency and
/// peripheral vision when scanning a full lane.</summary>
public sealed class PriorityStripHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double host || values[1] is not Priority p)
            return 0d;
        if (double.IsNaN(host) || host <= 0) return 0d;

        var fraction = p switch
        {
            Priority.Low => 0.25,
            Priority.Medium => 0.50,
            Priority.High => 0.75,
            Priority.Critical => 1.0,
            _ => 0.0
        };
        return Math.Round(host * fraction);
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Due date + now -> state. Amber inside 48h, red once past.</summary>
public sealed class DueStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not DateTime due || values[1] is not DateTime now)
            return DueState.None;

        var delta = due - now;
        if (delta < TimeSpan.Zero) return DueState.Overdue;
        if (delta < TimeSpan.FromHours(48)) return DueState.Soon;
        return DueState.Upcoming;
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Human due text. Relative near the boundary ("in 5h", "3d late") because that's
/// the question you're actually asking; absolute further out, where a date is more use.</summary>
public sealed class DueTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not DateTime due || values[1] is not DateTime now)
            return string.Empty;

        var local = due.ToLocalTime();
        var delta = due - now;

        if (delta < TimeSpan.Zero)
        {
            var late = now - due;
            if (late.TotalHours < 1) return "just now";
            if (late.TotalHours < 24) return $"{(int)late.TotalHours}h late";
            return $"{(int)late.TotalDays}d late";
        }

        if (delta.TotalHours < 24) return $"in {Math.Max(1, (int)delta.TotalHours)}h";
        if (delta.TotalDays < 7) return local.ToString("ddd HH:mm", culture);
        return local.ToString("d MMM", culture);
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>"3/7".</summary>
public sealed class ChecklistTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length >= 2 && values[0] is int done && values[1] is int total
            ? $"{done}/{total}"
            : string.Empty;

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Card is stale: untouched past the threshold, and the user wants to see it.</summary>
public sealed class AgingStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not DateTime touched
            || values[1] is not DateTime now || values[2] is not bool enabled)
            return false;

        if (!enabled) return false;

        var days = parameter is string s && double.TryParse(s, NumberStyles.Any,
            CultureInfo.InvariantCulture, out var d) ? d : 14d;

        return (now - touched).TotalDays >= days;
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Card label ids + the app-wide label set -> the label objects to draw.
/// Labels are shared across boards, so the card only ever stores ids.</summary>
public sealed class LabelLookupConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not IEnumerable ids || values[1] is not IEnumerable all)
            return Array.Empty<Label>();

        var byId = all.OfType<Label>().ToDictionary(l => l.Id);
        return ids.OfType<string>()
                  .Where(byId.ContainsKey)
                  .Select(id => byId[id])
                  .OrderBy(l => l.Position)
                  .ToList();
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Hex string -> brush, for user-chosen colours (board accents, label chips).
/// These are data, not theme tokens, so resolving them here is correct — they don't
/// change when the theme does.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;

        if (Cache.TryGetValue(hex, out var cached)) return cached;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();   // frozen brushes are cheap to share across every card
            Cache[hex] = brush;
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Zero-count things don't draw. Keeps the card face quiet when there's nothing
/// to say, which is what makes the loud cards readable.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Does this card survive the current filter?
///
/// Bound with every input the answer depends on — the card's own fields, each filter
/// field, and the clock (a "due soon" filter changes answer with time alone). Miss one and
/// cards quietly stop re-evaluating: the classic symptom is typing in the search box and
/// watching half the board fail to respond.
/// </summary>
public sealed class CardMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return true;
        if (values[0] is not Card card) return true;
        if (values[1] is not FilterViewModel filter) return true;
        var now = values[2] is DateTime t ? t : DateTime.UtcNow;

        return filter.Matches(card, now);
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Activity log line. The enum carries the verb; the detail carries the nouns.</summary>
public sealed class ActivityTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not ActivityKind kind) return string.Empty;
        var detail = values[1] as string ?? "";

        var verb = kind switch
        {
            ActivityKind.Created => "Created",
            ActivityKind.TitleChanged => "Renamed to",
            ActivityKind.DescriptionChanged => "Edited description",
            ActivityKind.Moved => "Moved",
            ActivityKind.MovedWorkspace => "Moved workspace",
            ActivityKind.PriorityChanged => "Priority",
            ActivityKind.DueChanged => "Due",
            ActivityKind.LabelsChanged => "Labels changed",
            ActivityKind.ChecklistChanged => "Checklist",
            ActivityKind.LinkChanged => "Links",
            ActivityKind.Archived => "Archived from",
            ActivityKind.Restored => "Restored to",
            _ => "Changed"
        };

        return string.IsNullOrWhiteSpace(detail) ? verb : $"{verb} {detail}";
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class BoardIsSelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length >= 2 && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Reference equality -> Visibility. "invert" flips it. Used to swap a lane's
/// "+ Add card" button for its quick-add box.</summary>
public sealed class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var same = values.Length >= 2 && ReferenceEquals(values[0], values[1]);
        if (parameter as string == "invert") same = !same;
        return same ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
