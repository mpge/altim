using System.Globalization;
using System.Text;
using Altim.Providers.Claude;
using Altim.Providers.Claude.StatusLine;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The status-line command Claude Code runs: what it writes down, and what it refuses to.
/// </summary>
/// <remarks>
/// <para>
/// This is the only component in Altim that is handed the user's working directory, their
/// project directory, a transcript path and a session id in one document, so the first test
/// here is the one that matters: the file it writes must contain none of them. It is
/// asserted over the raw bytes rather than over the parsed state, because a state object
/// that happens not to expose a field says nothing about what was written to disk.
/// </para>
/// <para>
/// Every test writes into its own temporary directory through
/// <see cref="ClaudeStatusLineHelper.Run"/>'s config-root override. Nothing here can reach
/// a real Claude Code configuration.
/// </para>
/// </remarks>
public sealed class ClaudeStatusLineHelperTests
{
    private const string WorkingDirectory = "C:\\\\work\\\\a-private-client-project";
    private const string SessionId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000";
    private const string ProjectDirectory = "C:\\\\work";
    private const string TranscriptPath = "C:\\\\Users\\\\someone\\\\.claude\\\\projects\\\\slug\\\\session.jsonl";

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = Now.AddHours(3).AddMinutes(12);
    private static readonly DateTimeOffset SevenDayReset = Now.AddDays(2).AddHours(4);

