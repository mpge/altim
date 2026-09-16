using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

namespace Altim.UI.Tests;

/// <summary>
/// The application the headless tests run inside.
/// </summary>
/// <remarks>
/// The base theme only. The Altim dictionary is merged per test through
/// <see cref="DesignSystem.Ensure"/>, which is also the arrangement the composition root uses:
/// application resources are searched ahead of application styles, so the Altim control themes
/// win over the base theme's whatever order the styles were added in.
/// </remarks>
public sealed class TestApp : Application
{
    /// <summary>
    /// Builds the headless session. Drawing is real rather than headless, because a captured
    /// frame is the only way to prove a control painted at all.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
