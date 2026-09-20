using Altim.Core.Models;
using Altim.Platform.Linux;
using Altim.Platform.Linux.Processes;
using Xunit;

namespace Altim.Platform.Tests;

/// <summary>
/// The walk over <c>/proc</c>, run against a hand-built one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LinuxProcessMonitor"/> takes its procfs root as a constructor argument, and
/// until now nothing passed one: the parsing had tests and the directory walk around it had
/// none, on any platform. Nothing in the walk is Linux-specific - it lists directories, reads
/// a file called <c>comm</c> out of each and takes a modification time - so a tree of
/// ordinary directories exercises all of it from anywhere.
/// </para>
/// <para>
/// <b>Only <c>comm</c> is ever opened.</b> The fixtures below put a <c>cmdline</c> carrying
/// an API key next to it, the way a real one would, and the scan has to come back with the
/// executable name and nothing else. A monitor that read the longer, more precise name out of
/// the file beside it would ingest other people's secrets, which is why the fifteen character
/// truncation is worn rather than worked around.
/// </para>
/// </remarks>
public sealed class LinuxProcessMonitorTests : IDisposable
{
    /// <summary>A watched name longer than the kernel will report, so it comes back cut.</summary>
    private const string LongName = "claude-code-session";

    private static readonly Dictionary<string, string> Watched = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
    };

    private readonly TempTree _tree = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// A watched executable is found, an unwatched one is not, and each is reported against
    /// the provider its name belongs to.
    /// </summary>
    [Fact]
    public async Task TheScanFindsTheWatchedExecutablesAndNothingElse()
    {
        Comm("1", "systemd");
        Comm("412", "bash");
        Comm("1234", "claude");
        Comm("5678", "codex");

        IReadOnlyList<DetectedProcess> found = await Monitor().ScanAsync(Ct);

        Assert.Equal(2, found.Count);
        Assert.Equal("claude", Single(found, 1234).ProviderId);
        Assert.Equal("claude", Single(found, 1234).ExecutableName);
        Assert.Equal("codex", Single(found, 5678).ProviderId);
    }

    /// <summary>
    /// Everything in <c>/proc</c> that is not a process id is skipped. The real one carries
    /// <c>self</c>, <c>net</c>, <c>meminfo</c> and dozens more beside the numbered ones, and
    /// <c>self</c> is a symlink to a process that would otherwise be counted twice.
    /// </summary>
    [Fact]
    public async Task EntriesThatAreNotProcessIdsAreSkipped()
    {
        Comm("1234", "claude");
        Comm("self", "claude");
        Comm("net", "claude");
        Comm("12a4", "claude");

        IReadOnlyList<DetectedProcess> found = await Monitor().ScanAsync(Ct);

        Assert.Equal(1234, Assert.Single(found).ProcessId);
    }

    /// <summary>
    /// A process that exits between the listing and the read simply disappears. That race is
    /// the normal case on a busy machine rather than an error, so a directory with no
    /// <c>comm</c> in it must not take the scan down with it.
    /// </summary>
    [Fact]
    public async Task AProcessThatExitedMidWalkIsSkippedRatherThanThrowing()
    {
        Comm("1234", "claude");
        _ = _tree.MakeDirectory("2222");
        Comm("3333", string.Empty);

        IReadOnlyList<DetectedProcess> found = await Monitor().ScanAsync(Ct);

        Assert.Equal(1234, Assert.Single(found).ProcessId);
    }

    /// <summary>
    /// A helper that shares a provider's name is excluded by its own executable name. Only a
    /// different name can be excluded, because the arguments that would tell two processes of
    /// the same name apart are never read.
    /// </summary>
    [Fact]
    public async Task AnExcludedExecutableIsNotAProviderSession()
    {
        Comm("1234", "claude");
        Comm("4321", "claude-helper");

        var monitor = new LinuxProcessMonitor(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude",
                ["claude-helper"] = "claude",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-helper" },
            _tree.Root);

        IReadOnlyList<DetectedProcess> found = await monitor.ScanAsync(Ct);

        Assert.Equal(1234, Assert.Single(found).ProcessId);
    }

    /// <summary>
    /// A name the kernel cut at fifteen characters still matches the executable it was cut
    /// from, and a shorter name that merely starts the same way does not.
    /// </summary>
    [Fact]
    public async Task ANameTheKernelTruncatedStillMatches()
    {
        Comm("1234", LongName[..ProcFileSystem.MaxCommLength]);
        Comm("4321", "claude-code");

        var monitor = new LinuxProcessMonitor(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [LongName] = "claude" },
            excludedProcessNames: null,
            _tree.Root);

        IReadOnlyList<DetectedProcess> found = await monitor.ScanAsync(Ct);

        DetectedProcess only = Assert.Single(found);
        Assert.Equal(1234, only.ProcessId);
        Assert.Equal("claude", only.ProviderId);
        Assert.Equal(LongName[..ProcFileSystem.MaxCommLength], only.ExecutableName);
    }

    /// <summary>
    /// The command line sitting beside the name is never opened. It is the file that carries
    /// tokens and prompts on a development machine, and the scan has to come back with the
    /// fifteen characters of <c>comm</c> rather than the precise name next door.
    /// </summary>
    [Fact]
    public async Task TheCommandLineBesideTheNameIsNeverRead()
    {
        Comm("1234", "claude");
        _ = _tree.Write("1234/cmdline", "/usr/bin/claude\0--api-key\0sk-not-a-real-key\0");

        DetectedProcess only = Assert.Single(await Monitor().ScanAsync(Ct));

        Assert.Equal("claude", only.ExecutableName);
        Assert.DoesNotContain("sk-", only.ExecutableName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The start time is the directory's own stamp, which is what the kernel writes when the
    /// process is created. A filesystem that will not report one answers null rather than
    /// 1970, because the interface says null means "the operating system would not say".
    /// </summary>
    [Fact]
    public async Task TheStartTimeComesFromTheProcessDirectory()
    {
        string directory = Path.GetDirectoryName(Comm("1234", "claude"))!;
        var stamped = new DateTime(2026, 4, 2, 9, 30, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(directory, stamped);

        DetectedProcess only = Assert.Single(await Monitor().ScanAsync(Ct));

        Assert.Equal(new DateTimeOffset(stamped, TimeSpan.Zero), only.StartedAt);
    }

    /// <summary>
    /// No procfs at all is an empty list and never a throw. Every host that is not Linux is
    /// in that state, and so is a container that did not mount one.
    /// </summary>
    [Fact]
    public async Task AProcfsThatIsNotThereScansToNothing()
    {
        var monitor = new LinuxProcessMonitor(Watched, excludedProcessNames: null, _tree.Root);

        Assert.Empty(await monitor.ScanAsync(Ct));
    }

    /// <summary>A monitor over the default provider names, pointed at the fixture tree.</summary>
    /// <returns>The monitor.</returns>
    private LinuxProcessMonitor Monitor() =>
        new(Watched, excludedProcessNames: null, _tree.Root);

    /// <summary>Writes one process's <c>comm</c>, with the trailing newline the kernel adds.</summary>
    /// <param name="entry">The entry name under the procfs root.</param>
    /// <param name="name">The executable name.</param>
    /// <returns>The path written.</returns>
    private string Comm(string entry, string name) => _tree.Write(entry + "/comm", name + "\n");

    /// <summary>The one detection for a process id.</summary>
    /// <param name="found">Everything the scan reported.</param>
    /// <param name="processId">The id to pick out.</param>
    /// <returns>That detection.</returns>
    private static DetectedProcess Single(IReadOnlyList<DetectedProcess> found, int processId) =>
        Assert.Single(found, process => process.ProcessId == processId);
}
