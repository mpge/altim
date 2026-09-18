namespace Altim.Providers.Gemini.Sessions;

/// <summary>
/// Which of the three kinds of record a session file line turned out to be.
/// </summary>
/// <remarks>
/// A Gemini CLI session file is append-only JSON Lines holding a metadata object, message
/// records, metadata patches and rewind markers, all in one stream. Altim reads two of them
/// and declines the rest.
/// </remarks>
public enum GeminiLineKind
{
    /// <summary>A line this reader does not care about, which is most of them.</summary>
    Other = 0,

    /// <summary>
    /// The file's own metadata: its session id and, on the first line, its start instant.
    /// It is the only place a session id appears in the file.
    /// </summary>
    Metadata = 1,

    /// <summary>A model turn, which may carry that response's token counts and its model.</summary>
    Message = 2,
}

/// <summary>
/// What one line of a Gemini CLI session file reduced to.
/// </summary>
/// <remarks>
/// <para>
/// A session line carries the user's prompt, the model's reasoning, tool arguments, tool
/// output, file contents and, on the metadata line, <b>the absolute paths of every workspace
/// directory the session was given</b>. This record is the entire vocabulary the reader has
/// for one: six counts, two instants, two opaque identifiers and one enum. There is no field
/// on it that can hold a sentence, a path, a project name or a project hash.
/// </para>
/// <para>
/// The counts are kept exactly as Gemini CLI wrote them, in Gemini's own units, and are
/// converted to Altim's four components only at the boundary where the contract needs them.
/// That matters because the units overlap: <c>input</c> is the API's
/// <c>promptTokenCount</c>, which Google documents as including the cached part of the
/// prompt, so a reader that added <c>input</c> and <c>cached</c> together would count the
/// cached tokens twice.
/// </para>
/// </remarks>
/// <param name="Kind">Which kind of record the line was.</param>
/// <param name="SessionId">
/// The session the file belongs to, from a metadata line. <see langword="null"/> on every
/// other kind of line, because message records do not repeat it.
/// </param>
/// <param name="MessageId">
/// The message's own id, from a message line. The de-duplication key: the recorder appends
/// the same message again when its token counts arrive and again when its tool calls are
/// enriched, so lines repeat ids and Gemini CLI's own loader keys messages by id.
/// </param>
/// <param name="ModelId">
/// The model the turn ran against, when it was a recognisable identifier.
/// </param>
/// <param name="StartedAt">The session's start instant, from a metadata line.</param>
/// <param name="Timestamp">
/// A message line's own instant, or a metadata line's last-updated instant.
/// </param>
/// <param name="PromptTokens">
/// <c>tokens.input</c>, the API's <c>promptTokenCount</c>. <b>Includes</b>
/// <paramref name="CachedTokens"/>.
/// </param>
/// <param name="OutputTokens">
/// <c>tokens.output</c>, the API's <c>candidatesTokenCount</c>: the visible response.
/// </param>
/// <param name="CachedTokens">
/// <c>tokens.cached</c>, the API's <c>cachedContentTokenCount</c>: the part of the prompt
/// that was served from cache.
/// </param>
/// <param name="ThoughtsTokens">
/// <c>tokens.thoughts</c>, the API's <c>thoughtsTokenCount</c>: reasoning tokens, which are
/// generated rather than sent and which Google counts inside the request total.
/// </param>
/// <param name="ToolTokens">
/// <c>tokens.tool</c>, the API's <c>toolUsePromptTokenCount</c>. Read and reported, and
/// deliberately <b>not</b> folded into any of Altim's four components: Google does not say
/// whether these tokens are already inside <c>promptTokenCount</c>, and adding them would
/// risk counting the same tokens twice.
/// </param>
/// <param name="TotalTokens">
/// <c>tokens.total</c>, the API's <c>totalTokenCount</c>, which Google documents as prompt
/// plus thoughts plus response candidates. Carried so a consumer can check Altim's
/// components against the provider's own total rather than trusting the arithmetic.
/// </param>
public readonly record struct GeminiSessionLine(
    GeminiLineKind Kind,
    string? SessionId,
    string? MessageId,
    string? ModelId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? Timestamp,
    long? PromptTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? ThoughtsTokens,
    long? ToolTokens,
    long? TotalTokens)
{
    /// <summary>True when at least one token count was reported on this line.</summary>
    public bool HasTokens =>
        PromptTokens is not null
        || OutputTokens is not null
        || CachedTokens is not null
        || ThoughtsTokens is not null
        || ToolTokens is not null
        || TotalTokens is not null;
}
