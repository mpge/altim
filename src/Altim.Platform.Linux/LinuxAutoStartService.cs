using Altim.Core.Abstractions;
using Altim.Platform.Linux.Desktop;

namespace Altim.Platform.Linux;

/// <summary>
/// Start with the session, through an XDG autostart desktop entry in
/// <c>$XDG_CONFIG_HOME/autostart</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disabling writes <c>Hidden=true</c>; it does not delete the file.</b> The reasoning is
/// in <see cref="DesktopEntry"/>, and it is not a style preference: on a machine that also
/// carries <c>/etc/xdg/autostart/altim.desktop</c>, deleting the user's copy un-masks the
/// system one and Altim starts anyway. <c>Hidden=true</c> is the specification's own way of
/// saying "the user removed this", and it masks the system entry as well.
/// </para>
/// <para>
/// <b>State is read from the file every time.</b> Anything can edit it — GNOME Tweaks, KDE's
/// autostart page, a dotfile manager — so a cached answer would drift. The read is a few
/// hundred bytes.
/// </para>
/// <para>
/// A session with no writable config home reports false and accepts a write that does
/// nothing, which <see cref="IAutoStartService"/> already tells callers to expect.
/// </para>
/// </remarks>
public sealed class LinuxAutoStartService : IAutoStartService
{
    /// <summary>The desktop file id Altim registers under, without its suffix.</summary>
    public const string DefaultEntryName = "altim";

    private readonly string _entryName;
    private readonly string? _autostartDirectory;
    private readonly string? _executablePath;
    private readonly string _displayName;
    private readonly string _comment;
    private readonly string _iconName;

    /// <summary>Creates the service for the running executable.</summary>
    /// <param name="entryName">
    /// The desktop file id, without the <c>.desktop</c> suffix. Also the file name.
    /// </param>
    /// <param name="autostartDirectory">
    /// The autostart directory, or <see langword="null"/> to resolve it from
    /// <c>XDG_CONFIG_HOME</c> and then <c>HOME</c>.
    /// </param>
    /// <param name="executablePath">
    /// The executable to register, or <see langword="null"/> for the running process.
    /// </param>
    /// <param name="displayName">The name session managers show.</param>
    /// <param name="comment">One sentence of description.</param>
    /// <param name="iconName">An icon theme name, or empty for none.</param>
    public LinuxAutoStartService(
        string entryName = DefaultEntryName,
        string? autostartDirectory = null,
        string? executablePath = null,
        string displayName = "Altim",
        string comment = "Monitor AI agent usage limits",
        string iconName = DefaultEntryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(iconName);

        _entryName = entryName;
        _autostartDirectory = autostartDirectory ?? XdgDirectories.CurrentAutostartDirectory();
        _executablePath = executablePath ?? Environment.ProcessPath;
        _displayName = displayName;
        _comment = comment;
        _iconName = iconName;
    }

    /// <summary>
    /// The full path of the desktop entry, or <see langword="null"/> when no config home
    /// could be resolved.
    /// </summary>
    public string? EntryPath =>
        _autostartDirectory is null ? null : Path.Combine(_autostartDirectory, _entryName + ".desktop");

    /// <summary>
    /// True when Altim knows where to write the entry. False means start at session is
    /// unavailable, which is reported rather than thrown.
    /// </summary>
    public bool IsSupported => _autostartDirectory is not null && !string.IsNullOrEmpty(_executablePath);

    /// <summary>The exact file Altim writes when start at session is switched on.</summary>
    public string EntryContents =>
        DesktopEntry.Build(_displayName, _comment, _executablePath ?? string.Empty, _iconName);

    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync() => ValueTask.FromResult(DesktopEntry.IsEnabled(ReadEntry()));

    /// <inheritdoc />
    public ValueTask SetAsync(bool on)
    {
        string? path = EntryPath;
        if (path is null || string.IsNullOrEmpty(_executablePath))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            string? existing = ReadEntry();

            if (existing is null)
            {
                // Nothing to un-mask. Switching off an entry that was never written is
                // already the state the user asked for.
                if (!on)
                {
                    return ValueTask.CompletedTask;
                }

                _ = Directory.CreateDirectory(_autostartDirectory!);
                File.WriteAllText(path, EntryContents);
                return ValueTask.CompletedTask;
            }

            // Rewrite rather than replace: a packager or the user may have added OnlyShowIn,
            // a start-up delay or a localised name, and none of that is Altim's to discard.
            File.WriteAllText(path, DesktopEntry.SetHidden(existing, hidden: !on));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or NotSupportedException)
        {
            // A read-only or missing home is not a crash: the caller reads the state back and
            // sees that nothing changed.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the existing entry.
    /// </summary>
    /// <returns>The file contents, or null when there is no entry or it cannot be read.</returns>
    public string? ReadEntry()
    {
        string? path = EntryPath;
        if (path is null)
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }
}
