namespace Altim.Providers.Codex.Limits;

/// <summary>
/// Where a quota reading came from.
/// </summary>
public enum CodexSnapshotSource
{
    /// <summary>
    /// Read from the app-server just now. Current, and best-effort: the interface is
    /// experimental and undocumented.
    /// </summary>
    Live = 0,

    /// <summary>
    /// Recovered from a rollout file written by an earlier Codex session. As old as that
    /// session, which the reader states rather than hides.
    /// </summary>
    LocalSnapshot = 1,
}

/// <summary>
/// The account's credit position, as the provider reports it.
/// </summary>
/// <remarks>
/// <para>
/// <c>credits</c> is an object, not a number. Both dialects agree on that: the app-server
/// declares <c>{ hasCredits, unlimited, balance }</c> and the rollout files write
/// <c>{"has_credits":false,"unlimited":false,"balance":"0"}</c>. A reader that treated the
/// property as a number found nothing and reported "no credits" on every account.
/// </para>
/// <para>
/// <see cref="Balance"/> is declared as a string by the provider and is parsed here. A
/// balance that does not parse is <see langword="null"/> — unavailable — rather than zero.
/// </para>
/// </remarks>
/// <param name="HasCredits">Whether the account has any credits, when reported.</param>
/// <param name="Unlimited">Whether the account's credits are unmetered, when reported.</param>
/// <param name="Balance">The balance, when reported and parseable as a number.</param>
public sealed record CodexCredits(bool? HasCredits, bool? Unlimited, double? Balance)
{
    /// <summary>True when the provider reported any part of the credit position.</summary>
    public bool HasAny => HasCredits is not null || Unlimited is not null || Balance is not null;
}

/// <summary>
/// Every quota window one source reported at one instant.
/// </summary>
/// <param name="Windows">
/// One entry per (family, window) actually present. Empty means the source reported no
/// windows, which happens before a plan's first request of a period and is not an error.
/// An empty live snapshot is a successful reading that says "no meters", and every meter
/// disappears rather than holding its last number.
/// </param>
/// <param name="PlanType">
/// The plan the account is on, when reported and short enough to be an identifier. It is
/// reported <em>inside</em> the rate-limits object in both dialects, not beside it.
/// </param>
/// <param name="Credits">The credit position, when reported.</param>
/// <param name="ResetCreditsAvailable">
/// How many rate-limit reset credits are available, when the provider reported the summary
/// that carries the count.
/// </param>
/// <param name="Source">Whether this is a live reading or a recovered local one.</param>
/// <param name="ObservedAt">
/// When the reading was taken. For a local snapshot this is the rollout line's timestamp
/// where it has one, and the file's last-write time otherwise, so the age shown to the
/// user is the age of the data and not the age of the read.
/// </param>
public sealed record CodexRateLimitSnapshot(
    IReadOnlyList<CodexLimitWindow> Windows,
    string? PlanType,
    CodexCredits? Credits,
    long? ResetCreditsAvailable,
    CodexSnapshotSource Source,
    DateTimeOffset? ObservedAt)
{
    /// <summary>True when the snapshot carries at least one window.</summary>
    public bool HasWindows => Windows.Count > 0;
}
