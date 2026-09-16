using Altim.Core.Settings;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// Settings must survive a round trip, and must survive a file somebody has edited by
/// hand. A bad value is a default, never an exception: a settings table is not allowed
/// to stop the application starting.
/// </summary>
public sealed class SqliteSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEmptyTableReadsAsTheDefaults()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        AltimSettings settings = await store.GetAsync(Ct);

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.False(settings.LaunchAtLogin);
        Assert.True(settings.StartMinimised);
        Assert.True(settings.NotificationsEnabled);
        Assert.True(settings.NotifyOnThreshold);
        Assert.True(settings.NotifyOnWindowReset);
        Assert.Equal(AltimSettings.DefaultSessionThresholdPercent, settings.SessionThresholdPercent);
        Assert.Equal(AltimSettings.DefaultWeeklyThresholdPercent, settings.WeeklyThresholdPercent);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.ActiveRefreshInterval);
        Assert.True(settings.RefreshOnResume);
        Assert.True(settings.AllowNetworkCalls);
        Assert.Equal(0L, temp.CountRows("setting"));
    }

    [Fact]
    public async Task EveryMemberRoundTrips()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        var written = new AltimSettings
        {
            Theme = ThemePreference.Dark,
            LaunchAtLogin = true,
            StartMinimised = false,
            NotificationsEnabled = false,
            NotifyOnThreshold = false,
            NotifyOnWindowReset = false,
            SessionThresholdPercent = 65,
            WeeklyThresholdPercent = 75,
            RefreshInterval = TimeSpan.FromSeconds(120),
            ActiveRefreshInterval = TimeSpan.FromSeconds(5),
            RefreshOnResume = false,
            AllowNetworkCalls = false,
        };

        await store.SaveAsync(written, Ct);
        AltimSettings read = await store.GetAsync(Ct);

        Assert.Equal(written, read);
    }

    [Fact]
    public async Task SavingTwiceUpdatesInPlace()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SaveAsync(AltimSettings.Default, Ct);
        long first = temp.CountRows("setting");

        await store.SaveAsync(AltimSettings.Default with { Theme = ThemePreference.Light }, Ct);

        Assert.Equal(first, temp.CountRows("setting"));
        Assert.Equal(ThemePreference.Light, (await store.GetAsync(Ct)).Theme);
    }

    [Fact]
    public async Task AMalformedValueFallsBackToItsDefaultWithoutThrowing()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SaveAsync(AltimSettings.Default with { Theme = ThemePreference.Dark }, Ct);

        await store.SetValueAsync(SqliteSettingsStore.Keys.NotificationsEnabled, "yes please", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.RefreshSeconds, "soon", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.ActiveRefreshSeconds, "-4", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.SessionThreshold, "eighty", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.WeeklyThreshold, "0", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.Theme, "chartreuse", Ct);

        AltimSettings settings = await store.GetAsync(Ct);

        Assert.Equal(AltimSettings.Default, settings);
    }

    [Fact]
    public async Task AnOutOfRangeThresholdFallsBackToItsDefault()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SetValueAsync(SqliteSettingsStore.Keys.SessionThreshold, "140", Ct);
        await store.SetValueAsync(SqliteSettingsStore.Keys.WeeklyThreshold, "-5", Ct);

        AltimSettings settings = await store.GetAsync(Ct);

        Assert.Equal(AltimSettings.DefaultSessionThresholdPercent, settings.SessionThresholdPercent);
        Assert.Equal(AltimSettings.DefaultWeeklyThresholdPercent, settings.WeeklyThresholdPercent);
    }

    [Fact]
    public async Task AnOutOfRangeThresholdIsNotWrittenBackEither()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SaveAsync(AltimSettings.Default with { SessionThresholdPercent = 0 }, Ct);

        Assert.Equal("80", await store.GetValueAsync(SqliteSettingsStore.Keys.SessionThreshold, Ct));
    }

    [Fact]
    public async Task ThemeSpellingIsToleratedButNeverInvented()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SetValueAsync(SqliteSettingsStore.Keys.Theme, "  DARK ", Ct);
        Assert.Equal(ThemePreference.Dark, (await store.GetAsync(Ct)).Theme);

        // A number is not a spelling. Enum.TryParse would have accepted this one.
        await store.SetValueAsync(SqliteSettingsStore.Keys.Theme, "7", Ct);
        Assert.Equal(ThemePreference.System, (await store.GetAsync(Ct)).Theme);
    }

    [Fact]
    public async Task ARemovedKeyGoesBackToItsDefault()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SaveAsync(AltimSettings.Default with { NotificationsEnabled = false }, Ct);
        Assert.False((await store.GetAsync(Ct)).NotificationsEnabled);

        await store.RemoveValueAsync(SqliteSettingsStore.Keys.NotificationsEnabled, Ct);

        Assert.True((await store.GetAsync(Ct)).NotificationsEnabled);
    }

    [Fact]
    public async Task AKeyThisBuildDoesNotKnowIsIgnoredRatherThanRejected()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        await store.SaveAsync(AltimSettings.Default, Ct);
        await store.SetValueAsync("appearance.something_from_a_later_release", "42", Ct);

        Assert.Equal(AltimSettings.Default, await store.GetAsync(Ct));
    }

    [Fact]
    public async Task ARawValueRoundTripsAndAMissingOneIsNull()
    {
        using var temp = new TempDatabase();
        var store = new SqliteSettingsStore(temp.Open());

        Assert.Null(await store.GetValueAsync("maintenance.nothing_here", Ct));

        await store.SetValueAsync("maintenance.nothing_here", "42", Ct);

        Assert.Equal("42", await store.GetValueAsync("maintenance.nothing_here", Ct));
    }

    [Fact]
    public async Task SettingsSurviveClosingAndReopeningTheDatabase()
    {
        using var temp = new TempDatabase();

        var store = new SqliteSettingsStore(temp.Open());
        await store.SaveAsync(
            AltimSettings.Default with { RefreshInterval = TimeSpan.FromSeconds(300) }, Ct);

        var reopened = new SqliteSettingsStore(temp.Open());

        Assert.Equal(TimeSpan.FromSeconds(300), (await reopened.GetAsync(Ct)).RefreshInterval);
    }
}
