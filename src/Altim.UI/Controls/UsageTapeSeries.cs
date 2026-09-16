namespace Altim.UI.Controls;

/// <summary>
/// How prominently a <see cref="UsageTapeSeries"/> is drawn. The tape carries no palette
/// of its own: colour on a chart would be decoration, and DESIGN.md reserves colour for
/// provider identity and status.
/// </summary>
public enum UsageTapeEmphasis
{
    /// <summary>Drawn in <c>AltimChartLinePrimaryBrush</c>.</summary>
    Primary = 0,

    /// <summary>Drawn in <c>AltimChartLineSecondaryBrush</c>.</summary>
    Secondary = 1,
}

/// <summary>
/// One provider's history on a <see cref="UsageTape"/>: an ordered run of levels, oldest
/// first, with the newest sample at the right edge of the plot.
/// </summary>
/// <param name="Name">
/// The provider name, drawn inline at the end of the line. There is no legend box.
/// </param>
/// <param name="Values">
/// Levels from 0 to 100, oldest first. A null is a gap the line breaks across, never a
/// zero: a sample the provider did not report is not a sample of nothing.
/// </param>
/// <param name="Emphasis">Which of the two line weights this series is drawn in.</param>
public sealed record UsageTapeSeries(
    string Name,
    IReadOnlyList<double?> Values,
    UsageTapeEmphasis Emphasis = UsageTapeEmphasis.Primary);
