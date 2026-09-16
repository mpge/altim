using Altim.Providers.Claude.Usage;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The headless summary is prose, so the parser is required to give up rather than guess.
/// </summary>
public sealed class ClaudeUsageSummaryParserTests
{
    [Fact]
    public void ReadsTheWindowsItRecognises()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText(
            """
            Usage

            Current session: 53% used (resets 3:00pm)
            Current week (all models): 85% used (resets Sun)
            Current week (Opus): 12% used
            Current week (Sonnet): 40% used
            """);

        Assert.Equal(53d, summary.SessionUsedPercent);
        Assert.Equal(85d, summary.WeeklyUsedPercent);
        Assert.Equal(12d, summary.WeeklyOpusUsedPercent);
        Assert.Equal(40d, summary.WeeklySonnetUsedPercent);
    }

    [Fact]
    public void AnOpusLineIsNeverMistakenForTheAllModelsWeekly()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText("Current week (Opus): 12% used");

        Assert.Equal(12d, summary.WeeklyOpusUsedPercent);
        Assert.Null(summary.WeeklyUsedPercent);
    }

    [Fact]
    public void UnrecognisedWordingYieldsUnavailableRatherThanAWrongNumber()
    {
        // A loose parser would take 97% from this and present a cache hit rate as a quota.
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText(
            """
            Prompt cache hit rate: 97%
            Tokens this month: 4,200,000
            """);

        Assert.False(summary.HasAny);
        Assert.Null(summary.SessionUsedPercent);
        Assert.Null(summary.WeeklyUsedPercent);
    }

    [Fact]
    public void APatternOnlyMatchesAtTheStartOfALine()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText(
            "The docs say Current session: 53% used, but this is not a reading.");

        Assert.False(summary.HasAny);
    }

    [Fact]
    public void ReadsFractionalPercentages()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText("Current session: 53.5% used");

        Assert.Equal(53.5d, summary.SessionUsedPercent);
    }

    [Fact]
    public void AnImplausiblePercentageIsDiscarded()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseText("Current session: 900% used");

        Assert.Null(summary.SessionUsedPercent);
    }

    [Fact]
    public void ReadsTheResultFieldOutOfTheHeadlessEnvelope()
    {
        ClaudeUsageSummary summary = ClaudeUsageSummaryParser.ParseEnvelope(
            """
            {
              "type": "result",
              "num_turns": 0,
              "total_cost_usd": 0,
              "result": "Current session: 53% used\nCurrent week (all models): 85% used"
            }
            """);

        Assert.Equal(53d, summary.SessionUsedPercent);
        Assert.Equal(85d, summary.WeeklyUsedPercent);
    }

    [Fact]
    public void AnEnvelopeWithoutAResultYieldsNothing() =>
        Assert.False(ClaudeUsageSummaryParser.ParseEnvelope("""{"type":"result","is_error":true}""").HasAny);

    [Fact]
    public void MalformedEnvelopeJsonYieldsNothing() =>
        Assert.False(ClaudeUsageSummaryParser.ParseEnvelope("not json at all").HasAny);

    [Fact]
    public void AnEmptySummaryYieldsNothing() =>
        Assert.False(ClaudeUsageSummaryParser.ParseText(string.Empty).HasAny);
}
