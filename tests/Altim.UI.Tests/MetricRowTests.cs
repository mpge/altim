using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The metric row's contract, and the one that matters most: a metric nobody reported shows a
/// sentence and an outlined meter, never a zero and never an empty track.
/// </summary>
public sealed class MetricRowTests
{
    private static readonly AltimSettings Settings = AltimSettings.Default;

    /// <summary>A reported metric carries a figure, a level and a reset caption.</summary>
    [Fact]
    public void ReportedMetricCarriesItsFigure()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric metric = Readings.Metric(
            "five_hour",
            "Session",
            62d,
            TimeSpan.FromHours(5),
            Readings.Now.AddMinutes(134));

        var row = new MetricViewModel(metric, Settings, clock, isPrimary: true);

        Assert.True(row.IsReported);
        Assert.Equal(62d, row.Value);
        Assert.Equal("62%", row.PercentText);
        Assert.Equal("Resets in 2h 14m", row.ResetText);
        Assert.True(row.IsPrimary);
        Assert.False(row.IsAboveThreshold);
    }

    /// <summary>A metric with no percentage has no figure and no level.</summary>
    [Fact]
    public void UnreportedMetricHasNoNumber()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric metric = Readings.Metric("seven_day", "Weekly", null, TimeSpan.FromDays(7));

        var row = new MetricViewModel(metric, Settings, clock);

        Assert.False(row.IsReported);
        Assert.Null(row.Value);
        Assert.Null(row.PercentText);
        Assert.Equal("Not reported by this provider", row.UnavailableText);
        Assert.False(row.HasReset);
    }

    /// <summary>A percentage at or past the threshold turns the label, and only the label.</summary>
    [Fact]
    public void MarksAMetricAboveItsThreshold()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric metric = Readings.Metric("five_hour", "Session", 81d, TimeSpan.FromHours(5));

        var row = new MetricViewModel(metric, Settings, clock);

        Assert.Equal(AltimSettings.DefaultSessionThresholdPercent, row.Threshold);
        Assert.True(row.IsAboveThreshold);
    }

    /// <summary>A weekly window ticks at the weekly threshold, not the session one.</summary>
    [Fact]
    public void TakesItsThresholdFromTheWindow()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric weekly = Readings.Metric("seven_day", "Weekly", 38d, TimeSpan.FromDays(7));

        var row = new MetricViewModel(weekly, Settings, clock);

        Assert.Equal(AltimSettings.DefaultWeeklyThresholdPercent, row.Threshold);
    }

    /// <summary>On screen, an unreported metric is a sentence and an outline.</summary>
    [AvaloniaFact]
    public void UnreportedMetricRendersTheSentenceAndNoZero()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric metric = Readings.Metric("seven_day", "Weekly", null, TimeSpan.FromDays(7));
        var view = new MetricRowView { DataContext = new MetricViewModel(metric, Settings, clock) };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.MetricUnavailable));
            Assert.DoesNotContain("0%", Surface.Lines(window));

            Meter meter = Assert.Single(Surface.Visible<Meter>(window));
            Assert.Null(meter.Value);
            Assert.True(meter.IsUnavailable);
            Assert.Equal(0d, meter.RenderedFillWidth);
        }, height: 120d);
    }

    /// <summary>On screen, a reported metric is a figure and a filled rail.</summary>
    [AvaloniaFact]
    public void ReportedMetricRendersItsFigure()
    {
        var clock = new TestClock(Readings.Now);
        UsageMetric metric = Readings.Metric(
            "five_hour",
            "Session",
            62d,
            TimeSpan.FromHours(5),
            Readings.Now.AddMinutes(134));
        var view = new MetricRowView { DataContext = new MetricViewModel(metric, Settings, clock, true) };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, "62%"));
            Assert.True(Surface.Shows(window, "Session"));
            Assert.True(Surface.Shows(window, "Resets in 2h 14m"));
            Assert.False(Surface.Shows(window, UsageFormat.MetricUnavailable));

            Meter meter = Assert.Single(Surface.Visible<Meter>(window));
            Assert.Equal(62d, meter.Value);
            Assert.True(meter.RenderedFillWidth > 0d);
        }, height: 120d);
    }
}
