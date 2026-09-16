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
/// Every quota window one source reported at one instant.
/// </summary>
/// <param name="Windows">
/// One entry per (family, window) actually present. Empty means the source reported no
/// windows, which happens before a plan's first request of a period and is not an error.
/// </param>
/// <param name="PlanType">
/// The plan the account is on, when reported and short enough to be an identifier.
/// </param>
/// <param name="Credits">Credit balance, when reported.</param>
/// <param name="ResetCredits">Credits restored at the next reset, when reported.</param>
/// <param name="Source">Whether this is a live reading or a recovered local one.</param>
/// <param name="ObservedAt">
/// When the reading was taken. For a local snapshot this is the rollout line's timestamp
/// where it has one, and the file's last-write time otherwise, so the age shown to the
/// user is the age of the data and not the age of the read.
/// </param>
public sealed record CodexRateLimitSnapshot(
    IReadOnlyList<CodexLimitWindow> Windows,
    string? PlanType,
    double? Credits,
    double? ResetCredits,
    CodexSnapshotSource Source,
    DateTimeOffset? ObservedAt)
{
    /// <summary>True when the snapshot carries at least one window.</summary>
    public bool HasWindows => Windows.Count > 0;
}
