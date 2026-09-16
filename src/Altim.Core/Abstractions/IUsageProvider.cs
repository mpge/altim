using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// One AI agent integration. Adding a provider means adding one project that
/// implements this interface and registering it; no view changes, because views
/// render whatever metrics a provider reports.
/// </summary>
/// <remarks>
/// Implementations never start their own timers. <c>MonitorScheduler</c> owns all
/// timing, so the process has a single wake source. Work happens off the UI thread
/// and returns immutable records, and a thrown exception becomes
/// <see cref="ProviderStatus.Error"/> rather than a crash.
/// </remarks>
public interface IUsageProvider
{
    /// <summary>
    /// Stable identifier, for example <c>claude</c> or <c>codex</c>. Used as a storage
    /// key, so it never changes for a given provider.
    /// </summary>
    string Id { get; }

    /// <summary>The name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Where the integration currently stands. <see cref="ProviderStatus.Unknown"/>
    /// until the first refresh completes.
    /// </summary>
    ProviderStatus Status { get; }

    /// <summary>
    /// Returns the last reading, refreshing it if the provider has none yet. Never
    /// throws for a provider level failure: a failure is reported as a
    /// <see cref="ProviderUsage"/> with <see cref="ProviderStatus.Error"/> and a
    /// <see cref="ProviderUsage.StatusDetail"/>.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The current reading. Metrics that are not reported are absent or null, never zero.</returns>
    ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct);

    /// <summary>
    /// Returns the sessions this provider knows about. An empty list means the
    /// provider reports no sessions, not that the read failed.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct);

    /// <summary>
    /// Re-reads the provider now and raises <see cref="UsageChanged"/> if the reading
    /// moved. Called by the scheduler, never by a timer the provider owns.
    /// </summary>
    /// <param name="ct">Cancels the refresh.</param>
    ValueTask RefreshAsync(CancellationToken ct);

    /// <summary>
    /// Raised after a refresh produced a reading different from the previous one.
    /// May be raised on any thread, so subscribers marshal to the UI thread themselves.
    /// </summary>
    event EventHandler<ProviderUsage>? UsageChanged;
}
