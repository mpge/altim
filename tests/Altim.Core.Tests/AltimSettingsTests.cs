using Altim.Core.Monitoring;
using Altim.Core.Settings;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The defaults a fresh install runs with. They are asserted rather than assumed, because
/// a missing row in the settings table falls back to them.
/// </summary>
public sealed class AltimSettingsTests
{
    [Fact]
    public void FreshInstallDefaults()
    {
        AltimSettings settings = AltimSettings.Default;

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.False(settings.LaunchAtLogin);
        Assert.True(settings.StartMinimised);
        Assert.True(settings.NotificationsEnabled);
        Assert.True(settings.NotifyOnThreshold);
        Assert.True(settings.NotifyOnWindowReset);
        Assert.Equal(80, settings.SessionThresholdPercent);
        Assert.Equal(90, settings.WeeklyThresholdPercent);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ActiveRefreshInterval);
        Assert.True(settings.RefreshOnResume);
        Assert.True(settings.AllowNetworkCalls);

        // Off, and it stays off until somebody asks: turning it on writes to Claude Code's
        // own settings file.
        Assert.False(settings.ClaudeStatusLineEnabled);
    }

    [Fact]
    public void ANewInstanceMatchesTheSharedDefault() =>
        Assert.Equal(AltimSettings.Default, new AltimSettings());

    [Fact]
    public void ChangingOneSettingLeavesTheRestAlone()
    {
        AltimSettings changed = AltimSettings.Default with { Theme = ThemePreference.Dark, SessionThresholdPercent = 65 };

        Assert.Equal(ThemePreference.Dark, changed.Theme);
        Assert.Equal(65, changed.SessionThresholdPercent);
        Assert.Equal(90, changed.WeeklyThresholdPercent);
        Assert.True(changed.NotificationsEnabled);
        Assert.NotEqual(AltimSettings.Default, changed);
    }

    [Theory]
    [InlineData(140, 100)]
    [InlineData(101, 100)]
    [InlineData(100, 100)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void NotificationThresholdsAreClampedToAUsablePercentage(int configured, int expected)
    {
        AltimSettings session = AltimSettings.Default with { SessionThresholdPercent = configured };
        AltimSettings weekly = AltimSettings.Default with { WeeklyThresholdPercent = configured };

        Assert.Equal(expected, session.SessionThresholdPercent);
        Assert.Equal(expected, weekly.WeeklyThresholdPercent);
    }

    [Fact]
    public void SchedulerOptionsFollowTheRefreshSettings()
    {
        AltimSettings settings = AltimSettings.Default with
        {
            RefreshInterval = TimeSpan.FromMinutes(5),
            ActiveRefreshInterval = TimeSpan.FromSeconds(5),
            RefreshOnResume = false,
        };

        MonitorSchedulerOptions options = MonitorSchedulerOptions.FromSettings(settings);

        Assert.Equal(TimeSpan.FromMinutes(5), options.RelaxedInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.TightenedInterval);
        Assert.False(options.RefreshOnResume);

        // The protections are not user configurable.
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.HintDebounce);
        Assert.Equal(TimeSpan.FromMinutes(1), options.NetworkMinimumInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ProviderTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ShutdownTimeout);
    }

    [Fact]
    public void SchedulerDefaultsMatchTheArchitecture()
    {
        MonitorSchedulerOptions options = MonitorSchedulerOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(60), options.RelaxedInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.TightenedInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.HintDebounce);
        Assert.Equal(TimeSpan.FromMinutes(1), options.NetworkMinimumInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ProviderTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ShutdownTimeout);
        Assert.True(options.RefreshOnResume);
    }
}
