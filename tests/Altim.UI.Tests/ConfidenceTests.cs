using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Whether a reader can tell a figure Altim derived from one a vendor stated.
/// </summary>
/// <remarks>
/// <para>
/// This is rule 2's mitigation and for a long time it was not wired to anything.
/// <c>MetricViewModel.IsBestEffort</c> was computed from the provider's own grading, exposed
/// as a public property, and read by no view and no test — so a percentage Altim had scraped
/// out of prose and one Anthropic states were byte for byte identical on screen. A caveat that
/// reaches nobody is not a caveat.
/// </para>
/// <para>
/// The shape is the one <c>ShowsLocalOnlyNotice</c> already set: one caption per card, and the
/// row itself carries the longer sentence on hover and to a screen reader. A badge on every
/// derived row would be furniture on a card where most rows are derived, which is the case
/// these tests are built from.
/// </para>
/// </remarks>
public class ConfidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static MetricViewModel Metric(MetricConfidence confidence) =>
        new(
            new UsageMetric("five_hour", "Session", 53d, new LimitWindow(TimeSpan.FromHours(5), null), confidence),
            AltimSettings.Default,
            TimeProvider.System,
            isPrimary: true);

    [Fact]
    public void ADerivedFigureSaysWhereItCameFrom()
    {
        MetricViewModel metric = Metric(MetricConfidence.BestEffort);

        Assert.True(metric.IsBestEffort);
        Assert.Equal(UsageFormat.BestEffortFigure, metric.ConfidenceNotice);
    }

    /// <summary>
    /// Null rather than a reassuring sentence, because most rows are documented and a caption
    /// under every one of them teaches a reader to stop looking — which costs exactly the rows
    /// the caption exists for.
    /// </summary>
    [Fact]
    public void ADocumentedFigureSaysNothing()
    {
        MetricViewModel metric = Metric(MetricConfidence.Documented);

        Assert.False(metric.IsBestEffort);
        Assert.Null(metric.ConfidenceNotice);
    }

    /// <summary>
    /// The two sentences have to be different sentences. One is read once per card and the
    /// other once per row, and a card that repeated the row's wording four times would be the
    /// noise this shape exists to avoid.
    /// </summary>
    [Fact]
    public void TheCardSentenceAndTheRowSentenceAreNotTheSameSentence()
    {
        Assert.NotEqual(UsageFormat.BestEffortFigure, UsageFormat.BestEffortOnThisCard);
        Assert.NotEmpty(UsageFormat.BestEffortFigure);
        Assert.NotEmpty(UsageFormat.BestEffortOnThisCard);
    }

    /// <summary>
    /// Neither sentence names a vendor. Every provider this applies to is a different one and
    /// the wording is the same for all of them; saying which vendor does not document a figure
    /// reads as a complaint about them rather than a caveat about the number.
    /// </summary>
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Claude")]
    [InlineData("OpenAI")]
    [InlineData("Codex")]
    [InlineData("Google")]
    [InlineData("Gemini")]
    public void NeitherSentenceNamesAVendor(string vendor)
    {
        Assert.DoesNotContain(vendor, UsageFormat.BestEffortFigure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vendor, UsageFormat.BestEffortOnThisCard, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACardWithADerivedFigureCarriesTheNotice()
    {
        ProviderViewModel provider = Card(
            new UsageMetric("five_hour", "Session", 53d, new LimitWindow(TimeSpan.FromHours(5), null), MetricConfidence.BestEffort));

        Assert.True(provider.ShowsBestEffortNotice);
        Assert.Equal(UsageFormat.BestEffortOnThisCard, provider.BestEffortText);
    }

    [Fact]
    public void ACardWhoseFiguresAreAllDocumentedDoesNot()
    {
        ProviderViewModel provider = Card(
            new UsageMetric("five_hour", "Session", 53d, new LimitWindow(TimeSpan.FromHours(5), null), MetricConfidence.Documented),
            new UsageMetric("seven_day", "Weekly", 85d, new LimitWindow(TimeSpan.FromDays(7), null), MetricConfidence.Documented));

        Assert.False(provider.ShowsBestEffortNotice);
    }

    /// <summary>
    /// One derived row among documented ones is enough, and it is the case that matters: a
    /// card where every figure is stated except one is exactly where an unmarked derived
    /// figure would be taken for another stated one.
    /// </summary>
    [Fact]
    public void OneDerivedRowAmongDocumentedOnesIsEnough()
    {
        ProviderViewModel provider = Card(
            new UsageMetric("five_hour", "Session", 53d, new LimitWindow(TimeSpan.FromHours(5), null), MetricConfidence.Documented),
            new UsageMetric("seven_day_opus", "Weekly (Opus)", 12d, new LimitWindow(TimeSpan.FromDays(7), null), MetricConfidence.BestEffort));

        Assert.True(provider.ShowsBestEffortNotice);

        // And the reader can tell which row it was.
        Assert.Collection(
            provider.Metrics,
            documented => Assert.Null(documented.ConfidenceNotice),
            derived => Assert.Equal(UsageFormat.BestEffortFigure, derived.ConfidenceNotice));
    }

    /// <summary>
    /// A reading that failed carries no rows at all, so there is nothing on the card whose
    /// provenance could be in question and the caption would be about nothing.
    /// </summary>
    [Fact]
    public void AFailedReadingCarriesNoNotice()
    {
        var provider = new ProviderViewModel(
            new FakeUsageProvider("claude", "Claude Code"),
            TimeProvider.System,
            AltimSettings.Default);

        provider.Apply(new ProviderUsage(
            "claude",
            ProviderStatus.Error,
            [new UsageMetric("five_hour", "Session", 53d, new LimitWindow(TimeSpan.FromHours(5), null), MetricConfidence.BestEffort)],
            null,
            Now,
            "Unavailable"));

        Assert.False(provider.ShowsBestEffortNotice);
    }

    private static ProviderViewModel Card(params UsageMetric[] metrics)
    {
        var provider = new ProviderViewModel(
            new FakeUsageProvider("claude", "Claude Code"),
            TimeProvider.System,
            AltimSettings.Default);

        provider.Apply(new ProviderUsage("claude", ProviderStatus.Idle, metrics, null, Now, null));
        return provider;
    }
}
