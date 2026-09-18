using Altim.Core.Models;

namespace Altim.Core.Accessibility;

/// <summary>
/// The one place a three-state <see cref="MotionPreference"/> becomes the yes-or-no a
/// renderer needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unknown does not animate.</b> Nothing else in Altim renders an unknown as a value,
/// and this is as close as the rule gets to being traded: a surface either animates or it
/// does not, so one of the two answers has to absorb the unknown. It goes to the quiet one,
/// for a reason that is about people rather than symmetry.
/// </para>
/// <para>
/// The two mistakes are not the same size. Animating for somebody who asked for reduced
/// motion and whose platform Altim could not question can make them ill; that is the whole
/// reason the setting exists. Not animating for somebody who never asked costs them a
/// 180ms ease on a bar that arrives at the same number either way — <c>docs/DESIGN.md</c>
/// already says every animation here is a response to a value changing, never decoration,
/// so suppressing one removes no information and hides no state. A guess that can hurt
/// somebody is not worth making to save a guess that cannot.
/// </para>
/// <para>
/// The unknown is not thrown away to get here. <see cref="MotionPreference.Unknown"/>
/// survives in the service, in the start-up log and in anything that reports what Altim
/// knows; it is only this one call that has to pick a side, and it is deliberately the only
/// one that does.
/// </para>
/// </remarks>
public static class MotionPolicy
{
    /// <summary>
    /// Turns a platform's "are animations wanted" answer into a preference.
    /// </summary>
    /// <param name="animationsEnabled">
    /// True when the platform says animations are wanted, false when it says they are not,
    /// and <see langword="null"/> when it did not answer - a Win32 call that returned false,
    /// an Objective-C runtime that could not be reached, a desktop portal with no such key.
    /// </param>
    /// <returns>The preference, with a missing answer reported as it is.</returns>
    /// <remarks>
    /// All three platforms come through here, so the mapping is asserted once instead of
    /// three times in code that only one machine can run. Two of the three ask the question
    /// this way round; macOS asks whether motion should be <em>reduced</em> and inverts its
    /// answer at the call site, which is the one place the sense is flipped.
    /// </remarks>
    public static MotionPreference FromAnimationsEnabled(bool? animationsEnabled) =>
        animationsEnabled switch
        {
            true => MotionPreference.Full,
            false => MotionPreference.Reduced,
            null => MotionPreference.Unknown,
        };

    /// <summary>
    /// Whether a surface may animate a change rather than showing it at once.
    /// </summary>
    /// <param name="preference">What the platform reported.</param>
    /// <returns>
    /// True only for <see cref="MotionPreference.Full"/>: an explicit "the user has not
    /// asked for reduced motion". <see cref="MotionPreference.Reduced"/> and
    /// <see cref="MotionPreference.Unknown"/> both answer false.
    /// </returns>
    public static bool AllowsAnimation(MotionPreference preference) =>
        preference == MotionPreference.Full;
}
