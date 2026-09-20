#if WINDOWS

using Altim.Platform.Windows;
using Xunit;

namespace Altim.Platform.Tests;

/// <summary>
/// The exact string Altim registers under the per-user <c>Run</c> key.
/// </summary>
/// <remarks>
/// <para>
/// This file is behind the same <c>WINDOWS</c> symbol every file in
/// <c>Altim.Platform.Windows</c> is behind, and for the same reason: off the Windows target
/// framework there is no such type to test. That is also why there is no
/// <c>WindowsFact</c> here. Those attributes skip a test that compiled; this one does not
/// compile off Windows at all, so off Windows there is nothing to skip and the Linux tests in
/// this project are what the assembly runs.
/// </para>
/// <para>
/// <b>The quoting is the whole of it.</b> Windows splits an unquoted <c>Run</c> value on
/// spaces, so a default installation under <c>C:\Program Files</c> registers the command
/// <c>C:\Program</c> with two arguments after it. Nothing reports that. The value is in the
/// registry, the settings page reads it back and says start with Windows is on, and Altim
/// does not start at the next sign-in.
/// </para>
/// <para>
/// Nothing here writes to the registry. The reads are aimed at a value name nothing has ever
/// registered under, so they answer from a real hive without touching what is in it: a test
/// that registers a startup entry on the machine running it has changed that machine.
/// </para>
/// </remarks>
public sealed class WindowsAutoStartServiceTests
{
    /// <summary>The path a default installation puts the executable at.</summary>
    private const string Installed = @"C:\Program Files\Altim\Altim.exe";

    /// <summary>A value name nothing registers under, so every read below answers null.</summary>
    private const string Unregistered = "Altim.Tests.NothingIsRegisteredUnderThisName";

    /// <summary>The path is one token, because it is wrapped in quotes.</summary>
    [Fact]
    public void ThePathIsQuotedSoASpaceInItIsNotAnArgumentBoundary()
    {
        var service = new WindowsAutoStartService("Altim", Installed);
        string command = service.CommandLine;

        Assert.Equal("\"" + Installed + "\"", command);

        // Stated a second way, because the line above is also satisfied by a constant: one
        // quote at each end and nowhere else, and the path back out from between them.
        Assert.Equal(2, command.Count(character => character == '"'));
        Assert.Equal(Installed, command.Trim('"'));
    }

    /// <summary>Arguments follow the quoted path, outside the quotes.</summary>
    [Fact]
    public void ArgumentsFollowTheQuotedPathRatherThanJoiningIt()
    {
        var service = new WindowsAutoStartService("Altim", Installed, "--tray");

        Assert.Equal("\"" + Installed + "\" --tray", service.CommandLine);
    }

    /// <summary>
    /// No executable is no command line, rather than a pair of empty quotes. An empty string
    /// is what stops <c>SetAsync</c> writing a value that would launch nothing.
    /// </summary>
    /// <param name="executablePath">What the caller could say about the executable.</param>
    [Theory]
    [InlineData("")]
    public void NoExecutableIsNoCommandLineRatherThanEmptyQuotes(string executablePath) =>
        Assert.Equal(string.Empty, new WindowsAutoStartService("Altim", executablePath).CommandLine);

    /// <summary>
    /// A value that was never written is not a registration, whatever <c>StartupApproved</c>
    /// holds: a stale approval record for an entry that has been removed is common and means
    /// nothing on its own.
    /// </summary>
    [Fact]
    public async Task AValueThatWasNeverWrittenIsNotARegistration()
    {
        var service = new WindowsAutoStartService(Unregistered, Installed);

        Assert.Null(service.ReadRunValue());
        Assert.Null(service.ReadApproval());
        Assert.False(await service.IsEnabledAsync());
    }
}

#endif
