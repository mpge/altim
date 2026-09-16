using System.ComponentModel;
using System.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.Platform.MacOS;

/// <summary>
/// Detects running provider processes on macOS by executable name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching is on the executable name and nothing else.</b> This is a privacy boundary,
/// not a shortcut. Command lines on a development machine routinely carry API keys and
/// tokens as arguments; a monitor that read them to sharpen its matching would ingest other
/// people's secrets and then leak them into whatever it wrote next. Nothing here opens
/// <c>argv</c>, and <see cref="Process.GetProcessesByName(string)"/> cannot expose one.
/// </para>
/// <para>
/// <b>What macOS actually reports.</b> The name comes from the kernel's <c>p_comm</c> field,
/// which is capped at sixteen characters. Altim's provider executables — <c>claude</c> and
/// <c>codex</c> — are well inside that, but a future provider with a longer name would be
/// silently truncated, and this is where that would show up.
/// </para>
/// <para>
/// <b>Unverified only in the small.</b> The BCL implements the process table read, so the
/// mechanism is not Altim's; what has not been checked on a Mac is whether the launcher a
/// user actually runs reports these names rather than <c>node</c> or <c>python3</c>.
/// Providers that can answer the question from their own session data do so and treat this
/// as the fallback signal.
/// </para>
/// </remarks>
public sealed class MacOSProcessMonitor : IProcessMonitor
{
    /// <summary>The executables Altim's own providers run as.</summary>
    private static readonly Dictionary<string, string> DefaultProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["codex"] = "codex",
    };

    private readonly Dictionary<string, string> _providerByProcessName;
    private readonly HashSet<string> _excludedProcessNames;

    /// <summary>Creates a monitor over the default provider executables.</summary>
    public MacOSProcessMonitor()
        : this(DefaultProcessNames)
    {
    }

    /// <summary>Creates a monitor over an explicit name table.</summary>
    /// <param name="providerByProcessName">
    /// Maps an executable name, with no directory component and compared case-insensitively,
    /// to the provider identifier it belongs to.
    /// </param>
    /// <param name="excludedProcessNames">
    /// Executables that match a provider name but are helpers rather than sessions. Only a
    /// helper with a <em>different executable name</em> can be excluded, because arguments
    /// are never read.
    /// </param>
    public MacOSProcessMonitor(
        IReadOnlyDictionary<string, string> providerByProcessName,
        IReadOnlySet<string>? excludedProcessNames = null)
    {
        ArgumentNullException.ThrowIfNull(providerByProcessName);

        _providerByProcessName = new Dictionary<string, string>(providerByProcessName, StringComparer.OrdinalIgnoreCase);
        _excludedProcessNames = excludedProcessNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludedProcessNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The executable names this monitor looks for.</summary>
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
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                          or Win32Exception or NotSupportedException)
            {
                // A refused process table is reported as "nothing matched", per IProcessMonitor.
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
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
                                      or NotSupportedException or PlatformNotSupportedException)
        {
            // Denied for a process owned by another user. Null means "macOS would not say",
            // never "it just started".
            startedAt = null;
        }

        return new DetectedProcess(id, providerId, name, startedAt);
    }
}
