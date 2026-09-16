namespace Altim.App.Diagnostics;

/// <summary>
/// Everything that went wrong during start-up and was survived.
/// </summary>
/// <remarks>
/// <para>
/// The rule this type exists for: a provider that is not installed, a failed notification
/// registration, a missing tray and an unreadable database are all conditions Altim keeps
/// running through. None of them may be silent, so each one adds a sentence here, and the
/// tray menu renders them as disabled entries above the real ones. That is the only surface
/// a process with no main window is guaranteed to have.
/// </para>
/// <para>
/// Sentences are written for a user, not for a developer: the exception text goes to
/// <see cref="AltimLog"/> instead.
/// </para>
/// </remarks>
internal sealed class StartupReport
{
    private readonly List<string> _issues = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Raised, on the thread that recorded it, the first time a given sentence is added.
    /// </summary>
    /// <remarks>
    /// Not every degraded condition is known at start-up. The Linux notification service
    /// connects to the session bus on its first message, so whether notifications work is
    /// not knowable until one is sent; the same is true of anything else that fails on
    /// first use rather than on construction. Without this the menu would be built once
    /// from the conditions detected before the icon went up and would never mention the
    /// rest. The composition root rebuilds the tray menu on it.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>The issues recorded so far, in the order they happened.</summary>
    public IReadOnlyList<string> Issues
    {
        get
        {
            lock (_gate)
            {
                return [.. _issues];
            }
        }
    }

    /// <summary>True when anything at all is degraded.</summary>
    public bool HasIssues
    {
        get
        {
            lock (_gate)
            {
                return _issues.Count > 0;
            }
        }
    }

    /// <summary>
    /// Records one degraded condition. Repeats of the same sentence are ignored, because a
    /// condition that is re-detected on every refresh must not grow the menu.
    /// </summary>
    /// <param name="issue">The sentence to show the user.</param>
    public void Add(string issue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);

        lock (_gate)
        {
            if (_issues.Contains(issue, StringComparer.Ordinal))
            {
                return;
            }

            _issues.Add(issue);
        }

        // Outside the lock: a subscriber rebuilds the tray menu, which reads Issues.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Withdraws a condition that has since been put right.
    /// </summary>
    /// <param name="issue">The sentence to withdraw. Unknown sentences are ignored.</param>
    /// <returns>True when it was there and has been removed.</returns>
    /// <remarks>
    /// Not every degraded condition is permanent, and one that is over must stop being
    /// reported. Explorer restarting takes every notification icon with it; Altim's own host
    /// notices the broadcast and adds the icon again, but the menu went on saying "the tray
    /// icon could not be added" for the rest of the session, which is a line the user can see
    /// is false while they are reading it in the menu on that very icon.
    /// </remarks>
    public bool Resolve(string issue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);

        lock (_gate)
        {
            if (!_issues.Remove(issue))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
