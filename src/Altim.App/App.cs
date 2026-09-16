using Altim.UI.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace Altim.App;

/// <summary>
/// The Avalonia application object. There is deliberately no main window: Altim is a
/// tray process, so the lifetime is told to shut down only on an explicit request and
/// the popup panel is created and hidden separately once the tray is wired up.
/// </summary>
public sealed class App : Application
{
    private readonly SingleInstance? _instance;
    private AltimRuntime? _runtime;

    /// <summary>Initializes the application without a single-instance guard.</summary>
    /// <remarks>Used by the XAML previewer and by any host that starts the app itself.</remarks>
    public App()
    {
    }

    /// <summary>Initializes the application over an owned single-instance guard.</summary>
    /// <param name="instance">
    /// The guard this process holds, so that a second launch surfaces this instance instead
    /// of adding a second tray icon. Null when no guard could be taken.
    /// </param>
    internal App(SingleInstance? instance) => _instance = instance;

    /// <inheritdoc />
    public override void Initialize()
    {
        // Simple theme plus Altim's own control themes. Fluent is never loaded: its
        // identity is the thing the design system is avoiding.
        //
        // Both, and in this order. Application.Resources is searched ahead of
        // Application.Styles, so merging the Altim dictionary into resources is what lets
        // its ControlThemes win over the base theme's whatever order the styles were added
        // in. This runs before any view exists, which is the only time it can run: a
        // control resolves its theme once, when it attaches.
        Styles.Add(new SimpleTheme());
        Resources.MergedDictionaries.Add(new AltimTheme());
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A tray application must not own a window that closing would end it, and must
            // not end when the last window closes: the dashboard is opened and closed
            // repeatedly, and the panel is hidden rather than closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = null;

            _runtime = new AltimRuntime(desktop);
            _runtime.Start(_instance);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
