using Altim.UI.Themes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Proves every token in the design system resolves. A missing resource key is a runtime
/// failure in Avalonia, not a compile error: the binding silently produces nothing and the
/// control renders with a default. These tests are the only thing standing between a typo
/// and a blank meter, so the key lists are written out in full rather than discovered.
/// </summary>
public sealed class TokenTests
{
    /// <summary>Keys that must exist in every theme dictionary bucket.</summary>
    public static readonly string[] ColourTokens =
    [
        "AltimSurfaceColor",
        "AltimSurfaceMutedColor",
        "AltimTextPrimaryColor",
        "AltimTextSecondaryColor",
        "AltimBorderColor",
        "AltimStatusOkColor",
        "AltimStatusWarnColor",
        "AltimStatusErrorColor",
        "AltimAccentAnthropicColor",
        "AltimAccentOpenAIColor",
        "AltimSidebarSelectedColor",
    ];

    /// <summary>Brush keys that must exist in every theme dictionary bucket.</summary>
    public static readonly string[] BrushTokens =
    [
        "AltimSurfaceBrush",
        "AltimSurfaceMutedBrush",
        "AltimTextPrimaryBrush",
        "AltimTextSecondaryBrush",
        "AltimBorderBrush",
        "AltimStatusOkBrush",
        "AltimStatusWarnBrush",
        "AltimStatusErrorBrush",
        "AltimAccentAnthropicBrush",
        "AltimAccentOpenAIBrush",
        "AltimSeparatorBrush",
        "AltimHoverBrush",
        "AltimHoverMutedBrush",
        "AltimFocusRingBrush",
        "AltimMeterTrackBrush",
        "AltimMeterFillBrush",
        "AltimMeterScaleBrush",
        "AltimMeterThresholdBrush",
        "AltimPopupBackgroundBrush",
        "AltimSidebarBackgroundBrush",
        "AltimSidebarHoverBrush",
        "AltimSidebarSelectedBrush",
        "AltimChartGroundBrush",
        "AltimChartLevelLineBrush",
        "AltimChartLinePrimaryBrush",
        "AltimChartLineSecondaryBrush",
        "AltimChartLabelBrush",

        // The usage map's ramp. Five steps of TextPrimary opacity, monochrome in both
        // variants: the map carries a quantity, and a provider's accent carries identity.
        // Level 0 is the faintest FILL and is not the outline a day nothing is known about
        // gets, which is AltimBorderBrush. See UsageMapPixelTests.
        "AltimMapLevel0Brush",
        "AltimMapLevel1Brush",
        "AltimMapLevel2Brush",
        "AltimMapLevel3Brush",
        "AltimMapLevel4Brush",
    ];

