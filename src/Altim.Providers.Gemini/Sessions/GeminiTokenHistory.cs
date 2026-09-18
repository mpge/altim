using Altim.Core.Models;

namespace Altim.Providers.Gemini.Sessions;

/// <summary>
/// Token counts for one model, one session, one day, or one whole scan, in Gemini CLI's own
/// units.
/// </summary>
/// <remarks>
/// <para>
/// The fields are the provider's, not Altim's, and they overlap on purpose.
/// <see cref="Prompt"/> includes <see cref="Cached"/>, because Google documents
/// <c>promptTokenCount</c> as the total effective prompt size "meaning this includes the
/// number of tokens in the cached content". Converting to Altim's four components happens in
/// one place, <see cref="ToTotals"/>, so there is exactly one piece of arithmetic to get
/// wrong and exactly one place to test it.
/// </para>
/// <para>
/// <see cref="Tool"/> is accumulated and reported but is not part of that conversion. Google
/// documents <c>totalTokenCount</c> as "prompt + thoughts + response candidates" and does not
/// say where tool-use prompt tokens sit, so folding them in could count tokens that
/// <see cref="Prompt"/> already counted. Leaving a component out under-reports; inventing one
/// misreports, and this document's rule is that the first is preferable.
/// </para>
/// </remarks>
/// <param name="Prompt">
/// The API's <c>promptTokenCount</c>, summed. Includes <paramref name="Cached"/>.
/// </param>
/// <param name="Output">The API's <c>candidatesTokenCount</c>, summed.</param>
/// <param name="Cached">The API's <c>cachedContentTokenCount</c>, summed.</param>
/// <param name="Thoughts">The API's <c>thoughtsTokenCount</c>, summed.</param>
/// <param name="Tool">The API's <c>toolUsePromptTokenCount</c>, summed.</param>
/// <param name="Total">The API's <c>totalTokenCount</c>, summed.</param>
/// <param name="MessageCount">Distinct messages counted.</param>
public readonly record struct GeminiTokenBucket(
    long Prompt,
    long Output,
    long Cached,
    long Thoughts,
    long Tool,
    long Total,
    int MessageCount)
{
    /// <summary>Adds one de-duplicated line to the bucket.</summary>
    /// <param name="line">The line.</param>
    public GeminiTokenBucket Add(GeminiSessionLine line) => new(
        Prompt + (line.PromptTokens ?? 0),
        Output + (line.OutputTokens ?? 0),
        Cached + (line.CachedTokens ?? 0),
        Thoughts + (line.ThoughtsTokens ?? 0),
        Tool + (line.ToolTokens ?? 0),
        Total + (line.TotalTokens ?? 0),
        MessageCount + 1);

    /// <summary>Adds another bucket to this one.</summary>
    /// <param name="other">The bucket to add.</param>
    public GeminiTokenBucket Add(GeminiTokenBucket other) => new(
        Prompt + other.Prompt,
        Output + other.Output,
        Cached + other.Cached,
        Thoughts + other.Thoughts,
        Tool + other.Tool,
        Total + other.Total,
        MessageCount + other.MessageCount);

    /// <summary>
    /// The prompt tokens that were <b>not</b> served from cache, which is what Altim's input
    /// component means.
    /// </summary>
    /// <remarks>
    /// Clamped at zero. A file reporting more cached tokens than prompt tokens is a shape
    /// this reader does not understand, and a negative token count is worse than a low one.
    /// </remarks>
    public long UncachedPrompt => Math.Max(0, Prompt - Cached);

    /// <summary>
    /// Everything the model generated: the response plus its reasoning tokens.
    /// </summary>
    /// <remarks>
    /// Reasoning tokens are generated rather than sent, and Google counts them inside the
    /// request total alongside the candidates. Leaving them out would report a thinking
    /// model as having produced a fraction of what it did.
    /// </remarks>
    public long GeneratedOutput => Output + Thoughts;

    /// <summary>
    /// The bucket as Altim's four components. This is the only conversion from Gemini's
    /// units to Altim's, and the only place the overlap between them is resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input is the uncached part of the prompt, cache-read is the cached part, and output
    /// is the response plus its reasoning tokens. Those three add up to
    /// <c>prompt + candidates + thoughts</c>, which is exactly what Google documents
    /// <c>totalTokenCount</c> to be, so the components sum to the provider's own total
    /// rather than to something Altim made up.
    /// </para>
    /// <para>
    /// Cache-write is <see langword="null"/>, and not zero. Gemini's response usage reports
    /// how much of a prompt was served from cache and never how much was written into one;
    /// explicit cache creation is a separate API call the CLI does not record. Reporting
    /// zero would claim the cache was never written to, which is not something this reader
    /// knows.
    /// </para>
    /// </remarks>
    public TokenTotals ToTotals() => new(UncachedPrompt, GeneratedOutput, Cached, null);
}

