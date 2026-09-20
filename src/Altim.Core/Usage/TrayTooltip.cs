using System.Globalization;
using System.Text;
using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// The tray icon's hover text: the application name, then one line per provider carrying the
/// metric closest to its limit. Pure: no clock, no state, same input gives the same output.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing is invented.</strong> A provider that reported no usable percentage says
/// so, a failed reading says it is unavailable, and neither is rendered as a zero. This is
/// the tray's whole enforcement of the rule that unknown and zero are different, and it is
/// the surface where the rule is easiest to break, because the honest answers are longer than
/// the dishonest one.
/// </para>
/// <para>
/// Only the providers this machine has, on the same rule as the panel's rows - see
/// <see cref="ProviderVisibility"/>. This is the surface with least room for a line about a
/// tool the reader does not own: the shell truncates the whole tooltip at
/// <see cref="Limit"/> characters, so a provider that is simply not installed costs one of
/// the three lines that fit. <strong>A failed reading still takes its line</strong>, and an
/// uninstalled provider is still reported by the tray's own menu, which carries a disabled
/// line for every degraded condition, so nothing is concealed by leaving it out here.
/// </para>
/// <para>
/// It lives in <c>Altim.Core</c> rather than beside the tray controller so that it can be
/// tested. A test project that referenced <c>Altim.App</c> would make building the
/// application mandatory for the whole suite, and a running Altim locks that project's
/// output, so "run the app while running the tests" would stop working. The provider names
/// are supplied by the caller for the same reason, which is also the shape
/// <see cref="UsageAggregator.Aggregate"/> already takes.
/// </para>
/// </remarks>
public static class TrayTooltip
{
    /// <summary>
    /// The longest tooltip the shell will show. Windows truncates at 128 characters including
    /// the terminator, so 127 is what fits.
    /// </summary>
    public const int Limit = 127;

    /// <summary>The first line, which is the application naming itself.</summary>
    public const string Heading = "Altim";

    /// <summary>
    /// Said of a provider that is installed and whose last reading failed.
    /// </summary>
    /// <remarks>
    /// Not "0%", and not silence. A failed read means the provider is there and Altim cannot
    /// see it, which is the one thing a monitor exists to say out loud.
    /// </remarks>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Said of a provider that answered and carried no percentage Altim is willing to show.
    /// </summary>
    public const string NotReported = "not reported";

    /// <summary>
    /// Said when every registered provider turned out not to be on this machine.
    /// </summary>
    /// <remarks>
    /// Word for word what <see cref="UsageAggregator.DescribeStatus"/> says about the same
    /// machine, so the tray and the panel header cannot end up describing it differently.
    /// <c>TrayTooltipTests</c> holds the two together.
    /// </remarks>
    public const string NoProvidersDetected = "No providers detected";

    /// <summary>
    /// Builds the hover text.
    /// </summary>
    /// <param name="readings">Every provider's current reading.</param>
    /// <param name="displayName">
    /// Resolves a provider id to the name to put on its line, or <see langword="null"/> to use
    /// the ids. A lookup that returns null or blank falls back to the id, so a line is never
    /// missing its subject.
    /// </param>
    /// <returns>
    /// At most <see cref="Limit"/> characters, cut at a line boundary. Never empty.
    /// </returns>
    public static string Build(
        IReadOnlyList<ProviderUsage> readings,
        Func<string, string>? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (readings.Count == 0)
        {
            return Join([Heading, UsageOverview.Empty.StatusLine]);
        }

        List<string> lines = [Heading];

        foreach (ProviderUsage reading in readings)
        {
            if (!ProviderVisibility.IsShown(reading.Status))
            {
                continue;
            }

            string name = NameOf(reading.ProviderId, displayName);

            if (reading.Status == ProviderStatus.Error)
            {
                lines.Add(string.Concat(name, " ", Unavailable));
                continue;
            }

            UsageOverview overview = UsageAggregator.Aggregate([reading]);

            // ReportedPercent rather than UsedPercent: the metric documents it as the value to
            // render, and the only one. The aggregator already hands back a metric whose raw
            // field has been normalised, so today the two agree - but reading the raw field
            // means this line would print 101% the day it stopped agreeing, and a percentage
            // above a full window is not a thing to put in front of a reader.
            lines.Add(
                overview.WorstMetric is { } metric && metric.ReportedPercent is { } percent
                    ? string.Concat(name, " ", metric.Label, " ", Percent(percent))
                    : string.Concat(name, " ", NotReported));
        }

        if (lines.Count == 1)
        {
            // Registered, but none of them here.
            lines.Add(NoProvidersDetected);
        }

        return Join(lines);
    }

    /// <summary>
    /// A percentage as a whole number, which is what <c>UsageFormat.Percent</c> produces for
    /// the same reading.
    /// </summary>
    /// <param name="percent">A normalised percentage.</param>
    /// <returns>Something like <c>62%</c>.</returns>
    /// <remarks>
    /// The interface's own formatter cannot be called from here: it belongs to
    /// <c>Altim.UI</c>, and <c>Altim.Core</c> depends on the base class library and nothing
    /// else, which is what makes it testable without a rendering platform. What matters is
    /// not which method runs but that the same normalisation does, and it does: the formatter
    /// in the interface is <see cref="UsagePercent.Normalise(double?)"/> followed by this same
    /// whole-number format, and the caller above has already normalised. The two are held to
    /// the same output by a test, in the one place either of them could drift.
    /// </remarks>
    private static string Percent(double percent) =>
        string.Concat(percent.ToString("0", CultureInfo.CurrentCulture), "%");

    private static string NameOf(string providerId, Func<string, string>? displayName)
    {
        string? resolved = displayName?.Invoke(providerId);
        return string.IsNullOrWhiteSpace(resolved) ? providerId : resolved;
    }

    /// <summary>
    /// Joins the lines that fit, and stops at the first one that does not.
    /// </summary>
    /// <param name="lines">The lines, heading first.</param>
    /// <returns>The tooltip.</returns>
    /// <remarks>
    /// <para>
    /// Whole lines, never part of one. The cut this replaced took the first
    /// <see cref="Limit"/> characters of the finished string, which lands wherever it lands:
    /// a tooltip whose last line read "Codex Weekly 4" for a provider at 47% was reporting a
    /// figure that was not true, in the one place a reader has no way of telling it had been
    /// shortened. Rule 1 does not have an exception for running out of room.
    /// </para>
    /// <para>
    /// It stops rather than skipping, so what is shown is always a prefix of what was built.
    /// Dropping a long line and keeping a later short one would silently reorder the
    /// providers, which reads as a provider having gone missing rather than as a tooltip
    /// having run out of room.
    /// </para>
    /// </remarks>
    private static string Join(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();

        foreach (string line in lines)
        {
            int wanted = text.Length == 0 ? line.Length : text.Length + 1 + line.Length;
            if (wanted > Limit)
            {
                break;
            }

            if (text.Length > 0)
            {
                _ = text.Append('\n');
            }

            _ = text.Append(line);
        }

        return text.ToString();
    }
}
