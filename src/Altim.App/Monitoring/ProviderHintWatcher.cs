using Altim.App.Diagnostics;
using Altim.Core.Monitoring;
using Altim.Providers;
using Altim.Providers.Claude;
using Altim.Providers.Codex;

namespace Altim.App.Monitoring;

/// <summary>
/// Turns filesystem activity in the providers' own stores into scheduler hints.
/// </summary>
/// <remarks>
/// <para>
/// This is the event-driven half of the monitoring design: polling is the floor, and a
/// session that writes a transcript line should move the meter before the next 60-second
/// tick. A hint says something may have changed and nothing more — no path, no content and
/// no filename ever leaves this class, and the scheduler coalesces everything inside its
/// 750ms debounce window, so a session writing continuously produces at most one refresh
/// per window rather than one per write.
/// </para>
/// <para>
/// Which directories to watch is provider knowledge, so the composition root asks the
/// provider projects for them — <c>ClaudePaths</c> and <c>CodexPaths</c> — rather than
/// pushing paths down through <c>Altim.Core</c>, which is not allowed to know that files
/// exist at all.
/// </para>
/// <para>
/// Nothing here is load-bearing. A watcher that cannot be created, a directory that is not
/// there and a buffer that overflows all degrade to the same thing: the polling floor, which
/// is what would have covered it anyway.
/// </para>
/// </remarks>
internal sealed class ProviderHintWatcher : IDisposable
{
    /// <summary>
    /// Large enough that a busy transcript directory does not overflow it between drains.
    /// The default 8KB overflows readily on a store the size the providers document.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly MonitorScheduler _scheduler;
    private bool _disposed;

    private ProviderHintWatcher(MonitorScheduler scheduler) => _scheduler = scheduler;

    /// <summary>How many watchers were actually armed.</summary>
    public int WatcherCount => _watchers.Count;

    /// <summary>
    /// Arms a watcher over every directory the installed providers write to.
    /// </summary>
    /// <param name="scheduler">The scheduler hints are delivered to.</param>
    /// <param name="providerIds">The providers that are actually registered.</param>
    public static ProviderHintWatcher Create(MonitorScheduler scheduler, IReadOnlyCollection<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(providerIds);

        var watcher = new ProviderHintWatcher(scheduler);

        if (providerIds.Contains(ProviderIds.Claude))
        {
            foreach (string root in ClaudePaths.ResolveConfigRoots())
            {
                // The status line file is rewritten on every prompt while a session runs,
                // and it is the only local source of reset instants.
                watcher.Arm(ProviderIds.Claude, root, ClaudePaths.StatusLineStateFileName, recursive: false);

                // Transcripts, including the subagents directories underneath them, which
                // hold most of the token volume.
                watcher.Arm(ProviderIds.Claude, ClaudePaths.ProjectsDirectory(root), "*.jsonl", recursive: true);
            }
        }

        if (providerIds.Contains(ProviderIds.Codex) && CodexPaths.ResolveHome() is { } home)
        {
            // The state database and its write-ahead log: every thread update lands here.
            watcher.Arm(ProviderIds.Codex, home, "state_*.sqlite*", recursive: false);

            // The rollout files, which carry the rate-limit snapshots.
            watcher.Arm(ProviderIds.Codex, CodexPaths.SessionsDirectory(home), "rollout-*.jsonl", recursive: true);
        }

        AltimLog.Write(
            "hints",
            "Armed " + watcher._watchers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " filesystem watchers.");

        return watcher;
    }

    /// <summary>Stops every watcher.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (FileSystemWatcher watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                AltimLog.Write("hints", "Stopping a watcher failed", ex);
            }
        }

        _watchers.Clear();
    }

    private void Arm(string providerId, string directory, string filter, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            // Not an error. A provider that has never run has no store, and the provider
            // itself reports that far better than a watcher could.
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = recursive,
                InternalBufferSize = BufferBytes,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            watcher.Changed += (_, _) => Hint(providerId);
            watcher.Created += (_, _) => Hint(providerId);
            watcher.Renamed += (_, _) => Hint(providerId);
            watcher.Deleted += (_, _) => Hint(providerId);
            watcher.Error += (_, e) => OnError(providerId, watcher, e);

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            // A watcher is an optimisation over the 60-second floor, never a requirement.
            AltimLog.Write("hints", "Could not watch a " + providerId + " directory", ex);
        }
    }

    private void Hint(string providerId)
    {
        // Hint() after disposal is documented as a no-op: a watcher is still delivering
        // events while the process closes its windows.
        _scheduler.Hint(providerId);
    }

    private void OnError(string providerId, FileSystemWatcher watcher, ErrorEventArgs e)
    {
        AltimLog.Write("hints", "Watcher for " + providerId + " faulted", e.GetException());

        // An overflow means events were lost, which is itself the strongest possible hint
        // that something changed. Re-arm, and refresh regardless of whether re-arming works.
        Hint(providerId);

        if (_disposed)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AltimLog.Write("hints", "Re-arming the watcher for " + providerId + " failed; polling covers it", ex);
        }
    }
}
