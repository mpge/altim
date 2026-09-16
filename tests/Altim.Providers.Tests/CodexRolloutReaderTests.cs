using System.Globalization;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Codex.Rollout;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The offline fallback: recovering the last quota snapshot from a rollout tail.
/// </summary>
public sealed class CodexRolloutReaderTests
{
    private const string SessionMeta =
        """{"type":"session_meta","payload":{"id":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","timestamp":"2026-09-15T10:00:00Z","cwd":"C:\\work\\private-project","instructions":"never read this"}}""";

    private static string TokenCount(string timestamp, double percent, int windowMinutes, long totalInput, long totalOutput) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + timestamp + "\""
        + ",\"payload\":{\"type\":\"token_count\""
        + ",\"rate_limits\":{\"primary\":{\"used_percent\":" + D(percent)
        + ",\"window_minutes\":" + N(windowMinutes)
        + ",\"resets_at\":1789549200},\"secondary\":null}"
        + ",\"info\":{\"model_context_window\":272000"
        + ",\"total_token_usage\":{\"input_tokens\":" + N(totalInput)
        + ",\"cached_input_tokens\":500,\"output_tokens\":" + N(totalOutput)
        + ",\"reasoning_output_tokens\":10,\"total_tokens\":999}"
        + ",\"last_token_usage\":{\"input_tokens\":7,\"output_tokens\":3}}}}";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string D(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    [Fact]
    public void RecoversTheNewestSnapshotFromTheTail()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-a.jsonl",
            SessionMeta,
            TokenCount("2026-09-15T10:05:00Z", 10, 10080, 100, 20),
            TokenCount("2026-09-15T10:15:00Z", 41.5, 10080, 300, 60));

        IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path);
        CodexRateLimitSnapshot? snapshot = CodexRolloutReader.LatestSnapshot(records, null);

        Assert.NotNull(snapshot);
        Assert.Equal(CodexSnapshotSource.LocalSnapshot, snapshot.Source);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 10, 15, 0, TimeSpan.Zero), snapshot.ObservedAt);
        CodexLimitWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal(41.5d, window.UsedPercent);
        Assert.Equal(10080, window.WindowMinutes);
    }

    [Fact]
    public void TakesTheLastCumulativeTotalOnceRatherThanSummingEveryLine()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-b.jsonl",
            SessionMeta,
            TokenCount("2026-09-15T10:05:00Z", 10, 300, 100, 20),
            TokenCount("2026-09-15T10:10:00Z", 20, 300, 250, 55),
            TokenCount("2026-09-15T10:15:00Z", 30, 300, 400, 90));

        IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path);
        CodexTokenCounts? counts = CodexRolloutReader.LatestCumulativeTokens(records);

        Assert.NotNull(counts);

        // 100 + 250 + 400 would be 750. The cumulative field restates the whole session on
        // every line, so the answer is the last one.
        Assert.Equal(400L, counts.Value.Input);
        Assert.Equal(90L, counts.Value.Output);
    }

    [Fact]
    public void ReadsTheNewerTokenUsageRecordSchema()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-c.jsonl",
            SessionMeta,
            """{"type":"token_usage_record","timestamp":"2026-09-15T11:00:00Z","payload":{"usage":{"input_tokens":5,"output_tokens":2},"turn_token_usage":{"input_tokens":5,"output_tokens":2},"thread_token_usage":{"input_tokens":900,"cached_input_tokens":120,"output_tokens":300,"total_tokens":1320}}}""");

        IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path);
        CodexRolloutRecord record = Assert.Single(records);

        Assert.Equal(900L, record.CumulativeTokens?.Input);
        Assert.Equal(120L, record.CumulativeTokens?.CachedInput);
        Assert.Equal(5L, record.TurnTokens?.Input);
    }

    [Fact]
    public void SkipsACorruptLineAndStillRecoversTheSnapshotAfterIt()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-d.jsonl",
            SessionMeta,
            "{\"type\":\"event_msg\",\"payload\":{ not json at all",
            TokenCount("2026-09-15T12:00:00Z", 66, 10080, 10, 10));

        IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path);
        CodexRateLimitSnapshot? snapshot = CodexRolloutReader.LatestSnapshot(records, null);

        Assert.NotNull(snapshot);
        Assert.Equal(66d, Assert.Single(snapshot.Windows).UsedPercent);
    }

    [Fact]
    public void IgnoresATruncatedFinalLine()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLinesWithPartialTail(
            "rollout-e.jsonl",
            [SessionMeta, TokenCount("2026-09-15T12:00:00Z", 66, 10080, 10, 10)],
            """{"type":"event_msg","timestamp":"2026-09-15T12:30:00Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":99""");

        IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path);
        CodexRateLimitSnapshot? snapshot = CodexRolloutReader.LatestSnapshot(records, null);

        Assert.NotNull(snapshot);
        Assert.Equal(66d, Assert.Single(snapshot.Windows).UsedPercent);
    }

    [Fact]
    public void FallsBackToTheFileTimeWhenNoLineCarriedATimestamp()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-f.jsonl",
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":5,"window_minutes":300}}}}""");

        var fileTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        CodexRateLimitSnapshot? snapshot = CodexRolloutReader.LatestSnapshot(CodexRolloutReader.ReadTail(path), fileTime);

        Assert.Equal(fileTime, snapshot?.ObservedAt);
    }

    [Fact]
    public void CompressedRolloutsAreSkippedRatherThanGuessedAt()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.Write("rollout-g.jsonl.zst", "not really compressed");

        Assert.Empty(CodexRolloutReader.ReadTail(path));
    }

    [Fact]
    public void ConversationLinesProduceNothing()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "rollout-h.jsonl",
            SessionMeta,
            """{"type":"event_msg","payload":{"type":"agent_message","message":"here is the API key sk-not-a-real-secret and the file C:\\work\\x.cs"}}""",
            """{"type":"response_item","payload":{"type":"message","content":[{"type":"input_text","text":"a prompt"}]}}""");

        Assert.Empty(CodexRolloutReader.ReadTail(path));
    }
}