    /// <summary>
    /// The privacy rule, asserted where it is actually kept: in the bytes on disk.
    /// </summary>
    [Fact]
    public void NothingButNumbersIsWrittenDown()
    {
        using var workspace = new TempWorkspace();

        string line = RunHelper(workspace, FullPayload());

        string written = File.ReadAllText(StateFile(workspace));

        foreach (string secret in new[] { "a-private-client-project", SessionId, "session.jsonl", "claude-opus-4-5-20260101", "Opus" })
        {
            Assert.DoesNotContain(secret, written, StringComparison.OrdinalIgnoreCase);

            // And it is not printed into the user's terminal either.
            Assert.DoesNotContain(secret, line, StringComparison.OrdinalIgnoreCase);
        }

        // The property names the payload uses for those fields are absent too, so a later
        // reader cannot start consuming one.
        foreach (string property in new[] { "cwd", "session_id", "transcript_path", "workspace", "model", "project_dir" })
        {
            Assert.DoesNotContain("\"" + property + "\"", written, StringComparison.Ordinal);
        }

        // What it does carry is the numbers, so this is not passing by writing an empty file.
        Assert.Contains("\"resets_at\"", written, StringComparison.Ordinal);
        Assert.Contains("\"used_percentage\"", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the feature: the file the helper writes parses back through the
    /// reader with the reset instants the transcript source cannot supply.
    /// </summary>
    [Fact]
    public void TheStateFileParsesBackThroughTheReaderWithItsResetTimes()
    {
        using var workspace = new TempWorkspace();

        _ = RunHelper(workspace, FullPayload());

        StatusLineReadOutcome outcome = ClaudeStatusLineReader.TryRead(StateFile(workspace), out ClaudeStatusLineState? state);

        Assert.Equal(StatusLineReadOutcome.Read, outcome);
        Assert.NotNull(state);
        Assert.Equal(53d, state.FiveHourUsedPercent);
        Assert.Equal(FiveHourReset, state.FiveHourResetsAt);
        Assert.Equal(85d, state.SevenDayUsedPercent);
        Assert.Equal(SevenDayReset, state.SevenDayResetsAt);
        Assert.Equal(1.23d, state.SessionCostUsd);
        Assert.Equal(42000L, state.ContextUsedTokens);
        Assert.Equal(200000L, state.ContextMaxTokens);
        Assert.Equal(9000L, state.PromptCacheReadTokens);
        Assert.Equal(1500L, state.PromptCacheCreationTokens);
        Assert.Equal(Now, state.WrittenAt);

        // Read back from the file, the model and the session are unknown, because they were
        // never written.
        Assert.Null(state.ModelId);
        Assert.Null(state.SessionId);
    }

    /// <summary>
    /// A figure the payload did not carry is absent from the file, not zero in it.
    /// </summary>
    [Fact]
    public void AFieldThePayloadOmitsIsOmittedRatherThanZeroed()
    {
        using var workspace = new TempWorkspace();

        // The five-hour window has been dropped because its reset passed, and this build of
        // Claude Code reports no prompt cache figures at all.
        _ = RunHelper(
            workspace,
            $$"""
            {
              "rate_limits": { "seven_day": { "used_percentage": 85, "resets_at": {{SevenDayReset.ToUnixTimeSeconds()}} } }
            }
            """);

        string written = File.ReadAllText(StateFile(workspace));
        Assert.DoesNotContain("five_hour", written, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache", written, StringComparison.Ordinal);
        Assert.DoesNotContain("context_window", written, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", written, StringComparison.Ordinal);

        ClaudeStatusLineState? state = ClaudeStatusLineReader.Read(StateFile(workspace));

        Assert.NotNull(state);
        Assert.Null(state.FiveHourUsedPercent);
        Assert.Null(state.FiveHourResetsAt);
        Assert.Null(state.SessionCostUsd);
        Assert.Null(state.PromptCacheReadTokens);
        Assert.Equal(85d, state.SevenDayUsedPercent);
    }

    /// <summary>
    /// The state file is replaced, not rewritten in place, so no reader can see half of one,
    /// and the replace goes through while a reader is holding it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted through a reader holding the file open with exactly the sharing
    /// <see cref="ClaudeStatusLineReader"/> asks for, because that is the live case: Altim's
    /// own provider opens this file on every refresh tick. A replace leaves that handle
    /// reading the file it opened and puts the new document in place; a truncate-and-write
    /// would show the same handle the new content or nothing at all.
    /// </para>
    /// <para>
    /// This is the test that caught <c>File.Move(overwrite: true)</c>. On Windows the move is
    /// refused outright while the destination has an open handle, even one sharing delete,
    /// and it leaves both the stale file and the temporary one behind. The replacement is
    /// also deliberately shorter than the file it replaces, so a writer that truncated
    /// without replacing would leave a tail of the previous document behind.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStateFileIsReplacedRatherThanRewrittenInPlace()
    {
        using var workspace = new TempWorkspace();

        _ = RunHelper(workspace, FullPayload());
        byte[] before = File.ReadAllBytes(StateFile(workspace));

        using (FileStream held = OpenAsTheReaderDoes(StateFile(workspace)))
        {
            _ = RunHelper(workspace, """{"rate_limits":{"five_hour":{"used_percentage":7}}}""");

            var buffer = new MemoryStream();
            held.CopyTo(buffer);
            Assert.Equal(before, buffer.ToArray());
        }

        // The replacement is what is on disk now, whole, and nothing of the mechanism is
        // left beside it.
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Read(StateFile(workspace));
        Assert.Equal(7d, state?.FiveHourUsedPercent);
        Assert.Null(state?.SevenDayUsedPercent);

        Assert.Equal(new[] { StateFile(workspace) }, Directory.GetFiles(workspace.Root));
    }

    /// <summary>The line Claude Code renders says what is left and when it comes back.</summary>
    [Fact]
    public void TheStatusLineReportsEveryFigureThePayloadCarried()
    {
        using var workspace = new TempWorkspace();

        string line = RunHelper(workspace, FullPayload());

        Assert.Equal("5h 53% (3h12m) | 7d 85% (2d4h) | ctx 21% | $1.23", line);
    }

    /// <summary>
    /// A blank status line reads as a broken helper, so there is always something to show.
    /// </summary>
    [Fact]
    public void AnEmptyOrUnparseablePayloadStillPrintsALine()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal(ClaudeStatusLineHelper.NothingReportedText, RunHelper(workspace, string.Empty));
        Assert.Equal(ClaudeStatusLineHelper.NothingReportedText, RunHelper(workspace, "{ not json"));

        // A payload with nothing but the fields Altim does not read is the same case: the
        // session has not had a response yet.
        Assert.Equal(
            ClaudeStatusLineHelper.NothingReportedText,
            RunHelper(workspace, $$"""{"session_id":"{{SessionId}}","cwd":"{{WorkingDirectory}}"}"""));
    }

    /// <summary>
    /// A byte-order mark in front of the payload is one invisible byte, and it used to cost
    /// the whole reading.
    /// </summary>
    /// <remarks>
    /// Found by driving the built executable from a PowerShell host, which writes a mark on
    /// the pipe. The parser counts it as a character, refused the document, and the helper
    /// printed "nothing reported" against a payload that carried both windows. Nothing in the
    /// suite saw it, because every fixture here is written by the test itself.
    /// </remarks>
    [Fact]
    public void AByteOrderMarkInFrontOfThePayloadIsNotAParseFailure()
    {
        using var workspace = new TempWorkspace();

        using var input = new MemoryStream(
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(FullPayload())]);
        using var output = new StringWriter();

        _ = ClaudeStatusLineHelper.Run(input, output, new FixedClock(Now), workspace.Root);

        Assert.Equal("5h 53% (3h12m) | 7d 85% (2d4h) | ctx 21% | $1.23", output.ToString());
        Assert.Equal(FiveHourReset, ClaudeStatusLineReader.Read(StateFile(workspace))?.FiveHourResetsAt);
    }

    /// <summary>
    /// Nothing is written for a payload that could not be parsed. The previous reading stays
    /// where it is rather than being replaced by an empty document.
    /// </summary>
    [Fact]
    public void AnUnparseablePayloadLeavesTheLastGoodStateFileAlone()
    {
        using var workspace = new TempWorkspace();

        _ = RunHelper(workspace, FullPayload());
        byte[] good = File.ReadAllBytes(StateFile(workspace));

        _ = RunHelper(workspace, "{ not json");

        Assert.Equal(good, File.ReadAllBytes(StateFile(workspace)));
    }

    /// <summary>
    /// A percentage above the plausible ceiling is the known defect, and it is not written
    /// down as a reading. Its reset instant still is.
    /// </summary>
    [Fact]
    public void AnImplausiblePercentageIsNotRecordedAsUsage()
    {
        using var workspace = new TempWorkspace();

        string epoch = FiveHourReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        _ = RunHelper(
            workspace,
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + epoch + ",\"resets_at\":" + epoch + "}}}");

        ClaudeStatusLineState? state = ClaudeStatusLineReader.Read(StateFile(workspace));

        Assert.Null(state?.FiveHourUsedPercent);
        Assert.Equal(FiveHourReset, state?.FiveHourResetsAt);
        Assert.DoesNotContain("used_percentage", File.ReadAllText(StateFile(workspace)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything the line prints is ASCII, because the console it lands in cannot always
    /// encode anything else.
    /// </summary>
    [Fact]
    public void TheStatusLineIsAscii()
    {
        using var workspace = new TempWorkspace();

        foreach (string line in new[] { RunHelper(workspace, FullPayload()), ClaudeStatusLineHelper.NothingReportedText })
        {
            Assert.All(line, static c => Assert.InRange(c, ' ', '~'));
        }
    }

    /// <summary>
    /// A reset instant that has already passed prints no countdown rather than a negative
    /// one. The percentage is still reported: the payload carried it.
    /// </summary>
    [Fact]
    public void AWindowWhoseResetHasPassedPrintsNoCountdown()
    {
        var state = new ClaudeStatusLineState(
            Now, 53d, Now.AddMinutes(-5), null, null, null, null, null, null, null, null, null, null, null);

        Assert.Equal("5h 53%", ClaudeStatusLineHelper.Render(state, Now));
    }

    /// <summary>The countdown shortens as the reset approaches, and never rounds to nothing.</summary>
    [Theory]
    [InlineData(20, "5h 53% (<1m)")]
    [InlineData(90, "5h 53% (1m)")]
    [InlineData(45 * 60, "5h 53% (45m)")]
    [InlineData((3 * 60 * 60) + (12 * 60), "5h 53% (3h12m)")]
    [InlineData((2 * 24 * 60 * 60) + (4 * 60 * 60), "5h 53% (2d4h)")]
    public void TheCountdownIsReadableAtEveryScale(int secondsAway, string expected)
    {
        var state = new ClaudeStatusLineState(
            Now, 53d, Now.AddSeconds(secondsAway), null, null, null, null, null, null, null, null, null, null, null);

        Assert.Equal(expected, ClaudeStatusLineHelper.Render(state, Now));
    }

    /// <summary>
    /// The branch is selected by the same marker the installer writes and revert matches, so
    /// the three cannot drift apart.
    /// </summary>
    [Fact]
    public void TheHelperIsSelectedByTheInstallersOwnMarker()
    {
        Assert.Equal(StatusLineInstaller.CommandMarker, ClaudeStatusLineHelper.Argument);

        Assert.True(ClaudeStatusLineHelper.IsHelperInvocation([StatusLineInstaller.CommandMarker]));
        Assert.True(ClaudeStatusLineHelper.IsHelperInvocation(["--flag", "altim-statusline"]));
        Assert.True(ClaudeStatusLineHelper.IsHelperInvocation(["--altim-statusline=1"]));

        Assert.False(ClaudeStatusLineHelper.IsHelperInvocation([]));
        Assert.False(ClaudeStatusLineHelper.IsHelperInvocation(null));
        Assert.False(ClaudeStatusLineHelper.IsHelperInvocation(["--squirrel-install", "1.0.0"]));
    }

    /// <summary>The file lands where the reader looks for it, and nowhere else.</summary>
    [Fact]
    public void TheStateFileLandsWhereTheReaderLooks()
    {
        using var workspace = new TempWorkspace();

        _ = RunHelper(workspace, FullPayload());

        Assert.Equal(
            Path.GetFullPath(ClaudePaths.StatusLineStateFile(workspace.Root)),
            Path.GetFullPath(StateFile(workspace)));
        Assert.True(File.Exists(StateFile(workspace)));
    }

    private static string StateFile(TempWorkspace workspace) =>
        ClaudePaths.StatusLineStateFile(workspace.Root);

    private static FileStream OpenAsTheReaderDoes(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });

    private static string RunHelper(TempWorkspace workspace, string payload)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var output = new StringWriter();

        int exitCode = ClaudeStatusLineHelper.Run(
            input,
            output,
            new FixedClock(Now),
            workspace.Root);

        // Claude Code runs this on a timer; a non-zero exit would be in the user's face for
        // as long as the condition lasted.
        Assert.Equal(0, exitCode);

        return output.ToString();
    }

    private static string FullPayload() =>
        $$"""
        {
          "hook_event_name": "Status",
          "session_id": "{{SessionId}}",
          "transcript_path": "{{TranscriptPath}}",
          "cwd": "{{WorkingDirectory}}",
          "model": { "id": "claude-opus-4-5-20260101", "display_name": "Opus" },
          "workspace": { "current_dir": "{{WorkingDirectory}}", "project_dir": "{{ProjectDirectory}}" },
          "version": "2.1.273",
          "rate_limits": {
            "five_hour": { "used_percentage": 53, "resets_at": {{FiveHourReset.ToUnixTimeSeconds()}} },
            "seven_day": { "used_percentage": 85, "resets_at": {{SevenDayReset.ToUnixTimeSeconds()}} }
          },
          "cost": { "total_cost_usd": 1.23, "total_duration_ms": 45000 },
          "context_window": { "used_tokens": 42000, "max_tokens": 200000 },
          "prompt_cache": { "cache_read_input_tokens": 9000, "cache_creation_input_tokens": 1500 }
        }
        """;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
