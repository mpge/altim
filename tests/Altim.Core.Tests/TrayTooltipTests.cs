using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The tray's hover text, which is the tray's whole enforcement of the rule that unknown and
/// zero are different.
/// </summary>
/// <remarks>
/// <para>
/// It had no tests at all until this file. It is a pure function over readings, it decides
/// what four separate states are called, and it is the one surface that has to fit all of
/// that into 127 characters, which is exactly where the temptation to round a null down to a
/// zero lives: "not reported" is twelve characters and "0%" is two.
/// </para>
/// <para>
/// The readings here are deliberately awkward: a metric with no percentage, a reading with no
/// metric at all, a reading that failed while still carrying figures, a provider that is not
/// on this machine, and a set too long to show.
/// </para>
/// </remarks>
public sealed class TrayTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A metric the provider did not report says so. This is rule 4 on the smallest surface
    /// in the product, and the failure it refuses is a tooltip reading "0%" for a figure
    /// nobody has.
    /// </summary>
    [Fact]
    public void AMetricWithNoPercentageReadsAsNotReported()
    {
        string tooltip = TrayTooltip.Build(
            [Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", null)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code not reported", tooltip);
        Assert.DoesNotContain("%", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("0", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading that carried no metric at all is the same answer. Nothing was reported, so
    /// nothing is reported, and a provider with an empty metric list is not a provider at
    /// zero.
    /// </summary>
    [Fact]
    public void AReadingWithNoMetricsReadsAsNotReported()
    {
        string tooltip = TrayTooltip.Build(
            [Usage("codex", ProviderStatus.Idle, [])],
            DisplayName);

        Assert.Equal("Altim\nCodex not reported", tooltip);
        Assert.DoesNotContain("%", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A figure Altim will not show is not a figure. The known provider defect that returns a
    /// Unix timestamp in the percentage field arrives here as an enormous number, and the
    /// tooltip must say it has nothing rather than print it or floor it.
    /// </summary>
    [Fact]
    public void AnImplausiblePercentageReadsAsNotReportedRatherThanAsZero()
    {
        string tooltip = TrayTooltip.Build(
            [Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 1_789_000_000d)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code not reported", tooltip);
        Assert.DoesNotContain("178", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading that failed says it is unavailable, and says nothing else. The reading still
    /// carries the figures from before it broke, and a tooltip that showed them would be
    /// presenting a stale number as a current one.
    /// </summary>
    [Fact]
    public void AFailedReadingReadsAsUnavailableAndCarriesNoFigure()
    {
        string tooltip = TrayTooltip.Build(
            [Usage(
                "claude",
                ProviderStatus.Error,
                [Metric("five_hour", "Session", 62d)],
                detail: ProviderUsage.UnavailableDetail)],
            DisplayName);

        Assert.Equal("Altim\nClaude Code unavailable", tooltip);
        Assert.DoesNotContain("62", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("%", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure is never hidden, however short of room the tooltip is. It keeps its line
    /// beside a provider that is working.
    /// </summary>
    [Fact]
    public void AFailedProviderKeepsItsLineBesideAWorkingOne()
    {
        string tooltip = TrayTooltip.Build(
            [
                Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 62d)]),
                Usage("codex", ProviderStatus.Error, [], detail: ProviderUsage.UnavailableDetail),
            ],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Session 62%\nCodex unavailable", tooltip);
    }

    /// <summary>
    /// A provider that is not on this machine has no line. That is the one status with
    /// nothing to report, and the tooltip is where a line about a tool the reader does not
    /// own costs a line about one they do.
    /// </summary>
    [Fact]
    public void AProviderThatIsNotDetectedIsAbsent()
    {
        string tooltip = TrayTooltip.Build(
            [
                Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 62d)]),
                Usage("codex", ProviderStatus.NotDetected, []),
            ],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Session 62%", tooltip);
        Assert.DoesNotContain("Codex", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other status keeps its line, including the one nothing is known about yet. Only
    /// a settled absence is left out.
    /// </summary>
    /// <param name="status">The status to render.</param>
    [Theory]
    [InlineData(ProviderStatus.Unknown)]
    [InlineData(ProviderStatus.Detected)]
    [InlineData(ProviderStatus.Active)]
    [InlineData(ProviderStatus.Idle)]
    public void EveryStatusButASettledAbsenceKeepsItsLine(ProviderStatus status)
    {
        string tooltip = TrayTooltip.Build(
            [Usage("claude", status, [Metric("five_hour", "Session", 62d)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Session 62%", tooltip);
    }

    /// <summary>
    /// Registered, and none of them here. The sentence is the one the panel header uses for
    /// the same machine, so the two cannot end up describing it differently.
    /// </summary>
    [Fact]
    public void NoProviderOnThisMachineIsTheSentenceThePanelUses()
    {
        ProviderUsage[] readings =
        [
            Usage("claude", ProviderStatus.NotDetected, []),
            Usage("codex", ProviderStatus.NotDetected, []),
        ];

        Assert.Equal("Altim\nNo providers detected", TrayTooltip.Build(readings, DisplayName));
        Assert.Equal(
            TrayTooltip.NoProvidersDetected,
            UsageAggregator.DescribeStatus(readings, DisplayName));
    }

    /// <summary>
    /// No readings at all is a different sentence from no providers on this machine: nothing
    /// has been registered, rather than nothing having been found.
    /// </summary>
    [Fact]
    public void NoReadingsAtAllSaysNothingIsConfigured()
    {
        Assert.Equal("Altim\nNo providers configured", TrayTooltip.Build([]));
        Assert.Equal("No providers configured", UsageOverview.Empty.StatusLine);
    }

    /// <summary>
    /// The line carries the metric closest to its limit, which is what the tooltip is for:
    /// one line per provider, and the line says the worst thing that is true.
    /// </summary>
    [Fact]
    public void TheLineCarriesTheMetricClosestToItsLimit()
    {
        string tooltip = TrayTooltip.Build(
            [Usage(
                "claude",
                ProviderStatus.Active,
                [Metric("five_hour", "Session", 31d), Metric("seven_day", "Weekly", 88d)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Weekly 88%", tooltip);
    }

    /// <summary>
    /// A reading a point over a full window is a full window, not 101 per cent. The slack
    /// above 100 absorbs rounding at the provider and is never shown.
    /// </summary>
    /// <param name="raw">The percentage as the provider reported it.</param>
    /// <param name="shown">What the tooltip must print.</param>
    [Theory]
    [InlineData(0d, "0%")]
    [InlineData(62.4d, "62%")]
    [InlineData(99.6d, "100%")]
    [InlineData(100d, "100%")]
    [InlineData(100.6d, "100%")]
    [InlineData(101d, "100%")]
    public void APercentageIsAWholeNumberAndNeverAboveAFullWindow(double raw, string shown)
    {
        string tooltip = TrayTooltip.Build(
            [Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", raw)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Session " + shown, tooltip);
    }

    /// <summary>
    /// Zero is a reading. A provider that answered and reported nothing used says zero, and
    /// this is here so that the tests above cannot pass by never printing one.
    /// </summary>
    [Fact]
    public void AReportedZeroIsShownAsZero()
    {
        string tooltip = TrayTooltip.Build(
            [Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 0d)])],
            DisplayName);

        Assert.Equal("Altim\nClaude Code Session 0%", tooltip);
    }

    /// <summary>
    /// A lookup that does not know an id, or answers with nothing, leaves the id standing. A
    /// line without a subject would be worse than a line with an unfamiliar one.
    /// </summary>
    [Fact]
    public void AnUnknownProviderKeepsItsIdAsItsName()
    {
        ProviderUsage[] readings =
            [Usage("mystery", ProviderStatus.Active, [Metric("k", "Session", 5d)])];

        Assert.Equal("Altim\nmystery Session 5%", TrayTooltip.Build(readings, DisplayName));
        Assert.Equal("Altim\nmystery Session 5%", TrayTooltip.Build(readings));
        Assert.Equal("Altim\nmystery Session 5%", TrayTooltip.Build(readings, _ => "  "));
    }

    /// <summary>
    /// <b>Truncation never lands inside a number.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shell shows the first <see cref="TrayTooltip.Limit"/> characters and nothing else,
    /// so a tooltip longer than that is cut whether Altim chooses where or not. The cut this
    /// replaced took the first 127 characters of the finished string: on the fixture below it
    /// lands between the second and third digits of a 100, and the reader is shown a provider
    /// at "10%" that is actually at its limit, with nothing on screen to say the line was
    /// shortened. That is rule 1, and running out of room is not an exception to it.
    /// </para>
    /// <para>
    /// The expected lines are not spelled out here. Each one is read back from the builder
    /// itself, one provider at a time, so this asserts how the lines are <em>assembled</em>
    /// without restating how they are <em>written</em> - a copy of the format here would
    /// agree with a wrong implementation as readily as with a right one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TruncationNeverLandsInsideANumber()
    {
        // Four lines of thirty characters under a five character heading. The fourth line's
        // "100%" straddles the limit, which is asserted below rather than assumed. The fifth
        // provider's line is short enough to fit in the room the fourth one leaves, so a
        // builder that skipped the line it could not fit and carried on would put this one
        // where the fourth belongs, and the prefix assertion below is what catches that.
        ProviderUsage[] readings =
        [
            Overflowing("alpha"),
            Overflowing("bravo"),
            Overflowing("gamma"),
            Overflowing("delta"),
            Usage("echo", ProviderStatus.Active, [Metric("day", "Day", 5d)]),
        ];

        string[] built =
            [TrayTooltip.Heading, .. readings.Select(reading => LineFor(reading))];
        string whole = string.Join('\n', built);

        Assert.True(
            whole.Length > TrayTooltip.Limit,
            $"The fixture is only {whole.Length} characters, so nothing is being truncated.");
        Assert.True(
            char.IsAsciiDigit(whole[TrayTooltip.Limit - 1]) && char.IsAsciiDigit(whole[TrayTooltip.Limit]),
            $"A cut at {TrayTooltip.Limit} no longer falls inside a number, so this fixture "
                + $"does not exercise what it claims: ...{whole[(TrayTooltip.Limit - 6)..]}");

        string tooltip = TrayTooltip.Build(readings, DisplayName);

        Assert.True(
            tooltip.Length <= TrayTooltip.Limit,
            $"The tooltip is {tooltip.Length} characters, which the shell would cut itself.");

        // Every line shown is a line that was built, whole. A line cut anywhere - inside a
        // number, inside a name, anywhere - is not one of these.
        foreach (string line in tooltip.Split('\n'))
        {
            Assert.Contains(line, built, StringComparer.Ordinal);
        }

        // What is shown is a prefix of what was built, so the providers keep their order and
        // one has been dropped rather than mangled.
        Assert.StartsWith(tooltip, whole, StringComparison.Ordinal);
        Assert.Contains("gamma", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("delta", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("echo", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider whose own line will not fit takes nothing else down with it beyond the
    /// lines after it, and the heading always survives.
    /// </summary>
    [Fact]
    public void TheHeadingSurvivesALineThatCannotFitAtAll()
    {
        string tooltip = TrayTooltip.Build(
            [Usage("huge", ProviderStatus.Active, [Metric("k", new string('x', 200), 5d)])],
            DisplayName);

        Assert.Equal(TrayTooltip.Heading, tooltip);
    }

    /// <summary>A reading of one provider, thirty characters wide once it is written out.</summary>
    private static ProviderUsage Overflowing(string id) =>
        Usage(id, ProviderStatus.Active, [Metric("seven_day", "Weekly", 100d)]);

    /// <summary>
    /// One provider's line, as the builder itself writes it. Read back from a tooltip of that
    /// provider alone, which is always short enough to fit.
    /// </summary>
    private static string LineFor(ProviderUsage reading) =>
        TrayTooltip.Build([reading], DisplayName).Split('\n')[1];

    private static string DisplayName(string providerId) => providerId switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "alpha" => "Coding agent alpha",
        "bravo" => "Coding agent bravo",
        "gamma" => "Coding agent gamma",
        "delta" => "Coding agent delta",
        "echo" => "Coding agent echo",
        _ => providerId,
    };

    private static ProviderUsage Usage(
        string id,
        ProviderStatus status,
        IReadOnlyList<UsageMetric> metrics,
        string? detail = null) =>
        new(id, status, metrics, null, Now, detail);

    private static UsageMetric Metric(string key, string label, double? percent) =>
        new(key, label, percent, new LimitWindow(TimeSpan.FromMinutes(300), Now.AddHours(2)), MetricConfidence.Documented);
}
