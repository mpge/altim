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
        Assert.Equal("Usage has reset.", notification.Title);
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
        Assert.Equal("Usage has reset.", next.Notifications[0].Title);
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

        Assert.Equal("Usage has reset.", Assert.Single(rolled.Notifications).Title);
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

    [Fact]
    public void AStaleReadingFiresTheResetOnceAndThenGoesQuiet()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);
        Assert.Single(crossed.Notifications);

        // The window has ended and the provider keeps handing back the same snapshot,
        // because the status line file is only rewritten when the tool next runs.
        time.Advance(TimeSpan.FromHours(3));
        ThresholdEvaluation first = evaluator.Evaluate([Reading("claude", Session(85d))], settings, crossed.State);

        Notification reset = Assert.Single(first.Notifications);
        Assert.Equal("Usage has reset.", reset.Title);
        Assert.Empty(first.State.Fired);

        // The same stale reading arrives on every poll for as long as the tool stays
        // closed. Every one of them after the first has to be silent.
        ThresholdEvaluation second = evaluator.Evaluate([Reading("claude", Session(85d))], settings, first.State);
        Assert.Empty(second.Notifications);
        Assert.Empty(second.State.Fired);

        ThresholdEvaluation third = evaluator.Evaluate([Reading("claude", Session(85d))], settings, second.State);
        Assert.Empty(third.Notifications);
    }

    [Fact]
    public void AStaleReadingNeverArmsAThreshold()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;

        time.Advance(TimeSpan.FromHours(3));
        ThresholdEvaluation result = evaluator.Evaluate([Reading("claude", Session(92d))], settings, seeded);

        Assert.Empty(result.Notifications);
        Assert.Empty(result.State.Fired);
    }

    [Fact]
    public void ForwardDriftInTheResetInstantIsNotARollover()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);
        Assert.Single(crossed.Notifications);

        // Derived from a relative "resets in 2h 14m", so it drifts forward a little on
        // every poll. That is the same window, not a new one.
        time.Advance(TimeSpan.FromSeconds(30));
        ThresholdEvaluation drifted = evaluator.Evaluate(
            [Reading("claude", Session(86d, resetsAt: Start.AddHours(2).AddSeconds(30)))], settings, crossed.State);

        Assert.Empty(drifted.Notifications);
        Assert.Equal<DateTimeOffset?>(Start.AddHours(2), Assert.Single(drifted.State.Fired).WindowResetsAt);
    }

    [Fact]
    public void AResetInstantAtLeastTwoMinutesLaterIsARollover()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);

        ThresholdEvaluation rolled = evaluator.Evaluate(
            [Reading("claude", Session(4d, resetsAt: Start.AddHours(2).AddMinutes(2)))], settings, crossed.State);

        Assert.Equal("Usage has reset.", Assert.Single(rolled.Notifications).Title);
        Assert.Empty(rolled.State.Fired);
    }

    [Fact]
    public void RolloverIsComparedAtSecondGranularity()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);
        Assert.Single(crossed.Notifications);

        // Sub-second movement cannot survive the unix-seconds column it is persisted in,
        // so it can never mean a new window.
        ThresholdEvaluation sameSecond = evaluator.Evaluate(
            [Reading("claude", Session(86d, resetsAt: Start.AddHours(2).AddMilliseconds(900)))], settings, crossed.State);
        Assert.Empty(sameSecond.Notifications);

        // Two whole minutes further on is, so the entry expires and the new window is
        // free to fire its own threshold.
        ThresholdEvaluation rolled = evaluator.Evaluate(
            [Reading("claude", Session(86d, resetsAt: Start.AddHours(2).AddSeconds(120).AddMilliseconds(900)))],
            settings,
            crossed.State);
        Assert.Equal(2, rolled.Notifications.Count);
        Assert.Equal("Usage has reset.", rolled.Notifications[0].Title);
    }

    [Fact]
    public void AnEntryWithNoResetInstantAdoptsTheOneTheMetricStartsReporting()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;
        var unknownReset = new UsageMetric(
            "five_hour", "Session", 85d, new LimitWindow(TimeSpan.FromMinutes(300), null), MetricConfidence.BestEffort);

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", unknownReset)], settings, seeded);
        Assert.Single(crossed.Notifications);
        Assert.Null(Assert.Single(crossed.State.Fired).WindowResetsAt);

        // The provider now reports a reset instant for the same metric. Adopting it is
        // what lets this entry expire; without one it would mute the metric forever.
        ThresholdEvaluation adopted = evaluator.Evaluate(
            [Reading("claude", Session(86d, resetsAt: Start.AddHours(4)))], settings, crossed.State);
        Assert.Empty(adopted.Notifications);
        Assert.Equal<DateTimeOffset?>(Start.AddHours(4), Assert.Single(adopted.State.Fired).WindowResetsAt);

        time.Advance(TimeSpan.FromHours(5));
        ThresholdEvaluation next = evaluator.Evaluate(
            [Reading("claude", Session(87d, resetsAt: Start.AddHours(9)))], settings, adopted.State);

        Assert.Equal(2, next.Notifications.Count);
        Assert.Equal("Usage has reset.", next.Notifications[0].Title);
        Assert.Equal("Session usage reached 80%", next.Notifications[1].Title);
    }

    [Fact]
    public void EveryProviderGetsItsOwnFirstEvaluationSilence()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState state = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;

        // codex reports for the first time here, already over its threshold. A provider's
        // first reading seeds state and says nothing, whichever evaluation it arrives in.
        ThresholdEvaluation firstSeen = evaluator.Evaluate(
            [Reading("claude", Session(12d)), Reading("codex", Session(95d))], settings, state);

        Assert.Empty(firstSeen.Notifications);
        Assert.Equal("codex", Assert.Single(firstSeen.State.Fired).ProviderId);

        // claude has been evaluated before, so it is not silenced by codex arriving.
        ThresholdEvaluation later = evaluator.Evaluate(
            [Reading("claude", Session(81d)), Reading("codex", Session(96d))], settings, firstSeen.State);

        Assert.Equal("claude", Assert.Single(later.Notifications).ProviderId);
    }

    [Fact]
    public void AProviderWhoseFirstReadingFailedIsStillOnItsFirstReadingAfterwards()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;
        var failed = new ProviderUsage(
            "codex", ProviderStatus.Error, [], null, null, ProviderUsage.UnavailableDetail);

        ThresholdState state = evaluator.Evaluate([failed], settings, ThresholdState.Initial).State;
        Assert.False(state.HasEvaluatedProvider("codex"));

        // A reading that failed carried no usage, so the one that follows it is still the
        // first thing this provider has said about usage: it seeds and stays quiet.
        ThresholdEvaluation firstReal = evaluator.Evaluate([Reading("codex", Session(95d))], settings, state);
        Assert.Empty(firstReal.Notifications);
        Assert.True(firstReal.State.HasEvaluatedProvider("codex"));
    }

    [Fact]
    public void LoweringTheThresholdBelowTheCurrentValueFiresOnce()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);
        Assert.Equal("Session usage reached 80%", Assert.Single(crossed.Notifications).Title);

        // The documented choice: a threshold the user has just lowered under the current
        // reading fires once, because a warning they have asked for that never arrives
        // reads as broken. It then behaves like any other fired threshold.
        AltimSettings lowered = settings with { SessionThresholdPercent = 70 };
        ThresholdEvaluation refired = evaluator.Evaluate([Reading("claude", Session(85d))], lowered, crossed.State);
        Assert.Equal("Session usage reached 70%", Assert.Single(refired.Notifications).Title);

        ThresholdEvaluation quiet = evaluator.Evaluate([Reading("claude", Session(86d))], lowered, refired.State);
        Assert.Empty(quiet.Notifications);
    }

    [Fact]
    public void AFailedReadingStillLetsAnEndedWindowExpire()
    {
        var time = new TestTimeProvider(Start);
        var evaluator = new ThresholdEvaluator(time);
        AltimSettings settings = AltimSettings.Default;

        ThresholdState seeded = evaluator.Evaluate(
            [Reading("claude", Session(10d))], settings, ThresholdState.Initial).State;
        ThresholdEvaluation crossed = evaluator.Evaluate([Reading("claude", Session(85d))], settings, seeded);
        Assert.Single(crossed.Notifications);

        time.Advance(TimeSpan.FromHours(3));
        var failed = new ProviderUsage(
            "claude", ProviderStatus.Error, [], null, null, ProviderUsage.UnavailableDetail);
        ThresholdEvaluation result = evaluator.Evaluate([failed], settings, crossed.State);

        // Nothing fires: a failed reading carries no metric to label a reset with. The
        // entry still goes, because the window it fired in is over either way.
        Assert.Empty(result.Notifications);
        Assert.Empty(result.State.Fired);
    }

    [Fact]
    public void AnUnclassifiedWindowLongerThanADayTakesTheWeeklyThreshold()
    {
        AltimSettings settings = AltimSettings.Default;

        Assert.Equal(90, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromDays(3), null)));
        Assert.Equal(90, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromDays(60), null)));
        Assert.Equal(90, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromDays(28), null)));
        Assert.Equal(80, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromMinutes(45), null)));
        Assert.Equal(80, ThresholdEvaluator.ThresholdFor(settings, new LimitWindow(TimeSpan.FromHours(6), null)));
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
