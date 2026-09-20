using Altim.Core.Updates;

namespace Altim.Core.Abstractions;

/// <summary>
/// Finding out whether a newer Altim exists, and where the installation shape allows it,
/// fetching one.
/// </summary>
/// <remarks>
/// <para>
/// Two different things sit behind this on purpose. A Velopack install can fetch a release
/// and stage it, so <see cref="FetchAsync"/> means something there. Every other shape — a
/// portable unzip, a <c>.deb</c>, a DMG dragged to Applications, a developer's
/// <c>dotnet run</c> — can only find out and say so, because replacing those is the
/// package manager's job or the user's. An implementation that cannot fetch reports
/// <see cref="UpdateState.Unsupported"/> rather than pretending, which is the same rule
/// the providers follow about metrics they cannot read.
/// </para>
/// <para>
/// Nothing here runs on its own. The caller decides when, from
/// <see cref="UpdateSchedule"/> and the user's setting, so that the one place Altim
/// reaches the internet is a policy somebody can read rather than a timer inside a
/// service.
/// </para>
/// </remarks>
public interface IUpdateService
{
    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>What the last check found. Never null.</summary>
    UpdateStatus Current { get; }

    /// <summary>The version this process is running.</summary>
    ReleaseVersion? Running { get; }

    /// <summary>Where a human goes to read about and download releases.</summary>
    Uri ReleasesPage { get; }

    /// <summary>
    /// Asks whether a newer release exists.
    /// </summary>
    /// <param name="ct">Cancels the check.</param>
    /// <returns>The status after the check.</returns>
    /// <remarks>
    /// Never throws for an ordinary failure — no network, a rate limit, a malformed feed —
    /// and reports <see cref="UpdateState.Failed"/> instead. The caller is a background
    /// tick and a settings button, and neither has anywhere to put an exception.
    /// </remarks>
    ValueTask<UpdateStatus> CheckAsync(CancellationToken ct);

    /// <summary>
    /// Fetches the release found by the last check, so that it applies on the next start.
    /// </summary>
    /// <param name="ct">Cancels the fetch.</param>
    /// <returns>The status after the attempt.</returns>
    /// <remarks>
    /// A no-op that returns <see cref="Current"/> unchanged when there is nothing to fetch
    /// or when this installation cannot apply one.
    /// </remarks>
    ValueTask<UpdateStatus> FetchAsync(CancellationToken ct);
}
