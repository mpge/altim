using Altim.Providers.Codex.Limits;

namespace Altim.Providers.Codex.Rollout;

/// <summary>
/// What one rollout line reduced to.
/// </summary>
/// <remarks>
/// <para>
/// Rollout files hold the full conversation: prompts, model reasoning, file contents,
/// command output, patches, and the working directory the session ran in. This record is
/// the entire vocabulary the reader has for describing one of those lines — five numbers,
/// two instants, a model id and a list of quota windows. There is no field on it that
/// could hold a sentence, so nothing else can escape the parse.
/// </para>
/// <para>
/// <paramref name="CumulativeTokens"/> and <paramref name="TurnTokens"/> are kept apart
/// because confusing them is expensive: the cumulative figure restates the whole session
/// on every line, so summing it across lines overcounts badly. The reader sums turn deltas,
/// or takes the last cumulative value once per session, never both.
/// </para>
/// </remarks>
/// <param name="ObservedAt">The line's own timestamp, when it carried one.</param>
/// <param name="Windows">Quota windows the line reported. Empty when it reported none.</param>
/// <param name="CumulativeTokens">
/// Tokens for the session so far, as of this line. <see langword="null"/> when the line did
/// not report them.
/// </param>
/// <param name="TurnTokens">Tokens attributable to this turn alone, when reported.</param>
/// <param name="ModelContextWindow">The model's context window in tokens, when reported.</param>
/// <param name="ModelId">The model the turn ran against, when reported.</param>
public sealed record CodexRolloutRecord(
    DateTimeOffset? ObservedAt,
    IReadOnlyList<CodexLimitWindow> Windows,
    CodexTokenCounts? CumulativeTokens,
    CodexTokenCounts? TurnTokens,
    long? ModelContextWindow,
    string? ModelId);

/// <summary>
/// Codex token counts, as reported.
/// </summary>
/// <param name="Input">Uncached input tokens.</param>
/// <param name="CachedInput">Input tokens served from cache.</param>
/// <param name="Output">Generated output tokens, including reasoning output.</param>
/// <param name="ReasoningOutput">
/// The reasoning portion of the output, when reported separately. It is a subset of
/// <paramref name="Output"/> and is never added to it.
/// </param>
/// <param name="Total">The provider's own total, when reported.</param>
public readonly record struct CodexTokenCounts(long? Input, long? CachedInput, long? Output, long? ReasoningOutput, long? Total)
{
    /// <summary>True when at least one component was reported.</summary>
    public bool HasAny => Input is not null || CachedInput is not null || Output is not null || Total is not null;
}
