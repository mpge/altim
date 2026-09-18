using Altim.UI.Formatting;

namespace Altim.UI.Controls;

/// <summary>
/// One sweep on a <see cref="Dial"/>: whose it is, what it reads, and what it is measured
/// against.
/// </summary>
/// <remarks>
/// <para>
/// A dial carries one of these per provider, each on its own concentric ring. <b>Nothing is
/// combined.</b> Two providers' percentages are proportions of two different, undisclosed
/// allowances, so adding, averaging or weighting them would produce an authoritative looking
/// figure that measures nothing. What the rings share is a <i>unit</i> - how much of that
/// provider's own allowance is gone - which is why they can stand on one face, one
/// graduation tape and one angular mapping without any arithmetic being done to them.
/// </para>
/// <para>
/// A reading with no <see cref="Value"/> is not a zero. It draws its ring as an outline with
/// no sweep, its figure is an em dash, and its words say it was not reported. A ring of no
/// length sitting at the start of the scale is the one picture that would read as a reported
/// nothing.
/// </para>
/// <para>
/// The control draws the list in order, outermost first. <see cref="Ring"/> carries that same
/// position on the item itself, because a legend bound to the list has no other way to know
/// which circle to draw, and a builder that numbers them from the list as it fills it cannot
/// get the two out of step.
/// </para>
/// </remarks>
public sealed class DialReading
{
    /// <summary>Initializes one sweep.</summary>
    /// <param name="ring">Which ring it is drawn on, 0 being the outermost.</param>
    /// <param name="label">Whose reading it is, in words. Empty for an unnamed single reading.</param>
    /// <param name="value">The level from 0 to 100, or null when the provider reports none.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <param name="detail">One more line for the tip, such as when the window resets.</param>
    public DialReading(
        int ring,
        string label,
        double? value,
        double? threshold = null,
        string? detail = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ring);
        ArgumentNullException.ThrowIfNull(label);

        Ring = ring;
        Label = label;
        Value = Normalise(value);
        Threshold = Normalise(threshold);
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
    }

    /// <summary>Which ring this is drawn on, 0 being the outermost.</summary>
    public int Ring { get; }

    /// <summary>Whose reading it is: <c>Session (Claude Code)</c>.</summary>
    public string Label { get; }

    /// <summary>The level from 0 to 100, or null when the provider reports none.</summary>
    public double? Value { get; }

    /// <summary>The level this reading's index stands at, or null when there is none.</summary>
    public double? Threshold { get; }

    /// <summary>One more line for the tip, such as <c>Resets in 2h 14m</c>.</summary>
    public string? Detail { get; }

    /// <summary>Whether a figure exists to show.</summary>
    public bool IsReported => Value is not null;

    /// <summary>The figure, or an em dash when the provider reports none.</summary>
    public string PercentText => UsageFormat.Percent(Value) ?? UsageFormat.Unknown;

    /// <summary>The band this reading has reached, or null when nothing is reported.</summary>
    public DialBand? Band => DialBands.BandFor(Value, Threshold);

    /// <summary>Whether the sweep's last degree stands in the normal band.</summary>
    public bool IsNormal => Band == DialBand.Normal;

    /// <summary>Whether the sweep's last degree stands in the caution band.</summary>
    public bool IsCaution => Band == DialBand.Caution;

    /// <summary>Whether the sweep has reached or passed the configured threshold.</summary>
    public bool IsExceeded => Band == DialBand.Exceeded;

    /// <summary>
    /// How wide the legend's mark for this ring is drawn, in device independent pixels.
    /// </summary>
    /// <remarks>
    /// The legend's circles stand in the same order as the rings they name, and shrink the
    /// same way, so "the big circle" and "the outer ring" are the same thing without anybody
    /// being told the convention. It is a diameter rather than a colour because two rings
    /// can easily be in the same band, and because a size survives having the colour taken
    /// away.
    /// </remarks>
    public double LegendMarkDiameter => Math.Max(Dial.LegendMarkStep, Dial.LegendMarkSize - (Dial.LegendMarkStep * Ring));

    /// <summary>
    /// What this ring reads, in words: whose it is, the level and the threshold.
    /// </summary>
    public string Words =>
        Label.Length == 0
            ? UsageFormat.InstrumentReading(Value, Threshold)
            : string.Concat(Label, ", ", UsageFormat.InstrumentReading(Value, Threshold));

    /// <summary>
    /// Everything a pointer or the keyboard reveals about this ring: the reading, when the
    /// window rolls over, and what the bands mean.
    /// </summary>
    public string Detailed =>
        string.Join(
            " ",
            Sentences(Words, Detail, DialBands.Words(Value, Threshold)));

    private static IEnumerable<string> Sentences(params string?[] parts)
    {
        foreach (string? part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            yield return part.EndsWith('.') ? part : string.Concat(part, ".");
        }
    }

    private static double? Normalise(double? value) =>
        value is not { } level || double.IsNaN(level) ? null : Math.Clamp(level, 0d, 100d);
}
