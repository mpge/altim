using Avalonia.Media;

namespace Altim.UI.Themes;

/// <summary>
/// The OpenType feature set every figure in Altim is rendered with: <c>tnum</c> so digits
/// share one advance width and a percentage does not shift as it ticks, and <c>zero</c>
/// so a slashed zero cannot be mistaken for the letter O in a token count.
/// </summary>
/// <remarks>
/// This exists as a type rather than as inline XAML so the feature strings are parsed by
/// the same code Avalonia uses everywhere else, start and end offsets included. It is
/// published to XAML as the <c>AltimTabularFigures</c> resource in Typography.axaml.
/// </remarks>
public sealed class TabularFigures : FontFeatureCollection
{
    /// <summary>Initializes a collection containing the tabular figure features.</summary>
    public TabularFigures()
    {
        Add(FontFeature.Parse("tnum"));
        Add(FontFeature.Parse("zero"));
    }
}
