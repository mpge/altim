using System.Globalization;
using Altim.Core.Models;
using Altim.Providers.Gemini;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Daily totals read back out of Gemini CLI session files, and the two distinctions the
/// usage map depends on: a day nothing is known about is <b>absent</b>, and a day is the
/// user's <b>local</b> calendar day.
/// </summary>
/// <remarks>
/// <para>
/// This provider implements <c>IUsageHistorySource</c> because Gemini genuinely records a
/// timestamp on every model turn it counts, not because implementing it would be tidy. The
/// day a turn lands on is the day its own line says it happened, and a day no line mentions
/// produces no row at all.
/// </para>
/// <para>
/// Every fixture here is synthetic and written at run time by <see cref="TempWorkspace"/>.
/// </para>
/// </remarks>
public sealed class GeminiHistorySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private const string SessionId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000";
    private const string ChatsDirectory = "tmp/my-secret-project/chats";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The instant at which a given wall-clock time occurred in the user's zone.</summary>
    private static DateTimeOffset LocalInstant(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset LocalNoon(int year, int month, int day) =>
        LocalInstant(year, month, day, 12, 0);

    private static string Metadata(DateTimeOffset startedAt) =>
        "{\"sessionId\":\"" + SessionId + "\""
        + ",\"projectHash\":\"my-secret-project\""
        + ",\"startTime\":\"" + Stamp(startedAt) + "\""
        + ",\"directories\":[\"C:\\\\work\\\\private-project\"]}";

    private static string ModelTurn(
        string messageId,
        DateTimeOffset? at,
        long input = 1000,
        long output = 200,
        long cached = 400,
        long thoughts = 50)
    {
        string timestamp = at is { } instant ? ",\"timestamp\":\"" + Stamp(instant) + "\"" : string.Empty;

        return "{\"id\":\"" + messageId + "\""
            + timestamp
            + ",\"type\":\"gemini\",\"model\":\"gemini-3-pro\""
            + ",\"content\":[{\"text\":\"a sentence the reader must not carry\"}]"
            + ",\"tokens\":{\"input\":" + N(input)
            + ",\"output\":" + N(output)
            + ",\"cached\":" + N(cached)
            + ",\"thoughts\":" + N(thoughts)
            + ",\"tool\":0"
            + ",\"total\":" + N(input + output + thoughts) + "}}";
    }

    private static GeminiUsageProvider CreateProvider(TempWorkspace workspace) =>
        new(
            GeminiOptions.Default with { SessionWindow = TimeSpan.FromDays(365) },
            new FakeCliRunner { CommandExists = false },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

    private static TempWorkspace CreateHome() => new();

    [Fact]
    public async Task TurnsOnTwoDaysLandInTwoBuckets()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 15)),
            ModelTurn("m1", LocalNoon(2026, 9, 15)),
            ModelTurn("m2", LocalNoon(2026, 9, 16)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        IReadOnlyList<UsageDay> days = await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 17), Ct);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 15), days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 16), days[1].Day);
        Assert.Equal(600L, days[0].Tokens?.Input);
        Assert.Equal(250L, days[0].Tokens?.Output);
        Assert.Equal(400L, days[0].Tokens?.CacheRead);
    }

    /// <summary>
    /// The rendering rule that may not be traded away: a day with no data is not a day that
    /// used nothing. A gap in the result is what the map draws as unknown.
    /// </summary>
    [Fact]
    public async Task ADayNoTurnMentionsIsAbsentRatherThanZero()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 15)),
            ModelTurn("m1", LocalNoon(2026, 9, 15)),
            ModelTurn("m2", LocalNoon(2026, 9, 17)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        IReadOnlyList<UsageDay> days = await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 17), Ct);

        Assert.Equal(2, days.Count);
        Assert.DoesNotContain(days, static d => d.Day == new DateOnly(2026, 9, 16));
        Assert.DoesNotContain(days, static d => d.Day == new DateOnly(2026, 9, 14));
    }

    /// <summary>
    /// No Gemini session line reports a quota, so no day may carry a percentage. A zero here
    /// would claim the user touched none of their allowance that day.
    /// </summary>
    [Fact]
    public async Task EveryDayHasAnUnknownPeakRatherThanAZeroOne()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 16)),
            ModelTurn("m1", LocalNoon(2026, 9, 16)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 17), Ct));

        Assert.Null(day.PeakPercent);
        Assert.Equal(UsageDaySource.Backfilled, day.Source);
        Assert.Equal(GeminiProviderInfo.Id, day.ProviderId);
    }

    /// <summary>
    /// A turn with no timestamp happened; the file does not say when. It counts towards the
    /// totals and towards no day, because putting it on today would be inventing the date.
    /// </summary>
    [Fact]
    public async Task ATurnWithNoTimestampCountsTowardsTheTotalAndTowardsNoDay()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 16)),
            ModelTurn("m1", at: null));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        Assert.Empty(await provider.GetHistoryAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Ct));

        ProviderUsage usage = await provider.GetUsageAsync(Ct);
        Assert.Equal(600L, usage.Tokens?.Input);
    }

    /// <summary>
    /// A turn is placed on the day the user was living in when it happened, not on its UTC
    /// day. Late evening local time is the following UTC day for most of the world west of
    /// Greenwich, and the reverse east of it.
    /// </summary>
    [Fact]
    public async Task ATurnIsPlacedOnTheUsersLocalCalendarDay()
    {
        using TempWorkspace workspace = CreateHome();
        DateTimeOffset lateEvening = LocalInstant(2026, 9, 16, 23, 30);

        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(lateEvening),
            ModelTurn("m1", lateEvening));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Ct));

        Assert.Equal(new DateOnly(2026, 9, 16), day.Day);
    }

    [Fact]
    public async Task DaysOutsideTheRequestedRangeAreNotReturned()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 10)),
            ModelTurn("m1", LocalNoon(2026, 9, 10)),
            ModelTurn("m2", LocalNoon(2026, 9, 16)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 17), Ct));

        Assert.Equal(new DateOnly(2026, 9, 16), day.Day);
    }

    [Fact]
    public async Task AnInvertedRangeReturnsNothing()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 16)),
            ModelTurn("m1", LocalNoon(2026, 9, 16)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        Assert.Empty(await provider.GetHistoryAsync(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 16), Ct));
    }

    /// <summary>
    /// The backfill and the ordinary refresh share one scanner and one gate, so a day counted
    /// by whichever ran first must not be counted again by the other.
    /// </summary>
    [Fact]
    public async Task ARefreshAndABackfillDoNotCountTheSameTurnTwice()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(LocalNoon(2026, 9, 16)),
            ModelTurn("m1", LocalNoon(2026, 9, 16)));

        using GeminiUsageProvider provider = CreateProvider(workspace);

        _ = await provider.GetUsageAsync(Ct);
        _ = await provider.GetHistoryAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Ct);
        await provider.RefreshAsync(Ct);

        UsageDay day = Assert.Single(await provider.GetHistoryAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Ct));

        Assert.Equal(600L, day.Tokens?.Input);
        Assert.Equal(600L, (await provider.GetUsageAsync(Ct)).Tokens?.Input);
    }

    /// <summary>
    /// The history source answers with nothing rather than failing when there is no store at
    /// all, so a machine without Gemini CLI leaves its days unknown rather than zero.
    /// </summary>
    [Fact]
    public async Task AMissingStoreProducesNoDaysRatherThanAnError()
    {
        using TempWorkspace workspace = CreateHome();
        using var provider = new GeminiUsageProvider(
            GeminiOptions.Default,
            new FakeCliRunner { CommandExists = false },
            new FakeProcessMonitor(),
            Path.Combine(workspace.Root, "no-such-home"),
            new FixedTimeProvider(Now));

        Assert.Empty(await provider.GetHistoryAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Ct));
    }
}
