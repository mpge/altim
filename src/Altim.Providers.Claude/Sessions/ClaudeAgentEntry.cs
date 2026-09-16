namespace Altim.Providers.Claude.Sessions;

/// <summary>
/// One entry from the CLI's own agents listing.
/// </summary>
/// <remarks>
/// <para>
/// The listing also reports each agent's working directory and display name. Neither is
/// read: the working directory is the user's project path, and the display name is derived
/// from it. What is kept is a process id, an opaque session id, a start instant and two short
/// identifiers.
/// </para>
/// <para>
/// This listing is authoritative for liveness in a way process enumeration is not. A stale
/// session registry file on the verification machine claimed an idle session whose process id
/// had been recycled to an unrelated program; the command correctly omitted it.
/// </para>
/// </remarks>
/// <param name="ProcessId">The agent's process id, when reported.</param>
/// <param name="SessionId">The session's identifier, when reported.</param>
/// <param name="StartedAt">When the session began, when reported.</param>
/// <param name="Kind">The agent kind, when it is a short identifier.</param>
/// <param name="Status">The agent status, when it is a short identifier.</param>
public sealed record ClaudeAgentEntry(
    int? ProcessId,
    string? SessionId,
    DateTimeOffset? StartedAt,
    string? Kind,
    string? Status);
