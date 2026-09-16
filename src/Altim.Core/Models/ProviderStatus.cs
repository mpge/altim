namespace Altim.Core.Models;

/// <summary>
/// Where a provider integration currently stands. This is a statement about the
/// integration, not about the user's quota.
/// </summary>
public enum ProviderStatus
{
    /// <summary>
    /// Nothing has been observed yet. The provider has not been probed since the
    /// process started, so neither presence nor absence has been established.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The provider's local installation was found, but no usage has been read from
    /// it yet.
    /// </summary>
    Detected = 1,

    /// <summary>
    /// The provider is not installed, or the location it keeps its data in does not
    /// exist on this machine. This is a settled answer, not a failure.
    /// </summary>
    NotDetected = 2,

    /// <summary>
    /// The provider is installed and an agent session is running right now.
    /// </summary>
    Active = 3,

    /// <summary>
    /// The provider is installed and readable, but no session has been active
    /// recently.
    /// </summary>
    Idle = 4,

    /// <summary>
    /// The last read failed. <see cref="ProviderUsage.StatusDetail"/> carries the
    /// reason, and every metric on that reading is unavailable rather than zero.
    /// </summary>
    Error = 5,
}
