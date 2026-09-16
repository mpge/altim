using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Linux.Processes;

namespace Altim.Platform.Linux;

/// <summary>
/// Detects running provider processes by walking <c>/proc</c> and reading nothing but each
/// process's executable name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching is on the executable name and nothing else.</b> <c>/proc/&lt;pid&gt;/cmdline</c>
/// sits in the same directory as the file this reads and would give a longer, more precise
/// name; it is never opened, because command lines on a development machine routinely carry
/// API keys and tokens as arguments. The reasoning, and the fifteen-character cost, are in
/// <see cref="ProcFileSystem"/>.
/// </para>
/// <para>
/// <b>Unverified.</b> There is no Linux host in the development environment. The parsing is
/// tested; the directory walk is not.
/// </para>
/// <para>
/// The whole table is walked once per scan rather than once per watched name, so the cost
/// does not grow with the number of providers. A process that exits mid-walk simply
/// disappears, and every read is guarded because that race is the normal case rather than an
/// error.
/// </para>
/// </remarks>
public sealed class LinuxProcessMonitor : IProcessMonitor
{
    /// <summary>The executables Altim's own providers run as.</summary>
    private static readonly Dictionary<string, string> DefaultProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
    };

    private readonly Dictionary<string, string> _providerByProcessName;
    private readonly HashSet<string> _excludedProcessNames;
    private readonly string _procRoot;

    /// <summary>Creates a monitor over the default provider executables.</summary>
    public LinuxProcessMonitor()
        : this(DefaultProcessNames)
    {
    }

    /// <summary>Creates a monitor over an explicit name table.</summary>
    /// <param name="providerByProcessName">
    /// Maps an executable name, compared case-insensitively, to the provider identifier it
    /// belongs to.
    /// </param>
    /// <param name="excludedProcessNames">
    /// Executables that match a provider name but are helpers rather than sessions. Only a
    /// helper with a <em>different executable name</em> can be excluded, because arguments
    /// are never read.
    /// </param>
    /// <param name="procRoot">
    /// The procfs mount point. Overridable so the walk can be pointed at a fixture directory.
    /// </param>
    public LinuxProcessMonitor(
        IReadOnlyDictionary<string, string> providerByProcessName,
        IReadOnlySet<string>? excludedProcessNames = null,
        string procRoot = ProcFileSystem.ProcRoot)
    {
        ArgumentNullException.ThrowIfNull(providerByProcessName);
        ArgumentException.ThrowIfNullOrWhiteSpace(procRoot);

        _providerByProcessName = new Dictionary<string, string>(providerByProcessName, StringComparer.OrdinalIgnoreCase);
        _excludedProcessNames = excludedProcessNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludedProcessNames, StringComparer.OrdinalIgnoreCase);
        _procRoot = procRoot;
    }

    /// <summary>The executable names this monitor looks for.</summary>
    public IReadOnlyCollection<string> WatchedProcessNames => _providerByProcessName.Keys;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct) =>
        ValueTask.FromResult(Scan(ct));

    private IReadOnlyList<DetectedProcess> Scan(CancellationToken ct)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(_procRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // No procfs, or it cannot be read. IProcessMonitor says that is an empty list,
            // not a failure.
            return [];
        }

        var found = new List<DetectedProcess>();

        foreach (string entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!ProcFileSystem.TryParseProcessId(Path.GetFileName(entry), out int processId))
            {
                continue;
            }

            string name = ReadComm(entry);
            if (name.Length == 0 || _excludedProcessNames.Contains(name))
            {
                continue;
            }

            string? providerId = MatchProvider(name);
            if (providerId is null)
            {
                continue;
            }

            found.Add(new DetectedProcess(processId, providerId, name, ReadStartTime(entry)));
        }

        return found;
    }

    private string? MatchProvider(string comm)
    {
        if (_providerByProcessName.TryGetValue(comm, out string? exact))
        {
            return exact;
        }

        // Only reached for a name the kernel truncated; see ProcFileSystem.MatchesWatchedName.
        foreach ((string watched, string providerId) in _providerByProcessName)
        {
            if (ProcFileSystem.MatchesWatchedName(comm, watched))
            {
                return providerId;
            }
        }

        return null;
    }

    private static string ReadComm(string processDirectory)
    {
        try
        {
            return ProcFileSystem.NormaliseComm(File.ReadAllText(Path.Combine(processDirectory, "comm")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The process exited between the listing and the read, or belongs to a user
            // Altim cannot see. Either way it is not a match.
            return string.Empty;
        }
    }

    private static DateTimeOffset? ReadStartTime(string processDirectory)
    {
        try
        {
            return ProcFileSystem.NormaliseStartTime(Directory.GetLastWriteTimeUtc(processDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
