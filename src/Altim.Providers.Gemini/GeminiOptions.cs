namespace Altim.Providers.Gemini;

/// <summary>
/// What the Gemini CLI reader is allowed to do, and how hard it is allowed to work.
/// </summary>
/// <remarks>
/// <para>
/// There is no network option here and no call timeout, because this reader makes no network
/// call and starts no process. Every figure it reports comes from reading bytes that Gemini
/// CLI has already written to disk. The CLI's own quota figures exist only inside a running
/// <c>gemini</c> process and are never written down, so there is nothing for a gate to gate.
/// See <c>PROVIDERS.md</c>.
/// </para>
/// <para>
/// The budgets matter more here than for the other two providers, because Gemini CLI's
/// session cleanup is a setting rather than a guarantee: <c>general.sessionRetention</c>
/// defaults to a 30-day policy in the documentation, the settings object it lives on defaults
/// to absent in the source, and the cleanup pass runs only for the project a session is
/// opened in. A store that nobody has pruned can therefore be arbitrarily large, and a
/// background utility that walks it is a bug.
/// </para>
/// </remarks>
public sealed record GeminiOptions
{
    /// <summary>The defaults.</summary>
    public static GeminiOptions Default { get; } = new();

    /// <summary>
    /// How far back the session scan looks for candidate files.
    /// </summary>
    /// <remarks>
    /// This bounds the work, not the answer. A day outside the window is absent from the
    /// history rather than reported as zero, and a file that has not been written to since
    /// the window opened has nothing new in it to read anyway.
    /// </remarks>
    public TimeSpan SessionWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The most session files one pass will read, newest first.
    /// </summary>
    public int MaxSessionFiles { get; init; } = 96;

    /// <summary>
    /// The most session files one pass will even look at the metadata of, as a guard against
    /// a store that has never been pruned.
    /// </summary>
    public int MaxSessionFilesEnumerated { get; init; } = 8192;

    /// <summary>
    /// The most project directories one pass will look inside.
    /// </summary>
    /// <remarks>
    /// Gemini CLI creates one directory under <c>tmp</c> per project the user has ever
    /// opened, and never removes it. The cap stops a long-lived installation turning each
    /// refresh into a walk over hundreds of directories.
    /// </remarks>
    public int MaxProjectDirectories { get; init; } = 512;

    /// <summary>
    /// How many message identities to remember for de-duplication.
    /// </summary>
    /// <remarks>
    /// A session file is append-only and the <b>same message id is appended more than
    /// once</b>: the recorder writes a model turn when it completes, then writes the whole
    /// record again when the response's usage figures arrive, and again when its tool calls
    /// are enriched. Gemini CLI's own loader keys messages by id for exactly this reason.
    /// Summing lines instead of distinct ids therefore counts the same tokens repeatedly.
    /// The set has to outlive a single pass, because an incremental scan boundary can fall
    /// between two lines that share an id.
    /// </remarks>
    public int MessageIdentityMemory { get; init; } = 16384;

    /// <summary>
    /// How many session files to remember the identity of, so that a later pass which reads
    /// only newly appended message lines can still attribute them to a session.
    /// </summary>
    /// <remarks>
    /// A session file's id and start instant are on its first line and nowhere else, so they
    /// are read once and kept. Past this many files the least recently seen is forgotten;
    /// its lines still count towards the totals and the per-day figures, and only its
    /// per-session attribution is lost.
    /// </remarks>
    public int SessionIdentityMemory { get; init; } = 512;

    /// <summary>
    /// How many sessions' running totals to remember. Past this the least recently seen is
    /// forgotten, so a long-lived process cannot grow this without bound.
    /// </summary>
    public int MaxRememberedSessions { get; init; } = 512;

    /// <summary>
    /// How recently a session file must have been written to for the provider to call itself
    /// active when no <c>gemini</c> process was detected.
    /// </summary>
    /// <remarks>
    /// Gemini CLI has no equivalent of the other two vendors' session listings, so liveness
    /// is a process scan or file recency and nothing more. Either is enough for the provider
    /// to call itself active; neither is evidence about a <em>particular</em> session, which
    /// is why no session row ever claims to be the running one.
    /// </remarks>
    public TimeSpan SessionActivityWindow { get; init; } = TimeSpan.FromMinutes(10);
}
