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
        Assert.Empty(await reader.ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailingAgentsCommandYieldsNoAgentsRatherThanThrowing()
    {
        var runner = new FakeCliRunner { CommandExists = true };
        var reader = new ClaudeAgentsReader(runner);

        Assert.Empty(await reader.ListAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Contains("agents --json", runner.Invocations);
    }
}
