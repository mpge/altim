using System.Globalization;
using Altim.Core.Models;
using Altim.Core.Usage;

namespace Altim.UI.Formatting;

/// <summary>
/// Every string the interface puts next to a number, in one place.
/// </summary>
/// <remarks>
/// The sentences are the ones DESIGN.md prescribes, held as constants so a view cannot drift
/// from the wording. Every formatter returns <see langword="null"/> rather than a stand in when
/// the source reports nothing: a null percentage is never a zero, and an unknown reset time is
/// never a guess.
/// </remarks>
public static class UsageFormat
{
    /// <summary>
    /// Said of a single figure Altim derived rather than read: the tooltip and the accessible
    /// name on that row.
    /// </summary>
    /// <remarks>
    /// Names the act, not the vendor. Every provider this applies to is a different vendor and
    /// the sentence is the same one, and saying which vendor does not document something reads
    /// as a complaint about them rather than a caveat about the number.
    /// </remarks>
    public const string BestEffortFigure =
        "Altim worked this out from local files rather than reading it from a figure the " +
        "vendor states, so it can change without notice.";

    /// <summary>
    /// Said once on a provider's card when any of its figures is derived rather than read.
    /// </summary>
    /// <remarks>
    /// One line per card rather than a mark on every row, which is the shape
    /// <c>ShowsLocalOnlyNotice</c> already set: a caption a reader meets once is read, and a
    /// badge on half the rows is furniture.
    /// </remarks>
    public const string BestEffortOnThisCard =
        "Some figures here are worked out from local files rather than read from a figure the " +
        "vendor states. Those rows say so.";

    /// <summary>Shown in place of a metric the provider does not report.</summary>
    public const string MetricUnavailable = "Not reported by this provider";

    /// <summary>
    /// Shown in place of a metric nobody has asked the provider for yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"We have not asked yet" is a fourth state</b>, beside a reading that failed, a
    /// reading that carried no metric and a metric carrying no figure. It must not render as
    /// <see cref="MetricUnavailable"/>: that sentence says the provider was asked and
    /// reported nothing, and saying it before the first reading has landed is Altim
    /// answering a question it has not put yet. The first read spawns a vendor command line
    /// and has taken seven seconds on the verification machine, so this is not one frame.
    /// </para>
    /// <para>
    /// One constant rather than a sentence per surface, and the same one
    /// <see cref="IntegrationSentence"/> gives for <see cref="ProviderStatus.Unknown"/>, so
    /// the metric block and the integration section cannot end up describing the same state
    /// in two ways.
    /// </para>
    /// </remarks>
    public const string WaitingForFirstReading = "Waiting for the first reading.";

    /// <summary>
    /// Shown in place of a whole reading that could not be taken. The sentence itself lives in
    /// <see cref="ProviderUsage.UnavailableDetail"/> so the words a provider puts in
    /// <c>StatusDetail</c> and the words the interface shows cannot drift apart.
    /// </summary>
    public const string ProviderUnavailable = ProviderUsage.UnavailableDetail;

    /// <summary>The label on the action that retries a failed reading.</summary>
    public const string RetryLabel = "Retry";

    /// <summary>
    /// The accessible name of the gear, which is a drawn icon and therefore has no text of
    /// its own for an assistive technology to read.
    /// </summary>
    public const string SettingsActionName = "Settings";

