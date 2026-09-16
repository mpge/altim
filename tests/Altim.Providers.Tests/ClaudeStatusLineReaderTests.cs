using Altim.Providers.Claude.StatusLine;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The documented status-line payload, and the two ways it lies.
/// </summary>
public sealed class ClaudeStatusLineReaderTests
{
    [Fact]
    public void ReadsBothWindowsWithTheirResetInstants()
    {
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """
            {
              "session_id": "0199aaaa-bbbb-cccc-dddd-eeeeffff0000",
              "cwd": "C:\\work\\private-project",
              "model": { "id": "claude-opus-4-5-20260101", "display_name": "Opus" },
              "rate_limits": {
                "five_hour": { "used_percentage": 53, "resets_at": 1789515600 },
                "seven_day": { "used_percentage": 85, "resets_at": 1789549200 }
              },
              "cost": { "total_cost_usd": 1.2345 },
              "context_window": { "used_tokens": 42000, "max_tokens": 200000 },
              "prompt_cache": { "cache_read_input_tokens": 9000, "cache_creation_input_tokens": 1500 }
            }
            """);

        Assert.NotNull(state);
        Assert.Equal(53d, state.FiveHourUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), state.FiveHourResetsAt);
        Assert.Equal(85d, state.SevenDayUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789549200), state.SevenDayResetsAt);
        Assert.Equal(1.2345d, state.SessionCostUsd);
        Assert.Equal(42000L, state.ContextUsedTokens);
        Assert.Equal(200000L, state.ContextMaxTokens);
        Assert.Equal(21d, state.ContextUsedPercent);
        Assert.Equal(9000L, state.PromptCacheReadTokens);
        Assert.Equal(1500L, state.PromptCacheCreationTokens);
        Assert.Equal("claude-opus-4-5-20260101", state.ModelId);
    }

    [Fact]
    public void AMissingWindowIsNoDataRatherThanZeroUsed()
    {
        // Windows are dropped from the payload once their reset passes.
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """{"rate_limits":{"seven_day":{"used_percentage":85,"resets_at":1789549200}}}""");

        Assert.NotNull(state);
        Assert.Null(state.FiveHourUsedPercent);
        Assert.Null(state.FiveHourResetsAt);
        Assert.Equal(85d, state.SevenDayUsedPercent);
    }

    [Fact]
    public void APercentageAboveTheCeilingIsTreatedAsUnavailable()
    {
        // The known defect: an epoch timestamp arrives where a percentage should be.
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """{"rate_limits":{"five_hour":{"used_percentage":1789515600,"resets_at":1789515600}}}""");

        Assert.NotNull(state);
        Assert.Null(state.FiveHourUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), state.FiveHourResetsAt);
    }

    [Fact]
    public void ReadsTheSpendLimitWindowWhenPresent()
    {
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """{"rate_limits":{"spend_limit":{"used_percentage":12,"resets_at":1789549200}}}""");

        Assert.Equal(12d, state?.SpendLimitUsedPercent);
    }

    [Fact]
    public void FallsBackToTheFileTimeWhenThePayloadHasNoTimestamp()
    {
        var fallback = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse("""{"rate_limits":{}}""", fallback);

        Assert.Equal(fallback, state?.WrittenAt);
    }

    [Fact]
    public void PrefersAWrittenAtCarriedInThePayload()
    {
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """{"written_at":1789515600,"rate_limits":{}}""",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), state?.WrittenAt);
    }

    [Fact]
    public void MalformedJsonYieldsNullRatherThanThrowing() =>
        Assert.Null(ClaudeStatusLineReader.Parse("{ not json"));

    [Fact]
    public void ANonObjectPayloadYieldsNull() =>
        Assert.Null(ClaudeStatusLineReader.Parse("[1,2,3]"));

    [Fact]
    public void AMissingFileYieldsNull() =>
        Assert.Null(ClaudeStatusLineReader.Read(Path.Combine(Path.GetTempPath(), "altim-absent-statusline.json")));

    [Fact]
    public void ContextPercentIsNullWhenOnlyOneHalfIsReported()
    {
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse("""{"context_window":{"used_tokens":42000}}""");

        Assert.Equal(42000L, state?.ContextUsedTokens);
        Assert.Null(state?.ContextMaxTokens);
        Assert.Null(state?.ContextUsedPercent);
    }
}
