using Altim.Providers.Codex.Rollout;

namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// The account-level token history the app-server reports, reduced to counts.
/// </summary>
/// <remarks>
/// Server-side accounting is the authoritative figure. Local token sums were measured about
/// 16 per cent apart from it, so when this is available it is reported as the server's
/// number and local aggregates are labelled as locally observed rather than quietly
/// replaced.
/// </remarks>
/// <param name="LifetimeTokens">
/// Tokens the account has used in total, when reported.
/// </param>
/// <param name="DailyBucketCount">
/// How many daily buckets the response carried. The buckets themselves are not retained:
/// Altim keeps its own history in SQLite, so copying the provider's would add nothing but
/// a second source of truth.
/// </param>
/// <param name="CurrentStreakDays">The current run of consecutive active days, when reported.</param>
/// <param name="LongestStreakDays">The longest such run, when reported.</param>
public sealed record CodexAccountUsage(
    CodexTokenCounts? LifetimeTokens,
    long? DailyBucketCount,
    long? CurrentStreakDays,
    long? LongestStreakDays)
{
    /// <summary>True when the response carried anything at all.</summary>
    public bool HasAny =>
        LifetimeTokens is { HasAny: true }
        || DailyBucketCount is not null
        || CurrentStreakDays is not null
        || LongestStreakDays is not null;
}
