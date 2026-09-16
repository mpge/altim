using Altim.Providers.Claude.Sessions;
using Altim.Providers.Codex;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The two CLI-backed readers: the doctor report and the agents listing.
/// </summary>
public sealed class CliReaderTests
{
    [Theory]
    [InlineData("""{"schemaVersion":1,"auth_mode":"chatgpt"}""", CodexAuthMode.ChatGpt)]
    [InlineData("""{"schemaVersion":1,"authMode":"ChatGPT"}""", CodexAuthMode.ChatGpt)]
    [InlineData("""{"schemaVersion":1,"auth":{"mode":"chatgpt_tokens"}}""", CodexAuthMode.ChatGpt)]
    [InlineData("""{"schemaVersion":1,"auth_mode":"api_key"}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"schemaVersion":1,"auth":{"method":"apikey"}}""", CodexAuthMode.ApiKey)]
    [InlineData("""{"schemaVersion":1,"auth_mode":"none"}""", CodexAuthMode.NotAuthenticated)]
    [InlineData("""{"schemaVersion":1,"config":{"home":"C:\\Users\\someone\\.codex"}}""", CodexAuthMode.Unknown)]
    [InlineData("not json", CodexAuthMode.Unknown)]
    public void ReadsTheAuthModeOutOfADoctorReport(string json, CodexAuthMode expected) =>
        Assert.Equal(expected, CodexDoctorReader.ParseAuthMode(json));

    [Fact]
    public async Task AMissingCodexCliLeavesTheAuthModeUnknown()
    {
        var reader = new CodexDoctorReader(new FakeCliRunner { CommandExists = false });

        Assert.Equal(
            CodexAuthMode.Unknown,
            await reader.ReadAuthModeAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsTheAgentsListingWithoutTheWorkingDirectoryOrDisplayName()
    {
        IReadOnlyList<ClaudeAgentEntry> entries = ClaudeAgentsReader.Parse(
            """
            [
              { "pid": 4242, "cwd": "C:\\work\\private-project", "kind": "main",
                "startedAt": "2026-09-15T15:30:00Z",
                "sessionId": "0199aaaa-bbbb-cccc-dddd-eeeeffff0000",
                "name": "private-project", "status": "running" }
            ]
            """);

        ClaudeAgentEntry entry = Assert.Single(entries);
        Assert.Equal(4242, entry.ProcessId);
        Assert.Equal("0199aaaa-bbbb-cccc-dddd-eeeeffff0000", entry.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 15, 30, 0, TimeSpan.Zero), entry.StartedAt);
        Assert.Equal("main", entry.Kind);
        Assert.Equal("running", entry.Status);
    }

    [Fact]
    public void TheStartInstantIsReadWhenTheCliReportsItAsUnixMilliseconds()
    {
        // What the installed CLI actually prints. A reader that accepted only ISO 8601 and
        // Unix seconds rejected this as a date beyond the year 2100 and reported no start
        // time at all, on every session, on every machine.
        IReadOnlyList<ClaudeAgentEntry> entries = ClaudeAgentsReader.Parse(
            """[{"pid":21568,"cwd":"C:\\Users\\work","kind":"interactive","startedAt":1789467994766,"sessionId":"bf52b29d-fe5a-4922-8852-409ea1bb81f2","name":"work-f8","status":"busy"}]""");

        ClaudeAgentEntry entry = Assert.Single(entries);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789467994766), entry.StartedAt);
        Assert.Equal("interactive", entry.Kind);
    }

    [Fact]
    public void AnEmptyAgentsListingIsAnEmptyList() =>
        Assert.Empty(ClaudeAgentsReader.Parse("[]"));

    [Fact]
    public void MalformedAgentsOutputIsAnEmptyList() =>
        Assert.Empty(ClaudeAgentsReader.Parse("Error: this command requires an interactive terminal"));

    [Fact]
    public async Task AMissingClaudeCliYieldsNoAgents()
    {
        var reader = new ClaudeAgentsReader(new FakeCliRunner { CommandExists = false });

        Assert.False(reader.IsAvailable);

        ClaudeAgentsListing listing = await reader.ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeAgentsOutcome.NotDetected, listing.Outcome);
        Assert.Empty(listing.Entries);
        Assert.False(listing.IsAuthoritative);
    }

    [Fact]
    public async Task AFailedListingIsNotTheSameAnswerAsAnEmptyOne()
    {
        // Both produce no entries, and they mean opposite things: one says nothing is
        // running, the other says nobody knows. Only the second may be second-guessed by
        // process enumeration.
        var runner = new FakeCliRunner { CommandExists = true };
        var reader = new ClaudeAgentsReader(runner);

        ClaudeAgentsListing failed = await reader.ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeAgentsOutcome.Failed, failed.Outcome);
        Assert.False(failed.IsAuthoritative);
        Assert.Contains("agents --json", runner.Invocations);

        runner.RespondWithJson("agents --json", "[]");
        ClaudeAgentsListing empty = await reader.ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeAgentsOutcome.Listed, empty.Outcome);
        Assert.True(empty.IsAuthoritative);
        Assert.Empty(empty.Entries);
    }

    [Fact]
    public async Task OutputThatIsNotAListingIsAFailureRatherThanAnEmptyListing()
    {
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("agents --json", "Error: this command requires an interactive terminal");

        ClaudeAgentsListing listing = await new ClaudeAgentsReader(runner)
            .ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeAgentsOutcome.Failed, listing.Outcome);
    }
}
