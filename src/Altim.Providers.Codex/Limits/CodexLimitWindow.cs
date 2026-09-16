namespace Altim.Providers.Codex.Limits;

/// <summary>
/// One reported quota window, normalised out of whichever dialect produced it.
/// </summary>
/// <remarks>
/// <para>
/// Codex spells the same three concepts two ways. The rollout files write
/// <c>used_percent</c>, <c>window_minutes</c> and <c>resets_at</c>; the app-server writes
/// <c>usedPercent</c>, <c>windowDurationMins</c> and <c>resetsAt</c>. Both arrive here as
/// the same shape, so nothing downstream has to know which source a reading came from.
/// </para>
/// <para>
/// There is no slot name on this record, on purpose. A window is identified by its family
/// and its length, because slot position has been observed to move: the <c>codex</c>
/// family reported 300 minutes in its primary slot in 2025-12 and 10,080 minutes in the
/// same slot in 2026-09.
/// </para>
/// <para>
/// Every field is a number, an instant or a short identifier.
/// </para>
/// </remarks>
/// <param name="LimitId">
/// The limit family, for example <c>codex</c>, <c>codex_bengalfox</c> or <c>premium</c>.
/// This is the key of <c>rateLimitsByLimitId</c>, or <c>codex</c> when a source reports a
/// single unnamed family.
/// </param>
/// <param name="WindowMinutes">
/// The window length as reported, before normalisation. Kept raw here so a caller can see
/// what the provider actually said; keys and labels use the normalised value.
/// </param>
/// <param name="UsedPercent">
/// Portion consumed. <see langword="null"/> when the provider did not report it or
/// reported a value outside the plausible range, which is how the known
/// timestamp-in-the-percentage defect is absorbed.
/// </param>
/// <param name="ResetsAt">
/// When the window rolls over. <see langword="null"/> when not reported; never computed
/// from the length.
/// </param>
/// <param name="LimitName">
/// The provider's own name for the family, when it reported one that is short enough to be
/// an identifier. Display only.
/// </param>
public sealed record CodexLimitWindow(
    string LimitId,
    long WindowMinutes,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    string? LimitName);
