using System.Windows.Input;

// Deliberately NOT importing Wpf.Ui.Controls. That namespace re-declares names
// System.Windows already owns -- Card, MessageBox, MessageBoxButton, MessageBoxResult --
// so importing it to reach one type poisons every one of those names in the file.
// FluentWindow is the only thing needed from it, so it's qualified below.

namespace FlowBoard.Views;

public partial class ShortcutsWindow : Wpf.Ui.Controls.FluentWindow
{
    public sealed record Shortcut(string Keys, string Action);

    /// <summary>
    /// The single source of truth for the cheat sheet.
    ///
    /// It sits next to the handler in MainWindow deliberately: a cheat sheet maintained in
    /// a separate file drifts from the code within two releases and then actively lies to
    /// people, which is worse than not having one.
    /// </summary>
    public static readonly Shortcut[] All =
    {
        new("N", "New card in the focused board"),
        new("Enter", "Open the selected card"),
        new("F", "Focus search"),
        new("Esc", "Clear search · cancel a drag · close a dialog"),
        new("← → ↑ ↓", "Move selection between cards and boards"),
        new("Ctrl + 1…9", "Focus the Nth board"),
        new("Del", "Archive the selected card"),
        new("Ctrl + Z", "Undo"),
        new("Ctrl + Y", "Redo"),
        new("Ctrl + Shift + Z", "Redo (alternative)"),
        new("Ctrl + E", "Export workspace to JSON"),
        new("Ctrl + B", "Collapse or show the sidebar"),
        new("Ctrl + Shift + A", "Open the archive"),
        new("?", "Show this list"),
        new("Ctrl + Enter", "Save and close the card editor"),
        new("Drag a card", "Reorder, or move to another board"),
        new("Drag onto a workspace", "Move the card to that workspace"),
        new("Drag onto Archive", "Archive the card"),
        new("Drag a board header", "Reorder boards"),
        new("Double-click header", "Rename a board"),
        new("Right-click header", "Board colour, rename, archive"),
        new("Sort", "Display only — your manual order is kept, and returns"),
        new("Double-click workspace", "Rename it"),
        new("Right-click workspace", "Rename or delete"),
    };

    public ShortcutsWindow()
    {
        InitializeComponent();
        Shortcuts.ItemsSource = All;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
