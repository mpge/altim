namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// The numbers Claude Code hands its status-line command, reduced to a record.
/// </summary>
/// <remarks>
/// <para>
/// This is the only documented local source of reset timestamps for the five-hour and
/// weekly windows, and it costs nothing: Claude Code is already invoking the command.
/// </para>
/// <para>
/// A window absent from the payload means <b>no data</b>, not zero used. Windows are dropped
/// once their reset instant passes, so a null here is rendered as "not reported" and no
/// meter is drawn, rather than a reassuring empty bar.
/// </para>
/// <para>
/// Every field is a number or an instant. The payload also carries the working directory,
/// the project, the model's display name and the transcript path; none of them is read.
/// </para>
/// </remarks>
/// <param name="WrittenAt">
/// When the state file was written. Falls back to the file's last-write time when the
/// payload did not carry one, so staleness can always be stated.
/// </param>
/// <param name="FiveHourUsedPercent">Five-hour window usage, when reported and plausible.</param>
/// <param name="FiveHourResetsAt">When the five-hour window rolls over, when reported.</param>
/// <param name="SevenDayUsedPercent">Weekly window usage, when reported and plausible.</param>
/// <param name="SevenDayResetsAt">When the weekly window rolls over, when reported.</param>
/// <param name="SpendLimitUsedPercent">Spend-limit usage, when reported and plausible.</param>
/// <param name="SpendLimitResetsAt">When the spend limit rolls over, when reported.</param>
/// <param name="SessionCostUsd">The session's cost so far in US dollars, when reported.</param>
/// <param name="ContextUsedTokens">Tokens currently in the context window, when reported.</param>
/// <param name="ContextMaxTokens">The context window's size, when reported.</param>
/// <param name="PromptCacheReadTokens">Tokens served from the prompt cache, when reported.</param>
/// <param name="PromptCacheCreationTokens">Tokens written to the prompt cache, when reported.</param>
/// <param name="ModelId">The model the session is running, when it is a valid identifier.</param>
/// <param name="SessionId">The session's identifier, when reported.</param>
public sealed record ClaudeStatusLineState(
    DateTimeOffset? WrittenAt,
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUsedPercent,
    DateTimeOffset? SevenDayResetsAt,
    double? SpendLimitUsedPercent,
    DateTimeOffset? SpendLimitResetsAt,
    double? SessionCostUsd,
    long? ContextUsedTokens,
    long? ContextMaxTokens,
    long? PromptCacheReadTokens,
    long? PromptCacheCreationTokens,
    string? ModelId,
    string? SessionId)
{
    /// <summary>True when at least one rate-limit window was reported.</summary>
    public bool HasRateLimits =>
        FiveHourUsedPercent is not null
        || SevenDayUsedPercent is not null
        || SpendLimitUsedPercent is not null
        || FiveHourResetsAt is not null
        || SevenDayResetsAt is not null;

    /// <summary>
    /// The portion of the context window in use, when both halves were reported.
    /// </summary>
    public double? ContextUsedPercent =>
        ContextUsedTokens is { } used && ContextMaxTokens is > 0
            ? Math.Round(used * 100d / ContextMaxTokens.Value, 2)
            : null;
}
