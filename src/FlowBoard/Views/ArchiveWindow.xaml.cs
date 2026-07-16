using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FlowBoard.Domain;
using FlowBoard.ViewModels;

// Deliberately NOT importing Wpf.Ui.Controls. That namespace re-declares names
// System.Windows already owns -- Card, MessageBox, MessageBoxButton, MessageBoxResult --
// so importing it to reach one type poisons every one of those names in the file.
// FluentWindow is the only thing needed from it, so it's qualified below.

namespace FlowBoard.Views;

/// <summary>
/// Restore or permanently delete archived cards.
///
/// Archive is a soft delete everywhere else in the app precisely so that this is the only
/// window where data can actually be destroyed — one screen to be careful on, rather than
/// a Delete key that means different things in different places.
/// </summary>
public partial class ArchiveWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ShellViewModel _shell;
    private readonly ObservableCollection<Card> _cards;

    public ArchiveWindow(ShellViewModel shell)
    {
        InitializeComponent();

        _shell = shell;
        _cards = shell.ArchivedCards();
        Items.ItemsSource = _cards;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Items.Visibility = _cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Card card) return;

        _shell.RestoreCard(card);
        _cards.Remove(card);
        UpdateEmptyState();
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Card card) return;

        // Name the card in the prompt. "Are you sure?" is not a question anyone can answer
        // — "Delete 'Ship v2' forever?" is.
        var confirmed = MessageBox.Show(
            $"Delete \u201c{card.Title}\u201d permanently?\n\nThis can't be undone.",
            "FlowBoard", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        if (!confirmed) return;

        _shell.PurgeCard(card);
        _cards.Remove(card);
        UpdateEmptyState();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
