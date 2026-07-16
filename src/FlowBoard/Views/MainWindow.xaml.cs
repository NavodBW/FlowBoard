using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FlowBoard.Domain;
using FlowBoard.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

// Deliberately NOT importing Wpf.Ui.Controls. That namespace re-declares names
// System.Windows already owns -- Card, MessageBox, MessageBoxButton, MessageBoxResult --
// so importing it to reach one type poisons every one of those names in the file.
// FluentWindow is the only thing needed from it, so it's qualified below.

namespace FlowBoard.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // App applied the theme before this window existed. Watching keeps us in step if
        // Windows flips to dark at sunset mid-session.
        SystemThemeWatcher.Watch(this);

        WindowPlacement.Restore(this);
        Closing += (_, _) => WindowPlacement.Save(this);

        DataContextChanged += (_, _) => Wire();
        Wire();
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    /// <summary>
    /// The view model asks for windows and files; it doesn't know how to make them. Keeps
    /// dialogs out of the VM without inventing a service layer for four callbacks.
    /// </summary>
    private void Wire()
    {
        if (Vm is not { } vm) return;

        vm.RequestOpenCard = card =>
        {
            var editor = new CardDetailWindow(new CardEditorViewModel(card, vm.Model, vm.Undo)) { Owner = this };
            editor.ShowDialog();
        };

        vm.RequestShowShortcuts = () => new ShortcutsWindow { Owner = this }.ShowDialog();
        vm.RequestShowArchive = () => new ArchiveWindow(vm) { Owner = this }.ShowDialog();

        vm.RequestShowLabels = () =>
            new LabelsWindow(new LabelsViewModel(vm.Model, vm.Undo)) { Owner = this }.ShowDialog();
        vm.RequestFocusSearch = () => { SearchBox.Focus(); SearchBox.SelectAll(); };

        vm.RequestConfirm = message => MessageBox.Show(this, message, "FlowBoard",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        vm.RequestNotify = message => MessageBox.Show(this, message, "FlowBoard",
            MessageBoxButton.OK, MessageBoxImage.Information);

        vm.RequestFilePath = saving =>
        {
            if (saving)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "FlowBoard export (*.json)|*.json",
                    FileName = $"flowboard-{DateTime.Now:yyyy-MM-dd}.json",
                    AddExtension = true
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            }

            var open = new OpenFileDialog { Filter = "FlowBoard export (*.json)|*.json", CheckFileExists = true };
            return open.ShowDialog(this) == true ? open.FileName : null;
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) { base.OnPreviewKeyDown(e); return; }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Anything typed into a text box belongs to that text box. Without this guard,
        // typing "n" into the search box would spawn a card — the single most annoying bug
        // a keyboard-driven app can have.
        if (IsTextInputFocused() && !ctrl)
        {
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); Focus(); e.Handled = true; }
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Z when ctrl && !shift: vm.UndoLastCommand.Execute(null); e.Handled = true; break;
            case Key.Y when ctrl: vm.RedoLastCommand.Execute(null); e.Handled = true; break;
            case Key.Z when ctrl && shift: vm.RedoLastCommand.Execute(null); e.Handled = true; break;

            case Key.E when ctrl: vm.ExportCommand.Execute(null); e.Handled = true; break;
            case Key.B when ctrl: vm.ToggleSidebarCommand.Execute(null); e.Handled = true; break;
            case Key.A when ctrl && shift: vm.ShowArchiveCommand.Execute(null); e.Handled = true; break;

            case Key.F when !ctrl: vm.FocusSearchCommand.Execute(null); e.Handled = true; break;
            case Key.N when !ctrl: vm.BeginQuickAddCommand.Execute(vm.FocusedBoard); e.Handled = true; break;
            case Key.Enter when vm.SelectedCard is not null: vm.OpenCardCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: vm.ArchiveCardCommand.Execute(null); e.Handled = true; break;

            case Key.Escape:
                if (vm.Filter.IsActive) { vm.Filter.ClearCommand.Execute(null); e.Handled = true; }
                break;

            case Key.Left: vm.MoveSelection(MoveDirection.Left); e.Handled = true; break;
            case Key.Right: vm.MoveSelection(MoveDirection.Right); e.Handled = true; break;
            case Key.Up: vm.MoveSelection(MoveDirection.Up); e.Handled = true; break;
            case Key.Down: vm.MoveSelection(MoveDirection.Down); e.Handled = true; break;

            // "?" is Shift+/ on most layouts, but not all — accept the OEM key too.
            case Key.OemQuestion when shift:
            case Key.OemQuestion:
                vm.ShowShortcutsCommand.Execute(null);
                e.Handled = true;
                break;

            default:
                if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
                {
                    vm.FocusBoardAt(e.Key - Key.D1);
                    e.Handled = true;
                }
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Click selects, double-click opens. Handled here rather than per-card so
    /// the card face template stays free of code-behind hooks.</summary>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Vm is { } vm && FindCard(e.OriginalSource as DependencyObject) is { } card)
        {
            vm.SelectedCard = card;
            if (e.ClickCount == 2) { vm.OpenCardCommand.Execute(card); e.Handled = true; }
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <summary>
    /// Which card was clicked, if any.
    ///
    /// Goes through TreeWalk rather than VisualTreeHelper directly. e.OriginalSource is a
    /// Run whenever the click lands on text — including every menu item label — and
    /// VisualTreeHelper.GetParent throws on a Run instead of returning null. This method
    /// runs on every left-click anywhere in the window, so a naive walk crashes the app the
    /// first time someone clicks a word.
    /// </summary>
    private static Card? FindCard(DependencyObject? source) => TreeWalk.DataContextOf<Card>(source);

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is TextBoxBase
        || (Keyboard.FocusedElement as DependencyObject)?.GetType().Name.Contains("TextBox") == true;
}
