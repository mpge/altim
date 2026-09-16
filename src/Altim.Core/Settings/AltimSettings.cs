namespace Altim.Core.Settings;

/// <summary>
/// Everything the user can change, as one immutable record. Persisted key by key into the
/// <c>setting</c> table, so every member has a default that is correct for a fresh
/// install and a missing key falls back to it rather than to zero or false by accident.
/// </summary>
public sealed record AltimSettings
{
    /// <summary>The threshold Altim ships with for short windows.</summary>
    public const int DefaultSessionThresholdPercent = 80;

    /// <summary>The threshold Altim ships with for weekly and monthly windows.</summary>
    public const int DefaultWeeklyThresholdPercent = 90;

    /// <summary>The settings a fresh install starts from.</summary>
    public static AltimSettings Default { get; } = new();

    /// <summary>Which theme to render in. Defaults to following the system.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>
    /// Whether Altim registers itself to start with the user session. Defaults to false:
    /// nothing installs an autostart entry without being asked. The operating system can
    /// still veto a registration, so the effective state is read back from
    /// <c>IAutoStartService</c> rather than assumed from this flag.
    /// </summary>
    public bool LaunchAtLogin { get; init; }

    /// <summary>
    /// Whether launching Altim shows only the tray icon. Defaults to true, because the
    /// product is a background utility and the dashboard is opened on demand.
    /// </summary>
    public bool StartMinimised { get; init; } = true;

    /// <summary>
    /// Master switch for desktop notifications. When false no notification is produced at
    /// all, though threshold state is still tracked, so switching notifications back on
    /// does not replay everything that happened while they were off.
    /// </summary>
    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>Whether crossing a usage threshold raises a notification.</summary>
    public bool NotifyOnThreshold { get; init; } = true;

    /// <summary>Whether a window rolling over raises a single notification.</summary>
    public bool NotifyOnWindowReset { get; init; } = true;

    /// <summary>
    /// Percentage at which a short window (session, daily, or an unclassified one) raises
    /// a notification. Defaults to <see cref="DefaultSessionThresholdPercent"/>.
    /// </summary>
    public int SessionThresholdPercent { get; init; } = DefaultSessionThresholdPercent;

    /// <summary>
    /// Percentage at which a weekly or monthly window raises a notification. Defaults to
    /// <see cref="DefaultWeeklyThresholdPercent"/>, higher than the session threshold
    /// because a long window crosses it slowly and a premature warning is noise.
    /// </summary>
    public int WeeklyThresholdPercent { get; init; } = DefaultWeeklyThresholdPercent;

    /// <summary>
    /// Polling floor while no Altim window is open. Filesystem hints normally refresh
    /// sooner; this is the interval that catches whatever the watchers miss.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Polling interval while the popup or dashboard is open. Tighter, because someone is
    /// looking at the numbers.
    /// </summary>
    public TimeSpan ActiveRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether waking from sleep triggers one immediate refresh. Defaults to true, so the
    /// first thing seen after a lid opens is current rather than hours old.
    /// </summary>
    public bool RefreshOnResume { get; init; } = true;

    /// <summary>
    /// Whether providers may make calls that reach the network, such as the Codex quota
    /// read or the headless Claude usage summary. Defaults to true; turning it off is the
    /// strict local-only mode, in which those sources report as unavailable rather than
    /// being silently estimated. Network-touching calls are rate limited separately and
    /// never run more than once a minute even when this is true.
    /// </summary>
    public bool AllowNetworkCalls { get; init; } = true;
}
