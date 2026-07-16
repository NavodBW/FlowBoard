using System.Windows;
using System.Windows.Threading;
using FlowBoard.Data;
using FlowBoard.Domain;
using FlowBoard.Services;
using FlowBoard.Theme;
using FlowBoard.ViewModels;
using FlowBoard.Views;
using Wpf.Ui.Appearance;

namespace FlowBoard;

public partial class App : Application
{
    private FlowBoardStore? _store;
    private AppSettings? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();

        // Theme before any window exists, so nothing renders in the wrong palette and
        // then flips.
        ApplyTheme(_settings.Theme);
        ThemeTokens.Initialize();

        // A manual override has to *stop* following Windows, not fight it — the watcher
        // would otherwise reassert the system theme on the next system change.
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme)) ApplyTheme(_settings.Theme);
        };

        // Missing DB => created here; missing content => seeded here. No install step.
        _store = new FlowBoardStore();
        BoardModel model = Seed.EnsureSeeded(_store, _store.Load());

        var undo = new UndoRedoService(new OpContext(model, _store));
        var vm = new ShellViewModel(model, undo, _settings, _store);

        new MainWindow { DataContext = vm }.Show();
    }

    private static void ApplyTheme(ThemePreference preference)
    {
        switch (preference)
        {
            case ThemePreference.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case ThemePreference.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Save();

        // Checkpoints the WAL. Even if we're killed before this runs, WAL replay on next
        // open recovers the last committed transaction — no data loss, no corruption.
        _store?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"FlowBoard hit an unexpected error:\n\n{e.Exception.Message}\n\n" +
            "Your board is saved after every change, so nothing should be lost.",
            "FlowBoard", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
