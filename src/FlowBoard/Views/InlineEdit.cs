using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlowBoard.ViewModels;

namespace FlowBoard.Views;

/// <summary>
/// An inline rename box: takes focus when it appears, Enter commits, Esc reverts, clicking
/// away commits.
///
/// Committing on lost focus rather than reverting is deliberate. Someone who typed a new
/// name and clicked back onto the board meant the rename; throwing it away because they
/// didn't press Enter would be the app being pedantic about a keystroke it doesn't need.
/// Esc is the explicit "no", and that's the one that reverts.
///
/// The Target tells it *what* is being renamed. Boards and workspaces are renamed the same
/// way but hold separate draft state, so one shared "is renaming" flag would let a
/// half-typed board name leak into a workspace row.
/// </summary>
public static class InlineEdit
{
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(string), typeof(InlineEdit),
            new PropertyMetadata(null, OnChanged));

    public static string? GetTarget(DependencyObject d) => (string?)d.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject d, string? v) => d.SetValue(TargetProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not string target) return;

        var isWorkspace = target.Equals("Workspace", StringComparison.OrdinalIgnoreCase);

        box.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true) return;

            // Focus can't be taken during the layout pass that revealed the box.
            box.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        box.PreviewKeyDown += (_, args) =>
        {
            if (Vm(box) is not { } vm) return;

            switch (args.Key)
            {
                case Key.Enter:
                    if (isWorkspace) vm.CommitRenameWorkspace(); else vm.CommitRename();
                    args.Handled = true;
                    break;

                case Key.Escape:
                    (isWorkspace ? vm.CancelRenameWorkspaceCommand : vm.CancelRenameCommand).Execute(null);
                    args.Handled = true;
                    break;
            }
        };

        box.LostKeyboardFocus += (_, _) =>
        {
            if (Vm(box) is not { } vm) return;

            if (isWorkspace)
            {
                if (vm.RenamingWorkspace is not null) vm.CommitRenameWorkspace();
            }
            else if (vm.RenamingBoard is not null)
            {
                vm.CommitRename();
            }
        };
    }

    private static ShellViewModel? Vm(DependencyObject d) => Window.GetWindow(d)?.DataContext as ShellViewModel;
}
