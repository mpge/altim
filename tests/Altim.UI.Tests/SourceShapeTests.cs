using System.Text.RegularExpressions;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Guards the parts of the design system the framework cannot report on.
/// </summary>
/// <remarks>
/// <para>
/// A colour token lives in a theme dictionary, so the right way to read it is
/// <c>DynamicResource</c>. <c>StaticResource</c> does not throw on one: it resolves the
/// <c>Default</c> bucket once, at load, and freezes that value. The control then renders
/// the light colour in dark mode, forever, with no warning and no exception. Nothing at
/// runtime can catch that, so it is caught here, in the source.
/// </para>
/// <para>
/// The focus ring has the same shape of problem. It is drawn outside the control's own
/// bounds and <c>TemplatedControl</c> clips to bounds by default, so a theme that hosts
/// the ring and forgets <c>ClipToBounds="False"</c> paints nothing while every property
/// assertion about the ring still passes.
/// </para>
/// </remarks>
public sealed partial class SourceShapeTests
{
    /// <summary>A static reference to a theme dictionary key: a frozen light colour.</summary>
    [GeneratedRegex(@"\{\s*StaticResource\s+Altim\w*(Brush|Color|Shadow)\s*\}")]
    private static partial Regex StaticThemeKey { get; }

    /// <summary>One ControlTheme element and everything up to the next one.</summary>
    [GeneratedRegex(@"<ControlTheme\b[\s\S]*?(?=<ControlTheme\b|</ResourceDictionary>)")]
    private static partial Regex ControlThemeBlock { get; }

    /// <summary>A resource key declared in a dictionary.</summary>
    [GeneratedRegex(@"x:Key=""(Altim[^""]+)""")]
    private static partial Regex ResourceKey { get; }

    /// <summary>
    /// No XAML in the UI project reaches a colour, brush or shadow token statically. The
    /// comment block in Tokens.axaml promises this test exists; this is it.
    /// </summary>
    [Fact]
    public void NoXamlReadsAThemeTokenWithStaticResource()
    {
        List<string> offences = [];

        foreach (string file in ProjectXaml())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in StaticThemeKey.Matches(lines[i]))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} {match.Value}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "A theme token read with StaticResource freezes one variant's value:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// Every control theme that hosts the focus ring also turns off clipping and nulls the
    /// framework's own adorner. Without the first the ring is clipped away; without the
    /// second Avalonia's 1px black dashed rectangle is drawn beside it.
    /// </summary>
    [Fact]
    public void EveryThemeHostingTheFocusRingUnclipsItAndDropsTheDefaultAdorner()
    {
        int checkedThemes = 0;

        foreach (string file in ProjectXaml())
        {
            string xaml = File.ReadAllText(file);
            foreach (Match block in ControlThemeBlock.Matches(xaml))
            {
                if (!block.Value.Contains("PART_FocusRing", StringComparison.Ordinal))
                {
                    continue;
                }

                checkedThemes++;
                string where = $"{Path.GetFileName(file)} offset {block.Index}";

                Assert.True(
                    block.Value.Contains(@"ClipToBounds"" Value=""False""", StringComparison.Ordinal),
                    $"{where} draws a focus ring outside its bounds but still clips to them.");
                Assert.True(
                    block.Value.Contains(@"FocusAdorner"" Value=""{x:Null}""", StringComparison.Ordinal),
                    $"{where} keeps the framework's dashed focus adorner beside the ring.");
            }
        }

        // Button, TextBox, ComboBox, ListBoxItem, ToggleSwitch.
        Assert.Equal(5, checkedThemes);
    }

    /// <summary>
    /// Every key declared in Primitives.axaml is named in <see cref="TokenTests"/>. A
    /// primitive nobody asserts on is a primitive nobody notices the loss of.
    /// </summary>
    [Fact]
    public void EveryPrimitiveIsCoveredByTheTokenTests()
    {
        string xaml = File.ReadAllText(Path.Combine(ThemesDirectory(), "Primitives.axaml"));
        var declared = ResourceKey.Matches(xaml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var asserted = TokenTests.PrimitiveTokens.ToHashSet(StringComparer.Ordinal);

        Assert.Equal([], declared.Except(asserted).Order());
        Assert.Equal([], asserted.Except(declared).Order());
    }

    /// <summary>
    /// Every key declared in Tokens.axaml is named in <see cref="TokenTests"/> too, so the
    /// exhaustive key count there stays exhaustive.
    /// </summary>
    [Fact]
    public void EveryColourTokenIsCoveredByTheTokenTests()
    {
        string xaml = File.ReadAllText(Path.Combine(ThemesDirectory(), "Tokens.axaml"));
        var declared = ResourceKey.Matches(xaml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        HashSet<string> asserted =
        [
            .. TokenTests.ColourTokens,
            .. TokenTests.BrushTokens,
            "AltimPopupShadow",
        ];

        Assert.Equal([], declared.Except(asserted).Order());
        Assert.Equal([], asserted.Except(declared).Order());
    }

    /// <summary>Every XAML file in the UI project.</summary>
    /// <returns>Absolute paths, sorted.</returns>
    private static IEnumerable<string> ProjectXaml() =>
        Directory.EnumerateFiles(ProjectDirectory(), "*.axaml", SearchOption.AllDirectories).Order();

    private static string ThemesDirectory() => Path.Combine(ProjectDirectory(), "Themes");

    /// <summary>
    /// Walks up from the test binary to the Altim.UI project. The test runner's working
    /// directory is not the repository, and a relative path from it would break the moment
    /// the suite ran from anywhere else.
    /// </summary>
    /// <returns>The absolute path of src/Altim.UI.</returns>
    private static string ProjectDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Altim.UI");
            if (File.Exists(Path.Combine(candidate, "Altim.UI.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find src/Altim.UI above {AppContext.BaseDirectory}.");
        return string.Empty;
    }
}
