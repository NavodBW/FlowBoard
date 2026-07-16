using System.Windows;
using System.Windows.Input;
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

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        // Re-render the preview only when it's actually shown. Markdown parsing on every
        // keystroke of a long description is wasted work nobody can see.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CardEditorViewModel.IsPreviewingDescription)
                              or nameof(CardEditorViewModel.Description)
                && vm.IsPreviewingDescription)
                DescriptionPreview.Document = MarkdownRenderer.Render(vm.Description);
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

    private void OnDiscard(object sender, RoutedEventArgs e) => Close();
}
