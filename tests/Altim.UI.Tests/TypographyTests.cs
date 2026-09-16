using Altim.UI.Controls;
using Altim.UI.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// One family, bundled, with the platform face behind it, and tabular figures wherever a
/// number can tick. Digits that shift width as a percentage counts up are the specific
/// thing these tests exist to prevent.
/// </summary>
public sealed class TypographyTests
{
    /// <summary>The family is bundled Inter, with the platform UI face as the fallback.</summary>
    [AvaloniaFact]
    public void FontFamilyIsBundledInterWithAPlatformFallback()
    {
        var family = Resolve<FontFamily>("AltimFontFamily");

        // Avalonia turns a family list into a composite font, so the key carries the whole
        // declaration rather than the bare asset path.
        Assert.NotNull(family.Key);
        Assert.Contains(
            "avares://Avalonia.Fonts.Inter/Assets",
            family.Key!.Source.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("Inter", family.FamilyNames);
        Assert.Contains(FontFamily.DefaultFontFamilyName, family.FamilyNames);
    }

    /// <summary>The bundled family actually resolves to a face the renderer can use.</summary>
    [AvaloniaFact]
    public void InterResolvesToAGlyphTypeface()
    {
        var family = Resolve<FontFamily>("AltimFontFamily");

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? face),
            "Inter did not resolve to a glyph typeface.");
        Assert.NotNull(face);
    }

    /// <summary>Tabular figures are tnum and zero, and only those two.</summary>
    [AvaloniaFact]
    public void TabularFiguresAreTnumAndZero()
    {
        var features = Resolve<FontFeatureCollection>("AltimTabularFigures");

        Assert.Equal(2, features.Count);
        Assert.Equal("tnum", features[0].Tag);
        Assert.Equal("zero", features[1].Tag);
        Assert.Equal(1, features[0].Value);
        Assert.Equal(1, features[1].Value);
        Assert.IsType<TabularFigures>(features);
    }

    /// <summary>The Figure role is 34 on 36, semibold, tracked in, and tabular.</summary>
    [AvaloniaFact]
    public void FigureRoleMatchesTheTypeScale()
    {
        TextBlock text = Role("AltimFigureText", "56%");

        Assert.Equal(34d, text.FontSize);
        Assert.Equal(36d, text.LineHeight);
        Assert.Equal(FontWeight.SemiBold, text.FontWeight);
        Assert.Equal(-0.68d, text.LetterSpacing, 6);
        Assert.NotNull(text.FontFeatures);
        Assert.Equal(2, text.FontFeatures!.Count);
    }

    /// <summary>The remaining roles match the scale in DESIGN.md.</summary>
    /// <param name="key">The role's resource key.</param>
    /// <param name="size">The expected font size.</param>
    /// <param name="line">The expected line height.</param>
    /// <param name="weight">The expected weight.</param>
    [AvaloniaTheory]
    [InlineData("AltimFigureSmallText", 20d, 24d, FontWeight.SemiBold)]
    [InlineData("AltimTitleText", 28d, 34d, FontWeight.SemiBold)]
    [InlineData("AltimHeadingText", 15d, 20d, FontWeight.SemiBold)]
    [InlineData("AltimBodyText", 13d, 18d, FontWeight.Normal)]
    [InlineData("AltimLabelText", 12d, 16d, FontWeight.Medium)]
    [InlineData("AltimCaptionText", 11d, 14d, FontWeight.Normal)]
    public void EveryRoleMatchesTheTypeScale(string key, double size, double line, FontWeight weight)
    {
        TextBlock text = Role(key, "Here is how your AI agents are doing.");

        Assert.Equal(size, text.FontSize);
        Assert.Equal(line, text.LineHeight);
        Assert.Equal(weight, text.FontWeight);
    }

    /// <summary>
    /// Metric roles carry tabular figures and their plain counterparts do not: DESIGN.md
    /// applies the features to figures, not to prose.
    /// </summary>
    /// <param name="plain">The plain role's key.</param>
    /// <param name="metric">The metric role's key.</param>
    [AvaloniaTheory]
    [InlineData("AltimBodyText", "AltimBodyMetricText")]
    [InlineData("AltimLabelText", "AltimLabelMetricText")]
    [InlineData("AltimCaptionText", "AltimCaptionMetricText")]
    public void OnlyMetricRolesCarryTabularFigures(string plain, string metric)
    {
        Assert.Null(Role(plain, "Unable to retrieve usage").FontFeatures);
        Assert.NotNull(Role(metric, "56.2K / 130K tokens").FontFeatures);
    }

    /// <summary>
    /// The window carries the family, so a view never sets a font. This is also what the
    /// tape reads when it draws its own labels.
    /// </summary>
    [AvaloniaFact]
    public void TheWindowHandsTheFamilyDownToEverythingInIt()
    {
        var text = new TextBlock { Text = "Good evening" };
        var tape = new UsageTape { Series = [new UsageTapeSeries("Claude", [20d, 60d])] };
        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(tape);

        using WriteableBitmap frame = DesignSystem.Render(stack, width: 320d, height: 200d);
        Assert.True(frame.PixelSize.Width > 0);

        var expected = Resolve<FontFamily>("AltimFontFamily");
        Assert.Equal(expected, text.FontFamily);
        Assert.Equal(expected, Avalonia.Controls.Documents.TextElement.GetFontFamily(tape));
    }

    private static TextBlock Role(string key, string content)
    {
        var text = new TextBlock { Text = content, Theme = Resolve<ControlTheme>(key) };

        using WriteableBitmap frame = DesignSystem.Render(text, width: 320d, height: 80d);
        Assert.True(frame.PixelSize.Width > 0);

        return text;
    }

    private static T Resolve<T>(string key)
    {
        _ = DesignSystem.Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource(key, ThemeVariant.Light, out object? value),
            $"{key} does not resolve.");
        return Assert.IsAssignableFrom<T>(value);
    }
}
