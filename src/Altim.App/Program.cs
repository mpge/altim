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
    /// Initialisation must not use any Avalonia type before
    /// <see cref="AppBuilder.Start"/> has run, which is why the builder is kept in its
    /// own method.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    /// <summary>
    /// Builds the Avalonia application. Also called by the XAML previewer and by the
    /// headless test host, so it must stay free of side effects.
    /// </summary>
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
