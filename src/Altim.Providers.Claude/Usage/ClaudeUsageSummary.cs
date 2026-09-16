namespace Altim.Providers.Claude.Usage;

/// <summary>
/// What the headless usage summary yielded.
/// </summary>
/// <remarks>
/// Every field is independently nullable, because the summary is human-readable text whose
/// wording is not a contract. A line that does not match its anchored pattern leaves its
/// field null, and null is rendered as "not reported by this provider". There is no
/// fallback that guesses a number out of a line the parser did not recognise.
/// </remarks>
/// <param name="SessionUsedPercent">The rolling session window, when the text stated one.</param>
/// <param name="WeeklyUsedPercent">The weekly window across all models, when stated.</param>
/// <param name="WeeklyOpusUsedPercent">
/// The Opus-only weekly window, when stated. This is the figure the status line does not
/// expose, and it is the reason this reader exists at all.
/// </param>
/// <param name="WeeklySonnetUsedPercent">The Sonnet-only weekly window, when stated.</param>
public sealed record ClaudeUsageSummary(
    double? SessionUsedPercent,
    double? WeeklyUsedPercent,
    double? WeeklyOpusUsedPercent,
    double? WeeklySonnetUsedPercent)
{
    /// <summary>A summary in which nothing was recognised.</summary>
    public static ClaudeUsageSummary Empty { get; } = new(null, null, null, null);

    /// <summary>True when at least one window was recognised.</summary>
    public bool HasAny =>
        SessionUsedPercent is not null
        || WeeklyUsedPercent is not null
        || WeeklyOpusUsedPercent is not null
        || WeeklySonnetUsedPercent is not null;
}
