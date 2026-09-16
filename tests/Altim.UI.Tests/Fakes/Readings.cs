using Altim.Core.Models;

namespace Altim.UI.Tests.Fakes;

/// <summary>
/// Builders for the readings a provider can hand the interface.
/// </summary>
/// <remarks>
/// Written out rather than generated, because the shapes that matter are the awkward ones: a
/// metric with no percentage, a reading with no metric at all, and a reading that failed.
/// </remarks>
internal static class Readings
{
    /// <summary>The instant every test clock starts at: an afternoon, so resets sit ahead of it.</summary>
    public static DateTimeOffset Now { get; } = new(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a metric.</summary>
    /// <param name="key">The provider's key for it.</param>
    /// <param name="label">Its label.</param>
    /// <param name="percent">Its percentage, or null when the provider does not report one.</param>
    /// <param name="window">Its window length, or null when it has none.</param>
    /// <param name="resetsAt">Its reset instant, or null when the provider does not report one.</param>
    public static UsageMetric Metric(
        string key,
        string label,
        double? percent,
        TimeSpan? window = null,
        DateTimeOffset? resetsAt = null) =>
        new(
            key,
            label,
            percent,
            window is { } length ? new LimitWindow(length, resetsAt) : null,
            MetricConfidence.Documented);

    /// <summary>A healthy reading carrying one session window and one weekly window.</summary>
    /// <param name="providerId">The provider the reading belongs to.</param>
    /// <param name="sessionPercent">The session window's percentage, which may be null.</param>
    public static ProviderUsage Healthy(string providerId, double? sessionPercent = 62d) =>
        new(
            providerId,
            ProviderStatus.Active,
            [
                Metric("five_hour", "Session", sessionPercent, TimeSpan.FromHours(5), Now.AddMinutes(134)),
                Metric("seven_day", "Weekly", 38d, TimeSpan.FromDays(7), Now.AddDays(3)),
            ],
            new TokenTotals(56_200, 12_800, 240_000, 9_000),
            Now.AddSeconds(-40),
            null);

    /// <summary>A reading that succeeded and carried no metric at all.</summary>
    /// <param name="providerId">The provider the reading belongs to.</param>
    public static ProviderUsage NoMetrics(string providerId) =>
        new(providerId, ProviderStatus.Idle, [], null, Now.AddMinutes(-1), null);

    /// <summary>A reading that could not be taken.</summary>
    /// <param name="providerId">The provider the reading belongs to.</param>
    public static ProviderUsage Failed(string providerId) =>
        new(providerId, ProviderStatus.Error, [], null, null, ProviderUsage.UnavailableDetail);

    /// <summary>One live agent session.</summary>
    /// <param name="providerId">The provider that reported it.</param>
    /// <param name="id">Its opaque identifier, which is never shown.</param>
    /// <param name="modelId">The model it runs, or null when the provider does not report one.</param>
    /// <param name="startedAt">When it began.</param>
    /// <param name="lastActivityAt">Its most recent activity, or null when not reported.</param>
    /// <param name="isActive">Whether it is working now.</param>
    /// <param name="tokens">Its token counts, or null when not reported.</param>
    public static AgentSession Session(
        string providerId,
        string id,
        string? modelId,
        DateTimeOffset startedAt,
        DateTimeOffset? lastActivityAt = null,
        bool isActive = true,
        TokenTotals? tokens = null) =>
        new(id, providerId, startedAt, lastActivityAt, tokens, modelId, isActive);

    /// <summary>A stored sample.</summary>
    /// <param name="providerId">The provider it belongs to.</param>
    /// <param name="metricKey">The metric it belongs to.</param>
    /// <param name="capturedAt">When it was written.</param>
    /// <param name="percent">The level recorded, which may be null.</param>
    /// <param name="window">The window length it describes.</param>
    /// <param name="resetsAt">When that window resets, or null when it was not reported.</param>
    public static UsageSample Sample(
        string providerId,
        string metricKey,
        DateTimeOffset capturedAt,
        double? percent,
        TimeSpan? window = null,
        DateTimeOffset? resetsAt = null) =>
        new(providerId, metricKey, capturedAt, percent, window, resetsAt, null);
}
