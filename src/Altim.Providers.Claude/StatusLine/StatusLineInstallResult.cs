namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// What an install or revert did, or would do.
/// </summary>
public enum StatusLineInstallOutcome
{
    /// <summary>
    /// A dry run that would have added the entry. Nothing was written.
    /// </summary>
    WouldInstall = 0,

    /// <summary>The entry was added.</summary>
    Installed = 1,

    /// <summary>Altim's entry was already there. Nothing to do.</summary>
    AlreadyInstalled = 2,

    /// <summary>
    /// The user already has a status line of their own, and it was left alone. Altim does
    /// not replace it, and does not offer to: a status line is something the user built.
    /// </summary>
    RefusedExistingStatusLine = 3,

    /// <summary>No Claude Code config directory was found.</summary>
    NoConfigDirectory = 4,

    /// <summary>
    /// The settings file exists and could not be read or is not a JSON object. Nothing is
    /// written over a file that was not understood.
    /// </summary>
    SettingsUnreadable = 5,

    /// <summary>The settings file could not be written.</summary>
    WriteFailed = 6,

    /// <summary>A dry run that would have removed Altim's entry. Nothing was written.</summary>
    WouldRevert = 7,

    /// <summary>Altim's entry was removed.</summary>
    Reverted = 8,

    /// <summary>There was no Altim entry to remove.</summary>
    NothingToRevert = 9,
}

/// <summary>
/// The outcome of an install or revert, and what it touched.
/// </summary>
/// <param name="Outcome">What happened, or what would happen.</param>
/// <param name="DryRun">True when nothing was written.</param>
/// <param name="SettingsPath">
/// The settings file involved, so a confirmation prompt can name it. This is the user's own
/// Claude Code settings file, which they are being asked to approve a change to; it is not a
/// project path, it is never persisted, and no reader in this assembly returns it.
/// </param>
/// <param name="BackupPath">
/// Where the previous settings file was copied before writing, when a write happened.
/// </param>
public sealed record StatusLineInstallResult(
    StatusLineInstallOutcome Outcome,
    bool DryRun,
    string? SettingsPath,
    string? BackupPath)
{
    /// <summary>True when the settings file now carries, or would carry, Altim's entry.</summary>
    public bool Succeeded => Outcome is StatusLineInstallOutcome.WouldInstall
        or StatusLineInstallOutcome.Installed
        or StatusLineInstallOutcome.AlreadyInstalled;
}
