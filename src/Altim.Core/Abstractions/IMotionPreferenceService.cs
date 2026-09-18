using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// The operating system's "reduce motion" accessibility setting, as Altim can see it.
/// One implementation per platform, resolved once at start-up.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>reading</em>, not a setting. Altim has no preference of its own and offers
/// none: the operating system already owns this answer, and a second copy of it in Altim's
/// settings could only ever disagree with the first.
/// </para>
/// <para>
/// <b><see cref="Current"/> is cached, and reading it is free.</b> The platform probe is a
/// system call or a bus round trip, so it runs once at start-up and again only when the
/// platform says the answer changed. Callers may read this per frame; implementations may
/// not probe per read.
/// </para>
/// <para>
/// <b>It can be <see cref="MotionPreference.Unknown"/> forever.</b> A platform with nothing
/// to ask, or one whose call failed, reports unknown and never changes, and that is a
/// supported state rather than a degraded one. The implementation still answers every call.
/// </para>
/// </remarks>
public interface IMotionPreferenceService
{
    /// <summary>
    /// The preference as it was last read. Never probes; never blocks; never throws.
    /// </summary>
    MotionPreference Current { get; }

    /// <summary>
    /// Raised when <see cref="Current"/> has moved to a different value, so a surface that
    /// animates can stop animating without Altim being restarted.
    /// </summary>
    /// <remarks>
    /// Raised only on a change, so a platform that re-broadcasts the same value does not
    /// wake the interface. Handlers run on whichever thread the platform delivered the
    /// change on — the tray message loop on Windows, a bus thread on Linux — so a handler
    /// that touches the interface marshals to the UI thread itself.
    /// </remarks>
    event EventHandler? Changed;
}
