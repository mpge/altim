using Altim.App.Diagnostics;
using Altim.Storage;
using Avalonia;
using Avalonia.Controls;

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
        if (!SingleInstance.TryAcquire(out SingleInstance? instance))
        {
            _ = SingleInstance.SignalExisting();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp(instance)
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            // There is no window to put this in and no console to print it to, so the log
            // file is the only place it can go. The guard is still released by the finally.
            AltimLog.Initialize(AltimDatabase.GetDefaultDirectory());
            AltimLog.Write("startup", "Altim could not start", ex);
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
    /// <returns>The configured builder.</returns>
    private static AppBuilder BuildAvaloniaApp(SingleInstance? instance) =>
        Configure(AppBuilder.Configure(() => new App(instance)));

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
