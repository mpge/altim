using Altim.Core.Accessibility;
using Altim.Core.Models;

namespace Altim.UI.Accessibility;

/// <summary>
/// What the interface knows about the operating system's reduce-motion setting: one value,
/// shared by every surface that would otherwise animate.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a setting of Altim's.</b> Nothing in the interface writes it and it is
/// absent from the settings page on purpose — the operating system owns the answer, and a
/// second copy of it here could only ever disagree with the first. The composition root
/// reads the platform's <see cref="Core.Abstractions.IMotionPreferenceService"/> and pushes
/// the value in through <see cref="Set"/>.
/// </para>
/// <para>
/// <b>Why one shared value rather than a property per control.</b> This is the same shape of
/// thing as the theme variant: an ambient fact about the machine that every surface has to
/// agree on, that arrives from outside the visual tree, and that no view model has any
/// business carrying. <c>Application.RequestedThemeVariant</c> is where the framework keeps
/// its equivalent; Avalonia 12.1.2 has no equivalent for motion, so this is it.
/// </para>
/// <para>
/// <b>It starts at <see cref="MotionPreference.Unknown"/>, which does not animate.</b> That
/// is <see cref="MotionPolicy"/>'s decision and the reasoning is there. The useful
/// consequence here is that the failure mode points the safe way: a composition root that
/// never called <see cref="Set"/> leaves the interface still, rather than leaving it moving
/// for somebody who asked it not to.
/// </para>
/// <para>
/// <b>Call <see cref="Set"/> on the UI thread.</b> <see cref="Changed"/> is raised inline on
/// the caller's thread, and its subscribers are controls.
/// </para>
/// </remarks>
public static class Motion
{
    private static volatile MotionPreference _preference = MotionPreference.Unknown;

    /// <summary>
    /// Raised when <see cref="Preference"/> moves to a different value, so a surface that is
    /// animating can stop and one that is about to can decide not to start.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>What the platform last reported, including that it could not say.</summary>
    public static MotionPreference Preference => _preference;

    /// <summary>
    /// Whether a surface may animate a change. False for an unknown as well as for an
    /// explicit request to reduce motion; see <see cref="MotionPolicy.AllowsAnimation"/>.
    /// </summary>
    public static bool AnimationsAllowed => MotionPolicy.AllowsAnimation(_preference);

    /// <summary>
    /// Publishes a new reading of the platform preference.
    /// </summary>
    /// <param name="preference">What the platform reports.</param>
    /// <remarks>
    /// Raises <see cref="Changed"/> only when the value actually moved, so a platform that
    /// re-reports the same answer does not disturb anything on screen.
    /// </remarks>
    public static void Set(MotionPreference preference)
    {
        if (preference == _preference)
        {
            return;
        }

        _preference = preference;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