    /// <summary>Variant invariant keys: spacing, radii, line, sizes, type scale, motion.</summary>
    public static readonly string[] PrimitiveTokens =
    [
        "AltimSpace2", "AltimSpace4", "AltimSpace6", "AltimSpace8", "AltimSpace12",
        "AltimSpace16", "AltimSpace20", "AltimSpace24", "AltimSpace32", "AltimSpace48",
        "AltimInset2", "AltimInset4", "AltimInset6", "AltimInset8", "AltimInset12",
        "AltimInset16", "AltimInset20", "AltimInset24", "AltimInset32", "AltimInset48",
        "AltimRadius4", "AltimRadius6", "AltimRadius8", "AltimRadius12",
        "AltimCornerRadius4", "AltimCornerRadius6", "AltimCornerRadius8", "AltimCornerRadius12",
        "AltimHairline", "AltimBorderThickness", "AltimBorderThicknessBottom",
        "AltimBorderThicknessTop", "AltimBorderThicknessRight", "AltimBorderThicknessNone",
        "AltimFocusRingWidth", "AltimFocusRingOffset", "AltimFocusRingThickness",
        "AltimFocusRingMargin",
        "AltimRowMinHeight", "AltimButtonMinHeight", "AltimControlMinHeight",
        "AltimSidebarWidth", "AltimSidebarItemHeight",
        "AltimBrandMarkSize", "AltimBrandHeaderHeight",
        "AltimPopupWidth", "AltimPopupWindowWidth",
        "AltimStatusDotSize", "AltimLegendDotSize", "AltimStatusIconSize",
        "AltimProviderGlyphSize", "AltimProviderGlyphSizeMedium", "AltimProviderGlyphSizeLarge",
        "AltimIconSize", "AltimIconStrokeThickness",
        "AltimMeterHeight", "AltimMeterRailHeight", "AltimMeterScaleGap",
        "AltimMeterScaleHeight", "AltimMeterMinWidth",
        "AltimDialSize", "AltimDialSweep",
        "AltimTapeMinHeight", "AltimTapeLineThickness", "AltimTapeDotDiameter",
        "AltimScrollBarThickness", "AltimToggleTrackWidth", "AltimToggleTrackHeight",
        "AltimToggleKnobSize", "AltimToggleKnobInset", "AltimToggleTrackRadius",
        "AltimToggleKnobRadius", "AltimDisabledOpacity",
        "AltimButtonPadding", "AltimTextBoxPadding", "AltimComboBoxPadding",
        "AltimToolTipPadding", "AltimSidebarItemPadding", "AltimRowPadding",
        "AltimCardPadding", "AltimChipPadding", "AltimCardMinWidth",
        "AltimPopupShadowMargin", "AltimNoShadow",
        "AltimFontSizeDisplay", "AltimLineHeightDisplay", "AltimFontWeightDisplay",
        "AltimLetterSpacingDisplay",
        "AltimFontSizeSubhead", "AltimLineHeightSubhead", "AltimFontWeightSubhead",
        "AltimLetterSpacingSubhead",
        "AltimFontSizeLead", "AltimLineHeightLead", "AltimFontWeightLead",
        "AltimFontWeightLeadMedium", "AltimFontWeightLeadStrong", "AltimLetterSpacingLead",
        "AltimFontSizeEyebrow", "AltimLineHeightEyebrow", "AltimFontWeightEyebrow",
        "AltimLetterSpacingEyebrow",
        "AltimFontSizeFigure", "AltimLineHeightFigure", "AltimFontWeightFigure",
        "AltimLetterSpacingFigure",
        "AltimFontSizeFigureSmall", "AltimLineHeightFigureSmall",
        "AltimFontWeightFigureSmall", "AltimLetterSpacingFigureSmall",
        "AltimFontSizeTitle", "AltimLineHeightTitle", "AltimFontWeightTitle",
        "AltimLetterSpacingTitle",
        "AltimFontSizeHeading", "AltimLineHeightHeading", "AltimFontWeightHeading",
        "AltimLetterSpacingHeading",
        "AltimFontSizeBody", "AltimLineHeightBody", "AltimFontWeightBody",
        "AltimLetterSpacingBody",
        "AltimFontSizeLabel", "AltimLineHeightLabel", "AltimFontWeightLabel",
        "AltimLetterSpacingLabel",
        "AltimFontSizeCaption", "AltimLineHeightCaption", "AltimFontWeightCaption",
        "AltimLetterSpacingCaption",
        "AltimDuration120", "AltimDuration180", "AltimPopupRiseDistance",
    ];

    /// <summary>The typography keys, including the per role TextBlock themes.</summary>
    public static readonly string[] TypographyTokens =
    [
        "AltimFontFamily",
        "AltimMonoFontFamily",
        "AltimTabularFigures",
        "AltimDisplayText",
        "AltimEyebrowText",
        "AltimSubheadText",
        "AltimLeadText",
        "AltimLeadMediumText",
        "AltimLeadStrongText",
        "AltimChipText",
        "AltimFigureText",
        "AltimFigureSmallText",
        "AltimTitleText",
        "AltimHeadingText",
        "AltimBodyText",
        "AltimBodyMetricText",
        "AltimLabelText",
        "AltimLabelMetricText",
        "AltimCaptionText",
        "AltimCaptionMetricText",
    ];

