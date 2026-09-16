using Altim.App.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Notifications;
using Altim.Core.Settings;
using Altim.Storage;

namespace Altim.App.Monitoring;

/// <summary>
/// What happens to a reading once the scheduler has produced one.
/// </summary>
/// <remarks>
/// <para>
/// Five steps, in this order, for every reading: record it to history, evaluate the
/// notification thresholds against the persisted state, show whatever fired, persist the new
/// state, and announce the full set of readings so the tray tooltip can be rewritten.
/// </para>
/// <para>
/// The order is not arbitrary. Evaluation is a pure function of
/// <c>(readings, settings, state)</c>, so it has to see the state that was persisted last
/// time rather than a state some other reading is midway through changing; and the state is
/// written after the notifications rather than before, so a crash between the two re-shows a
/// notification instead of silently swallowing one. Readings arrive from whichever thread
/// finished a refresh, and several providers finish at once, so the whole sequence is
/// serialised behind one gate.
/// </para>
/// <para>
/// Nothing here throws. A failed write, a failed notification and a failed state persist are
/// each recorded once and stepped over; the meters keep moving.
/// </para>
/// </remarks>
internal sealed class UsagePipeline : IDisposable
{
    private readonly Dictionary<string, ProviderUsage> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private readonly IUsageHistoryService _history;
    private readonly NotificationStateStore? _persisted;
    private readonly INotificationService _notifications;
    private readonly ThresholdEvaluator _evaluator;
    private readonly Func<AltimSettings> _settings;
    private readonly StartupReport _report;

    private ThresholdState _state = ThresholdState.Initial;
    private bool _historyFailed;
    private bool _persistFailed;
    private bool _disposed;

    /// <summary>Creates the pipeline.</summary>
    /// <param name="history">Where readings are recorded.</param>
    /// <param name="persisted">The notification state table, or null when storage is unavailable.</param>
    /// <param name="notifications">Where fired notifications are sent.</param>
    /// <param name="timeProvider">The clock thresholds and window rollovers are judged against.</param>
    /// <param name="settings">Reads the settings that are current at evaluation time.</param>
    /// <param name="report">Collects anything that had to be degraded.</param>
    public UsagePipeline(
        IUsageHistoryService history,
        NotificationStateStore? persisted,
        INotificationService notifications,
        TimeProvider timeProvider,
        Func<AltimSettings> settings,
        StartupReport report)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);

        _history = history;
        _persisted = persisted;
        _notifications = notifications;
        _evaluator = new ThresholdEvaluator(timeProvider);
        _settings = settings;
        _report = report;
    }

    /// <summary>
    /// Raised after a reading has been through the whole sequence, carrying every provider's
    /// current reading. Raised on a worker thread.
    /// </summary>
    public event EventHandler<IReadOnlyList<ProviderUsage>>? ReadingsChanged;

    /// <summary>
    /// Loads the persisted record of what has already fired, so a restart does not replay
    /// every threshold the user is already over.
    /// </summary>
    /// <param name="ct">Cancels the load.</param>
    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        if (_persisted is null)
        {
            return;
        }

        try
        {
            _state = await _persisted.LoadThresholdStateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Starting from Initial only risks re-firing a threshold the user is already
            // over, once, and the first evaluation of every provider is silent anyway.
            AltimLog.Write("notifications", "Loading the notification state failed; starting from empty", ex);
        }
    }

    /// <summary>
    /// Hands a reading to the pipeline. Returns immediately; the work runs on a worker.
    /// </summary>
    /// <param name="usage">The reading the scheduler produced.</param>
    public void Submit(ProviderUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (_disposed)
        {
            return;
        }

        _ = Task.Run(() => ProcessAsync(usage), CancellationToken.None);
    }

    /// <summary>Stops accepting readings and releases the gate.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }

    private async Task ProcessAsync(ProviderUsage usage)
    {
        CancellationToken ct;
        try
        {
            ct = _cts.Token;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            await RecordAsync(usage, ct).ConfigureAwait(false);

            _latest[usage.ProviderId] = usage;
            List<ProviderUsage> readings = [.. _latest.Values];

            ThresholdEvaluation evaluation = _evaluator.Evaluate(readings, _settings(), _state);
            _state = evaluation.State;

            await ShowAsync(evaluation.Notifications, ct).ConfigureAwait(false);
            await PersistAsync(evaluation.State, ct).ConfigureAwait(false);

            ReadingsChanged?.Invoke(this, readings);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            AltimLog.Write("pipeline", "Handling a reading failed", ex);
        }
        finally
        {
            try
            {
                _ = _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while this reading was in flight.
            }
        }
    }

    private async ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct)
    {
        try
        {
            await _history.RecordAsync(usage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_historyFailed)
            {
                _historyFailed = true;
                _report.Add("Usage history could not be written");
                AltimLog.Write("storage", "Recording a reading failed", ex);
            }
        }
    }

    private async ValueTask ShowAsync(IReadOnlyList<Notification> notifications, CancellationToken ct)
    {
        foreach (Notification notification in notifications)
        {
            try
            {
                await _notifications.ShowAsync(notification, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The contract says a platform that refuses notifications completes without
                // throwing. One that throws anyway still must not stop the meters.
                AltimLog.Write("notifications", "Showing a notification failed", ex);
            }
        }
    }

    private async ValueTask PersistAsync(ThresholdState state, CancellationToken ct)
    {
        if (_persisted is null)
        {
            return;
        }

        try
        {
            await _persisted.ReplaceAllAsync(state.Fired, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_persistFailed)
            {
                _persistFailed = true;
                AltimLog.Write("notifications", "Persisting the notification state failed", ex);
            }
        }
    }
}
