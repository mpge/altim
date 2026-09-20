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

        // On, and stated here rather than assumed: it is the only request Altim makes to a
        // server of its own, so a change to the default has to be a change to this line.
        Assert.True(settings.AutomaticUpdateChecks);
    }

    [Fact]
    public void ANewInstanceMatchesTheSharedDefault() =>
        Assert.Equal(AltimSettings.Default, new AltimSettings());

    /// <summary>
    /// The defect this covers: an unreadable database silently granting a permission the
    /// user had taken away. The memory fallback used to start from the defaults, which say
    /// yes to both switches that decide what leaves the machine, so somebody who had turned
    /// live quota checks <em>off</em> got them <em>on</em> the first time their database
    /// would not open — with nothing on screen and nothing in the log to say so.
    /// </summary>
    [Fact]
    public void TheFallbackForUnreadableSettingsSaysNoToBothPermissions()
    {
        Assert.False(AltimSettings.FailClosed.AllowNetworkCalls);
        Assert.False(AltimSettings.FailClosed.AutomaticUpdateChecks);

        // And the defaults it is standing in for say yes to both, which is the whole reason
        // it cannot be them.
        Assert.True(AltimSettings.Default.AllowNetworkCalls);
        Assert.True(AltimSettings.Default.AutomaticUpdateChecks);
    }

    /// <summary>
    /// Only the permissions differ. A theme or a threshold that reverts is a visible
    /// annoyance; a permission that reverts is a decision made on somebody's behalf, and
    /// widening this fallback past the two would be turning the first into the second.
    /// </summary>
    [Fact]
    public void TheFallbackDiffersFromTheDefaultsInNothingElse()
    {
        AltimSettings widened = AltimSettings.FailClosed with
        {
            AllowNetworkCalls = true,
            AutomaticUpdateChecks = true,
        };

        Assert.Equal(AltimSettings.Default, widened);
    }

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
