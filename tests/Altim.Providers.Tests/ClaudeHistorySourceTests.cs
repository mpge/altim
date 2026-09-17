using System.Globalization;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Claude;
using Altim.Providers.Claude.Transcripts;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Daily totals read back out of transcripts, and the two distinctions the usage map
/// depends on: a day nothing is known about is <b>absent</b>, and a day is the user's
/// <b>local</b> calendar day.
/// </summary>
/// <remarks>
/// Every fixture here is synthetic and written at run time by <see cref="TempWorkspace"/>.
/// A real transcript carries prompts, source code, command output and working directories,
/// and is never copied into this repository.
/// </remarks>
public sealed class ClaudeHistorySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One synthetic <c>assistant</c> line, stamped at a chosen instant or at none at all.
    /// </summary>
    /// <remarks>
    /// It carries a working directory and a sentence of message content on purpose: both
    /// must be gone by the time anything reaches a day bucket.
    /// </remarks>
    private static string Assistant(
        string messageId,
        DateTimeOffset? at,
        long output = 100,
        long input = 10,
        long cacheRead = 1500,
        long cache5m = 200,
        long cache1h = 800,
        string model = "claude-opus-4-5-20260101",
        string sessionId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000",
        bool sidechain = false)
    {
        string timestamp = at is { } instant
            ? ",\"timestamp\":\"" + instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\""
            : string.Empty;

        return "{\"type\":\"assistant\""
            + ",\"isSidechain\":" + (sidechain ? "true" : "false")
            + ",\"sessionId\":\"" + sessionId + "\""
            + ",\"requestId\":\"req_" + messageId + "\""
            + timestamp
            + ",\"cwd\":\"C:\\\\work\\\\private-project\""
            + ",\"message\":{\"id\":\"" + messageId + "\""
            + ",\"model\":\"" + model + "\""
            + ",\"content\":[{\"type\":\"text\",\"text\":\"a sentence the reader must not carry\"}]"
            + ",\"usage\":{\"input_tokens\":" + N(input)
            + ",\"output_tokens\":" + N(output)
            + ",\"cache_read_input_tokens\":" + N(cacheRead)
            + ",\"cache_creation\":{\"ephemeral_5m_input_tokens\":" + N(cache5m)
            + ",\"ephemeral_1h_input_tokens\":" + N(cache1h) + "}}}}";
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The instant at which a given wall-clock time occurred in the user's zone.</summary>
    private static DateTimeOffset LocalInstant(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset LocalNoon(int year, int month, int day) =>
        LocalInstant(year, month, day, 12, 0);

    private static ClaudeUsageProvider CreateProvider(TempWorkspace workspace, FakeCliRunner? runner = null) =>
        new(
            ClaudeOptions.Default,
            runner ?? new FakeCliRunner { CommandExists = false },
            new FakeProcessMonitor(),
            [workspace.Root],
            new FixedTimeProvider(Now));

    [Fact]
    public void LinesOnTwoDaysLandInTwoBuckets()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 14), output: 100),
            Assistant("msg_b", LocalNoon(2026, 9, 16), output: 250));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(2, history.ByDay.Count);
        Assert.Equal(100L, history.ByDay[new DateOnly(2026, 9, 14)].Output);
        Assert.Equal(250L, history.ByDay[new DateOnly(2026, 9, 16)].Output);
        Assert.Equal(350L, history.Totals.Output);
    }

    [Fact]
    public void ADayIsTheUsersLocalCalendarDayNotTheUtcOne()
    {
        using var workspace = new TempWorkspace();

        // Half an hour after local midnight and half an hour before the next one. On any
        // machine that is not itself on UTC, at least one of these two instants falls on a
        // different UTC calendar day from the local one, so a reader that keyed on the UTC
        // date would split a single local day in two.
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_early", LocalInstant(2026, 9, 15, 0, 30), output: 10),
            Assistant("msg_late", LocalInstant(2026, 9, 15, 23, 30), output: 20));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        _ = Assert.Single(history.ByDay);
        Assert.Equal(30L, history.ByDay[new DateOnly(2026, 9, 15)].Output);
    }

    [Fact]
    public void ALineWithNoTimestampCountsTowardsTheTotalsButBelongsToNoDay()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_dated", LocalNoon(2026, 9, 15), output: 100),
            Assistant("msg_undated", at: null, output: 40));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        // It happened; Altim just cannot say when. It is not silently moved to today.
        Assert.Equal(140L, history.Totals.Output);
        Assert.Equal(2, history.Totals.MessageCount);

        _ = Assert.Single(history.ByDay);
        Assert.Equal(100L, history.ByDay[new DateOnly(2026, 9, 15)].Output);
    }

    [Fact]
    public void DeduplicationOnMessageIdentityStillHoldsAcrossDays()
    {
        using var workspace = new TempWorkspace();

        // The same message restated, with the restatement stamped two days later. Counting
        // it twice would invent a day's worth of usage that never happened.
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_same", LocalNoon(2026, 9, 14), output: 500),
            Assistant("msg_same", LocalNoon(2026, 9, 16), output: 500));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        _ = Assert.Single(history.ByDay);
        Assert.Equal(500L, history.ByDay[new DateOnly(2026, 9, 14)].Output);
        Assert.Equal(1, history.DuplicateMessagesDropped);
    }

    [Fact]
    public void SubagentTranscriptsContributeToTheirDay()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_main", LocalNoon(2026, 9, 15), output: 100));
        _ = workspace.WriteLines(
            "projects/slug/session/subagents/agent-1.jsonl",
            Assistant(
                "msg_sub",
                LocalNoon(2026, 9, 15),
                output: 4000,
                sessionId: "0199bbbb-cccc-dddd-eeee-ffff00001111",
                sidechain: true));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().Scan([workspace.Root], Now);

        Assert.Equal(4100L, history.ByDay[new DateOnly(2026, 9, 15)].Output);
        Assert.Equal(1, history.SubagentFilesScanned);
    }

    [Fact]
    public void SyntheticModelEntriesReachNoDay()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_real", LocalNoon(2026, 9, 15), output: 100),
            Assistant("msg_fake", LocalNoon(2026, 9, 15), output: 999999, model: "<synthetic>"));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        _ = Assert.Single(history.ByDay);
        Assert.Equal(100L, history.ByDay[new DateOnly(2026, 9, 15)].Output);
        Assert.Equal(1, history.SyntheticMessagesExcluded);
    }

    [Fact]
    public void ADayKeepsTheTwoCacheCreationTiersApart()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), cache5m: 200, cache1h: 800));

        ClaudeTokenBucket day = new ClaudeTranscriptScanner().ScanFile(path).ByDay[new DateOnly(2026, 9, 15)];

        // The one-hour tier bills at twice the five-minute rate; a merged figure cannot be
        // priced. The split survives the day bucket too.
        Assert.Equal(200L, day.CacheCreation5m);
        Assert.Equal(800L, day.CacheCreation1h);
        Assert.Equal(1000L, day.CacheCreationTotal);
    }

    [Fact]
    public void TheProviderIsAHistorySource() =>
        Assert.True(typeof(IUsageHistorySource).IsAssignableFrom(typeof(ClaudeUsageProvider)));

    [Fact]
    public async Task HistoryIsReportedAsBackfilledDaysOldestFirst()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_b", LocalNoon(2026, 9, 16), output: 250),
            Assistant("msg_a", LocalNoon(2026, 9, 14), output: 100));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        IReadOnlyList<UsageDay> days = await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 17), Ct);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 14), days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 16), days[1].Day);
        Assert.All(days, static d => Assert.Equal(UsageDaySource.Backfilled, d.Source));
        Assert.All(days, static d => Assert.Equal(ClaudeProviderInfo.Id, d.ProviderId));

        Assert.Equal(10L, days[0].Tokens!.Input);
        Assert.Equal(100L, days[0].Tokens!.Output);
        Assert.Equal(1500L, days[0].Tokens!.CacheRead);
        Assert.Equal(1000L, days[0].Tokens!.CacheWrite);
    }

    [Fact]
    public async Task ADayTheScanCannotAccountForIsAbsentRatherThanZero()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 14), output: 100),
            Assistant("msg_b", LocalNoon(2026, 9, 16), output: 250));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        IReadOnlyList<UsageDay> days = await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 17), Ct);

        // Absent renders as unknown; a row of zeroes renders as a day that used nothing.
        // Conflating the two is the one thing this feature must not do.
        Assert.DoesNotContain(days, static d => d.Day == new DateOnly(2026, 9, 15));
        Assert.Equal(2, days.Count);
    }

    [Fact]
    public async Task TranscriptsCarryTokensNotQuotaSoThePeakIsNullRatherThanZero()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), output: 100));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16), Ct));

        Assert.Null(day.PeakPercent);
    }

    [Fact]
    public async Task DaysOutsideTheRequestedRangeAreNotReturned()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_before", LocalNoon(2026, 9, 12), output: 10),
            Assistant("msg_inside", LocalNoon(2026, 9, 15), output: 20),
            Assistant("msg_after", LocalNoon(2026, 9, 17), output: 30));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16), Ct));

        Assert.Equal(new DateOnly(2026, 9, 15), day.Day);
        Assert.Equal(20L, day.Tokens!.Output);
    }

    [Fact]
    public async Task AnInvertedRangeReadsNothing()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), output: 100));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        Assert.Empty(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 14), Ct));
    }

    [Fact]
    public async Task AnUndatedLineDoesNotInventARowForToday()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines("projects/slug/session.jsonl", Assistant("msg_undated", at: null, output: 40));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Empty(await provider.GetHistoryAsync(today.AddDays(-30), today, Ct));
    }

    [Fact]
    public async Task ReadingHistoryStartsNoProcessAndReachesNoNetwork()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), output: 100));

        // The CLI is installed, so the expensive listing and the headless usage summary are
        // both available. Backfill is a file read and must take neither.
        var runner = new FakeCliRunner { CommandExists = true };
        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);

        _ = await provider.GetHistoryAsync(new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16), Ct);

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ReadingHistoryTwiceDoesNotDoubleTheDay()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), output: 100));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        var from = new DateOnly(2026, 9, 14);
        var to = new DateOnly(2026, 9, 16);

        UsageDay first = Assert.Single(await provider.GetHistoryAsync(from, to, Ct));

        // The scan is incremental: a second read sees no new bytes, and the day it already
        // counted has to stand rather than either doubling or disappearing.
        UsageDay again = Assert.Single(await provider.GetHistoryAsync(from, to, Ct));

        Assert.Equal(100L, first.Tokens!.Output);
        Assert.Equal(100L, again.Tokens!.Output);
    }

    [Fact]
    public async Task AReadingTakenAfterARefreshStillSeesTheDay()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_a", LocalNoon(2026, 9, 15), output: 100));

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        // The ordinary refresh consumes the same bytes. Backfill must not depend on being
        // the first reader of a file.
        _ = await provider.GetUsageAsync(Ct);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 16), Ct));

        Assert.Equal(100L, day.Tokens!.Output);
    }

    [Fact]
    public async Task NoTranscriptsAtAllReadAsUnknownRatherThanEmptyDays()
    {
        using var workspace = new TempWorkspace();

        using ClaudeUsageProvider provider = CreateProvider(workspace);

        Assert.Empty(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 17), Ct));
    }
}
