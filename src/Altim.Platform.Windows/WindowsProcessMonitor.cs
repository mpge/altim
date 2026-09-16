#if WINDOWS

using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Windows.Interop;

namespace Altim.Platform.Windows;

/// <summary>
/// Detects running provider processes from a tool-help snapshot of the process table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching is on the executable name and nothing else.</b> This is a security
/// boundary, not an optimisation. Command lines on a developer machine routinely
/// contain API keys, database passwords and access tokens passed as arguments by
/// other tools; a monitor that read <c>argv</c> to sharpen its matching would ingest
/// those secrets and then leak them into whatever it wrote next — a log line, a crash
/// dump, a support bundle. The snapshot used here cannot expose a command line even
/// by accident: <c>PROCESSENTRY32W</c> has no field for one.
/// </para>
/// <para>
/// The cost is stated plainly: two agents launched from the same executable and told
/// apart only by their arguments are indistinguishable here. Providers that can answer
/// the question properly do so from their own session data, and treat this as the
/// fallback signal.
/// </para>
/// <para>
/// One snapshot is taken per scan rather than one lookup per name, so the process
/// table is walked once however many providers are registered, and no process handle
/// is opened except to read a start time.
/// </para>
/// </remarks>
public sealed class WindowsProcessMonitor : IProcessMonitor
{
    /// <summary>
    /// The executables Altim's own providers run as. Claude Code and Codex both ship a
    /// launcher of the same name on <c>PATH</c>.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
    };

    private readonly Dictionary<string, string> _providerByProcessName;
    private readonly HashSet<string> _excludedProcessNames;

    /// <summary>
    /// Creates a monitor over the default provider executables.
    /// </summary>
    public WindowsProcessMonitor()
        : this(DefaultProcessNames)
    {
    }

    /// <summary>
    /// Creates a monitor over an explicit name table.
    /// </summary>
    /// <param name="providerByProcessName">
    /// Maps an executable name, with or without its <c>.exe</c> extension and compared
    /// case-insensitively, to the provider identifier it belongs to.
    /// </param>
    /// <param name="excludedProcessNames">
    /// Executables that match a provider name but are helpers rather than sessions.
    /// Only a helper with a different executable name can be excluded, because
    /// arguments are never read.
    /// </param>
    public WindowsProcessMonitor(
        IReadOnlyDictionary<string, string> providerByProcessName,
        IReadOnlySet<string>? excludedProcessNames = null)
    {
        ArgumentNullException.ThrowIfNull(providerByProcessName);

        _providerByProcessName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string providerId) in providerByProcessName)
        {
            _providerByProcessName[StripExtension(name)] = providerId;
        }

        _excludedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedProcessNames is not null)
        {
            foreach (string name in excludedProcessNames)
            {
                _ = _excludedProcessNames.Add(StripExtension(name));
            }
        }
    }

    /// <summary>The executable names this monitor looks for, without extension.</summary>
    public IReadOnlyCollection<string> WatchedProcessNames => _providerByProcessName.Keys;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct) =>
        ValueTask.FromResult(Scan(ct));

    private static string StripExtension(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static unsafe string ReadExecutableName(PROCESSENTRY32W* entry)
    {
        var span = new ReadOnlySpan<char>((char*)entry->szExeFile, 260);
        int end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    private static unsafe DateTimeOffset? ReadStartTime(uint processId)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            // Denied, which is the normal answer for another user's process. Null means
            // "Windows would not say", never "it just started".
            return null;
        }

        try
        {
            long creation;
            long exit;
            long kernel;
            long user;
            if (!NativeMethods.GetProcessTimes(handle, &creation, &exit, &kernel, &user) || creation <= 0)
            {
                return null;
            }

            return DateTimeOffset.FromFileTime(creation);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    private unsafe IReadOnlyList<DetectedProcess> Scan(CancellationToken ct)
    {
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE || snapshot == IntPtr.Zero)
        {
            // A refused snapshot is reported as "nothing matched", per IProcessMonitor.
            return [];
        }

        var found = new List<DetectedProcess>();

        try
        {
            var entry = default(PROCESSENTRY32W);
            entry.dwSize = (uint)sizeof(PROCESSENTRY32W);

            if (!NativeMethods.Process32FirstW(snapshot, &entry))
            {
                return [];
            }

            do
            {
                ct.ThrowIfCancellationRequested();

                string executable = ReadExecutableName(&entry);
                if (executable.Length == 0)
                {
                    continue;
                }

                string name = StripExtension(executable);
                if (_excludedProcessNames.Contains(name) ||
                    !_providerByProcessName.TryGetValue(name, out string? providerId))
                {
                    continue;
                }

                found.Add(new DetectedProcess(
                    (int)entry.th32ProcessID,
                    providerId,
                    executable,
                    ReadStartTime(entry.th32ProcessID)));
            }
            while (NativeMethods.Process32NextW(snapshot, &entry));
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }

        return found;
    }
}

#endif
