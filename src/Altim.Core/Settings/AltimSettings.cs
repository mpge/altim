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

    /// <summary>
    /// The lowest threshold that means anything. Zero would fire the moment a window
    /// opened, and a negative one is not a percentage at all.
    /// </summary>
    public const int MinimumThresholdPercent = 1;

    /// <summary>
    /// The highest threshold that can ever be reached, since readings are clamped to a
    /// full window. Anything above it would never fire.
    /// </summary>
    public const int MaximumThresholdPercent = 100;

    private readonly int _sessionThresholdPercent = DefaultSessionThresholdPercent;
    private readonly int _weeklyThresholdPercent = DefaultWeeklyThresholdPercent;

    /// <summary>The settings a fresh install starts from.</summary>
    public static AltimSettings Default { get; } = new();

    /// <summary>
    /// What to run with when the stored settings cannot be read at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not <see cref="Default"/>, and the difference is the whole point.</b> An
    /// unreadable database used to fall back to the defaults, which say yes to both of the
    /// switches that decide what leaves the machine. So a user who had deliberately turned
    /// <see cref="AllowNetworkCalls"/> off got it back on, silently, the first time their
    /// database would not open — the one setting whose accidental inversion the user would
    /// never see and could not consent to.
    /// </para>
    /// <para>
    /// Everything else stays at its default, because a theme or a threshold that reverts is
    /// a visible annoyance rather than a decision made on somebody's behalf. Only the two
    /// permissions fail closed: the wrong answer costs a figure that reads as unavailable
    /// and a check that does not run, both of which are states Altim already renders and
    /// explains.
    /// </para>
    /// </remarks>
    public static AltimSettings FailClosed { get; } = new()
    {
        AllowNetworkCalls = false,
        AutomaticUpdateChecks = false,
    };

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
    /// Percentage at which a short window (session, daily, or an unclassified one shorter
    /// than a day) raises a notification. Defaults to
    /// <see cref="DefaultSessionThresholdPercent"/>, and is clamped to
    /// <see cref="MinimumThresholdPercent"/>..<see cref="MaximumThresholdPercent"/>, so a
    /// hand-edited settings row cannot switch notifications off by accident or make them
    /// unreachable.
    /// </summary>
    /// <remarks>
    /// Lowering this below a reading that is already above it fires once, immediately: the
    /// user has just asked to be warned at a level they are already past, and a warning
    /// they asked for that never arrives reads as a broken feature. It then behaves like
    /// any other fired threshold and stays quiet until the window rolls over. Raising it
    /// again fires nothing, because the entry for the higher threshold is still on record.
    /// </remarks>
    public int SessionThresholdPercent
    {
        get => _sessionThresholdPercent;
        init => _sessionThresholdPercent = Clamp(value);
    }

    /// <summary>
    /// Percentage at which a weekly or monthly window, or any unclassified window longer
    /// than a day, raises a notification. Defaults to
    /// <see cref="DefaultWeeklyThresholdPercent"/>, higher than the session threshold
    /// because a long window crosses it slowly and a premature warning is noise. Clamped
    /// the same way as <see cref="SessionThresholdPercent"/>, and lowering it behaves the
    /// same way.
    /// </summary>
    public int WeeklyThresholdPercent
    {
        get => _weeklyThresholdPercent;
        init => _weeklyThresholdPercent = Clamp(value);
    }

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
    /// <remarks>
    /// <para>
    /// Neither call is Altim reaching a vendor itself. Each runs the provider's <em>own</em>
    /// command line, which contacts the vendor with the credentials the user already gave
    /// it; Altim never opens a credential file and never calls a vendor endpoint. The
    /// settings page says exactly that rather than "allow network calls", because the
    /// shorter wording leaves a privacy-minded reader guessing who is being contacted.
    /// </para>
    /// <para>
    /// Changing this takes effect on the next refresh, not on the next restart. Providers
    /// are constructed once and outlive every settings change, so the permission reaches
    /// them through <see cref="Core.Abstractions.INetworkPolicy"/> and is read at the moment
    /// of the call. Switching it off also discards what those calls had already produced —
    /// a figure the user has just forbidden Altim to refresh is not a current reading.
    /// </para>
    /// </remarks>
    public bool AllowNetworkCalls { get; init; } = true;

    /// <summary>
    /// Whether Altim's status-line command is registered in Claude Code's settings.
    /// Defaults to false, and stays false until the user asks for it on the settings page:
    /// this is the one switch that writes to a configuration file Altim does not own, so it
    /// is never turned on by an upgrade, by a first run, or by anything other than somebody
    /// deciding to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status line is the only documented local source of reset instants, the spend
    /// limit and the Documented-grade window percentages. Leaving it off costs those and
    /// nothing else; <c>PROVIDERS.md</c> lists exactly what goes unavailable.
    /// </para>
    /// <para>
    /// Like <see cref="LaunchAtLogin"/>, this records what the user asked for, not what is
    /// true. The truth is in Claude Code's own settings file, which they can edit themselves,
    /// so the effective state is read back through
    /// <see cref="Core.Abstractions.IStatusLineService"/> and this flag follows it rather
    /// than overruling it. Altim never reinstates the entry on its own.
    /// </para>
    /// </remarks>
    public bool ClaudeStatusLineEnabled { get; init; }

    /// <summary>
    /// Whether Altim may ask GitHub, once a day, whether a newer release exists. Defaults
    /// to true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only request Altim makes to a server of its own, and it is worth being
    /// exact about what it is: an unauthenticated HTTPS GET for the newest release of a
    /// public repository. It carries no identifier, no usage figure and no machine detail
    /// beyond what any HTTPS request carries — an IP address and a user agent naming Altim
    /// and its version. Nothing about which providers are installed, how much has been
    /// used, or what is on this machine is in it or could be inferred from it.
    /// </para>
    /// <para>
    /// It defaults on because a desktop application that silently stops receiving fixes is
    /// worse for the user than a daily request they can see documented and switch off, and
    /// nothing here ships signed yet, so the ability to replace a bad build matters more
    /// than usual. <c>PRIVACY.md</c> states it in the same terms; the settings page offers
    /// the switch beside the explanation rather than behind it.
    /// </para>
    /// <para>
    /// Switching it off stops the automatic check and nothing else. A "check now" button
    /// is still a check the user explicitly triggered, which is a different act.
    /// </para>
    /// </remarks>
    public bool AutomaticUpdateChecks { get; init; } = true;

    private static int Clamp(int percent) =>
        Math.Clamp(percent, MinimumThresholdPercent, MaximumThresholdPercent);
}
