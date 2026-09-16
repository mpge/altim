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
    /// <summary>Shown in place of a metric the provider does not report.</summary>
    public const string MetricUnavailable = "Not reported by this provider";

    /// <summary>
    /// Shown in place of a whole reading that could not be taken. The sentence itself lives in
    /// <see cref="ProviderUsage.UnavailableDetail"/> so the words a provider puts in
    /// <c>StatusDetail</c> and the words the interface shows cannot drift apart.
    /// </summary>
    public const string ProviderUnavailable = ProviderUsage.UnavailableDetail;

    /// <summary>The label on the action that retries a failed reading.</summary>
    public const string RetryLabel = "Retry";

    /// <summary>Shown in place of a history chart with no samples in range.</summary>
    public const string HistoryEmpty = "No usage recorded yet. Altim starts collecting when an agent runs.";

    /// <summary>Shown when no provider reports a reset instant.</summary>
    public const string NoResetsReported = "No reset times reported";

    /// <summary>Shown when a provider has not produced a reading yet.</summary>
    public const string NotRefreshedYet = "Not refreshed yet";

    /// <summary>Shown on a provider page with no live agent session.</summary>
    public const string NoActivity = "No agent activity right now";

    /// <summary>Shown on Overview when nothing was detected to report on.</summary>
    public const string NoProviders = "No providers detected";

    /// <summary>Formats a percentage as a whole number, or nothing when it is not reported.</summary>
    /// <param name="usedPercent">The raw value from a provider, which may be null or implausible.</param>
    /// <returns>Something like <c>62%</c>, or <see langword="null"/>.</returns>
    public static string? Percent(double? usedPercent) =>
        UsagePercent.Normalise(usedPercent) is not { } value
            ? null
            : string.Concat(value.ToString("0", CultureInfo.CurrentCulture), "%");

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

    /// <summary>The sentence a provider page uses to describe its integration.</summary>
    /// <param name="status">The reported provider status.</param>
    public static string IntegrationSentence(ProviderStatus status) => status switch
    {
        ProviderStatus.Active => "Reading local session files. An agent is working now.",
        ProviderStatus.Idle => "Reading local session files. No agent is working now.",
        ProviderStatus.Detected => "Installed and readable.",
        ProviderStatus.NotDetected => "Not installed, or its files are not where Altim looks.",
        ProviderStatus.Error => ProviderUnavailable,
        _ => "Waiting for the first reading.",
    };
}
