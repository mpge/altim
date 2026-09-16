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
/// <para>
/// The rules, in the order they are applied per metric: an entry whose window has rolled
/// over is dropped and may raise one reset notification; an entry that never had a reset
/// instant adopts the one the metric now reports, so it can expire later; a metric at or
/// above its threshold with no live entry fires once and records an entry; anything
/// already recorded for the same provider, metric and threshold stays silent until its
/// window rolls over; and the first evaluation of a provider produces nothing at all
/// while still recording everything it found.
/// </para>
/// <para>
/// A reading whose reset instant has already passed is <em>stale</em>, not a rollover in
/// progress. Providers keep serving the last snapshot they have — the Claude status line
/// file is only rewritten when the tool next runs — so the same passed instant arrives on
/// every poll for as long as the tool stays closed. A stale reading may raise the one
/// reset notification its entries are due, and then it arms nothing: taking it as a live
/// reading would re-fire the threshold and re-arm the same passed instant on every tick,
/// forever.
/// </para>
/// </remarks>
public sealed class ThresholdEvaluator
{
    /// <summary>The design copy shown when a window rolls over.</summary>
    public const string ResetTitle = "Usage has reset.";

    /// <summary>
    /// How much later a reset instant has to be before it counts as a new window. A
    /// reset instant derived from a relative "resets in 2h 14m" drifts forward on every
    /// poll, and a zero tolerance reads each of those as a rollover.
    /// </summary>
    public static readonly TimeSpan RolloverTolerance = TimeSpan.FromMinutes(2);

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
    /// The weekly threshold for weekly and monthly windows and for any unclassified
    /// window longer than a day, because a long window crosses slowly and an early
    /// warning on one is noise. The session threshold for everything else. The choice is
    /// made from the window length, never from a slot name.
    /// </returns>
    public static int ThresholdFor(AltimSettings settings, LimitWindow? window)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LimitWindowKind kind = LimitWindowClassifier.Classify(window) ?? LimitWindowKind.Other;
        return kind switch
        {
            LimitWindowKind.Weekly or LimitWindowKind.Monthly => settings.WeeklyThresholdPercent,
            LimitWindowKind.Other when window is not null && window.Length > TimeSpan.FromDays(1) =>
                settings.WeeklyThresholdPercent,
            _ => settings.SessionThresholdPercent,
        };
    }

    /// <summary>
    /// Evaluates one set of readings.
    /// </summary>
    /// <param name="usages">
    /// The current reading per provider. A reading in <see cref="ProviderStatus.Error"/>
    /// carries no numbers, so it fires nothing and clears nothing on account of the
    /// failure; its entries are still swept for windows that have ended, exactly as the
    /// entries of a provider absent from this reading are. A metric with no usable
    /// percentage is skipped rather than treated as zero.
    /// </param>
    /// <param name="settings">The current settings. Never <see langword="null"/>.</param>
    /// <param name="state">
    /// What the previous evaluation returned, or <see cref="ThresholdState.Initial"/> at
    /// start-up. Never <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The notifications to fire, which is empty for any provider being evaluated for the
    /// first time, and the state to keep for next time, which is always returned even when
    /// nothing fired.
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

        List<Notification> notifications = [];
        List<NotificationState> next = [];
        List<string> evaluatedProviders = [.. state.EvaluatedProviders];
        HashSet<(string ProviderId, string MetricKey)> evaluated = [];

        foreach (ProviderUsage usage in usages)
        {
            if (usage.Status == ProviderStatus.Error)
            {
                // A failed reading says nothing about usage, so it neither fires nor
                // clears, and it does not count as this provider's first reading either:
                // the first one that carries numbers is still the first one. Its entries
                // fall to the sweep below like any other entry this reading did not carry
                // a metric for.
                continue;
            }

            // Start-up silence is per provider: a provider reporting for the first time
            // seeds state quietly however late in the run it turns up.
            bool silent = !state.HasEvaluatedProvider(usage.ProviderId);
            if (silent && !Contains(evaluatedProviders, usage.ProviderId))
            {
                evaluatedProviders.Add(usage.ProviderId);
            }

            bool thresholdsAudible = !silent && settings is { NotificationsEnabled: true, NotifyOnThreshold: true };
            bool resetsAudible = !silent && settings is { NotificationsEnabled: true, NotifyOnWindowReset: true };

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
            if (evaluated.Contains((entry.ProviderId, entry.MetricKey)) || HasRolledOver(entry, null, now))
            {
                continue;
            }

            next.Add(entry);
        }

        return new ThresholdEvaluation(notifications, new ThresholdState(evaluatedProviders, next));
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

        // The reading describes a window that is already over, so it is the last snapshot
        // of a finished window rather than a measurement of the current one.
        bool stale = resetsAt is { } instant && AtOrBefore(instant, now);

        bool rolledOver = false;
        bool alreadyFired = false;
        foreach (NotificationState entry in state.Fired)
        {
            if (!string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal)
                || !string.Equals(entry.MetricKey, metric.Key, StringComparison.Ordinal))
            {
                continue;
            }

            if (HasRolledOver(entry, resetsAt, now))
            {
                rolledOver = true;
                continue;
            }

            alreadyFired |= entry.Threshold == threshold;

            // An entry with no reset instant has nothing to expire against, which would
            // mute this metric for the life of the process. The instant the metric is now
            // reporting is the first thing that can end it, so it is adopted.
            next.Add(entry.WindowResetsAt is null && resetsAt is { } adopted
                ? entry with { WindowResetsAt = adopted }
                : entry);
        }

        if (rolledOver && resetsAudible)
        {
            notifications.Add(ResetNotification(providerId, metric));
        }

        if (stale)
        {
            // Nothing is armed from a stale reading. Arming here is what turns one closed
            // window into a notification on every poll for as long as the tool stays shut.
            return;
        }

        // One pair for everybody: the value that fires is the value a view would render.
        if (metric.ReportedPercent is not { } percent || percent < threshold || alreadyFired)
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
    /// has passed, or the provider is now reporting one at least
    /// <see cref="RolloverTolerance"/> later.
    /// </summary>
    /// <remarks>
    /// Instants are compared at second granularity, because the column they are persisted
    /// in holds unix seconds and a difference finer than that cannot survive a restart.
    /// An entry with no reset instant is stale only once the metric reports an instant
    /// that has itself passed; without one, nothing has said the window it fired in is
    /// over, and firing again on a guess would be worse than staying quiet.
    /// </remarks>
    private static bool HasRolledOver(NotificationState entry, DateTimeOffset? currentResetsAt, DateTimeOffset now)
    {
        if (entry.WindowResetsAt is not { } stored)
        {
            return currentResetsAt is { } adopted && AtOrBefore(adopted, now);
        }

        if (AtOrBefore(stored, now))
        {
            return true;
        }

        return currentResetsAt is { } current
            && current.ToUnixTimeSeconds() - stored.ToUnixTimeSeconds() >= (long)RolloverTolerance.TotalSeconds;
    }

    private static bool AtOrBefore(DateTimeOffset instant, DateTimeOffset now) =>
        instant.ToUnixTimeSeconds() <= now.ToUnixTimeSeconds();

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

    /// <summary>
    /// The one reset notification a rolled-over window is allowed. The copy is fixed by
    /// the design document; which window it was is carried by the tag and the provider,
    /// not by invented wording.
    /// </summary>
    private static Notification ResetNotification(string providerId, UsageMetric metric) =>
        new(
            ResetTitle,
            string.Empty,
            providerId,
            string.Create(CultureInfo.InvariantCulture, $"{providerId}:{metric.Key}:reset"));

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

    private static bool Contains(List<string> ids, string providerId)
    {
        foreach (string id in ids)
        {
            if (string.Equals(id, providerId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
