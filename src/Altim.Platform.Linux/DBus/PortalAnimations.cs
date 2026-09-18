using Altim.Core.Accessibility;
using Altim.Core.Models;
using Tmds.DBus.Protocol;

namespace Altim.Platform.Linux.DBus;

/// <summary>
/// Reads the desktop's animation preference out of the XDG desktop portal's
/// <c>org.gnome.desktop.interface</c> / <c>enable-animations</c> value.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no cross-desktop key for this.</b> The freedesktop portal namespace covers
/// appearance and contrast but not motion, so the only setting to ask for is GNOME's, by
/// name. It is answered by GNOME's own portal backend and by
/// <c>xdg-desktop-portal-gtk</c>, and refused by a session that has neither.
/// </para>
/// <para>
/// <b>A session with no such setting is <see cref="MotionPreference.Unknown"/>, not
/// "animate".</b> A KDE or Sway session has not said that motion is wanted; it has said
/// nothing, and it may well carry the same preference somewhere Altim is not reading yet.
/// Reporting that as a preference for motion would put the guess in the one place nobody
/// could see it. It is reported as unknown and
/// <see cref="Core.Accessibility.MotionPolicy"/> decides, once, what an unknown does.
/// </para>
/// <para>
/// <b>The double wrapping is the same as the appearance value's.</b>
/// <c>org.freedesktop.portal.Settings.Read</c> returns a variant whose contents are
/// themselves a variant, while <c>ReadOne</c> and <c>SettingChanged</c> can hand over the
/// value unwrapped, so <see cref="TryRead"/> peels variants until it finds a boolean.
/// </para>
/// </remarks>
public static class PortalAnimations
{
    /// <summary>
    /// How deep a nest of variants to unwrap before giving up. Two is what the portal
    /// produces; the limit exists so a malformed reply cannot spin.
    /// </summary>
    private const int MaxVariantDepth = 8;

    /// <summary>
    /// Maps an <c>enable-animations</c> value to Altim's three-state preference.
    /// </summary>
    /// <param name="enableAnimations">
    /// The portal value, or <see langword="null"/> when the portal did not answer with one.
    /// </param>
    /// <returns>The preference, which is unknown for a portal that did not answer.</returns>
    /// <remarks>
    /// The mapping itself belongs to <see cref="MotionPolicy"/>, where all three platforms
    /// share it and it is asserted once. This is the portal's half: turning "the portal said
    /// nothing" into the same absence the other platforms report.
    /// </remarks>
    public static MotionPreference ToPreference(bool? enableAnimations) =>
        MotionPolicy.FromAnimationsEnabled(enableAnimations);

    /// <summary>
    /// Pulls the <c>enable-animations</c> flag out of whatever depth of variant it arrived in.
    /// </summary>
    /// <param name="value">The value from a <c>Read</c>, <c>ReadOne</c> or <c>SettingChanged</c>.</param>
    /// <param name="enableAnimations">The flag, when one was found.</param>
    /// <returns>
    /// True when a boolean was found. False for a reply of some other shape, which is the
    /// unknown case: a portal answering with a number where the specification says boolean
    /// has not told Altim what the user wants.
    /// </returns>
    public static bool TryRead(VariantValue value, out bool enableAnimations)
    {
        VariantValue current = value;

        for (int depth = 0; depth < MaxVariantDepth; depth++)
        {
            switch (current.Type)
            {
                case VariantValueType.Variant:
                    current = current.GetVariantValue();
                    continue;

                case VariantValueType.Bool:
                    enableAnimations = current.GetBool();
                    return true;

                default:
                    enableAnimations = false;
                    return false;
            }
        }

        enableAnimations = false;
        return false;
    }
}
