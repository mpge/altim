using Tmds.DBus.Protocol;

namespace Altim.Platform.Linux.DBus;

/// <summary>
/// Reads the desktop's light or dark preference out of the XDG appearance portal's
/// <c>color-scheme</c> value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The portal is the only source.</b> There is no other cross-desktop way to ask: GNOME
/// keeps the answer in GSettings, KDE in a config file, and reading either would work on one
/// desktop and be wrong on every other. A machine with no portal simply has no preference,
/// which <see cref="IsDark"/> answers as light — not as an error.
/// </para>
/// <para>
/// <b>The value arrives late.</b> Reading a portal setting is a D-Bus round trip to a
/// service that may itself still be starting, so Altim renders light and corrects itself
/// when the answer comes back, shortly after start-up. That is why
/// <see cref="Core.Abstractions.IPlatformService.ThemeChanged"/> can fire once with no user
/// involvement at all.
/// </para>
/// <para>
/// <b>The double wrapping is real.</b> <c>org.freedesktop.portal.Settings.Read</c> is
/// declared as returning a variant, and for the appearance namespace the value inside it is
/// itself a variant holding the <c>u</c>. Newer portals also offer <c>ReadOne</c>, which
/// returns the value unwrapped. <see cref="TryReadColorScheme"/> therefore peels variants
/// until it finds a number, so both shapes work and neither needs a version check.
/// </para>
/// </remarks>
public static class PortalAppearance
{
    /// <summary>The portal has no preference either way.</summary>
    public const uint NoPreference = 0;

    /// <summary>The user prefers a dark appearance.</summary>
    public const uint PreferDark = 1;

    /// <summary>The user prefers a light appearance.</summary>
    public const uint PreferLight = 2;

    /// <summary>
    /// How deep a nest of variants to unwrap before giving up.
    /// </summary>
    /// <remarks>
    /// Two is what the portal actually produces; the limit exists so a malformed reply cannot
    /// spin.
    /// </remarks>
    private const int MaxVariantDepth = 8;

    /// <summary>
    /// Maps a <c>color-scheme</c> value to Altim's two-state theme.
    /// </summary>
    /// <param name="colorScheme">The portal value.</param>
    /// <returns>
    /// True only for <see cref="PreferDark"/>. <see cref="NoPreference"/>,
    /// <see cref="PreferLight"/> and any value the specification has not defined yet all mean
    /// light, because guessing dark from an unknown number would flip the whole UI on a
    /// desktop that added a value Altim has not heard of.
    /// </returns>
    public static bool IsDark(uint colorScheme) => colorScheme == PreferDark;

    /// <summary>
    /// Pulls the <c>color-scheme</c> number out of whatever depth of variant it arrived in.
    /// </summary>
    /// <param name="value">The value from a <c>Read</c>, <c>ReadOne</c> or <c>SettingChanged</c>.</param>
    /// <param name="colorScheme">The number, when one was found.</param>
    /// <returns>
    /// True when a number was found. False for a reply of some other shape, which is treated
    /// as "no preference" rather than as an error.
    /// </returns>
    public static bool TryReadColorScheme(VariantValue value, out uint colorScheme)
    {
        VariantValue current = value;

        for (int depth = 0; depth < MaxVariantDepth; depth++)
        {
            switch (current.Type)
            {
                case VariantValueType.Variant:
                    current = current.GetVariantValue();
                    continue;

                case VariantValueType.UInt32:
                    colorScheme = current.GetUInt32();
                    return true;

                case VariantValueType.Int32:
                    int signed = current.GetInt32();
                    colorScheme = signed < 0 ? NoPreference : (uint)signed;
                    return true;

                default:
                    colorScheme = NoPreference;
                    return false;
            }
        }

        colorScheme = NoPreference;
        return false;
    }
}
