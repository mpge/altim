using Altim.Platform.Linux;
using Altim.Platform.Linux.Desktop;
using Xunit;

namespace Altim.Platform.Tests;

/// <summary>
/// The XDG autostart entry, written into a real directory and read back off the disk.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LinuxAutoStartService"/> takes the autostart directory and the executable to
/// register as constructor arguments. That is the seam, and it is what lets the whole of the
/// file handling be exercised on a machine that is not the one it runs on: everything here
/// points the service at a throwaway directory and then reads what it actually wrote.
/// </para>
/// <para>
/// The text handling underneath is asserted over strings elsewhere. What these add is the
/// half that touches the disk, which is where the two rules that matter live.
/// </para>
/// <para>
/// <b>Switching off rewrites the file. It does not delete it, and it does not replace it.</b>
/// Deleting the user's copy un-masks <c>/etc/xdg/autostart/altim.desktop</c> on a machine
/// carrying one, and Altim starts anyway, which is the opposite of what was asked for.
/// Replacing it with a freshly built entry throws away whatever a packager or the user put
/// into it, and an <c>OnlyShowIn</c> or a start-up delay quietly going missing is a change
/// nobody made and nobody can see.
/// </para>
/// </remarks>
public sealed class LinuxAutoStartServiceTests : IDisposable
{
    /// <summary>Where a .deb or an .rpm puts the binary.</summary>
    private const string Executable = "/opt/altim/Altim";

    /// <summary>
    /// An entry a packager shipped and a desktop has edited: a localised name, a session
    /// restriction, a start-up delay, and an action group underneath that answers for nothing.
    /// </summary>
    private const string Packaged = """
        [Desktop Entry]
        Type=Application
        Name=Altim
        Name[de]=Altim
        Comment=Monitor AI agent usage limits
        Exec="/opt/altim/Altim"
        Icon=altim
        Terminal=false
        OnlyShowIn=GNOME;KDE;
        X-GNOME-Autostart-Delay=20
        Hidden=false
        X-GNOME-Autostart-enabled=true

        [Desktop Action Settings]
        Name=Settings
        Hidden=true

        """;

    private readonly TempTree _tree = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _tree.Dispose();

    /// <summary>Switching on writes the entry, in a directory it had to create first.</summary>
    [Fact]
    public async Task SwitchingOnWritesTheEntryAndItReadsBackAsEnabled()
    {
        LinuxAutoStartService service = Service();
        string path = EntryPath(service);

        Assert.False(Directory.Exists(_tree.Root), "The directory existed before anything wrote to it.");

        await service.SetAsync(true);

        Assert.True(File.Exists(path), $"Nothing was written to {path}.");
        Assert.Equal(service.EntryContents, await File.ReadAllTextAsync(path, Ct));
        Assert.Contains("Exec=\"" + Executable + "\"", service.EntryContents, StringComparison.Ordinal);
        Assert.True(await service.IsEnabledAsync());
    }

    /// <summary>
    /// Switching off masks the entry in place, leaving every line somebody else put into it
    /// exactly where it was.
    /// </summary>
    [Fact]
    public async Task SwitchingOffRewritesTheEntryAndKeepsWhatSomebodyElseAdded()
    {
        LinuxAutoStartService service = Service();
        string path = _tree.Write("altim.desktop", Packaged);

        await service.SetAsync(false);

        string after = await File.ReadAllTextAsync(path, Ct);

        Assert.True(File.Exists(path), "The entry was deleted, which un-masks a system-wide one.");
        Assert.Contains("OnlyShowIn=GNOME;KDE;", after, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-Delay=20", after, StringComparison.Ordinal);
        Assert.Contains("Name[de]=Altim", after, StringComparison.Ordinal);
        Assert.Contains("[Desktop Action Settings]", after, StringComparison.Ordinal);

        Assert.True(DesktopEntry.ReadBoolean(after, DesktopEntry.HiddenKey));
        Assert.False(DesktopEntry.ReadBoolean(after, DesktopEntry.GnomeEnabledKey));
        Assert.False(await service.IsEnabledAsync());
    }

    /// <summary>
    /// Off and on again ends where it started, with the packager's lines still there at the
    /// end of it.
    /// </summary>
    [Fact]
    public async Task TheRoundTripEndsWhereItStartedWithTheAdditionsIntact()
    {
        LinuxAutoStartService service = Service();
        string path = _tree.Write("altim.desktop", Packaged);

        Assert.True(await service.IsEnabledAsync());

        await service.SetAsync(false);
        Assert.False(await service.IsEnabledAsync());

        await service.SetAsync(true);
        string after = await File.ReadAllTextAsync(path, Ct);

        Assert.True(await service.IsEnabledAsync());
        Assert.Contains("OnlyShowIn=GNOME;KDE;", after, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-Delay=20", after, StringComparison.Ordinal);

        // Still the file the packager shipped, rather than the one Altim builds from nothing.
        Assert.NotEqual(service.EntryContents, after);
    }

    /// <summary>
    /// Switching off when nothing was ever switched on writes nothing at all: it is already
    /// the state that was asked for, and a masked entry where there was none would mask a
    /// system-wide one that a packager put there on purpose.
    /// </summary>
    [Fact]
    public async Task SwitchingOffBeforeAnythingWasOnWritesNothingAtAll()
    {
        LinuxAutoStartService service = Service();

        await service.SetAsync(false);

        Assert.False(Directory.Exists(_tree.Root), $"{_tree.Root} was created for a switch-off.");
        Assert.Null(service.ReadEntry());
        Assert.False(await service.IsEnabledAsync());
    }

    /// <summary>
    /// An entry the desktop switched off through its own key reads as disabled, and the answer
    /// comes off the file on every ask rather than out of anything remembered.
    /// </summary>
    [Fact]
    public async Task AnEntryTheDesktopSwitchedOffReadsAsDisabled()
    {
        LinuxAutoStartService service = Service();
        string path = _tree.Write(
            "altim.desktop",
            Packaged.Replace(
                "X-GNOME-Autostart-enabled=true",
                "X-GNOME-Autostart-enabled=false",
                StringComparison.Ordinal));

        Assert.False(await service.IsEnabledAsync());

        File.Delete(path);
        Assert.False(await service.IsEnabledAsync());
    }

    /// <summary>A service pointed at this test's own throwaway directory.</summary>
    /// <returns>The service.</returns>
    private LinuxAutoStartService Service() =>
        new(LinuxAutoStartService.DefaultEntryName, _tree.Root, Executable);

    /// <summary>The entry path the service resolved.</summary>
    /// <param name="service">The service to ask.</param>
    /// <returns>The path, asserted to be an answer rather than "no config home".</returns>
    private static string EntryPath(LinuxAutoStartService service)
    {
        string? path = service.EntryPath;
        Assert.NotNull(path);
        return path;
    }
}
