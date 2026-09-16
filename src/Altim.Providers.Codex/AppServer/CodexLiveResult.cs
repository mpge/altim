using Altim.Providers.Codex.Limits;

namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// How a live quota read ended.
/// </summary>
public enum CodexLiveOutcome
{
    /// <summary>The app-server answered.</summary>
    Succeeded = 0,

    /// <summary>The Codex CLI is not installed on this machine.</summary>
    NotDetected = 1,

    /// <summary>
    /// The call was not attempted: network calls are switched off, the poll interval has
    /// not elapsed, or the CLI is authenticated in a mode the call does not support.
    /// </summary>
    Skipped = 2,

    /// <summary>The app-server was reached and refused, errored or answered unusably.</summary>
    Failed = 3,

    /// <summary>The exchange did not complete in time and the process was killed.</summary>
    TimedOut = 4,
}

/// <summary>
/// The outcome of one live quota read.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="RateLimits">
/// The quota snapshot, or <see langword="null"/> for any outcome other than
/// <see cref="CodexLiveOutcome.Succeeded"/>.
/// </param>
/// <param name="AccountUsage">
/// The account token history, or <see langword="null"/> when that half of the exchange did
/// not produce a usable answer. A successful quota read with an unusable usage read is
/// still a success: the two are separate requests.
/// </param>
public sealed record CodexLiveResult(CodexLiveOutcome Outcome, CodexRateLimitSnapshot? RateLimits, CodexAccountUsage? AccountUsage)
{
    /// <summary>The result for a CLI that is not installed.</summary>
    public static CodexLiveResult NotDetected { get; } = new(CodexLiveOutcome.NotDetected, null, null);

    /// <summary>The result for a call that was deliberately not made.</summary>
    public static CodexLiveResult Skipped { get; } = new(CodexLiveOutcome.Skipped, null, null);

    /// <summary>The result for a call that was made and did not work.</summary>
    public static CodexLiveResult Failed { get; } = new(CodexLiveOutcome.Failed, null, null);

    /// <summary>The result for a call that ran out of time.</summary>
    public static CodexLiveResult TimedOut { get; } = new(CodexLiveOutcome.TimedOut, null, null);
}
