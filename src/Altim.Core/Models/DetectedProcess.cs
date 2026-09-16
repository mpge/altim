namespace Altim.Core.Models;

/// <summary>
/// A running process that looks like an agent. Only the fields below are read;
/// command lines, arguments and working directories are never inspected or stored.
/// </summary>
/// <param name="ProcessId">
/// The operating system process identifier at scan time. It may already be stale by
/// the time it is read.
/// </param>
/// <param name="ProviderId">
/// The provider this process was matched to. <see langword="null"/> means the process
/// looks like an agent but matches no known provider, so callers must not assume a
/// match.
/// </param>
/// <param name="ExecutableName">
/// The executable file name only, with no directory component.
/// </param>
/// <param name="StartedAt">
/// When the process started. <see langword="null"/> means the operating system
/// refused the query, which is common for processes owned by another user, and is
/// not a claim that the process just started.
/// </param>
public sealed record DetectedProcess(
    int ProcessId,
    string? ProviderId,
    string ExecutableName,
    DateTimeOffset? StartedAt);
