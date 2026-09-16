using System.Globalization;
using Altim.Providers.Claude;
using Altim.Providers.Claude.Transcripts;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The four measured transcript traps, each with a test that fails if the trap is stepped in.
/// </summary>
public sealed class ClaudeTranscriptScannerTests
{
    private static string Assistant(
        string messageId,
        long output,
        string model = "claude-opus-4-5-20260101",
        string sessionId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000",
        string requestId = "req_011AAA",
        long input = 10,
        long cacheRead = 100,
        long? cache5m = 200,
        long? cache1h = 800,
        long? flatCacheCreation = null,
        bool sidechain = false)
    {
        string cacheCreation = flatCacheCreation is { } flat
            ? "\"cache_creation_input_tokens\":" + N(flat)
            : "\"cache_creation\":{\"ephemeral_5m_input_tokens\":" + N(cache5m ?? 0)
              + ",\"ephemeral_1h_input_tokens\":" + N(cache1h ?? 0) + "}";

        // Built by concatenation rather than interpolation: the fixture is mostly braces, and
        // a template would be harder to read than the JSON it produces.
        return "{\"type\":\"assistant\""
            + ",\"isSidechain\":" + (sidechain ? "true" : "false")
            + ",\"sessionId\":\"" + sessionId + "\""
            + ",\"requestId\":\"" + requestId + "\""
            + ",\"timestamp\":\"2026-09-15T10:00:00Z\""
            + ",\"cwd\":\"C:\\\\work\\\\private-project\""
            + ",\"message\":{\"id\":\"" + messageId + "\""
            + ",\"model\":\"" + model + "\""
            + ",\"content\":[{\"type\":\"text\",\"text\":\"a secret the reader must not carry\"}]"
            + ",\"usage\":{\"input_tokens\":" + N(input)
            + ",\"output_tokens\":" + N(output)
            + ",\"cache_read_input_tokens\":" + N(cacheRead)
            + "," + cacheCreation + "}}}";
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void DeduplicatesRepeatedContentBlocksOnMessageIdentity()
    {
        using var workspace = new TempWorkspace();

        // The measured shape: one message, restated once per content block, each line
        // carrying the same usage object. Summing lines overcounted output by 3.15 times.
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_01AAA", output: 500),
            Assistant("msg_01AAA", output: 500),
            Assistant("msg_01AAA", output: 500),
            Assistant("msg_01BBB", output: 250));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(750L, history.Totals.Output);
        Assert.Equal(2, history.Totals.MessageCount);
        Assert.Equal(2, history.DuplicateMessagesDropped);
    }

    [Fact]
    public void DeduplicationSurvivesAnIncrementalScanBoundary()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("projects/slug/session.jsonl", Assistant("msg_01AAA", output: 500));

        var scanner = new ClaudeTranscriptScanner();
        ClaudeTokenHistory first = scanner.ScanFile(path);
        Assert.Equal(500L, first.Totals.Output);

        workspace.Append(path, Assistant("msg_01AAA", output: 500) + "\n");
        ClaudeTokenHistory second = scanner.ScanFile(path);

