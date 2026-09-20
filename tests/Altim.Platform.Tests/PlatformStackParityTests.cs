using System.Text.RegularExpressions;
using Xunit;

namespace Altim.Platform.Tests;

/// <summary>
/// The three branches of the composition root build the same platform stack.
/// </summary>
/// <remarks>
/// <para>
/// <c>PlatformStack.CreateWindows</c>, <c>CreateMacOS</c> and <c>CreateLinux</c> each construct
/// one service per role, in the same order, and a role that reaches one branch and not the
/// others is a capability that silently does not exist on that operating system. Nothing
/// catches that today: <c>Altim.Core.Tests.ForeignPlatformStackTests</c> exercises the macOS
/// and Linux services by writing the sequence out by hand, and says so - "a service added to
/// one and not the other is the failure mode this cannot catch".
/// </para>
/// <para>
/// <b>This reads the composition root rather than calling it, and that is deliberate.</b> No
/// test project references <c>Altim.App</c>. ARCHITECTURE.md states the direction as a
/// property - nothing depends on the composition root - and AGENTS.md depends on it a second
/// time: <c>dotnet test Altim.sln</c> runs while Altim itself is running precisely because it
/// never builds that project, and a running Altim holds a lock on its output. Calling the real
/// methods would also weaken what is asserted rather than strengthen it, because both of them
/// catch every constructor exception into a <c>StartupReport</c>: a service that threw and a
/// service that reported itself unsupported would arrive at a caller looking the same, and
/// telling those two apart is the whole point of the test that builds them by hand.
/// </para>
/// <para>
/// So this asserts the one thing that can be checked from outside, and it is the thing the
/// hand-written sequence cannot: that the three branches cover the same set of roles.
/// </para>
/// </remarks>
public sealed partial class PlatformStackParityTests
{
    /// <summary>
    /// The one role macOS does not build for itself. <c>NSStatusItem</c> is created and owned
    /// inside <c>MacOSPlatformService</c>, where the Windows and Linux branches hand their
    /// platform service a tray host built a line earlier - on Windows because the motion
    /// service needs the same hidden window, on Linux because the panel host outlives a
    /// platform service that failed to construct.
    /// </summary>
    private const string TrayHost = "TrayHost";

    /// <summary>A platform service being constructed, and the role it fills.</summary>
    [GeneratedRegex(@"new (?:Windows|MacOS|Linux)(?<role>\w+)\(")]
    private static partial Regex Constructed { get; }

    /// <summary>
    /// Every role one branch builds, every other branch builds too. The tray host is the one
    /// documented difference and it is subtracted by name rather than tolerated by a looser
    /// comparison, so a second difference appearing is a failure.
    /// </summary>
    [Fact]
    public void TheThreeBranchesOfTheCompositionRootCoverTheSameRoles()
    {
        string source = File.ReadAllText(CompositionRoot());

        IReadOnlyList<string> windows = RolesIn(source, "CreateWindows");
        IReadOnlyList<string> linux = RolesIn(source, "CreateLinux");
        IReadOnlyList<string> macOS = RolesIn(source, "CreateMacOS");

        Assert.Equal(windows, linux);
        Assert.Equal(windows.Where(role => role != TrayHost), macOS);
        Assert.DoesNotContain(TrayHost, macOS);

        // Named, so that a branch losing a role in every branch at once is still a failure
        // rather than three sets that agree about nothing.
        Assert.Equal(
            (string[])
            [
                "AutoStartService",
                "MotionPreferenceService",
                "NotificationService",
                "PlatformService",
                "ProcessMonitor",
                TrayHost,
            ],
            windows);
    }

    /// <summary>The roles one branch constructs, in order and without repeats.</summary>
    /// <param name="source">The composition root's source.</param>
    /// <param name="method">The branch's method name.</param>
    /// <returns>The role names, sorted.</returns>
    private static IReadOnlyList<string> RolesIn(string source, string method)
    {
        const string Signature = "private static PlatformStack ";

        int start = source.IndexOf(Signature + method + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} is not in the composition root.");

        int next = source.IndexOf(Signature, start + Signature.Length, StringComparison.Ordinal);
        string body = next < 0 ? source[start..] : source[start..next];

        SortedSet<string> roles = new(StringComparer.Ordinal);
        foreach (Match match in Constructed.Matches(body))
        {
            _ = roles.Add(match.Groups["role"].Value);
        }

        Assert.NotEmpty(roles);
        return [.. roles];
    }

    /// <summary>The composition root's own file, found from the repository root above this build.</summary>
    /// <returns>The full path.</returns>
    private static string CompositionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "Altim.App", "Composition", "PlatformStack.cs");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find src/Altim.App/Composition above {AppContext.BaseDirectory}.");
        return string.Empty;
    }
}
