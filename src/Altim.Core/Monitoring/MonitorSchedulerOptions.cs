using Altim.Core.Settings;

namespace Altim.Core.Monitoring;

/// <summary>
/// The cadences <see cref="MonitorScheduler"/> runs at. Every value is a
/// <see cref="TimeSpan"/> so tests can shrink them, and every default matches the
/// monitoring section of the architecture.
/// </summary>
public sealed record MonitorSchedulerOptions
{
    /// <summary>The options the app runs with unless settings override them.</summary>
    public static MonitorSchedulerOptions Default { get; } = new();

    /// <summary>
    /// Polling floor while no window is open. Missed ticks coalesce, so a machine waking
    /// from sleep produces one refresh rather than a backlog.
    /// </summary>
    public TimeSpan RelaxedInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Polling interval while the popup or dashboard is open.</summary>
    public TimeSpan TightenedInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long filesystem hints are collected for before one refresh runs. The first hint
    /// opens the window; every hint arriving inside it is absorbed into the same refresh.
    /// </summary>
    public TimeSpan HintDebounce { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// The shortest gap between two network-touching refreshes of the same provider. This
    /// is a hard floor: nothing, including a manual refresh or a resume from sleep, makes
    /// a network-touching provider run more often than this.
    /// </summary>
    public TimeSpan NetworkMinimumInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long one provider gets to answer a refresh before the scheduler stops waiting
    /// and reports an error reading instead. A provider that reaches a CLI or the network
    /// can hang for as long as that process does, and a refresh nobody is bounding is a
    /// refresh that holds a gate for the life of the process.
    /// </summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="MonitorScheduler.StopAsync"/> waits for work to unwind before
    /// giving up on it. Measured against the wall clock rather than the injected
    /// <see cref="TimeProvider"/>, because shutting down must not depend on somebody
    /// advancing a simulated one.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether <see cref="MonitorScheduler.Resume"/> triggers one refresh.</summary>
    public bool RefreshOnResume { get; init; } = true;

    /// <summary>
    /// Builds options from user settings.
    /// </summary>
    /// <param name="settings">The current settings. Never <see langword="null"/>.</param>
    /// <returns>
    /// Options carrying the two intervals and the resume behaviour from settings. The hint
    /// debounce and the network floor are not user configurable, because they protect the
    /// machine and the provider rather than expressing a preference.
    /// </returns>
    public static MonitorSchedulerOptions FromSettings(AltimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Default with
        {
            RelaxedInterval = settings.RefreshInterval,
            TightenedInterval = settings.ActiveRefreshInterval,
            RefreshOnResume = settings.RefreshOnResume,
        };
    }
}
