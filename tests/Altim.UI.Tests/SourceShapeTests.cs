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

    /// <summary>A member declared as a Geometry, whatever its accessibility.</summary>
    [GeneratedRegex(@"^\s*(?:public|internal|private|protected)[\w\s]*\bGeometry\b\s+\w+")]
    private static partial Regex GeometryMember { get; }

    /// <summary>A static field holding a Geometry, which parses at type initialisation.</summary>
    [GeneratedRegex(@"static\s+(?:readonly\s+)?Geometry\s+\w+\s*=(?!>)")]
    private static partial Regex StaticGeometryField { get; }

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
    /// A control that draws its own ring has no template part for the guard above to find,
    /// so it is found by the brush it is handed instead. It drops the framework adorner for
    /// the same reason every other focusable theme does: two indicators on one control.
    /// </summary>
    [Fact]
    public void EveryThemeHandingOverAFocusRingBrushDropsTheDefaultAdorner()
    {
        int checkedThemes = 0;

        foreach (string file in ProjectXaml())
        {
            string xaml = File.ReadAllText(file);
            foreach (Match block in ControlThemeBlock.Matches(xaml))
            {
                if (!block.Value.Contains(@"Property=""FocusRingBrush""", StringComparison.Ordinal))
                {
                    continue;
                }

                checkedThemes++;

                Assert.True(
                    block.Value.Contains(@"FocusAdorner"" Value=""{x:Null}""", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} offset {block.Index} draws its own focus ring and "
                        + "keeps the framework's dashed one beside it.");
            }
        }

        // Meter, Dial and UsageMap: the controls that draw themselves and take focus.
        Assert.Equal(3, checkedThemes);
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

    /// <summary>
    /// No view model declares a <c>Geometry</c>. A geometry cannot be built before
    /// Avalonia's rendering platform exists, so a view model that held one could not be
    /// constructed by a unit test, by the composition root before its first window, or by
    /// anything else that runs early - and the failure lands inside a type initialiser,
    /// which the CLR caches, so one early touch takes the type out for the whole process.
    /// The views choose the path from a style class instead, once the control is on screen.
    /// </summary>
    [Fact]
    public void NoViewModelDeclaresAGeometry()
    {
        List<string> offences = [];

        foreach (string file in ProjectSource("ViewModels"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (GeometryMember.IsMatch(lines[i]))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "A view model holding a Geometry cannot be built without a rendering platform:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// Neither icon source parses a geometry in a static field. The paths are strings and
    /// the parse is behind a <c>Lazy</c>, so constructing the type costs nothing and the
    /// parse happens on first read - which is when a style is applied to a live control.
    /// </summary>
    [Fact]
    public void NoIconSourceParsesAGeometryAtTypeInitialisation()
    {
        List<string> offences = [];

        foreach (string file in ProjectSource("Formatting"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (StaticGeometryField.IsMatch(lines[i]))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "A static Geometry field parses inside the type initialiser, which poisons the"
                + " type for the whole process the first time it is touched too early:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// Every button in the interface exposes a name an assistive technology can read out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A button whose whole content is a drawn icon has nothing for an automation peer to
    /// fall back on, so a screen reader announces the shape type — "Path" — which is the
    /// same amount of help as silence. The panel's gear, every provider's disclosure and the
    /// panel's one action were all in that state.
    /// </para>
    /// <para>
    /// The rule is that a button either sets <c>Content</c> on the element itself, which the
    /// content control's peer reads, or declares <c>AutomationProperties.Name</c>. A label
    /// nested inside a panel inside the button does not count: Avalonia's peer reads a
    /// string content and a directly presented text block, and a
    /// <c>StackPanel</c> of a caption and an arrow is neither.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryButtonExposesAnAccessibleName()
    {
        List<string> offences = [];
        int checkedButtons = 0;

        foreach (string file in ProjectXaml())
        {
            foreach (string element in OpeningTags(File.ReadAllText(file), "Button"))
            {
                checkedButtons++;

                if (element.Contains("Content=", StringComparison.Ordinal)
                    || element.Contains("AutomationProperties.Name=", StringComparison.Ordinal))
                {
                    continue;
                }

                offences.Add($"{Path.GetFileName(file)}: {Summarise(element)}");
            }
        }

        Assert.True(checkedButtons > 0, "No buttons were found to check.");
        Assert.True(
            offences.Count == 0,
            "A button with no Content and no AutomationProperties.Name is announced as its"
                + " icon's shape type:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// Every opening tag for one element name, as its own text, quotes respected.
    /// </summary>
    /// <param name="xaml">The document to scan.</param>
    /// <param name="elementName">The element to find, without its angle bracket.</param>
    /// <remarks>
    /// Written by hand rather than with an XML reader because these files carry
    /// <c>x:</c> and <c>avares://</c> markup the reader would want namespaces for, and
    /// because an offence has to be reported as the source text somebody can search for.
    /// </remarks>
    private static IEnumerable<string> OpeningTags(string xaml, string elementName)
    {
        string opening = "<" + elementName;

        for (int at = xaml.IndexOf(opening, StringComparison.Ordinal); at >= 0;
             at = xaml.IndexOf(opening, at + 1, StringComparison.Ordinal))
        {
            int after = at + opening.Length;
            if (after < xaml.Length && xaml[after] is not (' ' or '\r' or '\n' or '\t' or '>' or '/'))
            {
                // <ButtonSpinner and friends: a different element that starts the same way.
                continue;
            }

            bool quoted = false;
            for (int i = after; i < xaml.Length; i++)
            {
                if (xaml[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (xaml[i] == '>' && !quoted)
                {
                    yield return xaml[at..(i + 1)];
                    break;
                }
            }
        }
    }

    /// <summary>The first line of an element, for an assertion message.</summary>
    /// <param name="element">The element's source text.</param>
    private static string Summarise(string element)
    {
        string first = element.Split('\n')[0].Trim();
        return first.Length > 80 ? first[..80] : first;
    }

    /// <summary>Every C# file under one folder of the UI project.</summary>
    /// <param name="folder">The folder, relative to the project.</param>
    /// <returns>Absolute paths, sorted.</returns>
    private static IEnumerable<string> ProjectSource(string folder) =>
        Directory.EnumerateFiles(
            Path.Combine(ProjectDirectory(), folder), "*.cs", SearchOption.AllDirectories).Order();

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
