namespace Altim.Core.Updates;

/// <summary>What Altim currently knows about whether a newer version exists.</summary>
public enum UpdateState
{
    /// <summary>Nothing has been asked yet. The state a fresh process starts in.</summary>
    Unknown,

    /// <summary>
    /// Altim cannot find out on this installation, so it will not pretend to. A portable
    /// unzip, a <c>.deb</c> and a <c>dotnet run</c> all land here.
    /// </summary>
    Unsupported,

    /// <summary>The check ran and this is the newest release.</summary>
    UpToDate,

    /// <summary>A newer release exists and has not been fetched.</summary>
    Available,

    /// <summary>A newer release has been fetched and applies on the next start.</summary>
    Ready,

    /// <summary>The check was attempted and did not answer.</summary>
    Failed,
}

/// <summary>
/// The result of the last update check: what state it left, which version it found, and
/// when.
/// </summary>
/// <param name="State">What is known.</param>
/// <param name="Available">
/// The newer version, when one was found. Null in every other state — including
/// <see cref="UpdateState.Failed"/>, because a check that did not answer found nothing.
/// </param>
/// <param name="CheckedAt">
/// When the check ran, or null when it has not. Drives the cadence, so it is stamped on a
/// failure too: a machine with no network must not retry every tick.
/// </param>
/// <remarks>
/// Deliberately not a string on screen. The view decides the wording, because "checking
/// is off" and "this machine cannot check" read the same to code and very differently to
/// somebody deciding whether to go and look for themselves.
/// </remarks>
public sealed record UpdateStatus(
    UpdateState State,
    ReleaseVersion? Available,
    DateTimeOffset? CheckedAt)
{
    /// <summary>The state before anything has been asked.</summary>
    public static UpdateStatus Unknown { get; } = new(UpdateState.Unknown, null, null);

    /// <summary>True when there is a newer version, whether or not it has been fetched.</summary>
    public bool HasUpdate => State is UpdateState.Available or UpdateState.Ready;
}
