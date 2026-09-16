using Altim.Core.Models;
using Altim.Core.Notifications;
using Altim.Core.Settings;
using Altim.Core.Tests.Fakes;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Threshold evaluation as a pure function: same readings, same settings, same state, same
/// answer. Covers crossing, de-duplication inside a window, clearing on reset, and the
/// silence that has to hold over the first evaluation after start.
/// </summary>
public sealed class ThresholdEvaluatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstEvaluationFiresNothingButRemembersWhatItSaw()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);

        ThresholdEvaluation result = evaluator.Evaluate(
            [Reading("claude", Session(92d))],
            AltimSettings.Default,
            ThresholdState.Initial);

        Assert.Empty(result.Notifications);
        Assert.True(result.State.HasEvaluated);

        NotificationState entry = Assert.Single(result.State.Fired);
        Assert.Equal("claude", entry.ProviderId);
        Assert.Equal("five_hour", entry.MetricKey);
        Assert.Equal(80, entry.Threshold);
        Assert.Equal(Start, entry.FiredAt);
        Assert.Equal<DateTimeOffset?>(Start.AddHours(2), entry.WindowResetsAt);
    }

    [Fact]
    public void ARestartIsAStartAndStaysSilent()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        ThresholdState persisted = ThresholdState.FromPersisted([]);

        ThresholdEvaluation result = evaluator.Evaluate(
            [Reading("claude", Session(95d))],
            AltimSettings.Default,
            persisted);

        Assert.Empty(result.Notifications);
        Assert.Single(result.State.Fired);
    }

    [Fact]
    public void CrossingAThresholdUpwardFiresOnce()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);

        ThresholdState state = evaluator.Evaluate(
            [Reading("claude", Session(12d))], AltimSettings.Default, ThresholdState.Initial).State;
        Assert.Empty(state.Fired);

        ThresholdEvaluation crossed = evaluator.Evaluate(
            [Reading("claude", Session(84.2d))], AltimSettings.Default, state);

        Notification notification = Assert.Single(crossed.Notifications);
        Assert.Equal("Session usage reached 80%", notification.Title);
        Assert.Equal("Now at 84%. Resets in 2h.", notification.Body);
        Assert.Equal("claude", notification.ProviderId);
        Assert.Equal("claude:five_hour:80", notification.Tag);
    }

    [Fact]
    public void ItNeverFiresTwiceForTheSameWindow()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;

        ThresholdEvaluation first = evaluator.Evaluate([Reading("claude", Session(81d))], settings, state);
        Assert.Single(first.Notifications);

        time.Advance(TimeSpan.FromMinutes(20));
        ThresholdEvaluation second = evaluator.Evaluate([Reading("claude", Session(88d))], settings, first.State);
        Assert.Empty(second.Notifications);

        time.Advance(TimeSpan.FromMinutes(20));
        ThresholdEvaluation third = evaluator.Evaluate([Reading("claude", Session(99d))], settings, second.State);
        Assert.Empty(third.Notifications);
        Assert.Single(third.State.Fired);
    }

    [Fact]
    public void TheResetInstantPassingClearsTheStateAndFiresOneResetNotification()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, state);
        Assert.Single(crossed.Notifications);

        time.Advance(TimeSpan.FromHours(3));
        UsageMetric afterReset = Session(4d, resetsAt: Start.AddHours(7));
        ThresholdEvaluation reset = evaluator.Evaluate([Reading("claude", afterReset)], settings, crossed.State);

        Notification notification = Assert.Single(reset.Notifications);
        Assert.Equal("Session limit reset", notification.Title);
        Assert.Equal("claude:five_hour:reset", notification.Tag);
        Assert.Empty(reset.State.Fired);

        // And only once: the next evaluation of the new window is quiet.
        ThresholdEvaluation quiet = evaluator.Evaluate([Reading("claude", afterReset)], settings, reset.State);
        Assert.Empty(quiet.Notifications);
    }

    [Fact]
    public void AClearedThresholdCanFireAgainInTheNextWindow()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, state);
        Assert.Single(crossed.Notifications);

        time.Advance(TimeSpan.FromHours(3));
        ThresholdEvaluation next = evaluator.Evaluate(
            [Reading("claude", Session(86d, resetsAt: Start.AddHours(7)))], settings, crossed.State);

        Assert.Equal(2, next.Notifications.Count);
        Assert.Equal("Session limit reset", next.Notifications[0].Title);
        Assert.Equal("Session usage reached 80%", next.Notifications[1].Title);
        Assert.Single(next.State.Fired);
    }

    [Fact]
    public void ALaterWindowClearsTheStateEvenBeforeTheOldInstantPasses()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, state);

        // The provider now reports a window resetting later than the one that fired, which
        // can only mean the old one rolled over.
        ThresholdEvaluation rolled = evaluator.Evaluate(
            [Reading("claude", Session(3d, resetsAt: Start.AddHours(7)))], settings, crossed.State);

        Assert.Equal("Session limit reset", Assert.Single(rolled.Notifications).Title);
        Assert.Empty(rolled.State.Fired);
    }

    [Fact]
    public void AWeeklyWindowUsesTheWeeklyThreshold()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Weekly(10d))], settings, ThresholdState.Initial).State;

        ThresholdEvaluation between = evaluator.Evaluate([Reading("claude", Weekly(85d))], settings, state);
        Assert.Empty(between.Notifications);
        Assert.Empty(between.State.Fired);

        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Weekly(91d))], settings, between.State);
        Assert.Equal("Weekly usage reached 90%", Assert.Single(crossed.Notifications).Title);
        Assert.Equal(90, Assert.Single(crossed.State.Fired).Threshold);
    }

    [Fact]
    public void ThresholdsComeFromTheWindowLengthNotTheSlotName()
    {
        AltimSettings settings = AltimSettings.Default;

        Assert.Equal(80, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromMinutes(299), null)));
        Assert.Equal(90, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromMinutes(10_079), null)));
        Assert.Equal(90, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromMinutes(43_200), null)));
        Assert.Equal(80, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromMinutes(1_440), null)));
        Assert.Equal(80, ThresholdEvaluator.ThresholdFor(settings, null));
    }

    [Fact]
    public void AnUnreportedPercentageNeverFires()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(null))], settings, ThresholdState.Initial).State;
        Assert.Empty(state.Fired);

        ThresholdEvaluation result = evaluator.Evaluate([Reading("claude", Session(null))], settings, state);
        Assert.Empty(result.Notifications);

        // The timestamp defect is unavailable, not a crossing.
        ThresholdEvaluation defect = evaluator.Evaluate([Reading("claude", Session(1_789_515_600d))], settings, state);
        Assert.Empty(defect.Notifications);
        Assert.Empty(defect.State.Fired);
    }

    [Fact]
    public void AFailedReadingFiresNothingAndKeepsWhatFiredBefore()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, state);
        Assert.Single(crossed.Notifications);

        var failed = new ProviderUsage("claude", ProviderStatus.Error, [], null, Start, "Unable to retrieve usage");
        ThresholdEvaluation result = evaluator.Evaluate([failed], settings, crossed.State);

        Assert.Empty(result.Notifications);
        Assert.Single(result.State.Fired);
    }

    [Fact]
    public void OneProviderCrossingDoesNotSilenceAnother()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate(
            [Reading("claude", Session(10d)), Reading("codex", Session(10d))],
            settings,
            ThresholdState.Initial).State;

        ThresholdEvaluation result = evaluator.Evaluate(
            [Reading("claude", Session(81d)), Reading("codex", Session(95d))],
            settings,
            state);

        Assert.Equal(2, result.Notifications.Count);
        Assert.Equal(2, result.State.Fired.Count);
        Assert.Contains(result.Notifications, n => n.ProviderId == "claude");
        Assert.Contains(result.Notifications, n => n.ProviderId == "codex");
    }

    [Fact]
    public void TurningNotificationsOffStillTracksStateSoTurningThemOnIsNotAnAlertStorm()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings muted = AltimSettings.Default with { NotificationsEnabled = false };

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], muted, ThresholdState.Initial).State;
        ThresholdEvaluation whileMuted = evaluator.Evaluate([Reading("claude", Session(90d))], muted, state);

        Assert.Empty(whileMuted.Notifications);
        Assert.Single(whileMuted.State.Fired);

        ThresholdEvaluation afterUnmuting = evaluator.Evaluate(
            [Reading("claude", Session(90d))], AltimSettings.Default, whileMuted.State);

        Assert.Empty(afterUnmuting.Notifications);
    }

    [Fact]
    public void ResetNotificationsCanBeTurnedOffOnTheirOwn()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default with { NotifyOnWindowReset = false };

        ThresholdState state = evaluator.Evaluate([Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, state);
        Assert.Single(crossed.Notifications);

        time.Advance(TimeSpan.FromHours(3));
        ThresholdEvaluation reset = evaluator.Evaluate(
            [Reading("claude", Session(2d, resetsAt: Start.AddHours(7)))], settings, crossed.State);

        Assert.Empty(reset.Notifications);
        Assert.Empty(reset.State.Fired);
    }

    [Fact]
    public void AMetricWithNoResetInstantSaysNothingAboutWhenItResets()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;
        var metric = new UsageMetric(
            "five_hour", "Session", 85d, new LimitWindow(TimeSpan.FromMinutes(300), null), MetricConfidence.BestEffort);

        ThresholdState state = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", metric)], settings, state);

        Assert.Equal("Now at 85%.", Assert.Single(crossed.Notifications).Body);
        Assert.Null(Assert.Single(crossed.State.Fired).WindowResetsAt);

        // With no reset instant there is nothing to expire, so it stays quiet rather than
        // guessing a new window has begun.
        time.Advance(TimeSpan.FromDays(2));
        ThresholdEvaluation later = evaluator.Evaluate([Reading("claude", metric)], settings, crossed.State);
        Assert.Empty(later.Notifications);
        Assert.Single(later.State.Fired);
    }

    private static ProviderUsage Reading(string providerId, UsageMetric metric) =>
        new(providerId, ProviderStatus.Active, [metric], null, Start, null);

    private static UsageMetric Session(double? percent, DateTimeOffset? resetsAt = null) =>
        new("five_hour", "Session", percent,
            new LimitWindow(TimeSpan.FromMinutes(300), resetsAt ?? Start.AddHours(2)), MetricConfidence.Documented);

    private static UsageMetric Weekly(double? percent, DateTimeOffset? resetsAt = null) =>
        new("seven_day", "Weekly", percent,
            new LimitWindow(TimeSpan.FromMinutes(10_080), resetsAt ?? Start.AddDays(3)), MetricConfidence.Documented);
}
