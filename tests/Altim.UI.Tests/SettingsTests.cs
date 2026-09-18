using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Settings round-trip through the injected store and nothing else. The page never sees SQLite,
/// a file path or a platform service, which is what lets this run without either.
/// </summary>
public sealed class SettingsTests
{
    private static SettingsViewModel Page(
        FakeSettingsStore store,
        FakeHistoryService history,
        params ProviderViewModel[] providers) =>
        new(store, history, new FakeStatusLineService(), providers);

    private static SettingsViewModel Page(
        FakeSettingsStore store,
        FakeStatusLineService statusLine,
        params ProviderViewModel[] providers) =>
        new(store, new FakeHistoryService(), statusLine, providers);

    private static ProviderViewModel Row() =>
        new(
            new FakeUsageProvider("claude", "Claude Code"),
            new TestClock(Readings.Now),
            AltimSettings.Default);

    /// <summary>Loading takes every value from the store.</summary>
    [Fact]
    public async Task LoadsFromTheStore()
    {
        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with
            {
                LaunchAtLogin = true,
                StartMinimised = false,
                Theme = ThemePreference.Dark,
                RefreshInterval = TimeSpan.FromMinutes(5),
                NotificationsEnabled = false,
                SessionThresholdPercent = 70,
                WeeklyThresholdPercent = 85,
                NotifyOnWindowReset = false,
                AllowNetworkCalls = false,
            },
        };

        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.LaunchAtLogin);
        Assert.False(page.StartMinimised);
        Assert.Equal(ThemePreference.Dark, page.SelectedTheme.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), page.SelectedRefresh.Value);
        Assert.False(page.NotificationsEnabled);
        Assert.Equal(70, page.SelectedSessionThreshold.Value);
        Assert.Equal(85, page.SelectedWeeklyThreshold.Value);
        Assert.False(page.ResetAlertsEnabled);
        Assert.False(page.AllowNetworkCalls);
    }

    /// <summary>
    /// The live-quota permission round-trips, and the provider rows on the same page hear
    /// about it.
    /// </summary>
    /// <remarks>
    /// The defect this covers: <c>AllowNetworkCalls</c> existed in the settings record and in
    /// the database and had no control anywhere, so the only way to reach it was to edit the
    /// row by hand. A setting nothing can set is not a setting.
    /// </remarks>
    [Fact]
    public async Task TheLiveQuotaPermissionRoundTripsAndReachesTheProviderRows()
    {
        var store = new FakeSettingsStore();
        ProviderViewModel row = Row();
        SettingsViewModel page = Page(store, new FakeHistoryService(), row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.AllowNetworkCalls);
        Assert.False(page.ShowsLocalOnlyNotice);
        Assert.False(row.ShowsLocalOnlyNotice);

        page.AllowNetworkCalls = false;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.False(saved.AllowNetworkCalls);

        // And the page says what the figures now are, rather than leaving the toggle to
        // speak for itself.
        Assert.True(page.ShowsLocalOnlyNotice);
        Assert.True(row.ShowsLocalOnlyNotice);
        Assert.Equal(row.LocalOnlyText, page.LocalOnlyNotice);
    }

    /// <summary>Every edited value comes back out through the same store.</summary>
    [Fact]
    public async Task SavesThroughTheStore()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.LaunchAtLogin = true;
        page.StartMinimised = false;
        page.SelectedTheme = ThemeOption.For(ThemePreference.Light);
        page.SelectedRefresh = RefreshOption.Standard[0];
        page.SelectedSessionThreshold = ThresholdOption.For(70);
        page.SelectedWeeklyThreshold = ThresholdOption.For(95);
        page.ResetAlertsEnabled = false;

        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.True(saved.LaunchAtLogin);
        Assert.False(saved.StartMinimised);
        Assert.Equal(ThemePreference.Light, saved.Theme);
        Assert.Equal(TimeSpan.FromSeconds(30), saved.RefreshInterval);
        Assert.Equal(70, saved.SessionThresholdPercent);
        Assert.Equal(95, saved.WeeklyThresholdPercent);
        Assert.False(saved.NotifyOnWindowReset);
        Assert.False(page.SaveFailed);
    }

    /// <summary>A change writes itself: there is no apply button to forget to press.</summary>
    [Fact]
    public async Task AChangeSavesItself()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        store.ExpectWrite();
        page.LaunchAtLogin = true;

        await store.Written.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(store.Saved?.LaunchAtLogin);
    }

    /// <summary>
    /// Several changes in a row leave the store holding the last of them. The page saves itself
    /// on every change, so the writes overlap; writes handed to the thread pool independently
    /// complete in any order, and the record left behind would be whichever finished last rather
    /// than the one the user ended on.
    /// </summary>
    [Fact]
    public async Task RapidChangesLeaveTheNewestRecordStored()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.SelectedTheme = ThemeOption.For(ThemePreference.Dark);
        page.SelectedRefresh = RefreshOption.Standard[0];
        page.NotificationsEnabled = false;
        page.LaunchAtLogin = true;
        page.SelectedTheme = ThemeOption.For(ThemePreference.Light);

        // Awaiting the last save awaits every save queued ahead of it.
        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.Equal(ThemePreference.Light, saved.Theme);
        Assert.Equal(TimeSpan.FromSeconds(30), saved.RefreshInterval);
        Assert.False(saved.NotificationsEnabled);
        Assert.True(saved.LaunchAtLogin);
    }

    /// <summary>A setting the page does not show survives a save it was not part of.</summary>
    [Fact]
    public async Task KeepsSettingsThePageDoesNotShow()
    {
        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with
            {
                NotifyOnThreshold = false,
                ActiveRefreshInterval = TimeSpan.FromSeconds(3),
                AllowNetworkCalls = false,
            },
        };

        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.LaunchAtLogin = true;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.False(saved.NotifyOnThreshold);
        Assert.Equal(TimeSpan.FromSeconds(3), saved.ActiveRefreshInterval);
        Assert.False(saved.AllowNetworkCalls);
    }

    /// <summary>A stored value that is not one of the offered ones is offered anyway.</summary>
    [Fact]
    public async Task OffersAStoredValueThatIsNotStandard()
    {
        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with
            {
                RefreshInterval = TimeSpan.FromSeconds(90),
                SessionThresholdPercent = 77,
            },
        };

        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(90), page.SelectedRefresh.Value);
        Assert.Contains(page.RefreshOptions, option => option.Value == TimeSpan.FromSeconds(90));
        Assert.Equal(77, page.SelectedSessionThreshold.Value);
        Assert.Contains(page.ThresholdOptions, option => option.Value == 77);
    }

    /// <summary>
    /// A threshold outside the range it can mean anything in is clamped, and the picker follows
    /// the clamp. A picker reading 150% beside a meter ticking at 100% would be showing a
    /// threshold nothing is measured against.
    /// </summary>
    [Fact]
    public async Task ClampsAThresholdAboveTheRange()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.SelectedSessionThreshold = ThresholdOption.For(150);
        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.Equal(AltimSettings.MaximumThresholdPercent, saved.SessionThresholdPercent);
        Assert.Equal(AltimSettings.MaximumThresholdPercent, page.SelectedSessionThreshold.Value);
        Assert.Contains(page.ThresholdOptions, option => option.Value == AltimSettings.MaximumThresholdPercent);
    }

    /// <summary>A threshold of zero would fire the moment a window opened, so it is clamped too.</summary>
    [Fact]
    public async Task ClampsAThresholdBelowTheRange()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.SelectedWeeklyThreshold = ThresholdOption.For(0);
        await page.SaveAsync(TestContext.Current.CancellationToken);

        AltimSettings saved = Assert.IsType<AltimSettings>(store.Saved);
        Assert.Equal(AltimSettings.MinimumThresholdPercent, saved.WeeklyThresholdPercent);
        Assert.Equal(AltimSettings.MinimumThresholdPercent, page.SelectedWeeklyThreshold.Value);
    }

    /// <summary>A clamped threshold ticks the meters at the number that is actually in force.</summary>
    [Fact]
    public async Task AClampedThresholdReachesTheMetersAsClamped()
    {
        var provider = new FakeUsageProvider("claude", "Claude Code");
        using ProviderViewModel row = new(provider, new TestClock(Readings.Now), AltimSettings.Default);
        await row.LoadAsync(TestContext.Current.CancellationToken);

        SettingsViewModel page = Page(new FakeSettingsStore(), new FakeHistoryService(), row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.SelectedSessionThreshold = ThresholdOption.For(140);
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal((double)AltimSettings.MaximumThresholdPercent, row.Metrics[0].Threshold);
        Assert.Equal(AltimSettings.MaximumThresholdPercent, page.SelectedSessionThreshold.Value);
    }

    /// <summary>A stored value outside the range loads as the clamped one, never as itself.</summary>
    [Fact]
    public async Task LoadsAStoredThresholdClamped()
    {
        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with { SessionThresholdPercent = 320, WeeklyThresholdPercent = -5 },
        };

        SettingsViewModel page = Page(store, new FakeHistoryService());
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AltimSettings.MaximumThresholdPercent, page.SelectedSessionThreshold.Value);
        Assert.Equal(AltimSettings.MinimumThresholdPercent, page.SelectedWeeklyThreshold.Value);
    }

    /// <summary>A new threshold reaches the meters without a fresh reading.</summary>
    [Fact]
    public async Task ThresholdReachesTheProviderRows()
    {
        var provider = new FakeUsageProvider("claude", "Claude Code");
        using ProviderViewModel row = new(provider, new TestClock(Readings.Now), AltimSettings.Default);
        await row.LoadAsync(TestContext.Current.CancellationToken);

        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with { SessionThresholdPercent = 50 },
        };

        SettingsViewModel page = Page(store, new FakeHistoryService(), row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(50d, row.Metrics[0].Threshold);
        Assert.True(row.Metrics[0].IsAboveThreshold);
    }

    /// <summary>Clearing history empties the store and says so.</summary>
    [Fact]
    public async Task ClearsHistory()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample("claude", "five_hour", Readings.Now.AddHours(-1), 62d, TimeSpan.FromHours(5)));

        SettingsViewModel page = Page(new FakeSettingsStore(), history);
        int announced = 0;
        page.HistoryCleared += (_, _) => announced++;

        await page.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Equal(1, history.Clears);
        Assert.Equal(1, announced);
        Assert.Equal("Usage history cleared.", page.ClearHistoryResult);
    }

    /// <summary>
    /// A read that failed is reported as a read that failed, and locks the page.
    /// </summary>
    /// <remarks>
    /// It used to set <c>SaveFailed</c>, so the page said "Settings could not be saved" about
    /// an event that had not happened, while every control underneath it went on showing the
    /// constructor's defaults as though they were the user's own values.
    /// </remarks>
    [Fact]
    public async Task AFailedReadIsReportedAsAFailedReadAndLocksThePage()
    {
        var store = new FakeSettingsStore { Failure = new IOException("locked") };
        SettingsViewModel page = Page(store, new FakeHistoryService());

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.LoadFailed);
        Assert.False(page.SaveFailed);
        Assert.False(page.CanEditSettings);
        Assert.Contains("could not be read", page.LoadFailedText, StringComparison.Ordinal);

        // And it says what the controls under it are actually showing, because they are not
        // the user's settings.
        Assert.Contains("defaults", page.LoadFailedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A read that failed cannot write the defaults over the stored settings.</b>
    /// </summary>
    /// <remarks>
    /// This is the most serious thing on this page. The page is built on
    /// <c>AltimSettings.Default</c>, and a failed read left <c>_current</c> there, so the
    /// first change the user made saved Default-plus-that-change: one unreadable read and one
    /// toggle, and a configuration nobody could get back. The store is made readable again
    /// here before the change, so the test is discriminating - a page that writes would find a
    /// store perfectly willing to take it.
    /// </remarks>
    [Fact]
    public async Task AFailedReadCannotWriteDefaultsOverTheStoredSettings()
    {
        AltimSettings theirs = AltimSettings.Default with
        {
            LaunchAtLogin = true,
            NotificationsEnabled = false,
            SessionThresholdPercent = 55,
            WeeklyThresholdPercent = 65,
            AllowNetworkCalls = false,
            RefreshInterval = TimeSpan.FromMinutes(5),
        };

        var store = new FakeSettingsStore { Stored = theirs, Failure = new IOException("locked") };
        SettingsViewModel page = Page(store, new FakeHistoryService());

        await page.LoadAsync(TestContext.Current.CancellationToken);

        // The page is showing defaults, because they are all it has.
        Assert.True(page.LoadFailed);
        Assert.NotEqual(theirs.SessionThresholdPercent, page.SelectedSessionThreshold.Value);

        // The store is fine now. Nothing has re-read it, so the page still knows nothing.
        store.Failure = null;

        page.StartMinimised = !page.StartMinimised;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Null(store.Saved);
        Assert.Equal(0, store.Writes);
        Assert.Equal(theirs, store.Stored);
    }

    /// <summary>And a read that succeeds unlocks it and saves normally.</summary>
    [Fact]
    public async Task AReadThatSucceedsLeavesThePageEditable()
    {
        var store = new FakeSettingsStore();
        SettingsViewModel page = Page(store, new FakeHistoryService());

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.LoadFailed);
        Assert.True(page.CanEditSettings);

        page.LaunchAtLogin = true;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.IsType<AltimSettings>(store.Saved).LaunchAtLogin);
    }

    /// <summary>
    /// A Claude Code settings file that could not be read is not a settings file that says
    /// "off".
    /// </summary>
    /// <remarks>
    /// <c>Failed</c> means the file could not be read or updated and nothing was changed, so
    /// what is true is whatever was last actually observed. It was mapped to "not installed",
    /// which made an unreadable file render as a definite answer; the switch then moved, and
    /// because the switch moving is what records the choice, Altim wrote that guess into its
    /// own store. The switch was left enabled over it as well, so the user could act on a
    /// state nobody knew.
    /// </remarks>
    [Fact]
    public async Task AStatusLineFileThatCannotBeReadIsNotRecordedAsOff()
    {
        var store = new FakeSettingsStore
        {
            Stored = AltimSettings.Default with { ClaudeStatusLineEnabled = true },
        };

        var statusLine = new FakeStatusLineService { Failure = new IOException("locked") };
        SettingsViewModel page = Page(store, statusLine);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusLineInstallState.Failed, page.StatusLineState);

        // What the user asked for, unchanged: nothing has been observed to contradict it.
        Assert.True(page.ClaudeStatusLineEnabled);

        // And nothing can be asked of a file Altim cannot read.
        Assert.False(page.CanChangeStatusLine);
        Assert.True(page.ShowsStatusLineNotice);
        Assert.Contains("Nothing was changed", page.StatusLineNotice!, StringComparison.Ordinal);
        Assert.Empty(statusLine.Changes);
    }

    /// <summary>A store that cannot be written says so rather than pretending it saved.</summary>
    [Fact]
    public async Task SaysWhenAWriteFails()
    {
        var store = new FakeSettingsStore { Failure = new IOException("read only") };
        SettingsViewModel page = Page(store, new FakeHistoryService());

        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(page.SaveFailed);
    }

    /// <summary>The providers section lists the rows it was given.</summary>
    [Fact]
    public void ListsTheProviders()
    {
        using ProviderViewModel row = Row();
        SettingsViewModel page = Page(new FakeSettingsStore(), new FakeHistoryService(), row);

        ProviderViewModel listed = Assert.Single(page.Providers);
        Assert.Equal("Claude Code", listed.DisplayName);
    }

    /// <summary>
    /// The status-line switch is off on a fresh install, and opening the page does not install
    /// anything. This is the one switch that edits a file Altim does not own.
    /// </summary>
    [Fact]
    public async Task TheStatusLineIsOffUntilSomebodyAsksForIt()
    {
        var statusLine = new FakeStatusLineService();
        SettingsViewModel page = Page(new FakeSettingsStore(), statusLine);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(AltimSettings.Default.ClaudeStatusLineEnabled);
        Assert.False(page.ClaudeStatusLineEnabled);
        Assert.True(page.CanChangeStatusLine);
        Assert.False(page.ShowsStatusLineNotice);

        // Looked at, never written to.
        Assert.Equal(1, statusLine.Inspections);
        Assert.Empty(statusLine.Changes);
    }

    /// <summary>Switching it on installs it and records the choice.</summary>
    [Fact]
    public async Task SwitchingTheStatusLineOnInstallsItAndPersistsTheChoice()
    {
        var store = new FakeSettingsStore();
        var statusLine = new FakeStatusLineService();
        SettingsViewModel page = Page(store, statusLine);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        page.ClaudeStatusLineEnabled = true;
        await page.StatusLineChange;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true], statusLine.Changes);
        Assert.True(page.ClaudeStatusLineEnabled);
        Assert.False(page.ShowsStatusLineNotice);
        Assert.True(Assert.IsType<AltimSettings>(store.Saved).ClaudeStatusLineEnabled);
    }

    /// <summary>And switching it off reverts it.</summary>
    [Fact]
    public async Task SwitchingTheStatusLineOffRevertsIt()
    {
        var store = new FakeSettingsStore { Stored = AltimSettings.Default with { ClaudeStatusLineEnabled = true } };
        var statusLine = new FakeStatusLineService { State = StatusLineInstallState.Installed };
        SettingsViewModel page = Page(store, statusLine);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.ClaudeStatusLineEnabled);

        page.ClaudeStatusLineEnabled = false;
        await page.StatusLineChange;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal([false], statusLine.Changes);
        Assert.False(page.ClaudeStatusLineEnabled);
        Assert.False(Assert.IsType<AltimSettings>(store.Saved).ClaudeStatusLineEnabled);
    }

    /// <summary>
    /// A status line the user built themselves is not replaced, and the page says so rather
    /// than leaving a switch that silently refuses to move.
    /// </summary>
    [Fact]
    public async Task AStatusLineOfTheUsersOwnIsReportedAndNeverReplaced()
    {
        var statusLine = new FakeStatusLineService
        {
            State = StatusLineInstallState.AnotherStatusLine,
            RefuseChanges = true,
        };

        SettingsViewModel page = Page(new FakeSettingsStore(), statusLine);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.ClaudeStatusLineEnabled);
        Assert.False(page.CanChangeStatusLine);
        Assert.True(page.ShowsStatusLineNotice);
        Assert.Contains("will not replace", page.StatusLineNotice!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Claude Code not being installed is an ordinary answer, not a failure, and the switch
    /// has nothing to do.
    /// </summary>
    [Fact]
    public async Task NoClaudeCodeConfigurationDisablesTheSwitchWithoutAnError()
    {
        var statusLine = new FakeStatusLineService { State = StatusLineInstallState.NoConfiguration };
        SettingsViewModel page = Page(new FakeSettingsStore(), statusLine);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.CanChangeStatusLine);
        Assert.True(page.ShowsStatusLineNotice);
        Assert.False(page.SaveFailed);
    }

    /// <summary>
    /// A settings file that cannot be written surfaces as a sentence, and leaves the switch
    /// showing what is true rather than what was asked for.
    /// </summary>
    /// <remarks>
    /// The stored flag follows it back down too. A record that says the status line is on,
    /// against a settings file that does not have it, would put the page back into the wrong
    /// state on the next open and never correct itself.
    /// </remarks>
    [Fact]
    public async Task AFailedWriteIsAMessageRatherThanAnExceptionAndTheSwitchTellsTheTruth()
    {
        var store = new FakeSettingsStore();
        var statusLine = new FakeStatusLineService();
        SettingsViewModel page = Page(store, statusLine);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        statusLine.Failure = new UnauthorizedAccessException("settings.json");

        page.ClaudeStatusLineEnabled = true;
        await page.StatusLineChange;
        await page.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(page.ClaudeStatusLineEnabled);
        Assert.True(page.ShowsStatusLineNotice);
        Assert.Contains("Nothing was changed", page.StatusLineNotice!, StringComparison.Ordinal);
        Assert.False(Assert.IsType<AltimSettings>(store.Saved).ClaudeStatusLineEnabled);
    }

    /// <summary>
    /// The stored flag is what the user asked for; Claude Code's settings file is what is
    /// true. Somebody who removed the entry by hand gets a switch that says off, and Altim
    /// does not put it back.
    /// </summary>
    [Fact]
    public async Task TheSwitchFollowsTheSettingsFileRatherThanTheStoredFlag()
    {
        var store = new FakeSettingsStore { Stored = AltimSettings.Default with { ClaudeStatusLineEnabled = true } };
        var statusLine = new FakeStatusLineService { State = StatusLineInstallState.NotInstalled };
        SettingsViewModel page = Page(store, statusLine);

        // Nothing is saved here by hand: correcting the flag is the load's own doing, and the
        // write it queues is not awaited by anything. Every other test on this switch awaits
        // SaveAsync, so it never had to wait; this one must, or it reads Saved before the
        // write lands. It passed on a fast machine and failed on a CI runner.
        store.ExpectWrite();

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await store.Written.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(page.ClaudeStatusLineEnabled);
        Assert.Empty(statusLine.Changes);
        Assert.False(Assert.IsType<AltimSettings>(store.Saved).ClaudeStatusLineEnabled);
    }

    /// <summary>
    /// On screen, a failed read is the sentence and a page nothing can be changed on.
    /// </summary>
    [AvaloniaFact]
    public async Task RendersAFailedReadAsALockedPage()
    {
        var store = new FakeSettingsStore { Failure = new IOException("locked") };
        using ProviderViewModel row = Row();
        SettingsViewModel page = Page(store, new FakeHistoryService(), row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new SettingsView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, page.LoadFailedText));
            Assert.False(Surface.Shows(window, page.SaveFailedText));

            // Nothing that writes a setting can be reached. The About section and the
            // history action are not settings and stay where they are.
            foreach (ToggleSwitch toggle in Surface.Visible<ToggleSwitch>(window))
            {
                Assert.False(toggle.IsEffectivelyEnabled);
            }

            foreach (ComboBox picker in Surface.Visible<ComboBox>(window))
            {
                Assert.False(picker.IsEffectivelyEnabled);
            }
        }, width: 760d, height: 1600d);
    }

    /// <summary>On screen, the five sections are all there.</summary>
    [AvaloniaFact]
    public void RendersEverySection()
    {
        using ProviderViewModel row = Row();
        SettingsViewModel page = Page(new FakeSettingsStore(), new FakeHistoryService(), row);
        var view = new SettingsView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, "General"));
            Assert.True(Surface.Shows(window, "Notifications"));
            Assert.True(Surface.Shows(window, "Providers"));
            Assert.True(Surface.Shows(window, "Privacy"));
            Assert.True(Surface.Shows(window, "About"));
            Assert.True(Surface.Shows(window, "Clear usage history"));
            Assert.True(Surface.Shows(window, "altim.dev"));
            Assert.True(Surface.Shows(window, "Claude Code"));
            Assert.True(Surface.Shows(window, "Add Altim's status line to Claude Code"));
        }, width: 760d, height: 1600d);
    }
}
