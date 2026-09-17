using Altim.Core.Models;

namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// One day of the account's own token history, as the app-server reports it.
/// </summary>
/// <remarks>
/// <para>
/// The reply gives <b>one undifferentiated token figure per day</b>. There is no input,
/// output or cache breakdown behind it, and there is no percentage: it is a volume, not a
/// quota reading. Anything that turns this into a breakdown is inventing one.
/// </para>
/// <para>
/// Both property names here were <b>inferred from the wire, not documented</b>. The
/// interface is marked experimental by its vendor, so a bucket whose date or count is
/// missing or in a shape this reader does not recognise is dropped rather than guessed at.
/// The visible result of a rename by the vendor is that no bucket survives, which the map
/// renders as unknown days.
/// </para>
/// </remarks>
/// <param name="Day">The calendar day the figure covers, as the provider dated it.</param>
/// <param name="Tokens">
/// The day's total tokens. A single figure with no components behind it.
/// </param>
public sealed record CodexDailyBucket(DateOnly Day, long Tokens);

/// <summary>
/// The account-level token history the app-server reports.
/// </summary>
/// <remarks>
/// <para>
/// Server-side accounting is the authoritative figure, and the lifetime one is a <b>grand
/// total</b>: one number, with no input, output or cache breakdown behind it. It is
/// therefore never merged into the locally observed per-component totals. Local sums were
/// measured about 16 per cent apart from server accounting, so a field that alternated
/// between the two would produce a history series that moved when nothing had happened.
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
/// How many daily buckets the response carried, counted from the raw array. This is
/// deliberately <b>not</b> <c>DailyBuckets.Count</c>: the array's length says the provider
/// answered, while the parsed list says how much of that answer this reader understood, and
/// the gap between them is the signal that the inferred field names have gone stale.
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
    /// <summary>
    /// The daily history the response carried, in the order it arrived, limited to buckets
    /// whose date and token count were both readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is roughly three months of dated totals, and it is the reason the usage map is
    /// worth anything on the day it ships rather than three months later. It used to be
    /// thrown away and replaced by <see cref="DailyBucketCount"/>.
    /// </para>
    /// <para>
    /// Empty means "nothing to backfill" and never "nothing was used". A day the provider
    /// did not account for is absent from this list; it is not present with a zero.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CodexDailyBucket> DailyBuckets { get; init; } = [];

    /// <summary>True when the response carried anything at all.</summary>
    public bool HasAny =>
        LifetimeTokens is not null
        || DailyBucketCount is not null
        || CurrentStreakDays is not null
        || LongestStreakDays is not null
        || PeakDailyTokens is not null
        || DailyBuckets.Count > 0;

    /// <summary>
    /// How much weight these figures carry. Always
    /// <see cref="MetricConfidence.BestEffort"/>: the interface that produces them is
    /// experimental, and nothing here is ever presented as a published figure.
    /// </summary>
    public MetricConfidence Confidence => MetricConfidence.BestEffort;
}
