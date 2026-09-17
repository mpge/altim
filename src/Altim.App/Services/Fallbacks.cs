using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;

namespace Altim.App.Services;

/// <summary>
/// Settings kept for the life of the process because the database could not be opened.
/// </summary>
/// <remarks>
/// The user can still change anything; the change simply does not survive a restart, and the
/// tray menu says so. Refusing to start instead would be the wrong trade for a utility whose
/// whole job is to sit in the tray.
/// </remarks>
internal sealed class MemorySettingsBackend : ISettingsBackend
{
    private readonly Lock _gate = new();
    private AltimSettings _settings = AltimSettings.Default;

    /// <inheritdoc />
    public ValueTask<AltimSettings> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_settings);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(AltimSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _settings = settings;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// History that records nothing, for a machine whose database could not be opened.
/// </summary>
/// <remarks>
/// Every read returns empty, which the history page already renders as "No usage recorded
/// yet". That sentence is true here: nothing was recorded. The reason is in the tray menu
/// and in the log, so the empty chart is explained rather than mysterious.
/// </remarks>
internal sealed class NullUsageHistoryService : IUsageHistoryService
{
    /// <inheritdoc />
    public ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
        string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
        string providerId, DateTimeOffset at, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(
        string providerId, DateOnly from, DateOnly to, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<UsageDay>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// Accepted and dropped, like every other write here. Every day therefore reads back as
    /// absent, which the map renders as unknown rather than as a row of zeroes — the truthful
    /// answer for a machine whose database would not open.
    /// </remarks>
    public ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
/// A notification service for a platform that has none, or one that refused registration.
/// </summary>
/// <remarks>
/// <see cref="INotificationService"/>'s contract is that a platform which refuses
/// notifications completes without throwing, so the degraded case is genuinely this: accept
/// the message and drop it. The user is told once, in the tray menu, rather than once per
/// threshold.
/// </remarks>
internal sealed class NullNotificationService : INotificationService
{
    /// <inheritdoc />
    public ValueTask ShowAsync(Notification n, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(n);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Autostart on a platform Altim cannot register with.
/// </summary>
/// <remarks>
/// Reports false forever, including straight after a request to switch it on.
/// <see cref="IAutoStartService"/> already says callers read the state back rather than
/// assuming the write took, so a settings toggle that springs back is the contract working.
/// </remarks>
internal sealed class NullAutoStartService : IAutoStartService
{
    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync() => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask SetAsync(bool on) => ValueTask.CompletedTask;
}
