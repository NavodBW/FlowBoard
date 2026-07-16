using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlowBoard.ViewModels;

namespace FlowBoard.Views;

/// <summary>
/// Quick-add box behaviour: take focus the moment it appears, Enter commits and stays,
/// Esc closes.
///
/// The "stays" is the whole point. Adding cards is a burst activity — you sit down with a
/// list in your head and type six of them. A box that closes after each one turns that
/// into six round trips to the mouse.
/// </summary>
public static class QuickAdd
{
    public static readonly DependencyProperty FocusWhenVisibleProperty =
        DependencyProperty.RegisterAttached("FocusWhenVisible", typeof(bool), typeof(QuickAdd),
            new PropertyMetadata(false, OnChanged));

    public static bool GetFocusWhenVisible(DependencyObject d) => (bool)d.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(DependencyObject d, bool v) => d.SetValue(FocusWhenVisibleProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || !(bool)e.NewValue) return;

        box.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
                // Focus can't be taken during the layout pass that made it visible.
                box.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }),
                    System.Windows.Threading.DispatcherPriority.Input);
        };

        box.PreviewKeyDown += (_, args) =>
        {
            if (Window.GetWindow(box)?.DataContext is not ShellViewModel vm) return;

            switch (args.Key)
            {
                case Key.Enter:
                    vm.CommitQuickAdd();
                    args.Handled = true;
                    break;

                case Key.Escape:
                    vm.CancelQuickAddCommand.Execute(null);
                    args.Handled = true;
                    break;
            }
        };

        // Clicking away is the other way people mean "I'm done".
        box.LostKeyboardFocus += (_, _) =>
        {
            if (Window.GetWindow(box)?.DataContext is ShellViewModel vm && vm.QuickAddBoard is not null)
                vm.CancelQuickAddCommand.Execute(null);
        };
    }
}
