namespace Altim.Core.Abstractions;

/// <summary>
/// Whether Altim starts with the user session.
/// </summary>
public interface IAutoStartService
{
    /// <summary>
    /// Reports whether Altim will actually start with the session.
    /// </summary>
    /// <returns>
    /// True only when the registration exists and the operating system has not
    /// disabled it. On Windows this means reading <c>StartupApproved</c> as well as
    /// the <c>Run</c> key, because a user can switch an entry off in Task Manager
    /// without the <c>Run</c> value being removed.
    /// </returns>
    ValueTask<bool> IsEnabledAsync();

    /// <summary>
    /// Registers or unregisters Altim for start-up.
    /// </summary>
    /// <param name="on">True to register, false to unregister.</param>
    /// <remarks>
    /// Setting this to true cannot override an operating system level disable, so
    /// <see cref="IsEnabledAsync"/> may still report false afterwards. Callers read the
    /// state back instead of assuming the write took effect.
    /// </remarks>
    ValueTask SetAsync(bool on);
}
