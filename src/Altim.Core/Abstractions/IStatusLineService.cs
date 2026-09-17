namespace Altim.Core.Abstractions;

/// <summary>
/// What Claude Code's status-line setting currently says, from Altim's point of view.
/// </summary>
/// <remarks>
/// Five states rather than a boolean, because the three that are not "on" or "off" each need
/// the interface to say something different, and a boolean would flatten all of them into a
/// switch that quietly refuses to move.
/// </remarks>
public enum StatusLineInstallState
{
    /// <summary>Altim's entry is not there, and nothing is in its way.</summary>
    NotInstalled = 0,

    /// <summary>Altim's entry is there and Claude Code is running it.</summary>
    Installed = 1,

    /// <summary>
    /// The user has a status line of their own. Altim does not replace it and does not offer
    /// to; a status line is something they built.
    /// </summary>
    AnotherStatusLine = 2,

    /// <summary>
    /// Claude Code has no configuration on this machine, so there is nothing to add an entry
    /// to. Not a failure.
    /// </summary>
    NoConfiguration = 3,

    /// <summary>
    /// The settings file could not be read or written, or was not a JSON object. Nothing was
    /// changed.
    /// </summary>
    Failed = 4,
}

/// <summary>
/// Adds Altim's status-line command to Claude Code's settings, and takes it back out.
/// </summary>
/// <remarks>
/// <para>
/// This is the one thing Altim does that writes to a file somebody else owns, so it is opt
/// in, it is never done on first run, and every call reports what it found rather than
/// assuming the write took. Callers read the state back instead of trusting the request, the
/// same way <see cref="IAutoStartService"/> requires.
/// </para>
/// <para>
/// The abstraction lives here because <c>Altim.UI</c> must not reference a provider
/// assembly: the settings page is what offers the switch, and the composition root is what
/// knows about Claude Code.
/// </para>
/// </remarks>
public interface IStatusLineService
{
    /// <summary>
    /// Reports where things stand without changing anything.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// The current state. <see cref="StatusLineInstallState.Failed"/> rather than an
    /// exception when the settings file cannot be read.
    /// </returns>
    ValueTask<StatusLineInstallState> InspectAsync(CancellationToken ct);

    /// <summary>
    /// Adds or removes Altim's entry, and says what the settings file holds afterwards.
    /// </summary>
    /// <param name="install">True to add Altim's entry, false to remove it.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>
    /// The state as it now stands, read back rather than assumed. A request to install that
    /// comes back <see cref="StatusLineInstallState.AnotherStatusLine"/> or
    /// <see cref="StatusLineInstallState.Failed"/> changed nothing.
    /// </returns>
    ValueTask<StatusLineInstallState> SetAsync(bool install, CancellationToken ct);
}
