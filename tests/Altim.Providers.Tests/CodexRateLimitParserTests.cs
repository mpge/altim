using System.Text.Json;
using Altim.Core.Models;
using Altim.Providers.Codex.Limits;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Both Codex dialects, and the slot-position trap.
/// </summary>
public sealed class CodexRateLimitParserTests
{
    private static IReadOnlyList<CodexLimitWindow> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CodexRateLimitParser.ReadWindows(document.RootElement);
    }

    [Fact]
    public void ReadsTheSnakeCaseRolloutDialect()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """
            {
              "rate_limits": {
                "primary":   { "used_percent": 41.5, "window_minutes": 300,   "resets_at": 1789515600 },
                "secondary": { "used_percent": 12.0, "window_minutes": 10080, "resets_at": 1789549200 }
              }
            }
            """);

        Assert.Equal(2, windows.Count);
        Assert.Equal(41.5d, windows[0].UsedPercent);
        Assert.Equal(300, windows[0].WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), windows[0].ResetsAt);
        Assert.Equal(10080, windows[1].WindowMinutes);
        Assert.All(windows, w => Assert.Equal("codex", w.LimitId));
    }

    [Fact]
    public void ReadsTheCamelCaseAppServerDialect()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """
            {
              "rateLimits": {
                "primary": { "usedPercent": 53, "windowDurationMins": 300, "resetsAt": 1789515600 }
              }
            }
            """);

        CodexLimitWindow window = Assert.Single(windows);
        Assert.Equal(53d, window.UsedPercent);
        Assert.Equal(300, window.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), window.ResetsAt);
    }

    [Fact]
    public void BothDialectsProduceTheSameNormalisedShape()
    {
        IReadOnlyList<CodexLimitWindow> snake = Parse(
            """{"rate_limits":{"primary":{"used_percent":77,"window_minutes":10080,"resets_at":1789549200}}}""");
        IReadOnlyList<CodexLimitWindow> camel = Parse(
            """{"rateLimits":{"primary":{"usedPercent":77,"windowDurationMins":10080,"resetsAt":1789549200}}}""");

        Assert.Equal(snake, camel);
    }

    [Fact]
    public void PrefersByLimitIdOverTheTopLevelObjectThatPicksOneFamily()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """
            {
              "rateLimits": {
                "primary": { "usedPercent": 1, "windowDurationMins": 10080 }
              },
              "rateLimitsByLimitId": {
                "codex":            { "primary": { "usedPercent": 60, "windowDurationMins": 10080, "resetsAt": 1789549200 },
                                      "secondary": null },
                "codex_bengalfox":  { "primary": { "usedPercent": 22, "windowDurationMins": 300 },
                                      "secondary": { "usedPercent": 44, "windowDurationMins": 10080 } }
              }
            }
            """);

        Assert.Equal(3, windows.Count);
        Assert.DoesNotContain(windows, w => w.UsedPercent == 1d);
        Assert.Contains(windows, w => w.LimitId == "codex" && w.WindowMinutes == 10080 && w.UsedPercent == 60d);
        Assert.Contains(windows, w => w.LimitId == "codex_bengalfox" && w.WindowMinutes == 300);
        Assert.Contains(windows, w => w.LimitId == "codex_bengalfox" && w.WindowMinutes == 10080);
    }

    [Fact]
    public void AFamilyWhoseFiveHourWindowIsAbsentSimplyHasNoFiveHourMeter()
    {
        // The 2026-09 shape on a real account: the "codex" family reports only a weekly
        // window, and its primary slot is that weekly window rather than a five-hour one.
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """
            {
              "rateLimitsByLimitId": {
                "codex": { "primary": { "usedPercent": 85, "windowDurationMins": 10080, "resetsAt": 1789549200 },
                           "secondary": null }
              }
            }
            """);

        IReadOnlyList<UsageMetric> metrics = CodexMetricFactory.Build(windows, MetricConfidence.BestEffort);

        UsageMetric metric = Assert.Single(metrics);
        Assert.Equal("codex:10080", metric.Key);
        Assert.Equal("Weekly", metric.Label);
        Assert.DoesNotContain(metrics, m => m.Key == "codex:300");
    }

    [Fact]
    public void TheWindowInThePrimarySlotIsLabelledByLengthNotBySlot()
    {
        // 2025-12 on this account: primary was the five-hour window.
        IReadOnlyList<CodexLimitWindow> older = Parse(
            """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":10080}}}}""");

        // 2026-09 on the same account: primary became the weekly window.
        IReadOnlyList<CodexLimitWindow> newer = Parse(
            """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":10080},"secondary":null}}}""");

        UsageMetric olderWeekly = Assert.Single(CodexMetricFactory.Build(older, MetricConfidence.BestEffort), m => m.Key == "codex:10080");
        UsageMetric newerWeekly = Assert.Single(CodexMetricFactory.Build(newer, MetricConfidence.BestEffort));

        Assert.Equal("Weekly", olderWeekly.Label);
        Assert.Equal(olderWeekly.Key, newerWeekly.Key);
        Assert.Equal(20d, olderWeekly.UsedPercent);
        Assert.Equal(20d, newerWeekly.UsedPercent);
    }

    [Fact]
    public void AnOffByOneWindowLengthLandsOnTheSameKeyAsTheExactOne()
    {
        IReadOnlyList<CodexLimitWindow> drifted = Parse(
            """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":30,"windowDurationMins":10079},"secondary":{"usedPercent":5,"windowDurationMins":299}}}}""");

        IReadOnlyList<UsageMetric> metrics = CodexMetricFactory.Build(drifted, MetricConfidence.BestEffort);

        Assert.Contains(metrics, m => m.Key == "codex:10080" && m.Label == "Weekly");
        Assert.Contains(metrics, m => m.Key == "codex:300" && m.Label == "5 hour");
        Assert.All(metrics, m => Assert.NotNull(m.Window));
    }

    [Fact]
    public void APercentageAboveTheCeilingBecomesUnavailableButKeepsItsWindow()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """{"rateLimits":{"primary":{"used_percent":1789515600,"window_minutes":300,"resets_at":1789515600}}}""");

        CodexLimitWindow window = Assert.Single(windows);
        Assert.Null(window.UsedPercent);
        Assert.Equal(300, window.WindowMinutes);

        UsageMetric metric = Assert.Single(CodexMetricFactory.Build(windows, MetricConfidence.BestEffort));
        Assert.False(metric.IsUsedPercentReported);
        Assert.NotNull(metric.Window);
    }

    [Fact]
    public void AWindowWithNoLengthIsDroppedRatherThanClassifiedBySlot()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """{"rateLimits":{"primary":{"used_percent":50,"resets_at":1789515600}}}""");

        Assert.Empty(windows);
    }

    [Fact]
    public void LegacyResetsInSecondsIsIgnored()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """{"rateLimits":{"primary":{"used_percent":50,"window_minutes":300,"resets_in_seconds":900}}}""");

        CodexLimitWindow window = Assert.Single(windows);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void ReadsPlanAndCreditFields()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"planType":"pro","credits":12.5,"rateLimitResetCredits":40}""");

        CodexRateLimitParser.ReadAccountFields(document.RootElement, out string? plan, out double? credits, out double? reset);

        Assert.Equal("pro", plan);
        Assert.Equal(12.5d, credits);
        Assert.Equal(40d, reset);
    }

    [Fact]
    public void ANonDefaultFamilyIsNamedInItsLabel()
    {
        IReadOnlyList<CodexLimitWindow> windows = Parse(
            """{"rateLimitsByLimitId":{"codex_bengalfox":{"primary":{"usedPercent":22,"windowDurationMins":300}}}}""");

        UsageMetric metric = Assert.Single(CodexMetricFactory.Build(windows, MetricConfidence.BestEffort));

        Assert.Equal("codex_bengalfox:300", metric.Key);
        Assert.Contains("codex_bengalfox", metric.Label, StringComparison.Ordinal);
        Assert.Contains("5 hour", metric.Label, StringComparison.Ordinal);
    }
}
