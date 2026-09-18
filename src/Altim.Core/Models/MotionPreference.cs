namespace Altim.Core.Models;

/// <summary>
/// What the operating system says about animation, in the three states the question
/// actually has.
/// </summary>
/// <remarks>
/// <para>
/// Every desktop Altim runs on carries an accessibility setting that asks applications to
/// stop animating — "Animation effects" on Windows, "Reduce motion" on macOS,
/// <c>enable-animations</c> on a GNOME desktop. People switch it on because motion makes
/// them unwell, so it is not a taste and it is not Altim's to override.
/// </para>
/// <para>
/// <b><see cref="Unknown"/> is a real answer, not a placeholder.</b> A machine with no
/// desktop portal, an Objective-C runtime that could not be reached, or a Win32 call that
/// refused, has not told Altim that motion is wanted: it has told Altim nothing. Collapsing
/// that into <see cref="Full"/> at the point it is read would be presenting an assumption
/// as the user's answer, which is the one thing this product does not do with an unknown.
/// Where it goes when a yes-or-no is finally unavoidable is
/// <see cref="Accessibility.MotionPolicy"/>'s decision and only its decision.
/// </para>
/// <para>
/// <see cref="Unknown"/> is zero deliberately, so a default-constructed value claims
/// nothing.
/// </para>
/// </remarks>
public enum MotionPreference
{
    /// <summary>Altim could not find out. Not "no preference", and not "animate".</summary>
    Unknown = 0,

    /// <summary>The platform reports that the user has not asked for reduced motion.</summary>
    Full = 1,

    /// <summary>The platform reports that the user has asked for reduced motion.</summary>
    Reduced = 2,
}
