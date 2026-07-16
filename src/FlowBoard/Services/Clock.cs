using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowBoard.Services;

/// <summary>
/// One app-wide clock that ticks every 30s.
///
/// Due-date colours and card ageing depend on the wall clock, not on any card property —
/// a card sitting untouched goes amber, then red, with nothing raising PropertyChanged.
/// Bindings multi-bind against <see cref="Now"/> so a single tick re-evaluates every
/// visible card. The alternative (a DispatcherTimer per card) is hundreds of timers to
/// answer one question.
/// </summary>
public sealed partial class Clock : ObservableObject
{
    public static Clock Instance { get; } = new();

    [ObservableProperty] private DateTime _now = DateTime.UtcNow;

    private readonly DispatcherTimer _timer;

    private Clock()
    {
        // 30s: fine enough that "in 48 hours" flips to amber promptly, coarse enough to
        // stay off the CPU. Nothing here is second-accurate by design.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += (_, _) => Now = DateTime.UtcNow;
        _timer.Start();
    }
}
