using System.Globalization;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.Core.Usage;

namespace Altim.Core.Notifications;

/// <summary>
/// Decides which notifications to fire. A pure function over the current readings, the
/// settings and the state of what has already fired: no timers, no queue, no side effects,
/// and therefore no way to spam.
/// </summary>
/// <remarks>
/// The rules, in the order they are applied per metric:
/// a window whose reset instant has passed clears its entries and may raise one reset
/// notification; a metric at or above its threshold with no live entry fires once and
/// records an entry; anything already recorded for the same provider, metric and threshold
/// stays silent until its window rolls over; and the first evaluation after start produces
/// nothing at all while still recording everything it found.
/// </remarks>
public sealed class ThresholdEvaluator
{
    private readonly ResetCalculator _resets;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an evaluator that reads the current instant from <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="timeProvider">The clock. Never <see langword="null"/>.</param>
    public ThresholdEvaluator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _resets = new ResetCalculator(timeProvider);
    }

    /// <summary>
    /// The threshold that applies to a window.
    /// </summary>
    /// <param name="settings">The current settings. Never <see langword="null"/>.</param>
    /// <param name="window">
    /// The window the metric is measured over, or <see langword="null"/> when the provider
    /// reported none, which uses the session threshold as the safer default.
    /// </param>
    /// <returns>
    /// The weekly threshold for weekly and monthly windows, and the session threshold for
    /// everything else. The choice is made from the window length, never from a slot name.
    /// </returns>
    public static int ThresholdFor(AltimSettings settings, LimitWindow? window)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LimitWindowKind kind = LimitWindowClassifier.Classify(window) ?? LimitWindowKind.Other;
        return kind switch
        {
            LimitWindowKind.Weekly or LimitWindowKind.Monthly => settings.WeeklyThresholdPercent,
            _ => settings.SessionThresholdPercent,
        };
    }

    /// <summary>
    /// Evaluates one set of readings.
    /// </summary>
    /// <param name="usages">
    /// The current reading per provider. A reading in <see cref="ProviderStatus.Error"/>
    /// carries no numbers, so it fires nothing and leaves its entries alone. A metric with
    /// no usable percentage is skipped rather than treated as zero.
    /// </param>
    /// <param name="settings">The current settings. Never <see langword="null"/>.</param>
    /// <param name="state">
    /// What the previous evaluation returned, or <see cref="ThresholdState.Initial"/> at
    /// start-up. Never <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The notifications to fire, which is empty on the first evaluation, and the state to
    /// keep for next time, which is always returned even when nothing fired.
    /// </returns>
    public ThresholdEvaluation Evaluate(
        IReadOnlyList<ProviderUsage> usages,
        AltimSettings settings,
        ThresholdState state)
    {
        ArgumentNullException.ThrowIfNull(usages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool silent = !state.HasEvaluated;
        bool thresholdsAudible = !silent && settings is { NotificationsEnabled: true, NotifyOnThreshold: true };
        bool resetsAudible = !silent && settings is { NotificationsEnabled: true, NotifyOnWindowReset: true };

        List<Notification> notifications = [];
        List<NotificationState> next = [];
        HashSet<(string ProviderId, string MetricKey)> evaluated = [];

        foreach (ProviderUsage usage in usages)
        {
            if (usage.Status == ProviderStatus.Error)
            {
                // A failed reading says nothing about usage, so it neither fires nor clears.
                continue;
            }

            foreach (UsageMetric metric in usage.Metrics)
            {
                if (!evaluated.Add((usage.ProviderId, metric.Key)))
                {
                    continue;
                }

                EvaluateMetric(usage.ProviderId, metric, settings, state, now, thresholdsAudible,
                    resetsAudible, notifications, next);
            }
        }

        // Entries for metrics this reading did not carry. A window that has rolled over is
        // dropped: providers stop reporting a window once it resets, so the entry can never
        // be cleared by a later reading. It is dropped quietly, because there is no metric
        // left to label a reset notification with.
        foreach (NotificationState entry in state.Fired)
        {
            if (evaluated.Contains((entry.ProviderId, entry.MetricKey)) || IsStale(entry, null, now))
            {
                continue;
            }

            next.Add(entry);
        }

        return new ThresholdEvaluation(notifications, new ThresholdState(true, next));
    }

    private void EvaluateMetric(
        string providerId,
        UsageMetric metric,
        AltimSettings settings,
        ThresholdState state,
        DateTimeOffset now,
        bool thresholdsAudible,
        bool resetsAudible,
        List<Notification> notifications,
        List<NotificationState> next)
    {
        DateTimeOffset? resetsAt = metric.Window?.ResetsAt;
        int threshold = ThresholdFor(settings, metric.Window);

        bool rolledOver = false;
        bool alreadyFired = false;
        foreach (NotificationState entry in state.Fired)
        {
            if (!string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal)
                || !string.Equals(entry.MetricKey, metric.Key, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsStale(entry, resetsAt, now))
            {
                rolledOver = true;
                continue;
            }

            next.Add(entry);
            alreadyFired |= entry.Threshold == threshold;
        }

        if (rolledOver && resetsAudible)
        {
            notifications.Add(ResetNotification(providerId, metric));
        }

        if (UsagePercent.Normalise(metric.UsedPercent) is not { } percent || percent < threshold || alreadyFired)
        {
            return;
        }

        next.Add(new NotificationState(providerId, metric.Key, threshold, now, resetsAt));
        if (thresholdsAudible)
        {
            notifications.Add(ThresholdNotification(providerId, metric, threshold, percent));
        }
    }

    /// <summary>
    /// True when an entry belongs to a window that is over: either its own reset instant
    /// has passed, or the provider is now reporting a later one for the same metric.
    /// An entry with no reset instant is never stale, because nothing has said otherwise.
    /// </summary>
    private static bool IsStale(NotificationState entry, DateTimeOffset? currentResetsAt, DateTimeOffset now)
    {
        if (entry.WindowResetsAt is not { } stored)
        {
            return false;
        }

        return stored <= now || (currentResetsAt is { } current && current > stored);
    }

    private Notification ThresholdNotification(string providerId, UsageMetric metric, int threshold, double percent)
    {
        string label = DisplayLabel(metric);
        string title = string.Create(CultureInfo.InvariantCulture, $"{label} usage reached {threshold}%");
        string remaining = _resets.DescribeTimeUntilReset(metric.Window) is { } humanised
            ? string.Create(CultureInfo.InvariantCulture, $" Resets in {humanised}.")
            : string.Empty;
        string body = string.Create(CultureInfo.InvariantCulture, $"Now at {percent:0}%.{remaining}");

        return new Notification(
            title,
            body,
            providerId,
            string.Create(CultureInfo.InvariantCulture, $"{providerId}:{metric.Key}:{threshold}"));
    }

    private static Notification ResetNotification(string providerId, UsageMetric metric)
    {
        string label = DisplayLabel(metric);
        return new Notification(
            string.Create(CultureInfo.InvariantCulture, $"{label} limit reset"),
            "A new window has started.",
            providerId,
            string.Create(CultureInfo.InvariantCulture, $"{providerId}:{metric.Key}:reset"));
    }

    /// <summary>
    /// The label to put in front of a user: the one the provider normalised at the parse
    /// boundary, the one the window length implies, or the metric key as a last resort.
    /// </summary>
    private static string DisplayLabel(UsageMetric metric)
    {
        if (!string.IsNullOrWhiteSpace(metric.Label))
        {
            return metric.Label;
        }

        return LimitWindowClassifier.Label(metric.Window) ?? metric.Key;
    }
}
