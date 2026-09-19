namespace Altim.Core.Monitoring;

/// <summary>
/// Whether launching should put a window on screen, or leave Altim in the tray.
/// </summary>
/// <remarks>
/// Pure, so the rule can be tested without a composition root, a database or a display.
/// The runtime owns finding out whether this is a first run; this owns what that means.
/// </remarks>
public static class StartupWindows
{
    /// <summary>
    /// Records that Altim has shown itself to this user at least once.
    /// </summary>
    /// <remarks>
    /// A scalar rather than a setting: it is not a preference, and nothing should offer to
    /// change it.
    /// </remarks>
    public const string FirstRunKey = "startup.first_run_shown";

    /// <summary>
    /// Whether the dashboard opens on launch.
    /// </summary>
    /// <param name="firstRun">True when Altim has never shown itself to this user.</param>
    /// <param name="startMinimised">The user's "start minimised" setting.</param>
    /// <returns>True to open the dashboard, false to stay in the tray.</returns>
    /// <remarks>
    /// <para>
    /// A first run opens it whatever the setting says. The setting defaults to on, and a
    /// user who has never seen Altim cannot have chosen it, so honouring a default here
    /// means the installer finishes and nothing visible happens: this process has no main
    /// window by design, and Windows files a tray icon nobody has seen before into the
    /// overflow. The whole application ends up behind a chevron the user has no reason to
    /// click.
    /// </para>
    /// <para>
    /// After that the setting is the answer, because by then it is a choice somebody could
    /// have made.
    /// </para>
    /// </remarks>
    public static bool ShouldOpenDashboard(bool firstRun, bool startMinimised) =>
        firstRun || !startMinimised;
}
