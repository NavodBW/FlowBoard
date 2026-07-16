using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FlowBoard.Views;

/// <summary>
/// Walking up from a hit-test result is not as simple as VisualTreeHelper.GetParent.
///
/// WPF's tree is not one tree. Text lives in ContentElements — Run, Span, Hyperlink — which
/// are *not* Visuals and have no place in the visual tree. Hand one to
/// VisualTreeHelper.GetParent and it doesn't return null, it throws:
///
///     'System.Windows.Documents.Run' is not a Visual or Visual3D
///
/// Since e.OriginalSource on any click that lands on text is exactly such an element, any
/// naive upward walk is a crash waiting for the user to click on a word. This hops from
/// the content tree back onto the visual tree at the first host that has both.
/// </summary>
public static class TreeWalk
{
    public static DependencyObject? ParentOf(DependencyObject? node)
    {
        switch (node)
        {
            case null:
                return null;

            // Run, Span, Paragraph… — climb the content tree until it lands on a Visual.
            case ContentElement content:
                return ContentOperations.GetParent(content)
                       ?? (content as FrameworkContentElement)?.Parent;

            case Visual or Visual3D:
                return VisualTreeHelper.GetParent(node);

            default:
                return LogicalTreeHelper.GetParent(node);
        }
    }

    /// <summary>First ancestor (or self) whose DataContext is a T. Safe from any source.</summary>
    public static T? DataContextOf<T>(DependencyObject? source) where T : class
    {
        for (var node = source; node is not null; node = ParentOf(node))
            if (node is FrameworkElement { DataContext: T match }) return match;
        return null;
    }

    /// <summary>First ancestor (or self) of type T. Safe from any source.</summary>
    public static T? AncestorOf<T>(DependencyObject? source) where T : class
    {
        for (var node = source; node is not null; node = ParentOf(node))
            if (node is T match) return match;
        return null;
    }
}
