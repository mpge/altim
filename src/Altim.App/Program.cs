using System.Runtime.CompilerServices;
using Altim.App.Diagnostics;
using Altim.Providers.Claude.StatusLine;
using Altim.Storage;
using Avalonia;
using Avalonia.Controls;
using Velopack;

namespace Altim.App;

/// <summary>
/// The composition root. Nothing else in the solution references this project.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Process entry point.
    /// </summary>
    /// <param name="args">Command line arguments, passed through to Avalonia.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// This process has two entirely different jobs, and which one it is doing is decided
    /// here. With <see cref="ClaudeStatusLineHelper.Argument"/> among the arguments it is the
    /// status-line command Claude Code is running: it reads one JSON payload from standard
    /// input, writes the numbers to Altim's state file, prints one line and exits, inside the
    /// budget Claude Code allows a status-line command. Everything else is the application.
    /// </para>
    /// <para>
    /// The branch is first, ahead of the packaging hooks, the single-instance guard and every
    /// Avalonia type, because all three are wrong for a command that has to return in
    /// milliseconds: the guard would make the second concurrent session's helper exit without
    /// writing, and starting a windowing subsystem to print a line of text would blow the
    /// budget on its own. <see cref="RunApplication"/> holds the rest so that the types this
    /// method forces the runtime to load are only the ones the helper needs.
    /// </para>
    /// <para>
    /// The single-instance check happens before Avalonia is touched, so a second launch
    /// costs a named handle rather than a windowing subsystem: it signals the instance that
    /// already owns the session, which surfaces its panel, and exits reporting success. From
    /// the user's side they asked for Altim and Altim appeared.
    /// </para>
    /// <para>
    /// Initialisation must not use any Avalonia type before
    /// <see cref="AppBuilder.Start"/> has run, which is why the builder is kept in its
    /// own method.
    /// </para>
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (ClaudeStatusLineHelper.IsHelperInvocation(args))
        {
            // Standard input is where the payload is. When it is not redirected there is no
            // payload and no Claude Code either, so the empty stream stands in rather than a
            // read that would block on a console nobody is typing into.
            using Stream input = Console.IsInputRedirected ? Console.OpenStandardInput() : Stream.Null;
            return ClaudeStatusLineHelper.Run(input, Console.Out);
        }

        return RunApplication(args);
    }

    /// <summary>
    /// Starts the application proper.
    /// </summary>
    /// <param name="args">Command line arguments, passed through to Avalonia.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// Separate from <see cref="Main"/>, and never inlined into it, so that the helper branch
    /// does not pay for loading Velopack and Avalonia: the runtime resolves the types a
    /// method names when it compiles that method, and this is where they are named.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] args)
    {
        // First statement, before anything else looks at the process. The installer runs
        // this same executable with its own arguments to perform install and uninstall
        // hooks; this call services them and exits. Reaching the single-instance check
        // first would start a tray icon inside the installer and hold it until it timed
        // out, and the packaging tool refuses a binary that does not carry this call.
        VelopackApp.Build().Run();

        if (!SingleInstance.TryAcquire(out SingleInstance? instance))
        {
            _ = SingleInstance.SignalExisting();
            return 0;
        }

        App? app = null;

        try
        {
            return BuildAvaloniaApp(instance, created => app = created)
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            // There is no window to put this in and no console to print it to, so the log
            // file is the only place it can go. The guard is still released by the finally.
            AltimLog.Initialize(AltimDatabase.GetDefaultDirectory());
            AltimLog.Write("startup", "Altim could not start", ex);

            // And the log is not enough on its own. An exception that reaches here has
            // already unwound the dispatcher loop, so the tray icon is still in the
            // notification area and the database is still open, and returning from Main
            // leaves both to the operating system: a ghost icon that only clears when
            // somebody hovers over it, and a database closed by process exit rather than by
            // its own teardown. This runs the same synchronous teardown the session-end path
            // does, on this thread, before the return.
            app?.ShutDownRuntime("an unhandled exception reached the entry point");
            return 1;
        }
        finally
        {
            instance?.Dispose();
        }
    }

    /// <summary>
    /// Builds the Avalonia application. Also called by the XAML previewer and by the
    /// headless test host, so it must stay free of side effects.
    /// </summary>
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        Configure(AppBuilder.Configure<App>());

    /// <summary>
    /// Builds the application for a process that owns the session.
    /// </summary>
    /// <param name="instance">The single-instance guard, or null when none was taken.</param>
    /// <param name="onCreated">
    /// Handed the application as it is constructed, so the entry point can tear it down if
    /// an exception ever reaches it. Avalonia builds the object itself and there is nowhere
    /// else to get hold of it from.
    /// </param>
    /// <returns>The configured builder.</returns>
    private static AppBuilder BuildAvaloniaApp(SingleInstance? instance, Action<App> onCreated) =>
        Configure(AppBuilder.Configure(() =>
        {
            var app = new App(instance);
            onCreated(app);
            return app;
        }));

    /// <summary>
    /// The configuration both entry points share, so the previewer and the headless test
    /// host build the same application the process does.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="MacOSPlatformOptions.ShowInDock"/> is the accessory-app switch, and it is
    /// set here rather than left to the bundle's <c>LSUIElement</c> because Avalonia calls
    /// <c>NSApplication.setActivationPolicy</c> during initialisation and that call wins
    /// over the plist. Both are needed: the plist keeps the Dock tile from flashing up
    /// before managed code runs, and this keeps Avalonia from putting it back. Without it
    /// Altim is a tray utility with a Dock icon and an application menu bar it has no
    /// windows for.
    /// </para>
    /// <para>
    /// The option is inert off macOS, so it is applied unconditionally rather than behind a
    /// platform test.
    /// </para>
    /// </remarks>
    private static AppBuilder Configure(AppBuilder builder) =>
        builder
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .WithInterFont();
}
