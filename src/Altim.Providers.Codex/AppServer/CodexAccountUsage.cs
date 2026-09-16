using Altim.Core.Models;

namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// The account-level token history the app-server reports, reduced to counts.
/// </summary>
/// <remarks>
/// <para>
/// Server-side accounting is the authoritative figure, and it is a <b>grand total</b>: one
/// number, with no input, output or cache breakdown behind it. It is therefore never merged
/// into the locally observed per-component totals. Local sums were measured about 16 per
/// cent apart from server accounting, so a field that alternated between the two would
/// produce a history series that moved when nothing had happened.
/// </para>
/// <para>
/// Every value here is <see cref="MetricConfidence.BestEffort"/>. The interface is marked
/// experimental by its vendor, so a field that is not there, or is there in a shape this
/// reader does not recognise, reads as unavailable rather than as a number.
/// </para>
/// </remarks>
/// <param name="LifetimeTokens">
/// Tokens the account has used in total, when reported. A single total, not a breakdown.
/// </param>
/// <param name="DailyBucketCount">
/// How many daily buckets the response carried. The buckets themselves are not retained:
/// Altim keeps its own history in SQLite, so copying the provider's would add nothing but
/// a second source of truth.
/// </param>
/// <param name="CurrentStreakDays">The current run of consecutive active days, when reported.</param>
/// <param name="LongestStreakDays">The longest such run, when reported.</param>
/// <param name="PeakDailyTokens">The busiest single day, when reported.</param>
public sealed record CodexAccountUsage(
    long? LifetimeTokens,
    long? DailyBucketCount,
    long? CurrentStreakDays,
    long? LongestStreakDays,
    long? PeakDailyTokens)
{
    /// <summary>True when the response carried anything at all.</summary>
    public bool HasAny =>
        LifetimeTokens is not null
        || DailyBucketCount is not null
        || CurrentStreakDays is not null
        || LongestStreakDays is not null
        || PeakDailyTokens is not null;

    /// <summary>
    /// How much weight these figures carry. Always
    /// <see cref="MetricConfidence.BestEffort"/>: the interface that produces them is
    /// experimental, and nothing here is ever presented as a published figure.
    /// </summary>
    public MetricConfidence Confidence => MetricConfidence.BestEffort;
}