        Assert.Equal(0L, second.Totals.Output);
        Assert.Equal(1, second.DuplicateMessagesDropped);
    }

    [Fact]
    public void ARepeatScanWithNoNewBytesReadsNothing()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("projects/slug/session.jsonl", Assistant("msg_01AAA", output: 500));

        var scanner = new ClaudeTranscriptScanner();
        _ = scanner.ScanFile(path);
        ClaudeTokenHistory second = scanner.ScanFile(path);

        Assert.Equal(0, second.Totals.MessageCount);
        Assert.Equal(0L, second.Totals.Output);
    }

    [Fact]
    public void IncludesSubagentTranscripts()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines("projects/slug/session.jsonl", Assistant("msg_main", output: 100));
        _ = workspace.WriteLines(
            "projects/slug/session/subagents/agent-1.jsonl",
            Assistant("msg_sub_1", output: 4000, sessionId: "0199bbbb-cccc-dddd-eeee-ffff00001111", sidechain: true));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().Scan([workspace.Root], DateTimeOffset.UtcNow);

        Assert.Equal(4100L, history.Totals.Output);
        Assert.Equal(2, history.FilesScanned);
        Assert.Equal(1, history.SubagentFilesScanned);
    }

    [Fact]
    public void ExcludesSyntheticModelEntries()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_real", output: 100),
            Assistant("msg_fake", output: 999999, model: "<synthetic>"));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(100L, history.Totals.Output);
        Assert.Equal(1, history.SyntheticMessagesExcluded);
        Assert.Equal(1, history.Totals.MessageCount);
        Assert.DoesNotContain("<synthetic>", history.ByModel.Keys);
    }

    [Fact]
    public void KeepsTheTwoCacheCreationTiersApart()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_01AAA", output: 10, cache5m: 200, cache1h: 800));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        // The one-hour tier bills at twice the five-minute rate, so a single merged number
        // cannot be priced. They stay separate all the way out of the reader.
        Assert.Equal(200L, history.Totals.CacheCreation5m);
        Assert.Equal(800L, history.Totals.CacheCreation1h);
        Assert.Equal(0L, history.Totals.CacheCreationUnsplit);
        Assert.Equal(1000L, history.Totals.CacheCreationTotal);
    }

    [Fact]
    public void TheFlatCacheCreationFieldIsOnlyUsedWhenTheTieredObjectIsAbsent()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_flat", output: 10, flatCacheCreation: 700));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(0L, history.Totals.CacheCreation5m);
        Assert.Equal(0L, history.Totals.CacheCreation1h);
        Assert.Equal(700L, history.Totals.CacheCreationUnsplit);
    }

    [Fact]
    public void TheFlatFieldIsNotAddedOnTopOfTheTieredOne()
    {
        using var workspace = new TempWorkspace();

        // A line carrying both. Reading both would count the same tokens twice.
        string line =
            """
            {"type":"assistant","sessionId":"s1","requestId":"r1","message":{"id":"msg_both","model":"claude-opus-4-5-20260101","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":1000,"cache_creation":{"ephemeral_5m_input_tokens":200,"ephemeral_1h_input_tokens":800}}}}
            """;
        string path = workspace.WriteLines("projects/slug/session.jsonl", line);

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(1000L, history.Totals.CacheCreationTotal);
        Assert.Equal(0L, history.Totals.CacheCreationUnsplit);
    }

    [Fact]
    public void KeepsTheLongContextModelVariantSeparateFromItsBase()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_base", output: 10, model: "claude-opus-4-5-20260101"),
            Assistant("msg_long", output: 20, model: "claude-opus-4-5-20260101[1m]"));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(2, history.ByModel.Count);
        Assert.Equal(10L, history.ByModel["claude-opus-4-5-20260101"].Output);
        Assert.Equal(20L, history.ByModel["claude-opus-4-5-20260101[1m]"].Output);

        Assert.True(ClaudeTokenHistory.IsLongContext("claude-opus-4-5-20260101[1m]"));
        Assert.False(ClaudeTokenHistory.IsLongContext("claude-opus-4-5-20260101"));
        Assert.Equal("claude-opus-4-5-20260101", ClaudeTokenHistory.BaseModelId("claude-opus-4-5-20260101[1m]"));
    }

    [Fact]
    public void SkipsACorruptLineWithoutLosingTheRestOfTheFile()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            Assistant("msg_01AAA", output: 100),
            """{"type":"assistant","message":{"usage":{"output_tokens": broken""",
            Assistant("msg_01BBB", output: 200));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(300L, history.Totals.Output);
        Assert.Equal(1, history.CorruptLinesSkipped);
    }

    [Fact]
    public void IgnoresATruncatedFinalLineAndPicksItUpOnceComplete()
    {
        using var workspace = new TempWorkspace();
        string complete = Assistant("msg_01AAA", output: 100);
        string partial = Assistant("msg_01BBB", output: 200);
        string path = workspace.WriteLinesWithPartialTail(
            "projects/slug/session.jsonl",
            [complete],
            partial[..(partial.Length / 2)]);

        var scanner = new ClaudeTranscriptScanner();
        ClaudeTokenHistory first = scanner.ScanFile(path);
        Assert.Equal(100L, first.Totals.Output);
        Assert.Equal(0, first.CorruptLinesSkipped);

        // The writer finishes the line. Because the cursor stopped at the last boundary, the
        // line is read whole rather than lost.
        workspace.Append(path, partial[(partial.Length / 2)..] + "\n");
        ClaudeTokenHistory second = scanner.ScanFile(path);
        Assert.Equal(200L, second.Totals.Output);
    }

    [Fact]
    public void UserAndToolLinesContributeNothing()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "projects/slug/session.jsonl",
            """{"type":"user","message":{"role":"user","content":"my prompt, which is none of Altim's business"}}""",
            """{"type":"system","subtype":"init","cwd":"C:\\work\\private-project","tools":["Bash","Read"]}""",
            Assistant("msg_01AAA", output: 100));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().ScanFile(path);

        Assert.Equal(1, history.Totals.MessageCount);
        Assert.Equal(100L, history.Totals.Output);
    }

    [Fact]
    public void ScanningRespectsTheTranscriptWindow()
    {
        using var workspace = new TempWorkspace();
        string old = workspace.WriteLines("projects/slug/old.jsonl", Assistant("msg_old", output: 5000));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-60));
        _ = workspace.WriteLines("projects/slug/new.jsonl", Assistant("msg_new", output: 7, sessionId: "0199cccc-dddd-eeee-ffff-000011112222"));

        ClaudeTokenHistory history = new ClaudeTranscriptScanner().Scan([workspace.Root], DateTimeOffset.UtcNow);

        Assert.Equal(7L, history.Totals.Output);
        Assert.Equal(1, history.FilesScanned);
    }

    [Fact]
    public void SubagentDetectionIsByDirectoryName()
    {
        Assert.True(ClaudePaths.IsSubagentTranscript(Path.Combine("projects", "slug", "session", "subagents", "agent-1.jsonl")));
        Assert.False(ClaudePaths.IsSubagentTranscript(Path.Combine("projects", "slug", "session.jsonl")));
    }
}
