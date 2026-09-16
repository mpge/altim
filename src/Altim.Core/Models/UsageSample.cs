namespace Altim.Core.Models;

/// <summary>
/// One historical row: the value of one metric of one provider at one instant. This
/// is the shape stored in the <c>usage_sample</c> table, where every nullable member
/// below is a nullable column, so an unknown stays unknown on the way back out.
/// </summary>
/// <param name="ProviderId">
/// The provider the sample belongs to.
/// </param>
/// <param name="MetricKey">
/// The <see cref="UsageMetric.Key"/> the sample belongs to.
/// </param>
/// <param name="CapturedAt">
/// When the sample was taken. Persisted as Unix seconds in UTC.
/// </param>
/// <param name="UsedPercent">
/// The reading. <see langword="null"/> means the provider did not report a value at
/// that instant. It is never stored, nor read back, as zero.
/// </param>
/// <param name="WindowLength">
/// The limit window in force at that instant. <see langword="null"/> means the
/// provider did not report one.
/// </param>
/// <param name="ResetsAt">
/// When that window was due to roll over. <see langword="null"/> means not reported.
/// </param>
/// <param name="Tokens">
/// Token totals captured alongside the reading. <see langword="null"/> means the
/// provider does not report tokens at all; individual components may be null on their
/// own when only some are reported.
/// </param>
public sealed record UsageSample(
    string ProviderId,
    string MetricKey,
    DateTimeOffset CapturedAt,
    double? UsedPercent,
    TimeSpan? WindowLength,
    DateTimeOffset? ResetsAt,
    TokenTotals? Tokens);
