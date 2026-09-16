namespace Altim.Providers.Claude.Transcripts;

/// <summary>
/// What one <c>assistant</c> transcript line reduced to.
/// </summary>
/// <remarks>
/// <para>
/// A transcript line carries the prompt, the model's reasoning, tool input and output, file
/// contents, patches and the working directory. This record is the entire vocabulary the
/// reader has for one: six counts, one instant, three opaque identifiers and two booleans.
/// There is no field on it that can hold a sentence, a path or a URL.
/// </para>
/// <para>
/// The two cache-creation tiers are separate fields rather than one, because they are
/// separate prices. The one-hour tier bills at twice the five-minute rate and is the
/// dominant one in practice, so collapsing them — or pricing the flat
/// <c>cache_creation_input_tokens</c> field that sits alongside them — under-reports cost.
/// </para>
/// </remarks>
/// <param name="MessageId">
/// <c>message.id</c>. The de-duplication key: content blocks repeat the same usage object
/// under one id, and one measured transcript had 642 lines carrying 267 distinct ids.
/// </param>
/// <param name="RequestId">The request the message belonged to, when reported.</param>
/// <param name="SessionId">The session the line belongs to, when reported.</param>
/// <param name="ModelId">
/// The model, when it is a recognisable identifier. <see langword="null"/> together with
/// <paramref name="IsSynthetic"/> set means the line named a model that is not one.
/// </param>
/// <param name="IsSynthetic">
/// True when the model field held something that is not a model id, which is how
/// locally-generated <c>&lt;synthetic&gt;</c> placeholders present. These are excluded from
/// cost: no request was made, so no money was spent.
/// </param>
/// <param name="IsSidechain">True when the line came from a subagent transcript.</param>
/// <param name="InputTokens">Uncached input tokens, when reported.</param>
/// <param name="OutputTokens">Output tokens, when reported.</param>
/// <param name="CacheReadTokens">Tokens served from the prompt cache, when reported.</param>
/// <param name="CacheCreation5mTokens">
/// <c>cache_creation.ephemeral_5m_input_tokens</c>, when reported.
/// </param>
/// <param name="CacheCreation1hTokens">
/// <c>cache_creation.ephemeral_1h_input_tokens</c>, when reported. Bills at twice the
/// five-minute rate.
/// </param>
/// <param name="CacheCreationUnsplitTokens">
/// The flat <c>cache_creation_input_tokens</c> field, read <b>only</b> when the split object
/// is absent. Kept apart from the tiers so a consumer can tell a real tier breakdown from a
/// total that cannot be priced accurately.
/// </param>
/// <param name="Timestamp">The line's own timestamp, when it carried one.</param>
public readonly record struct ClaudeUsageLine(
    string? MessageId,
    string? RequestId,
    string? SessionId,
    string? ModelId,
    bool IsSynthetic,
    bool IsSidechain,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheCreation5mTokens,
    long? CacheCreation1hTokens,
    long? CacheCreationUnsplitTokens,
    DateTimeOffset? Timestamp)
{
    /// <summary>
    /// The identity used for de-duplication, or <see langword="null"/> when the line
    /// carried no message id.
    /// </summary>
    public string? Identity => MessageId is null
        ? null
        : string.Concat(SessionId ?? string.Empty, "|", RequestId ?? string.Empty, "|", MessageId);

    /// <summary>True when at least one token count was reported.</summary>
    public bool HasTokens =>
        InputTokens is not null
        || OutputTokens is not null
        || CacheReadTokens is not null
        || CacheCreation5mTokens is not null
        || CacheCreation1hTokens is not null
        || CacheCreationUnsplitTokens is not null;
}