/// <summary>
/// One session file's identity, as the scan learned it.
/// </summary>
/// <remarks>
/// A session file's id and start instant are written on its first line and never repeated,
/// so a pass that resumes part way through a growing file will not see them. That is why
/// both instants are optional here and why the scanner remembers what it has already read.
/// </remarks>
/// <param name="SessionId">The session's own opaque identifier.</param>
/// <param name="StartedAt">
/// When the session began, when the metadata line has been read. <see langword="null"/>
/// means the scan has not seen that line, never that the session started now.
/// </param>
/// <param name="LastActivityAt">
/// The newest instant any line of the file carried this pass. <see langword="null"/> when
/// no line carried one.
/// </param>
/// <param name="ModelId">The last model the file named, when it named one.</param>
/// <param name="IsSubagent">True when the file is a subagent's rather than a main session's.</param>
public sealed record GeminiSessionRecord(
    string SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt,
    string? ModelId,
    bool IsSubagent);

/// <summary>
/// The result of scanning Gemini CLI session files.
/// </summary>
/// <remarks>
/// These are <b>locally observed</b> figures taken from a format the vendor does not
/// document and has already changed once: the same files were one JSON document per session
/// with no token counts in them at all a few months and seventy-odd releases ago. Every
/// field is optional at the parse and an unexpected shape degrades a figure to unavailable
/// rather than failing the scan.
/// </remarks>
/// <param name="Totals">Counts across every session and model.</param>
/// <param name="ByModel">Counts per model id, keyed by the id exactly as written.</param>
/// <param name="BySession">
/// Counts per session id, for files whose metadata line this scanner has read.
/// </param>
/// <param name="ByDay">
/// Counts per <b>local</b> calendar day, for lines that carried a timestamp. The day is the
/// one the user was living in when the line was written, because that is the day they will
/// look for it on.
/// <para>
/// A line with no timestamp is counted in <paramref name="Totals"/> and appears in no day at
/// all. It happened; Altim cannot say when, and putting it on today would be inventing that.
/// </para>
/// <para>
/// A day with no entry is a day this scan could not account for. It is <b>absent</b>, not
/// present with zeroes: absent means unknown, zero means a day that used nothing.
/// </para>
/// </param>
/// <param name="Sessions">One entry per session file that had new bytes this pass.</param>
/// <param name="DuplicateMessagesDropped">
/// How many lines repeated an already-counted message id. Expected to be substantial: the
/// recorder rewrites a model turn when its usage figures arrive, so every turn that reported
/// tokens was written at least twice.
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
public sealed record GeminiTokenHistory(
    GeminiTokenBucket Totals,
    IReadOnlyDictionary<string, GeminiTokenBucket> ByModel,
    IReadOnlyDictionary<string, GeminiTokenBucket> BySession,
    IReadOnlyDictionary<DateOnly, GeminiTokenBucket> ByDay,
    IReadOnlyList<GeminiSessionRecord> Sessions,
    int DuplicateMessagesDropped,
    int MessagesWithoutIdentity,
    int FilesScanned,
    int SubagentFilesScanned,
    int CorruptLinesSkipped,
    DateTimeOffset? EarliestAt,
    DateTimeOffset? LatestAt)
{
    /// <summary>An empty history.</summary>
    public static GeminiTokenHistory Empty { get; } = new(
        default,
        new Dictionary<string, GeminiTokenBucket>(StringComparer.Ordinal),
        new Dictionary<string, GeminiTokenBucket>(StringComparer.Ordinal),
        new Dictionary<DateOnly, GeminiTokenBucket>(),
        [],
        0,
        0,
        0,
        0,
        0,
        null,
        null);
}
