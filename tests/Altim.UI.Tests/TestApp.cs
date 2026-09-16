using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Altim.UI.Tests;

/// <summary>
/// The Avalonia application the headless test host starts. Fluent is loaded here, and
/// only here: the headless host needs a complete control theme to measure and arrange
/// against, and keeping it in the test assembly leaves the Simple based design system
/// in <c>Altim.UI</c> untouched by the tests.
/// </summary>
public sealed class TestApp : Application
{
    /// <summary>
    /// Builds the headless application. Named by convention; the headless xUnit
    /// integration looks for this method on the type named by
    /// <c>AvaloniaTestApplicationAttribute</c>.
    ///
    /// <c>UseHeadlessDrawing</c> is off so the platform renders through Skia. The stub
    /// drawing backend measures and arranges but produces no surface, and
    /// <c>CaptureRenderedFrame</c> returns null against it, so a render assertion would
    /// assert nothing.
    /// </summary>
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();

    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}
