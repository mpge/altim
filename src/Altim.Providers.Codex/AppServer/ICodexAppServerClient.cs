namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// Reads the live quota and account usage from the Codex app-server.
/// </summary>
/// <remarks>
/// Abstracted so the provider's fallback behaviour is testable without the CLI: the test
/// that a failing live call degrades to a local snapshot with its age stated substitutes an
/// implementation here rather than breaking the machine's Codex installation.
/// </remarks>
public interface ICodexAppServerClient
{
    /// <summary>True when the Codex CLI exists on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Performs one <c>initialize</c> / <c>initialized</c> / <c>account/rateLimits/read</c>
    /// / <c>account/usage/read</c> exchange.
    /// </summary>
    /// <param name="timeout">How long the whole exchange may take.</param>
    /// <param name="ct">Cancels the exchange and kills the process.</param>
    /// <returns>
    /// The outcome. This method does not throw for a provider-level problem: a missing CLI,
    /// a refusal and a timeout are all outcomes.
    /// </returns>
    Task<CodexLiveResult> ReadAsync(TimeSpan timeout, CancellationToken ct);
}
