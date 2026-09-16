namespace Altim.Core.Settings;

/// <summary>
/// Which theme Altim renders in. This is the stored preference, not the resolved theme:
/// <see cref="System"/> resolves at runtime and can change while the app is running.
/// </summary>
public enum ThemePreference
{
    /// <summary>Follow the operating system, including changes made while running.</summary>
    System = 0,

    /// <summary>Always light, whatever the operating system says.</summary>
    Light = 1,

    /// <summary>Always dark, whatever the operating system says.</summary>
    Dark = 2,
}
