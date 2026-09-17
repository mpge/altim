using Altim.Core.Abstractions;

namespace Altim.UI.Tests.Fakes;

/// <summary>
/// A status-line seam that records what it was asked for and answers with whatever the test
/// has decided the user's Claude Code settings hold.
/// </summary>
/// <remarks>
/// The page is the thing under test, so this stands in for the settings file rather than
/// simulating one: a test that wants "the user already has their own status line" sets
/// <see cref="State"/> and never goes near a file.
/// </remarks>
internal sealed class FakeStatusLineService : IStatusLineService
{
    /// <summary>What an inspection reports, and what a change settles on.</summary>
    public StatusLineInstallState State { get; set; } = StatusLineInstallState.NotInstalled;

    /// <summary>The value of every <see cref="SetAsync"/> call, in order.</summary>
    public List<bool> Changes { get; } = [];

    /// <summary>How many inspections have happened.</summary>
    public int Inspections { get; private set; }

    /// <summary>Whether the next call throws, and with what.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// When set, a change is refused and leaves <see cref="State"/> where it was. Stands in
    /// for a settings file Altim will not overwrite or could not write.
    /// </summary>
    public bool RefuseChanges { get; set; }

    /// <inheritdoc />
    public ValueTask<StatusLineInstallState> InspectAsync(CancellationToken ct)
    {
        Inspections++;
        return Failure is { } failure
            ? ValueTask.FromException<StatusLineInstallState>(failure)
            : ValueTask.FromResult(State);
    }

    /// <inheritdoc />
    public ValueTask<StatusLineInstallState> SetAsync(bool install, CancellationToken ct)
    {
        Changes.Add(install);

        if (Failure is { } failure)
        {
            return ValueTask.FromException<StatusLineInstallState>(failure);
        }

        if (!RefuseChanges)
        {
            State = install ? StatusLineInstallState.Installed : StatusLineInstallState.NotInstalled;
        }

        return ValueTask.FromResult(State);
    }
}
