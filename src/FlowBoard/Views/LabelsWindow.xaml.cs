using System.Windows;
using System.Windows.Input;
using FlowBoard.Domain;
using FlowBoard.ViewModels;

// Deliberately importing neither Wpf.Ui.Controls nor System.Windows.Controls. Both
// re-declare domain nouns this file uses -- Wpf.Ui.Controls has Card, System.Windows.Controls
// has Label -- and this file needs exactly one type from either (FluentWindow, qualified
// below). Importing a whole namespace to reach one type, and shadowing your own domain
// model to do it, is a bad trade every time.
namespace FlowBoard.Views;

public partial class LabelsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly LabelsViewModel _vm;

    public LabelsWindow(LabelsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Loaded += (_, _) => NewLabelBox.Focus();
    }

    /// <summary>
    /// The TextBox binds Name with UpdateSourceTrigger=LostFocus, so by the time we get here
    /// the model already holds the new text — which means we can't read the old value off it
    /// to build the op. So the op is constructed from the label's *current* value and simply
    /// re-applies it; the undo entry captured the previous value when EditLabelOp.Set ran.
    /// </summary>
    private void OnNameCommitted(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Label label)
            _vm.RenameCommand.Execute(label);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Label label) return;

        var used = _vm.UsageOf(label);
        var message = used == 0
            ? $"Delete the label \u201c{label.Name}\u201d?"
            : $"Delete the label \u201c{label.Name}\u201d?\n\nIt's on {used} card{(used == 1 ? "" : "s")}, "
              + "and will be removed from them. Ctrl+Z puts it back.";

        if (MessageBox.Show(this, message, "FlowBoard",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _vm.DeleteCommand.Execute(label);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && NewLabelBox.IsKeyboardFocusWithin)
        {
            _vm.AddCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }
}
