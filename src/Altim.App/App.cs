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
    /// <inheritdoc />
    public override void Initialize()
    {
        // Simple theme plus Altim's own control themes. Fluent is never loaded: its
        // identity is the thing the design system is avoiding.
        Styles.Add(new SimpleTheme());
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
