namespace Altim.Providers.Claude;

/// <summary>
/// What the Claude Code reader is allowed to do, and how hard it is allowed to work.
/// </summary>
public sealed record ClaudeOptions
{
    /// <summary>The defaults.</summary>
    public static ClaudeOptions Default { get; } = new();

    /// <summary>
    /// Whether the headless usage summary may be run.
    /// </summary>
    /// <remarks>
    /// <c>claude -p --output-format json "/usage"</c> consumes no tokens and costs nothing,
    /// but it does reach the network, so strict local-only mode turns it off. The status
    /// line and the transcripts are unaffected: both are purely local.
    /// </remarks>
    public bool AllowNetworkCalls { get; init; } = true;

    /// <summary>The shortest interval between two headless usage summaries.</summary>
    public TimeSpan MinimumSummaryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the headless usage summary may take.</summary>
    public TimeSpan SummaryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <c>claude agents --json</c> may take.</summary>
    public TimeSpan AgentsTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How stale the status-line state file may be before it stops counting as a live
    /// reading.
    /// </summary>
    /// <remarks>
    /// The status line is only written while a session is running. Past this age the file
    /// describes a session that has ended, so its percentages are reported with their age
    /// rather than as the current position.
    /// </remarks>
    public TimeSpan StatusLineFreshWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far back the transcript scan looks. Transcripts are pruned after about 30 days
    /// by default, so nothing beyond that is derivable from them anyway.
    /// </summary>
    public TimeSpan TranscriptWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The most transcript files one pass will read, newest first.
    /// </summary>
    /// <remarks>
    /// The verification machine held 2,069 transcripts, of which 2,056 were subagent files.
    /// Reading them all on every refresh is not something a tray utility gets to do; the
    /// incremental scanner means steady-state cost is the newly appended bytes of the few
    /// files that changed.
    /// </remarks>
    public int MaxTranscriptFiles { get; init; } = 96;

    /// <summary>
    /// The most transcript files one pass will even look at the metadata of, as a guard
    /// against a pathological store.
    /// </summary>
    public int MaxTranscriptFilesEnumerated { get; init; } = 8192;

    /// <summary>
    /// How many message identities to remember for de-duplication.
    /// </summary>
    /// <remarks>
    /// Content blocks repeat the same usage object, so the same <c>message.id</c> arrives
    /// many times: one measured transcript had 642 assistant lines carrying 267 distinct
    /// ids, and summing lines overcounted output tokens by 3.15 times. The set has to
    /// outlive a single pass, because an incremental scan boundary can fall between two
    /// lines that share an id.
    /// </remarks>
    public int MessageIdentityMemory { get; init; } = 16384;
}
