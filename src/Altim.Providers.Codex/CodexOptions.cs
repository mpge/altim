namespace Altim.Providers.Codex;

/// <summary>
/// What the Codex reader is allowed to do, and how hard it is allowed to work.
/// </summary>
/// <remarks>
/// Defaults are the conservative ones. Nothing here makes the reader faster at the cost
/// of the machine: the budgets exist because the session store on the verification machine
/// was 28.3 GB across 2,518 files, and a background utility that walks that is a bug.
/// </remarks>
public sealed record CodexOptions
{
    /// <summary>The defaults.</summary>
    public static CodexOptions Default { get; } = new();

    /// <summary>
    /// Whether the live quota call may be made at all.
    /// </summary>
    /// <remarks>
    /// The app-server call is local IPC but it is not offline: the CLI contacts OpenAI
    /// with the user's stored token. Strict local-only mode turns this off and the reader
    /// falls back to the newest local snapshot, labelled with its age.
    /// </remarks>
    public bool AllowNetworkCalls { get; init; } = true;

    /// <summary>
    /// The shortest interval between two live quota calls. Never shorter than a minute.
    /// </summary>
    public TimeSpan MinimumLiveCallInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the app-server exchange may take before it is abandoned and killed.</summary>
    public TimeSpan LiveCallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long <c>codex doctor --json</c> may take.</summary>
    public TimeSpan DoctorTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many recent sessions to read for the offline fallback. Candidates come from the
    /// state database ordered by last activity, so this is the newest handful, not a sample
    /// of everything.
    /// </summary>
    public int MaxSessionsToRead { get; init; } = 12;

    /// <summary>
    /// How many bytes to read from the end of each rollout file. The last rate-limit
    /// record sits within about 1.2 KB of end-of-file.
    /// </summary>
    public int RolloutTailBytes { get; init; } = 8 * 1024;

    /// <summary>
    /// How old a local snapshot may be before the reader stops presenting it as a current
    /// reading. Beyond this the metrics are still returned, with their age stated.
    /// </summary>
    public TimeSpan LocalSnapshotFreshWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How recently a session must have been active to count as running when no process is
    /// detected.
    /// </summary>
    public TimeSpan SessionActivityWindow { get; init; } = TimeSpan.FromMinutes(10);
}
