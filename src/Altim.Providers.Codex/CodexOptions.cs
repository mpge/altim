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
    /// <remarks>
    /// This is the fallback floor, used when no <see cref="Core.Abstractions.IRefreshGate"/>
    /// is supplied. When one is, the gate owns the decision and this value only describes
    /// how long a live reading is presented as current.
    /// </remarks>
    public TimeSpan MinimumLiveCallInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a successful live reading stays the answer after the call that produced it.
    /// </summary>
    /// <remarks>
    /// A skipped call is not a missing reading. The floor exists so the CLI is not asked
    /// more than once a minute, and inside that minute the last live figure is at most
    /// sixty seconds old — fresher, usually by hours, than the local rollout snapshot.
    /// Falling back to the local file on every skipped tick made the popup alternate
    /// between a live reading and a stale one, flip its status line, and write alternating
    /// rows into the history table. The retention is longer than the floor so that one
    /// failed or slow call does not immediately demote a good reading.
    /// </remarks>
    public TimeSpan LiveSnapshotRetention { get; init; } = TimeSpan.FromMinutes(5);

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
