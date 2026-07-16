using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FlowBoard.ViewModels;
using FlowBoard.Views.Markdown;

// Deliberately NOT importing Wpf.Ui.Controls. That namespace re-declares names
// System.Windows already owns -- Card, MessageBox, MessageBoxButton, MessageBoxResult --
// so importing it to reach one type poisons every one of those names in the file.
// FluentWindow is the only thing needed from it, so it's qualified below.

namespace FlowBoard.Views;

public partial class CardDetailWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CardEditorViewModel _vm;

    public CardDetailWindow(CardEditorViewModel vm)
    {
        InitializeComponent();

        _vm = vm;
        DataContext = vm;
        vm.RequestClose = Close;

        vm.RequestBrowseFiles = () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Attach files",
                Multiselect = true,        // attaching one file at a time is a chore
                CheckFileExists = true,
                Filter = "All files (*.*)|*.*"
            };
            return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        };

        Loaded += (_, _) =>
        {
            // The description renders on open, so the first paint is the formatted view
            // rather than a blank panel waiting for a property to change.
            Render();

            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        // Re-render only when the formatted view is actually on screen. Parsing Markdown on
        // every keystroke of a long description is work nobody can see.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CardEditorViewModel.IsPreviewingDescription)
                              or nameof(CardEditorViewModel.Description))
                Render();
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.Enter)
        {
            _vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc discards. The draft was never applied, so there is nothing to roll back —
            // closing *is* the discard.
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && NewChecklistBox.IsKeyboardFocusWithin)
        {
            // Enter in the checklist box commits that item and keeps focus, for the same
            // reason quick-add does on the board: people add items in runs.
            _vm.AddChecklistItemCommand.Execute(null);
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void Render()
    {
        if (_vm.IsPreviewingDescription)
            DescriptionPreview.Document = MarkdownRenderer.Render(_vm.Description);
    }

    private void OnManageLabels(object sender, RoutedEventArgs e)
    {
        var shell = (Owner as MainWindow)?.DataContext as ViewModels.ShellViewModel;
        if (shell is null) return;

        new LabelsWindow(new ViewModels.LabelsViewModel(shell.Model, shell.Undo)) { Owner = this }
            .ShowDialog();

        // A label added or deleted while this editor is open has to show up in the
        // checkbox list, which was built from a snapshot when the editor opened.
        _vm.RefreshLabels();
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => Close();
}
