namespace Altim.Providers.Codex.State;

/// <summary>
/// One recently active thread, as the state database describes it.
/// </summary>
/// <remarks>
/// The <c>threads</c> table also stores the rollout path for each thread. That column is
/// read — it is how the fallback finds which few files to tail — but it is held inside
/// <see cref="CodexStateDatabase"/> and does not appear here, so no path can travel with a
/// thread summary into a view model or a database row.
/// </remarks>
/// <param name="ThreadId">The provider's own identifier for the thread, when it is one.</param>
/// <param name="ModelId">The model the thread ran against, when reported.</param>
/// <param name="TokensUsed">
/// The provider's own token count for the thread, when reported. A cumulative figure: it is
/// not added to anything derived from the same thread's rollout file.
/// </param>
/// <param name="UpdatedAt">The thread's last activity, when reported.</param>
/// <param name="CreatedAt">When the thread began, when reported.</param>
public sealed record CodexThreadSummary(
    string? ThreadId,
    string? ModelId,
    long? TokensUsed,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CreatedAt);
