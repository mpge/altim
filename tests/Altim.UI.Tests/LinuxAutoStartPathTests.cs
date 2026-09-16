using Altim.Platform.Linux;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Which executable a Linux autostart entry names.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Environment.ProcessPath"/> is the right answer for a <c>.deb</c>, an
/// <c>.rpm</c> and a build tree, and the wrong one inside an AppImage. The AppImage runtime
/// mounts the image on a temporary directory and starts the payload from inside it, so the
/// running executable is <c>/tmp/.mount_Altim&lt;random&gt;/usr/bin/Altim</c>: unmounted when
/// the process ends, and a different random suffix next time. An entry written from it
/// fails silently — the desktop file is there, the session manager runs it, and nothing
/// starts.
/// </para>
/// <para>
/// The runtime exports <c>APPIMAGE</c> with the image file's own path for exactly this. The
/// choice between the two is pure, so it is asserted here rather than on a Linux host.
/// </para>
/// </remarks>
public sealed class LinuxAutoStartPathTests
{
    /// <summary>What the AppImage runtime leaves in <c>APPIMAGE</c>.</summary>
    private const string ImageFile = "/home/u/Applications/Altim-0.1.0-x86_64.AppImage";

    /// <summary>What the process sees as itself inside that image.</summary>
    private const string MountPoint = "/tmp/.mount_Altim3kFq2p/usr/bin/Altim";

    /// <summary>Where a .deb or .rpm puts the binary.</summary>
    private const string Installed = "/opt/altim/Altim";

    /// <summary>The defect: the entry must name the image, not the mount it is running from.</summary>
    [Fact]
    public void InsideAnAppImageTheImageFileIsRegisteredRatherThanTheMountPoint() =>
        Assert.Equal(
            ImageFile,
            LinuxAutoStartService.ResolveExecutablePath(preferred: null, ImageFile, MountPoint));

    /// <summary>
    /// Everywhere else nothing changes. <c>APPIMAGE</c> is set by the AppImage runtime and by
    /// nothing else, so its absence is what says this is an ordinary installation.
    /// </summary>
    /// <param name="appImage">The value of <c>APPIMAGE</c>: unset, or set to nothing.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAppImageTheRunningExecutableIsRegistered(string? appImage) =>
        Assert.Equal(
            Installed,
            LinuxAutoStartService.ResolveExecutablePath(preferred: null, appImage, Installed));

    /// <summary>
    /// A relative <c>APPIMAGE</c> is ignored rather than resolved, on the rule
    /// <c>XDG_CONFIG_HOME</c> already follows: a session manager launches an autostart entry
    /// from an unspecified working directory, so a relative <c>Exec</c> names whatever that
    /// turns out to be. The runtime always exports an absolute path; anything else is
    /// somebody else's environment and the running executable is the better guess.
    /// </summary>
    [Fact]
    public void ARelativeAppImageValueIsIgnoredRatherThanResolved() =>
        Assert.Equal(
            MountPoint,
            LinuxAutoStartService.ResolveExecutablePath(preferred: null, "Altim.AppImage", MountPoint));

    /// <summary>
    /// An explicit path wins over both. A packager knows where it put the binary, and the
    /// argument exists so it can say so.
    /// </summary>
    [Fact]
    public void AnExplicitPathBeatsTheEnvironment() =>
        Assert.Equal(
            Installed,
            LinuxAutoStartService.ResolveExecutablePath(Installed, ImageFile, MountPoint));

    /// <summary>
    /// Nothing at all stays nothing. <c>IsSupported</c> reads this, and start at session
    /// reports itself unavailable rather than writing an entry that launches nothing.
    /// </summary>
    /// <param name="processPath">What the runtime could report for the process, if anything.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPathAnywhereMeansNothingToRegister(string? processPath) =>
        Assert.Null(LinuxAutoStartService.ResolveExecutablePath(preferred: null, appImagePath: null, processPath));

    /// <summary>
    /// The service reaches the same answer through its constructor, which is the path the
    /// composition root actually takes: it passes no executable at all.
    /// </summary>
    [Fact]
    public void TheServiceRegistersTheExplicitPathItWasGiven()
    {
        var service = new LinuxAutoStartService(
            LinuxAutoStartService.DefaultEntryName,
            autostartDirectory: "/home/u/.config/autostart",
            executablePath: ImageFile);

        Assert.True(service.IsSupported);

        // Not the whole of EntryPath: Path.Combine writes the host's separator, and this
        // test runs on the machine that builds Altim rather than on the one that runs it.
        Assert.EndsWith("altim.desktop", service.EntryPath, StringComparison.Ordinal);
        Assert.Contains("Exec=\"" + ImageFile + "\"", service.EntryContents, StringComparison.Ordinal);
    }
}
