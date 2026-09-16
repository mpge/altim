namespace Altim.Providers.Claude.Transcripts;

/// <summary>
/// Token counts for one model, or for one bucket of a scan.
/// </summary>
/// <param name="Input">Uncached input tokens.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Tokens served from the prompt cache.</param>
/// <param name="CacheCreation5m">Tokens written to the five-minute cache tier.</param>
/// <param name="CacheCreation1h">
/// Tokens written to the one-hour cache tier. Priced at twice the five-minute rate, which
/// is why it is never merged with it.
/// </param>
/// <param name="CacheCreationUnsplit">
/// Cache-creation tokens from a line that reported only the flat field. These cannot be
/// priced exactly, so they are reported apart from the tiered figures rather than folded in.
/// </param>
/// <param name="MessageCount">Distinct messages counted.</param>
public readonly record struct ClaudeTokenBucket(
    long Input,
    long Output,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long CacheCreationUnsplit,
    int MessageCount)
{
    /// <summary>Adds one de-duplicated line to the bucket.</summary>
    /// <param name="line">The line.</param>
    public ClaudeTokenBucket Add(ClaudeUsageLine line) => new(
        Input + (line.InputTokens ?? 0),
        Output + (line.OutputTokens ?? 0),
        CacheRead + (line.CacheReadTokens ?? 0),
        CacheCreation5m + (line.CacheCreation5mTokens ?? 0),
        CacheCreation1h + (line.CacheCreation1hTokens ?? 0),
        CacheCreationUnsplit + (line.CacheCreationUnsplitTokens ?? 0),
        MessageCount + 1);

    /// <summary>Total cache-creation tokens across both tiers and the unsplit remainder.</summary>
    public long CacheCreationTotal => CacheCreation5m + CacheCreation1h + CacheCreationUnsplit;
}

/// <summary>
/// The result of scanning transcripts.
/// </summary>
/// <remarks>
/// These are <b>locally observed</b> figures. Local sums were measured about 16 per cent
/// apart from server accounting, and transcripts are pruned after about 30 days, so this is
/// never a lifetime total and is never presented as the provider's own number.
/// </remarks>
/// <param name="Totals">Counts across every model.</param>
/// <param name="ByModel">
/// Counts per model id, keyed by the id exactly as written, so a long-context
/// <c>[1m]</c> variant stays separate from its base model. They price differently.
/// </param>
/// <param name="BySession">Counts per session id, for sessions that reported one.</param>
/// <param name="DuplicateMessagesDropped">
/// How many lines repeated an already-counted message id. Expected to be large: this is the
/// 3.15-times overcount that a naive sum would have produced, made visible.
/// </param>
/// <param name="SyntheticMessagesExcluded">
/// How many lines named something that is not a model and were excluded from cost.
/// </param>
/// <param name="MessagesWithoutIdentity">
/// How many counted lines carried no message id and so could not be de-duplicated. Reported
/// rather than hidden, because it is the one input that could still inflate a total.
/// </param>
/// <param name="FilesScanned">How many files contributed at least one new byte this pass.</param>
/// <param name="SubagentFilesScanned">How many of those were subagent transcripts.</param>
/// <param name="CorruptLinesSkipped">How many lines were malformed and skipped.</param>
/// <param name="EarliestAt">The oldest timestamp seen, when any line carried one.</param>
/// <param name="LatestAt">The newest timestamp seen, when any line carried one.</param>
public sealed record ClaudeTokenHistory(
    ClaudeTokenBucket Totals,
    IReadOnlyDictionary<string, ClaudeTokenBucket> ByModel,
    IReadOnlyDictionary<string, ClaudeTokenBucket> BySession,
    int DuplicateMessagesDropped,
    int SyntheticMessagesExcluded,
    int MessagesWithoutIdentity,
    int FilesScanned,
    int SubagentFilesScanned,
    int CorruptLinesSkipped,
    DateTimeOffset? EarliestAt,
    DateTimeOffset? LatestAt)
{
    /// <summary>An empty history.</summary>
    public static ClaudeTokenHistory Empty { get; } = new(
        default,
        new Dictionary<string, ClaudeTokenBucket>(StringComparer.Ordinal),
        new Dictionary<string, ClaudeTokenBucket>(StringComparer.Ordinal),
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null);

    /// <summary>
    /// True when a model id denotes the long-context variant, which carries its own pricing.
    /// </summary>
    /// <param name="modelId">The model id as written in the transcript.</param>
    public static bool IsLongContext(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return modelId.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The base model id, with any long-context suffix removed.
    /// </summary>
    /// <param name="modelId">The model id as written in the transcript.</param>
    public static string BaseModelId(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return IsLongContext(modelId) ? modelId[..^"[1m]".Length] : modelId;
    }
}
