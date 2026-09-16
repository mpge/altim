using System.ComponentModel;
using System.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.Providers.Diagnostics;

/// <summary>
/// Detects running agent processes by executable name.
/// </summary>
/// <remarks>
/// <para>
/// <b>This scanner never reads a command line, and there is no code path here that
/// could.</b> Enumerating processes on the verification machine found a third-party tool
/// passing an API key in plaintext in its arguments; a monitor that captured argv would
/// quietly ingest other people's secrets and then write them into a log or a crash dump.
/// What is recorded is an executable name, a process id and, when the operating system
/// allows it, a start time. Nothing else is read.
/// </para>
/// <para>
/// The cost of that rule is stated plainly in
/// <see cref="ProcessScanner(IReadOnlyDictionary{string, string}, IReadOnlySet{string}?)"/>:
/// helper invocations that differ from a real session only by their arguments cannot be
/// told apart here. Providers that can answer the question properly — Claude Code's own
/// agents listing, for one — use that instead, and treat this as the fallback.
/// </para>
/// <para>
/// Processes are looked up by name rather than by enumerating everything, so unrelated
/// processes are not touched at all.
/// </para>
/// </remarks>
public sealed class ProcessScanner : IProcessMonitor
{
    private readonly Dictionary<string, string> _providerByProcessName;
    private readonly HashSet<string> _excludedProcessNames;

    /// <summary>
    /// Creates a scanner over an explicit name table.
    /// </summary>
    /// <param name="providerByProcessName">
    /// Maps a process name, without directory or extension, to the provider id it belongs
    /// to. Compared case-insensitively.
    /// </param>
    /// <param name="excludedProcessNames">
    /// Process names that look like an agent but are helper executables, for example a
    /// browser native-messaging host. Only helpers that carry a <em>different executable
    /// name</em> can be excluded: one that differs from a real session only in its
    /// arguments is indistinguishable here, because arguments are not read.
    /// </param>
    public ProcessScanner(
        IReadOnlyDictionary<string, string> providerByProcessName,
        IReadOnlySet<string>? excludedProcessNames = null)
    {
        ArgumentNullException.ThrowIfNull(providerByProcessName);

        _providerByProcessName = new Dictionary<string, string>(providerByProcessName, StringComparer.OrdinalIgnoreCase);
        _excludedProcessNames = excludedProcessNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludedProcessNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The process names this scanner looks for, without extension.
    /// </summary>
    public IReadOnlyCollection<string> WatchedProcessNames => _providerByProcessName.Keys;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct)
    {
        var found = new List<DetectedProcess>();

        foreach ((string processName, string providerId) in _providerByProcessName)
        {
            ct.ThrowIfCancellationRequested();

            if (_excludedProcessNames.Contains(processName))
            {
                continue;
            }

            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(processName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or Win32Exception or NotSupportedException)
            {
                // A denied or unavailable process table is the normal case, not a failure.
                continue;
            }

            foreach (Process process in matches)
            {
                using (process)
                {
                    DetectedProcess? detected = Describe(process, providerId);
                    if (detected is not null)
                    {
                        found.Add(detected);
                    }
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<DetectedProcess>>(found);
    }

    /// <summary>
    /// True when at least one process for <paramref name="providerId"/> is running.
    /// </summary>
    /// <param name="processes">A scan result.</param>
    /// <param name="providerId">The provider to look for.</param>
    public static bool HasProcess(IReadOnlyList<DetectedProcess> processes, string providerId)
    {
        ArgumentNullException.ThrowIfNull(processes);

        foreach (DetectedProcess process in processes)
        {
            if (string.Equals(process.ProviderId, providerId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private DetectedProcess? Describe(Process process, string providerId)
    {
        string name;
        int id;
        try
        {
            name = process.ProcessName;
            id = process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Exited between enumeration and inspection.
            return null;
        }

        if (_excludedProcessNames.Contains(name))
        {
            return null;
        }

        DateTimeOffset? startedAt;
        try
        {
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
            // Denied for a process owned by another user, or already exited. Null means
            // "the operating system would not say", not "it just started".
            startedAt = null;
        }

        return new DetectedProcess(id, providerId, name, startedAt);
    }
}