    /// <summary>
    /// The accessible name of a provider's disclosure, which is the chevron that opens that
    /// provider's own page.
    /// </summary>
    /// <param name="providerName">The provider's display name.</param>
    /// <returns>A sentence naming what the control opens, not what it looks like.</returns>
    public static string DisclosureName(string providerName)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        return "Open " + providerName;
    }

    /// <summary>Shown in place of a history chart with no samples in range.</summary>
    public const string HistoryEmpty = "No usage recorded yet. Altim starts collecting when an agent runs.";

    /// <summary>Shown when no provider reports a reset instant.</summary>
    public const string NoResetsReported = "No reset times reported";

    /// <summary>Shown when a provider has not produced a reading yet.</summary>
    public const string NotRefreshedYet = "Not refreshed yet";

    /// <summary>
    /// Shown wherever the figures on screen were read from local files alone, because the
    /// user has switched off the live quota check.
    /// </summary>
    /// <remarks>
    /// One constant rather than one sentence per page, so the settings toggle and the
    /// provider it affects cannot end up saying different things about the same state.
    /// </remarks>
    public const string LocalFiguresOnly =
        "Live quota checks are off. These figures come from local files and may be behind.";

    /// <summary>Shown on a provider page with no live agent session.</summary>
    public const string NoActivity = "No agent activity right now";

    /// <summary>Shown on Overview when nothing was detected to report on.</summary>
    public const string NoProviders = "No providers detected";

    /// <summary>
    /// Shown in place of a figure Altim has no way to produce. It is an em dash rather than
    /// a zero, because "not known" is not "none", and it is one character rather than a
    /// sentence because it stands where a figure would stand.
    /// </summary>
    public const string Unknown = "\u2014";

    /// <summary>
    /// Shown in place of a pacing figure there is not enough local history to compute. It
    /// is an em dash rather than a zero, because "no comparison" is not "no change".
    /// </summary>
    public const string PacingUnknown = Unknown;

    /// <summary>Shown when a session was last active less than a minute ago.</summary>
    public const string JustNow = "just now";

    /// <summary>The label over the pacing figure in a provider card's footer.</summary>
    public const string PacingLabel = "Pacing";

    /// <summary>The label over the reset time in a provider card's footer.</summary>
    public const string ResetsLabel = "Resets in";

    /// <summary>Formats a percentage as a whole number, or nothing when it is not reported.</summary>
    /// <param name="usedPercent">The raw value from a provider, which may be null or implausible.</param>
    /// <returns>Something like <c>62%</c>, or <see langword="null"/>.</returns>
    public static string? Percent(double? usedPercent) =>
        UsagePercent.Normalise(usedPercent) is not { } value
            ? null
            : string.Concat(value.ToString("0", CultureInfo.CurrentCulture), "%");

    /// <summary>
    /// What an instrument reads, in words: the level, and the threshold it is measured
    /// against.
    /// </summary>
    /// <param name="value">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>A sentence naming the level and what it is measured against.</returns>
    /// <remarks>
    /// The meter and the dial both read this out, as their accessible name and as the tip a
    /// pointer and the keyboard open. One sentence, because two controls showing the same
    /// reading must not describe it in two ways. An unreported metric says so and never
    /// reads as a zero, which is the distinction the drawn states also keep; the threshold
    /// is named in words because the index marking it is a mark on a scale, and a reader who
    /// cannot see it, or can see it but cannot tell which level it stands at, both need the
    /// number said.
    /// </remarks>
    public static string InstrumentReading(double? value, double? threshold)
    {
        if (Percent(value) is not { } level)
        {
            return MetricUnavailable;
        }

        string used = string.Concat(level, " used");
        return Percent(threshold) is { } limit
            ? string.Concat(used, ", threshold ", limit)
            : used;
    }

    /// <summary>Formats a count compactly: 842, 56.2K, 1.3M.</summary>
    /// <param name="value">The count. Negative counts are not meaningful and read as zero.</param>
    public static string Count(long value)
    {
        double magnitude = Math.Max(0L, value);
        return magnitude switch
        {
            < 1_000d => magnitude.ToString("0", CultureInfo.CurrentCulture),
            < 1_000_000d => string.Concat((magnitude / 1_000d).ToString("0.#", CultureInfo.CurrentCulture), "K"),
            < 1_000_000_000d => string.Concat((magnitude / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture), "M"),
            _ => string.Concat((magnitude / 1_000_000_000d).ToString("0.#", CultureInfo.CurrentCulture), "B"),
        };
    }

    /// <summary>Adds the two totals a provider charges against a plan, if either is reported.</summary>
    /// <param name="tokens">The reported totals, which may be null or partly null.</param>
    public static long? TotalTokens(TokenTotals? tokens)
    {
        if (tokens is null)
        {
            return null;
        }

        return (tokens.Input, tokens.Output) switch
        {
            (null, null) => null,
            ({ } input, null) => input,
            (null, { } output) => output,
            ({ } input, { } output) => input + output,
        };
    }

    /// <summary>Formats the headline token line, or nothing when no count is reported.</summary>
    /// <param name="tokens">The reported totals.</param>
    /// <returns>Something like <c>56.2K tokens</c>, or <see langword="null"/>.</returns>
    public static string? Tokens(TokenTotals? tokens) =>
        TotalTokens(tokens) is { } total ? string.Concat(Count(total), " tokens") : null;

    /// <summary>Formats an instant as a local clock time in the user's locale format.</summary>
    /// <param name="instant">The instant, in any offset.</param>
    /// <returns>Something like <c>8:00 PM</c>, or <see langword="null"/>.</returns>
    public static string? ClockTime(DateTimeOffset? instant) =>
        instant?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Formats the time remaining in a limit window.</summary>
    /// <param name="window">The window, which may be null or report no reset instant.</param>
    /// <param name="timeProvider">The clock the remaining time is measured against.</param>
    /// <returns>Something like <c>2h 14m</c>, or <see langword="null"/>.</returns>
    public static string? Remaining(LimitWindow? window, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new ResetCalculator(timeProvider).DescribeTimeUntilReset(window);
    }

    /// <summary>Formats the reset caption that sits under a metric.</summary>
    /// <param name="window">The window, which may be null or report no reset instant.</param>
    /// <param name="timeProvider">The clock the remaining time is measured against.</param>
    /// <returns>Something like <c>Resets in 2h 14m</c>, or <see langword="null"/>.</returns>
    public static string? ResetsIn(LimitWindow? window, TimeProvider timeProvider) =>
        Remaining(window, timeProvider) is { } remaining ? string.Concat("Resets in ", remaining) : null;

    /// <summary>Formats the last refreshed caption.</summary>
    /// <param name="instant">When the reading was taken, or null if none has been.</param>
    public static string LastRefreshed(DateTimeOffset? instant) =>
        ClockTime(instant) is { } clock ? string.Concat("Updated ", clock) : NotRefreshedYet;

    /// <summary>The single word a status is allowed to appear as.</summary>
    /// <param name="status">The reported provider status.</param>
    public static string StatusWord(ProviderStatus status) => status switch
    {
        ProviderStatus.Active => "Active",
        ProviderStatus.Idle => "Idle",
        ProviderStatus.Detected => "Detected",
        ProviderStatus.NotDetected => "Not detected",
        ProviderStatus.Error => "Unavailable",
        _ => "Waiting",
    };

    /// <summary>
    /// The shorthand a window is named by where there is only room for two characters,
    /// such as the popup's one line per provider: <c>5h</c>, <c>7d</c>.
    /// </summary>
    /// <param name="window">The window, or null when the provider reported none.</param>
    /// <returns>The shorthand, or <see langword="null"/> when there is no window.</returns>
    public static string? WindowShort(LimitWindow? window)
    {
        if (window is null || window.Length <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan length = window.Length;
        if (length.TotalHours < 24d)
        {
            int hours = (int)Math.Round(length.TotalHours, MidpointRounding.AwayFromZero);
            return hours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{hours}h")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(length.TotalMinutes)}m");
        }

        int days = (int)Math.Round(length.TotalDays, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{days}d");
    }

    /// <summary>
    /// How one metric is named where there is only room for a couple of characters, such as
    /// the popup's one line per provider: the window's length, and whatever the provider's
    /// own label adds to tell it apart from another window of the same length.
    /// </summary>
    /// <param name="window">The window, or null when the provider reported none.</param>
    /// <param name="label">The provider's own label for the metric.</param>
    /// <returns>Something like <c>7d</c> or <c>7d Opus</c>, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <see cref="WindowShort(LimitWindow?)"/> is keyed on length alone, and a provider can
    /// report several windows of one length: Claude Code reports Weekly, Weekly (Opus) and
    /// Weekly (Sonnet), all seven days long. Keyed on length the line printed <c>7d</c>
    /// three times with three different figures beside it, which is three readings under one
    /// name.
    /// </remarks>
    public static string? MetricShort(LimitWindow? window, string? label)
    {
        if (WindowShort(window) is not { } shorthand)
        {
            return null;
        }

        return Qualifier(label) is { } qualifier
            ? string.Concat(shorthand, " ", qualifier)
            : shorthand;
    }

    /// <summary>
    /// The parenthesised part of a label, such as <c>Opus</c> in <c>Weekly (Opus)</c>, or
    /// null when the label has none.
    /// </summary>
    private static string? Qualifier(string? label)
    {
        if (label is null)
        {
            return null;
        }

        int open = label.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        int close = label.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        string inner = label[(open + 1)..close].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>
    /// Formats a pacing figure: this window's level against the same point in the previous
    /// one, in percentage points.
    /// </summary>
    /// <param name="deltaPercent">
    /// The difference, or <see langword="null"/> when there is not enough local history to
    /// compute one. A null reads as <see cref="PacingUnknown"/>, never as a zero.
    /// </param>
    public static string Pacing(double? deltaPercent) => deltaPercent is not { } delta
        ? PacingUnknown
        : string.Concat(
            Math.Round(delta, MidpointRounding.AwayFromZero).ToString("+0;-0;0", CultureInfo.CurrentCulture),
            "%");

    /// <summary>The caption naming what a pacing figure was compared against.</summary>
    /// <param name="window">The window the comparison was made over.</param>
    /// <returns>Something like <c>vs. last week</c>, or null when there is no window.</returns>
    public static string? PacingCaption(LimitWindow? window) =>
        LimitWindowClassifier.Classify(window) switch
        {
            LimitWindowKind.FiveHour => "vs. last session",
            LimitWindowKind.Daily => "vs. yesterday",
            LimitWindowKind.Weekly => "vs. last week",
            LimitWindowKind.Monthly => "vs. last month",
            null => null,
            _ => "vs. the window before",
        };

    /// <summary>Formats how long ago an instant was, in the coarsest useful unit.</summary>
    /// <param name="instant">The instant, or null when the provider reports none.</param>
    /// <param name="timeProvider">The clock the distance is measured against.</param>
    /// <returns>Something like <c>2m ago</c>, or <see langword="null"/>.</returns>
    public static string? RelativeTime(DateTimeOffset? instant, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (instant is not { } at)
        {
            return null;
        }

        TimeSpan elapsed = timeProvider.GetUtcNow() - at;
        return elapsed < TimeSpan.FromMinutes(1d)
            ? JustNow
            : string.Concat(ResetCalculator.Humanise(elapsed), " ago");
    }

    /// <summary>Formats how long something has been running.</summary>
    /// <param name="from">When it started.</param>
    /// <param name="timeProvider">The clock the duration is measured against.</param>
    /// <returns>Something like <c>42m</c>, or <see langword="null"/> when it has not begun.</returns>
    public static string? Elapsed(DateTimeOffset? from, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (from is not { } started)
        {
            return null;
        }

        TimeSpan elapsed = timeProvider.GetUtcNow() - started;
        return elapsed < TimeSpan.Zero ? null : ResetCalculator.Humanise(elapsed);
    }

    /// <summary>Joins the parts of a caption that are actually reported, with a middle dot.</summary>
    /// <param name="parts">The parts, any of which may be null or blank.</param>
    /// <returns>The joined line, or <see langword="null"/> when nothing was reported.</returns>
    public static string? Join(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        List<string> kept = [];
        foreach (string? part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                kept.Add(part);
            }
        }

        return kept.Count == 0 ? null : string.Join(" \u00b7 ", kept);
    }

    /// <summary>The sentence a provider page uses to describe its integration.</summary>
    /// <param name="status">The reported provider status.</param>
    public static string IntegrationSentence(ProviderStatus status) => status switch
    {
        ProviderStatus.Active => "Reading local session files. An agent is working now.",
        ProviderStatus.Idle => "Reading local session files. No agent is working now.",
        ProviderStatus.Detected => "Installed and readable.",
        ProviderStatus.NotDetected => "Not installed, or its files are not where Altim looks.",
        ProviderStatus.Error => ProviderUnavailable,
        _ => WaitingForFirstReading,
    };
}