    private static readonly ThemeVariant[] Variants =
        [ThemeVariant.Default, ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>Every colour token resolves to a Color in Default, Light and Dark.</summary>
    [AvaloniaFact]
    public void EveryColourTokenResolvesInEveryVariant()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        foreach (string key in ColourTokens)
        {
            foreach (ThemeVariant variant in Variants)
            {
                Assert.True(
                    theme.TryGetResource(key, variant, out object? value),
                    $"{key} does not resolve under {variant}.");
                Assert.IsType<Color>(value);
            }
        }
    }

    /// <summary>Every brush token resolves to a brush in Default, Light and Dark.</summary>
    [AvaloniaFact]
    public void EveryBrushTokenResolvesInEveryVariant()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        foreach (string key in BrushTokens)
        {
            foreach (ThemeVariant variant in Variants)
            {
                Assert.True(
                    theme.TryGetResource(key, variant, out object? value),
                    $"{key} does not resolve under {variant}.");
                Assert.IsAssignableFrom<IBrush>(value);
            }
        }
    }

    /// <summary>The one shadow resolves, and it differs between Light and Dark.</summary>
    [AvaloniaFact]
    public void PopupShadowResolvesAndIsDesignedPerVariant()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        var light = Resolve<BoxShadows>(theme, "AltimPopupShadow", ThemeVariant.Light);
        var dark = Resolve<BoxShadows>(theme, "AltimPopupShadow", ThemeVariant.Dark);

        Assert.Equal(1, light.Count);
        Assert.Equal(1, dark.Count);
        Assert.Equal(8d, light[0].OffsetY);
        Assert.Equal(24d, light[0].Blur);
        Assert.NotEqual(light[0].Color, dark[0].Color);
    }

    /// <summary>
    /// Primitives resolve, and resolve to the same object in every variant. They are
    /// declared outside the theme dictionaries on purpose, so a variant cannot move them.
    /// </summary>
    [AvaloniaFact]
    public void EveryPrimitiveResolvesIdenticallyInEveryVariant()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        foreach (string key in PrimitiveTokens)
        {
            Assert.True(
                theme.TryGetResource(key, ThemeVariant.Light, out object? light),
                $"{key} does not resolve under Light.");
            Assert.True(
                theme.TryGetResource(key, ThemeVariant.Dark, out object? dark),
                $"{key} does not resolve under Dark.");
            Assert.True(
                theme.TryGetResource(key, ThemeVariant.Default, out object? fallback),
                $"{key} does not resolve under Default.");

            Assert.Equal(light, dark);
            Assert.Equal(light, fallback);
        }
    }

    /// <summary>The typography keys resolve in every variant.</summary>
    [AvaloniaFact]
    public void EveryTypographyTokenResolvesInEveryVariant()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        foreach (string key in TypographyTokens)
        {
            foreach (ThemeVariant variant in Variants)
            {
                Assert.True(
                    theme.TryGetResource(key, variant, out object? value),
                    $"{key} does not resolve under {variant}.");
                Assert.NotNull(value);
            }
        }
    }

    /// <summary>
    /// The three buckets declare exactly the same keys. Adding a token to Light and
    /// forgetting Dark is the failure this catches, and it is invisible until a user
    /// switches theme.
    /// </summary>
    [AvaloniaFact]
    public void EveryThemeDictionaryDeclaresTheSameKeys()
    {
        ResourceDictionary tokens = ThemeDictionaryHost(DesignSystem.LoadStandalone());

        string[] Keys(ThemeVariant variant)
        {
            IThemeVariantProvider bucket = tokens.ThemeDictionaries[variant];
            var dictionary = Assert.IsType<ResourceDictionary>(bucket);
            return [.. dictionary.Keys.Select(k => k.ToString() ?? string.Empty).Order()];
        }

        string[] fallback = Keys(ThemeVariant.Default);
        Assert.Equal(fallback, Keys(ThemeVariant.Light));
        Assert.Equal(fallback, Keys(ThemeVariant.Dark));
        Assert.Equal(ColourTokens.Length + BrushTokens.Length + 1, fallback.Length);
    }

    /// <summary>
    /// The palettes are the ones in DESIGN.md, and Dark is a designed set rather than an
    /// inversion: its surface lifts off black, and its border stays lower contrast than
    /// its text.
    /// </summary>
    [AvaloniaFact]
    public void PaletteMatchesTheBriefAndDarkIsNotAnInversion()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        Assert.Equal(Color.Parse("#FFFFFF"), Colour(theme, "AltimSurfaceColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#FAFAFA"), Colour(theme, "AltimSurfaceMutedColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#0A0A0A"), Colour(theme, "AltimTextPrimaryColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#666666"), Colour(theme, "AltimTextSecondaryColor", ThemeVariant.Light));
        Assert.Equal(Color.Parse("#EAEAEA"), Colour(theme, "AltimBorderColor", ThemeVariant.Light));

        Color surface = Colour(theme, "AltimSurfaceColor", ThemeVariant.Dark);
        Color muted = Colour(theme, "AltimSurfaceMutedColor", ThemeVariant.Dark);
        Color text = Colour(theme, "AltimTextPrimaryColor", ThemeVariant.Dark);
        Color secondary = Colour(theme, "AltimTextSecondaryColor", ThemeVariant.Dark);
        Color border = Colour(theme, "AltimBorderColor", ThemeVariant.Dark);

        Assert.Equal(Color.Parse("#0C0C0D"), surface);
        Assert.Equal(Color.Parse("#151517"), muted);
        Assert.Equal(Color.Parse("#F2F2F3"), text);
        Assert.Equal(Color.Parse("#8A8A90"), secondary);
        Assert.Equal(Color.Parse("#232326"), border);

        // Not an inversion of Light.
        Assert.NotEqual(Color.FromRgb(0x00, 0x00, 0x00), surface);
        Assert.NotEqual(Invert(Colour(theme, "AltimSurfaceColor", ThemeVariant.Light)), surface);
        Assert.NotEqual(Invert(Colour(theme, "AltimBorderColor", ThemeVariant.Light)), border);

        // Backgrounds lift rather than invert, and the border stays under the text.
        Assert.True(muted.R > surface.R, "SurfaceMuted should lift above Surface in Dark.");
        Assert.True(border.R < secondary.R, "Border should stay lower contrast than text.");
    }

    /// <summary>The meter fill is TextPrimary in both variants, as the brief requires.</summary>
    [AvaloniaFact]
    public void MeterFillIsTextPrimaryInBothVariants()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        foreach (ThemeVariant variant in Variants)
        {
            var fill = Resolve<ISolidColorBrush>(theme, "AltimMeterFillBrush", variant);
            var text = Resolve<ISolidColorBrush>(theme, "AltimTextPrimaryBrush", variant);
            var track = Resolve<ISolidColorBrush>(theme, "AltimMeterTrackBrush", variant);
            var border = Resolve<ISolidColorBrush>(theme, "AltimBorderBrush", variant);

            Assert.Equal(text.Color, fill.Color);
            Assert.Equal(border.Color, track.Color);
        }
    }

    private static Color Colour(AltimTheme theme, string key, ThemeVariant variant) =>
        Resolve<Color>(theme, key, variant);

    private static T Resolve<T>(AltimTheme theme, string key, ThemeVariant variant)
    {
        Assert.True(theme.TryGetResource(key, variant, out object? value), $"{key} is missing.");
        return Assert.IsAssignableFrom<T>(value);
    }

    private static Color Invert(Color colour) =>
        Color.FromArgb(colour.A, (byte)(255 - colour.R), (byte)(255 - colour.G), (byte)(255 - colour.B));

    private static ResourceDictionary ThemeDictionaryHost(AltimTheme theme)
    {
        foreach (IResourceProvider merged in theme.MergedDictionaries)
        {
            if (merged is ResourceDictionary dictionary && dictionary.ThemeDictionaries.Count > 0)
            {
                return dictionary;
            }
        }

        Assert.Fail("The design system contains no theme dictionaries.");
        return null!;
    }
}
