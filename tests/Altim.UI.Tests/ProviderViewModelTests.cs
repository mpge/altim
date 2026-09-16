using System.Diagnostics;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The provider row's contract: it reads nothing until asked, it never blocks the dispatcher,
/// and a failure becomes the contract's sentence and an enabled retry rather than an exception
/// on screen.
/// </summary>
public sealed class ProviderViewModelTests
{
    private const string ProviderId = "claude";
    private const string ProviderName = "Claude Code";

    /// <summary>Constructing a row reads nothing and claims nothing.</summary>
    [Fact]
    public void ConstructionReadsNothing()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        Assert.Equal(0, provider.UsageReads);
        Assert.Empty(row.Metrics);
        Assert.False(row.HasMetrics);
        Assert.False(row.HasError);
        Assert.Null(row.ResetText);
        Assert.Equal(UsageFormat.NotRefreshedYet, row.LastRefreshedText);
    }

    /// <summary>
    /// A provider that blocks its caller costs the dispatcher nothing. The fake blocks the
    /// thread before its first await, which is what opening a database or running a CLI does.
    /// </summary>
    [Fact]
    public async Task SlowProviderDoesNotBlockConstruction()
    {
        using var gate = new ManualResetEventSlim(false);
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Block = gate };

        var stopwatch = Stopwatch.StartNew();
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);
        Task reading = row.LoadAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Construction and dispatch took {stopwatch.Elapsed}.");
        Assert.False(reading.IsCompleted);

        // Nothing is shown while the reading is outstanding. A zero here would be a number
        // no provider has reported yet.
        Assert.Empty(row.Metrics);
        Assert.Equal(UsageFormat.NotRefreshedYet, row.LastRefreshedText);

        gate.Set();
        await reading;

        Assert.True(row.HasMetrics);
        Assert.Equal(2, row.Metrics.Count);
    }

    /// <summary>A reading that throws becomes the contract's sentence, not the exception's.</summary>
    [Fact]
    public async Task FailedReadingShowsTheSentence()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName)
        {
            Failure = new InvalidOperationException("token 1234 rejected at C:\\Users\\someone\\.claude"),
        };

        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);
        await row.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(row.HasError);
        Assert.Equal("Unable to retrieve usage", row.ErrorText);
        Assert.Equal(ProviderUsage.UnavailableDetail, row.CurrentUsage.StatusDetail);
        Assert.Equal("Unavailable", row.Status.Word);
        Assert.True(row.Status.IsError);
        Assert.Empty(row.Metrics);
        Assert.False(row.ShowsNoMetricsNotice);
    }

    /// <summary>A failure enables retry, and a success does not offer one.</summary>
    [Fact]
    public async Task FailureEnablesRetry()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Failure = new IOException("no") };
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        Assert.False(row.RetryCommand.CanExecute(null));

        await row.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(row.RetryCommand.CanExecute(null));

        provider.Failure = null;
        provider.Set(Readings.Healthy(ProviderId));
        await row.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.Refreshes);
        Assert.False(row.HasError);
        Assert.True(row.HasMetrics);
        Assert.False(row.RetryCommand.CanExecute(null));
    }

    /// <summary>A reading that carried no metric says so, which is not the same as a failure.</summary>
    [Fact]
    public async Task ReadingWithNoMetricSaysSo()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.NoMetrics(ProviderId));
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        await row.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(row.HasError);
        Assert.False(row.HasMetrics);
        Assert.True(row.ShowsNoMetricsNotice);
        Assert.Equal("Not reported by this provider", row.NoMetricsText);
    }

    /// <summary>
    /// A window that has already rolled over is not shown as a current level. Core drops it, and
    /// the row shows the metric as unreported rather than as a stale percentage.
    /// </summary>
    [Fact]
    public async Task StaleWindowIsNotShownAsCurrent()
    {
        ProviderUsage stale = new(
            ProviderId,
            ProviderStatus.Active,
            [Readings.Metric("five_hour", "Session", 62d, TimeSpan.FromHours(5), Readings.Now.AddHours(-1))],
            null,
            Readings.Now,
            null);

        var provider = new FakeUsageProvider(ProviderId, ProviderName, stale);
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        await row.LoadAsync(TestContext.Current.CancellationToken);

        MetricViewModel metric = Assert.Single(row.Metrics);
        Assert.False(metric.IsReported);
        Assert.Null(metric.Value);
        Assert.False(metric.HasReset);
    }

    /// <summary>Tokens are listed only when the provider reports them.</summary>
    [Fact]
    public async Task TokensAppearOnlyWhenReported()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.NoMetrics(ProviderId));
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        await row.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(row.HasTokens);
        Assert.Empty(row.TokenRows);

        provider.Set(Readings.Healthy(ProviderId));
        await row.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(row.HasTokens);
        Assert.Equal(4, row.TokenRows.Count);
        Assert.Equal("69K tokens", row.TokensText);
    }

    /// <summary>A new threshold moves the tick without a fresh reading.</summary>
    [Fact]
    public async Task NewThresholdMovesTheTick()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);
        await row.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(row.Metrics[0].IsAboveThreshold);

        row.ApplySettings(AltimSettings.Default with { SessionThresholdPercent = 50 });

        Assert.Equal(50d, row.Metrics[0].Threshold);
        Assert.True(row.Metrics[0].IsAboveThreshold);
        Assert.Equal(1, provider.UsageReads);
    }

    /// <summary>A reading the provider announces lands on the row without anyone asking.</summary>
    [AvaloniaFact]
    public void AnnouncedReadingLands()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.NoMetrics(ProviderId));
        using var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        provider.Push(Readings.Healthy(ProviderId));

        Assert.True(row.HasMetrics);
        Assert.Equal("62%", row.Metrics[0].PercentText);
        Assert.Equal(0, provider.UsageReads);
    }

    /// <summary>A disposed row stops listening, so a closed window is not still updating.</summary>
    [AvaloniaFact]
    public void DisposedRowStopsListening()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.NoMetrics(ProviderId));
        var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);

        row.Dispose();
        provider.Push(Readings.Healthy(ProviderId));

        Assert.False(row.HasMetrics);
    }
}
