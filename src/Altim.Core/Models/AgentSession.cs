namespace Altim.Core.Models;

/// <summary>
/// One agent conversation a provider knows about. Carries no project name, prompt,
/// command or file path: none of that is read out of the provider's store, and none
/// of it is ever persisted.
/// </summary>
/// <param name="Id">
/// The provider's own opaque identifier for the session. Stable for the life of the
/// session, and meaningless outside its provider.
/// </param>
/// <param name="ProviderId">
/// The provider that reported the session.
/// </param>
/// <param name="StartedAt">
/// When the session began, as reported by the provider.
/// </param>
/// <param name="LastActivityAt">
/// The most recent activity in the session. <see langword="null"/> means the provider
/// does not report one, which is not the same as the session having been idle since
/// it started.
/// </param>
/// <param name="Tokens">
/// Tokens attributed to this session, de-duplicated on message identity and including
/// any subagent transcripts. <see langword="null"/> when the provider does not report
/// per-session token counts.
/// </param>
/// <param name="ModelId">
/// The model the session ran against. <see langword="null"/> when not reported.
/// </param>
/// <param name="IsActive">
/// True when the provider says the session is running now.
/// </param>
public sealed record AgentSession(
    string Id,
    string ProviderId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastActivityAt,
    TokenTotals? Tokens,
    string? ModelId,
    bool IsActive);
