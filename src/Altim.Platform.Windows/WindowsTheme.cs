#if WINDOWS

using Microsoft.Win32;

namespace Altim.Platform.Windows;

/// <summary>
/// Reads the two light or dark preferences Windows keeps, which are separate: the
/// taskbar and system surfaces follow one, applications follow the other, and a user
/// may well run a dark taskbar with light applications.
/// </summary>
/// <remarks>
/// There is no public Win32 API for either value below the WinRT
/// <c>UISettings.ColorValuesChanged</c> surface, so both are read from the
/// <c>Personalize</c> key. A missing value means the Windows default, which is light.
/// </remarks>
internal static class WindowsTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// True when the taskbar and notification area are dark, which is the case where
    /// the tray needs the light glyph.
    /// </summary>
    /// <returns>True for a dark taskbar.</returns>
    public static bool TaskbarIsDark() => !ReadFlag("SystemUsesLightTheme", defaultValue: true);

    /// <summary>
    /// True when applications are asked to render dark.
    /// </summary>
    /// <returns>True for a dark application theme.</returns>
    public static bool AppIsDark() => !ReadFlag("AppsUseLightTheme", defaultValue: true);

    private static bool ReadFlag(string valueName, bool defaultValue)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            if (key?.GetValue(valueName) is int flag)
            {
                return flag != 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked down or missing key is the documented Windows default.
        }

        return defaultValue;
    }
}

#endif
