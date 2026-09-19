using System.Diagnostics;
using System.Globalization;
using Altim.App.Composition;
using Altim.App.Diagnostics;
using Altim.App.Monitoring;
using Altim.App.Services;
using Altim.App.Tray;
using Altim.App.Views;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Monitoring;
using Altim.Core.Settings;
using Altim.Storage;
using Altim.UI.Accessibility;
using Altim.UI.ViewModels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Altim.App;

/// <summary>
/// The composition root proper: what exists, in what order, and what happens when it does
/// not.
/// </summary>
/// <remarks>
/// <para>
/// Start-up runs in two phases, and the split is the whole reason the budget is met. The
/// first phase is synchronous and does only what the tray icon needs: a log file, the native
/// stack, the icon. The second phase opens the database, reads the settings, builds the
/// container, constructs the panel and starts monitoring, and it runs off the dispatcher so
/// none of it stands between the process starting and the icon appearing.
/// </para>
/// <para>
/// Everything in the second phase is allowed to fail. A provider that is not installed, a
/// notification platform that refused registration, a tray that could not be created and a
/// database that could not be opened each degrade to a recorded condition — visible in the
/// tray menu, and in the popup for anything provider-shaped — and the rest carries on.
/// </para>
/// </remarks>
internal sealed class AltimRuntime : IAsyncDisposable
{
    /// <summary>How often the housekeeping pass runs.</summary>
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a teardown that cannot wait is given before the process gives up on it.
    /// </summary>
    /// <remarks>
    /// The session-end path and the unhandled-exception path both run on the dispatcher
    /// thread with nothing behind them, so they block on the teardown rather than handing it
    /// to a worker that will never be scheduled. Bounded, because a shutdown that hangs is
    /// what the operating system kills the process over.
    /// </remarks>
    private static readonly TimeSpan SynchronousShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the database must have gone unwritten before the write-ahead log is
    /// emptied. Comfortably longer than the tightest refresh cadence, so a checkpoint never
    /// lands in the middle of a burst of samples.
    /// </summary>
    private static readonly TimeSpan WalCheckpointIdleWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often the reset countdowns on screen are re-measured against the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only while a window is on screen.</b> A reading is announced when its value moves,
    /// which is what a figure needs and not what a countdown needs: the time left in a
    /// window runs out whether or not anything is read, so on a quiet machine every "Resets
    /// in" on the panel stood still. Measured on the running application, the panel held
    /// "Resets in 2h 59m" for eight and a half minutes while the true figure reached 2h 51m.
    /// </para>
    /// <para>
    /// Fifteen seconds, from the granularity the text is written to: the countdowns read to
    /// the minute, so a quarter of a minute bounds how far behind one can be without asking
    /// for a wake per second. It is not the scheduler's tightened interval, which the user
    /// owns and can set to minutes - how current a clock reads is not a polling preference.
    /// A tick is one string per metric and a comparison, and the sections are rebuilt only
    /// when the words actually moved; the panel's own foreground watch already runs sixty
    /// times as often over the same interval. Nothing runs while nothing is on screen, which
    /// is the state the process is in almost all of the time and the one the idle budget is
    /// measured in.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CountdownInterval = TimeSpan.FromSeconds(15);

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly StartupReport _report = new();
    private readonly SchedulerNetworkGate _networkGate = new();
    private readonly LiveNetworkPolicy _networkPolicy = new();
    private readonly SchedulerHandle _schedulers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DateTimeOffset _processStarted;

    private SingleInstance? _instance;
    private PlatformStack? _platform;
    private StorageStack? _storage;
    private SettingsGateway? _settings;
    private ServiceProvider? _services;
    private ProviderHintWatcher? _hints;
    private UsagePipeline? _pipeline;
    private TrayController? _tray;
    private PopupHost? _popup;
    private DashboardHost? _dashboard;
    private DispatcherTimer? _countdowns;
    private IReadOnlyList<IUsageProvider> _providers = [];

    /// <summary>
    /// The local day the last rollup of this run treated as today, or null when none has
    /// run yet. Only ever touched from the maintenance loop, which is one task.
    /// </summary>
    private DateOnly? _lastRolledUpDay;

    private AltimSettings _current = AltimSettings.Default;

    /// <summary>
    /// Whether a window is on screen, as anything off the dispatcher thread may read it.
    /// </summary>
    /// <remarks>
    /// <b>Asking the window directly is a thread violation, and it was one.</b>
    /// <c>Window.IsVisible</c> is an Avalonia property and reading it off the dispatcher
    /// thread throws — and the one caller that does so is the scheduler rebuild, which runs
    /// on a worker. So changing the refresh interval threw before the replacement was
    /// started, the old scheduler had already been let go, and monitoring stopped for the
    /// rest of the session with one line in the tray menu to say so. Reproduced by changing
    /// Refresh from 1 minute to 5 in the running application. The flag is written on the
    /// dispatcher thread, where the answer is legal to ask for, and read anywhere.
    /// </remarks>
    private volatile bool _windowOnScreen;
    private Altim.Core.Models.PixelRect? _pendingAnchor;
    private bool _activationPending;
    private int _shuttingDown;
    private int _disposed;

    /// <summary>Creates the runtime over the desktop lifetime.</summary>
    /// <param name="lifetime">The classic desktop lifetime, already on explicit shutdown.</param>
    public AltimRuntime(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        _lifetime = lifetime;
        _processStarted = ReadProcessStart();
    }

    /// <summary>
    /// Brings the application up. Returns as soon as the tray icon has been asked for;
    /// everything else continues on a worker.
    /// </summary>
    /// <param name="instance">The single-instance guard, or null when one could not be taken.</param>
    public void Start(SingleInstance? instance)
    {
        _instance = instance;
        if (instance is not null)
        {
            instance.SurfaceRequested += OnSurfaceRequested;
        }

        AltimLog.Initialize(AltimDatabase.GetDefaultDirectory());
        AltimLog.Write("startup", "Altim starting.");

        _lifetime.ShutdownRequested += OnShutdownRequested;
        _lifetime.Exit += OnExit;

        // Subscribed before the stack is built, because building it is what finds most of
        // the conditions. A condition found later — the Linux notification service learning
        // on its first message that nothing is listening — reaches the menu the same way.
        _report.Changed += OnReportChanged;

        _platform = PlatformStack.Create(_report);

        if (_platform.Platform is { } platform)
        {
            _tray = new TrayController(platform.Tray, _report);
            _tray.Activated += OnTrayActivated;
            _tray.MenuInvoked += OnMenuInvoked;
            platform.SystemSuspending += OnSystemSuspending;
            platform.SystemResumed += OnSystemResumed;
            platform.ThemeChanged += OnPlatformThemeChanged;

            _ = ShowTrayAsync();
        }

        // Before any view exists, so the first meter the panel builds already knows whether
        // it is allowed to move. The reading is cached in the platform service; this only
        // copies it across, and the subscription keeps it in step for the rest of the run.
        // Read back from the interface rather than from the platform service, for the reason
        // OnMotionPreferenceChanged gives: no test project binds to the composition root, so
        // this line is the only evidence the hand-off happened, and a line reporting what the
        // platform said would print the right sentence even if nothing had been told.
        Motion.Set(_platform.Motion.Current);
        AltimLog.Write("motion", "Reduced motion at start-up: " + Describe(Motion.Preference));
        _platform.Motion.Changed += OnMotionPreferenceChanged;

        // A sensible variant before the settings are known, so that if anything at all is
        // constructed early it is not built against the wrong palette. The loaded
        // preference is applied again before the panel is created.
        ApplyTheme(AltimSettings.Default.Theme);

        _ = InitializeAsync();
    }

    /// <summary>
    /// Tears everything down in the reverse order it was built: watchers, then the
    /// scheduler, then the pipeline, then the windows, then the tray, then storage.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AltimLog.Write("shutdown", "Stopping.");

        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled.
        }

        // Watchers first: a hint delivered to a scheduler that is stopping is documented as
        // harmless, but there is no reason to keep producing them.
        _hints?.Dispose();

        MonitorScheduler? scheduler = _schedulers.Current;
        _schedulers.Bind(null);

        if (scheduler is not null)
        {
            scheduler.UsageUpdated -= OnUsageUpdated;
            scheduler.ProviderFailed -= OnProviderFailed;

            try
            {
                await scheduler.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AltimLog.Write("shutdown", "Stopping the scheduler failed", ex);
            }
        }

        if (_pipeline is not null)
        {
            _pipeline.ReadingsChanged -= OnReadingsChanged;
            _pipeline.Dispose();
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        await DisposeWindowsAsync().ConfigureAwait(false);

        if (_tray is not null)
        {
            _tray.Activated -= OnTrayActivated;
            _tray.MenuInvoked -= OnMenuInvoked;
            _tray.Dispose();
        }

        if (_platform?.Platform is { } platform)
        {
            platform.SystemSuspending -= OnSystemSuspending;
            platform.SystemResumed -= OnSystemResumed;
            platform.ThemeChanged -= OnPlatformThemeChanged;
        }

        if (_platform is not null)
        {
            _platform.Motion.Changed -= OnMotionPreferenceChanged;
        }

        // The container owns the providers and the scheduler it created, and both are
        // idempotent about a second dispose.
        if (_services is not null)
        {
            try
            {
                await _services.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AltimLog.Write("shutdown", "Disposing the container failed", ex);
            }
        }

        // The tray host goes with the platform service, and it removes its icon while its
        // own window is still alive, which is what stops a ghost icon being left behind.
        _platform?.Dispose();
        _storage?.Dispose();

        if (_instance is not null)
        {
            _instance.SurfaceRequested -= OnSurfaceRequested;
            _instance.Dispose();
        }

        _lifetime.ShutdownRequested -= OnShutdownRequested;
        _lifetime.Exit -= OnExit;
        _report.Changed -= OnReportChanged;

        // The token source is cancelled but deliberately not disposed. Work that was in
        // flight when shutdown started still holds its token, and registering a
        // continuation on a token whose source has been disposed throws — which, on a
        // thread-pool thread during shutdown, is an unhandled exception rather than a
        // tidy-up. The source dies with the process a moment later.
        AltimLog.Write("shutdown", "Stopped.");
    }

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// What a reduce-motion reading says, in a sentence for the log.
    /// </summary>
    /// <param name="preference">The reading.</param>
    /// <remarks>
    /// The unknown names what Altim does about it, because "unknown" alone reads like a
    /// defect and this one is a supported state with a deliberate consequence.
    /// </remarks>
    private static string Describe(MotionPreference preference) => preference switch
    {
        MotionPreference.Reduced => "this machine asks for reduced motion",
        MotionPreference.Full => "this machine does not ask for reduced motion",
        _ => "this machine could not be asked, so nothing animates",
    };

    private static ThemeVariant VariantFor(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => OperatingSystemVariant(),
    };

    private static ThemeVariant OperatingSystemVariant()
    {
        // Resolved explicitly rather than by leaving the variant on Default, so that the
        // "follow the system" setting is a value this process can read back and act on
        // when the platform service reports the preference changing.
        PlatformThemeVariant? os = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return os == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private TimeSpan SinceProcessStart() => DateTimeOffset.UtcNow - _processStarted;

    private async Task ShowTrayAsync()
    {
        if (_tray is null)
        {
            return;
        }

        try
        {
            await _tray.ShowAsync(_shutdown.Token).ConfigureAwait(false);
            AltimLog.Timing("cold start to tray icon", SinceProcessStart());

            if (!_tray.IsVisible)
            {
                // Recorded by the controller, which also withdraws it again if the icon
                // turns up later — Explorer restarting is the ordinary way that happens.
                AltimLog.Write("tray", "Shell_NotifyIcon did not accept the icon.");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the icon went up.
        }
        catch (Exception ex)
        {
            _report.Add("Tray icon unavailable; Altim is running without one");
            AltimLog.Write("tray", "Showing the tray icon failed", ex);
        }
    }

    private async Task InitializeAsync()
    {
        CancellationToken ct = _shutdown.Token;

        try
        {
            _storage = await StorageStack.OpenAsync(_report, ct).ConfigureAwait(false);

            _settings = new SettingsGateway(_storage.Settings);
            AltimSettings loaded = await _settings.GetAsync(ct).ConfigureAwait(false);
            _current = loaded;
            _networkPolicy.Set(loaded.AllowNetworkCalls);

            _services = ServiceRegistration.Build(
                _report, _platform!, _storage, _settings, loaded, _networkGate, _networkPolicy);
            _providers = [.. _services.GetServices<IUsageProvider>()];

            var scheduler = _services.GetRequiredService<MonitorScheduler>();
            _networkGate.Bind(scheduler.NetworkGate);
            scheduler.UsageUpdated += OnUsageUpdated;
            scheduler.ProviderFailed += OnProviderFailed;
            _schedulers.Bind(scheduler);

            _pipeline = new UsagePipeline(
                _storage.History,
                _storage.NotificationState,
                _platform!.Notifications,
                TimeProvider.System,
                () => _current,
                _report);

            await _pipeline.InitializeAsync(ct).ConfigureAwait(false);
            _pipeline.ReadingsChanged += OnReadingsChanged;

            _settings.Changed += OnSettingsChanged;

            bool firstRun = await IsFirstRunAsync(ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => BuildWindows(loaded, firstRun)).GetTask()
                .ConfigureAwait(false);

            scheduler.Start();

            // Filesystem hints are the event-driven half of monitoring; the 60-second floor
            // covers anything they miss, which is why failing to arm one is not fatal. They
            // are delivered through the handle rather than to this instance, because
            // changing the refresh interval replaces it.
            _hints = ProviderHintWatcher.Create(_schedulers, BuildProviderIdSet());

            await ApplyAutoStartAsync(loaded).ConfigureAwait(false);

            if (_tray is not null)
            {
                await _tray.RefreshMenuAsync(ct).ConfigureAwait(false);
            }

            AltimLog.Timing("start-up complete", SinceProcessStart());

            // Deliberately not awaited. The first reading of a provider is its most
            // expensive one — a store measured at 28.3GB has to be walked once before the
            // incremental cursor has anywhere to start from — and nothing above depends on
            // it. Start-up finishes; the numbers arrive when they arrive.
            _ = Task.Run(() => FirstReadingsAsync(ct), CancellationToken.None);
            _ = Task.Run(() => MaintainAsync(ct), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Shutdown arrived mid-start.
        }
        catch (Exception ex)
        {
            _report.Add("Altim started with reduced function; see the log");
            AltimLog.Write("startup", "Initialisation failed", ex);
        }
    }

    private HashSet<string> BuildProviderIdSet()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (IUsageProvider provider in _providers)
        {
            _ = ids.Add(provider.Id);
        }

        return ids;
    }

    /// <param name="settings">The settings the windows are built against.</param>
    /// <param name="firstRun">True when Altim has never shown itself to this user.</param>
    private void BuildWindows(AltimSettings settings, bool firstRun)
    {
        // The theme is settled before the first view exists, so nothing is built against
        // one palette and restyled into another.
        ApplyTheme(settings.Theme);

        var popup = new PopupViewModel(_providers, TimeProvider.System, settings);
        popup.OpenRequested += OnPopupOpenRequested;

        _popup = new PopupHost(popup);
        _popup.Opened += OnWindowVisibilityChanged;
        _popup.Closed += OnWindowVisibilityChanged;
        _popup.Prime();

        _dashboard = new DashboardHost(
            _providers,
            _storage!.History,
            _settings!,
            new ClaudeStatusLineService(),
            TimeProvider.System);
        _dashboard.Opened += OnWindowVisibilityChanged;
        _dashboard.Closed += OnWindowVisibilityChanged;

        // Fills the panel with the current readings while it is still hidden, which is what
        // lets the first open be a position and a show rather than a read.
        _ = popup.LoadAsync(CancellationToken.None);

        if (_activationPending)
        {
            _activationPending = false;
            _popup.Open(_pendingAnchor);
            return;
        }

        // The one thing "Start minimised" can mean for a process whose main surface is a
        // tray icon: off, launching opens the dashboard as well. It was persisted and shown
        // in Settings and read by nothing at all, which is a control that lies.
        //
        // A first run opens it whatever that setting says, because the setting defaults to
        // on and a user who has never seen Altim cannot have chosen it. Without this the
        // installer finishes and nothing visible happens: the process has no main window by
        // design, and Windows puts a tray icon nobody has seen before into the overflow, so
        // the whole application is behind a chevron the user has no reason to click.
        if (StartupWindows.ShouldOpenDashboard(firstRun, settings.StartMinimised))
        {
            AltimLog.Write(
                "startup",
                firstRun ? "First run; opening the dashboard."
                         : "Start minimised is off; opening the dashboard.");
            _dashboard.Open();
        }
    }

    /// <summary>
    /// Closes both windows on the dispatcher thread, inline when the caller is already on
    /// it.
    /// </summary>
    /// <remarks>
    /// The inline case is not an optimisation. The session-end and unhandled-exception paths
    /// run on the dispatcher thread and block it waiting for this, so posting the work back
    /// to that thread would be waiting for a frame that cannot run until the wait is over.
    /// </remarks>
    private async ValueTask DisposeWindowsAsync()
    {
        if (_popup is null && _dashboard is null)
        {
            return;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                DisposeWindows();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(DisposeWindows);
        }
        catch (Exception ex)
        {
            AltimLog.Write("shutdown", "Closing the windows failed", ex);
        }
    }

    private void DisposeWindows()
    {
        // Before the view models it ticks go away: a DispatcherTimer holds its handler for
        // as long as it is running, and the handler reaches into both of them.
        _countdowns?.Stop();
        _countdowns = null;

        if (_popup is not null)
        {
            _popup.Opened -= OnWindowVisibilityChanged;
            _popup.Closed -= OnWindowVisibilityChanged;
            _popup.ViewModel.OpenRequested -= OnPopupOpenRequested;
            _popup.Dispose();
            _popup = null;
        }

        if (_dashboard is not null)
        {
            _dashboard.Opened -= OnWindowVisibilityChanged;
            _dashboard.Closed -= OnWindowVisibilityChanged;
            _dashboard.Dispose();
            _dashboard = null;
        }
    }

    private void ApplyTheme(ThemePreference preference)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = VariantFor(preference);
        }
    }

    /// <summary>
    /// Takes the first reading of every provider, which is the expensive one.
    /// </summary>
    /// <param name="ct">Cancelled at shutdown.</param>
    /// <remarks>
    /// Through the handle rather than through a captured instance, because this is the pass
    /// a settings change is most likely to land in the middle of: it walks stores measured in
    /// tens of gigabytes, and a scheduler replaced underneath it would answer with nothing
    /// and report nothing. The handle notices the rebuild and takes the reading again.
    /// </remarks>
    private async Task FirstReadingsAsync(CancellationToken ct)
    {
        try
        {
            await _schedulers.RefreshAllAsync(ct).ConfigureAwait(false);
            AltimLog.Timing("first readings", SinceProcessStart());
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // RefreshAllAsync is documented never to fault; this is belt and braces.
            AltimLog.Write("monitor", "The first refresh failed", ex);
        }
    }

    /// <summary>
    /// The housekeeping loop: down-sampling, compaction, rolling days up, backfilling them
    /// from the providers, and emptying the write-ahead log.
    /// </summary>
    /// <param name="ct">Cancelled at shutdown.</param>
    /// <remarks>
    /// <para>
    /// Periodic rather than once at start-up. Altim is a tray utility that is expected to
    /// run for weeks, and a pass that only ran at launch meant a machine that is never
    /// rebooted never down-sampled and never compacted — both of which are stated
    /// behaviours with intervals measured in days.
    /// </para>
    /// <para>
    /// The cadence is chosen for the log rather than for the retention work. Down-sampling
    /// is a no-op until rows are 30 days old and compaction runs monthly, so the interval
    /// only has to be short enough that an Altim that has gone quiet leaves a tidy log
    /// behind it within a few minutes.
    /// </para>
    /// <para>
    /// This does not hold a timer of its own: it is the only periodic work outside the
    /// scheduler, and a <see cref="PeriodicTimer"/> that is awaited is one wake source
    /// rather than a callback that can overlap itself.
    /// </para>
    /// </remarks>
    private async Task MaintainAsync(CancellationToken ct)
    {
        await MaintainOnceAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(MaintenanceInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await MaintainOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task MaintainOnceAsync(CancellationToken ct)
    {
        StorageStack? storage = _storage;
        if (storage is null)
        {
            return;
        }

        try
        {
            if (storage.Retention is { } retention)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;

                RetentionResult result = await retention.DownsampleAsync(now, ct).ConfigureAwait(false);
                if (!result.ChangedNothing)
                {
                    AltimLog.Write(
                        "storage",
                        "Down-sampled " + result.CollapsedRows.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " rows into " + result.RetainedRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                }

                if (await retention.VacuumIfDueAsync(now, UsageRetention.DefaultVacuumInterval, ct).ConfigureAwait(false))
                {
                    AltimLog.Write("storage", "Compacted the database.");
                }
            }

            // The usage map's two writers, both folded in here rather than given a timer.
            // Altim is a tray utility with a measured idle cost and a single wake source
            // outside the scheduler; a second one would be a regression the README would
            // have to document, and the map is not urgent work.
            await RollUpDaysAsync(storage, ct).ConfigureAwait(false);
            await BackfillDaysAsync(storage, ct).ConfigureAwait(false);

            if (storage.Database is { } database)
            {
                // Last, and only when nothing has written for a while: the log is bounded
                // either way, and this is what takes an idle Altim's to nothing.
                WalCheckpoint checkpoint = await database
                    .CheckpointIfIdleAsync(WalCheckpointIdleWindow, ct)
                    .ConfigureAwait(false);

                if (checkpoint == WalCheckpoint.Truncated)
                {
                    AltimLog.Write("storage", "Emptied the write-ahead log.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Housekeeping. Failing it costs disk, never a reading.
            AltimLog.Write("storage", "Maintenance pass failed", ex);
        }
    }

    /// <summary>
    /// Turns the samples Altim has taken into one observed row per provider per local day,
    /// each carrying that day's peak percentage and no token figure.
    /// </summary>
    /// <param name="storage">The storage stack, which may have no database behind it.</param>
    /// <param name="ct">Cancelled at shutdown.</param>
    /// <remarks>
    /// <para>
    /// The range is <see cref="HistoryBackfill.RollUpFrom"/>'s: yesterday and today on an
    /// ordinary pass, back to the last day this run covered after a machine has been asleep,
    /// and back to the bound on the first pass of the process. Rolling a day up again is
    /// idempotent and can only raise a peak, so re-reading yesterday all day costs a small
    /// read and changes nothing — and a row nothing in the write would move is not rewritten
    /// at all, which is what keeps the write-ahead log emptiable on a machine left switched
    /// on.
    /// </para>
    /// <para>
    /// It cannot disturb a backfilled day's tokens, because it never carries any: a sample's
    /// token totals are a running total rather than a per-day amount. The order this and the
    /// backfill run in therefore does not matter, and neither can undo the other.
    /// </para>
    /// <para>
    /// Called through <see cref="IUsageHistoryService"/> rather than through the SQLite
    /// class, so the machine whose database would not open reaches a no-op that returns zero
    /// instead of a type test at this call site that would skip the whole thing silently.
    /// </para>
    /// <para>
    /// A failure leaves the last-rolled-up day alone, so the next pass covers the same range
    /// again, and leaves the days it could not write unknown rather than zero.
    /// </para>
    /// </remarks>
    private async ValueTask RollUpDaysAsync(StorageStack storage, CancellationToken ct)
    {
        bool first = _lastRolledUpDay is null;

        // The user's local calendar day, which is what a row is filed under. Resolved once,
        // so a pass that straddles midnight does not work from two different todays.
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = HistoryBackfill.RollUpFrom(today, _lastRolledUpDay);

        try
        {
            int days = await storage.History.RollUpDaysAsync(from, today, ct).ConfigureAwait(false);

            _lastRolledUpDay = today;

            if (first && days > 0)
            {
                // Once per run, for the catch-up pass only. Logging the routine pass would
                // write a line every five minutes for as long as the machine is switched on.
                AltimLog.Write(
                    "storage",
                    "Rolled up " + days.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " days of usage history.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type and nothing else. This runs against the user's own history file, and
            // an IOException from it names the path, which altim.log may not carry.
            AltimLog.Write("storage", "Rolling usage days up failed", ex);
        }
    }

    /// <summary>
    /// Asks each provider that can reach into the past for the days Altim was not watching.
    /// </summary>
    /// <param name="storage">The storage stack.</param>
    /// <param name="ct">Cancelled at shutdown.</param>
    /// <remarks>
    /// <para>
    /// At most daily per provider, and the stamp that says so is per provider in the
    /// <c>setting</c> table beside the compaction stamp: a provider whose source is
    /// unavailable must not hold back one whose source answers. Whether it is due at all is
    /// <see cref="HistoryBackfill.ShouldRun"/>'s decision, not this method's.
    /// </para>
    /// <para>
    /// <b>Only an answer counts as a run.</b> Both sources return empty rather than throwing
    /// when they cannot reach what they read — no permission, no CLI, a refresh gate that is
    /// closed because the live reading has just taken it — and stamping that would record a
    /// skip as a run. The Codex gate in particular opens once a minute and is usually spent
    /// by the scheduler, so a pass that stamped every attempt would mark the backfill done
    /// for the day without ever having reached the app-server once. An empty answer
    /// therefore leaves the stamp alone and leaves those days unknown, which is the correct
    /// square, and the next pass asks again.
    /// </para>
    /// <para>
    /// A throw <em>is</em> stamped. Neither provider is meant to throw at all, so one that
    /// does is broken rather than unavailable, and retrying a broken source every five
    /// minutes would do nothing but fill the log.
    /// </para>
    /// <para>
    /// Skipped entirely when there is no database: there would be nowhere to put the days,
    /// and asking a provider for history in order to drop it is work a user pays for and
    /// never sees.
    /// </para>
    /// </remarks>
    private async ValueTask BackfillDaysAsync(StorageStack storage, CancellationToken ct)
    {
        if (storage.Scalars is not { } scalars)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly from = HistoryBackfill.EarliestDay(today);

        foreach (IUsageProvider provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            // A provider that cannot reach into the past simply does not implement the
            // interface, and the days it cannot account for stay unknown. Its stamp is not
            // even read: there is nothing for one to be about.
            IUsageHistorySource? source = provider as IUsageHistorySource;
            DateTimeOffset? lastRun = source is null
                ? null
                : HistoryBackfill.ParseLastRun(
                    await ReadScalarAsync(scalars, HistoryBackfill.LastRunKey(provider.Id), ct)
                        .ConfigureAwait(false));

            if (!HistoryBackfill.ShouldRun(now, lastRun, source is not null))
            {
                continue;
            }

            await BackfillProviderAsync(scalars, storage.History, provider.Id, source!, from,
                                        today, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Asks one provider for its history and stores whatever it hands back.</summary>
    /// <param name="scalars">Where the last-run stamp is kept.</param>
    /// <param name="history">Where the days are written.</param>
    /// <param name="providerId">The provider being asked.</param>
    /// <param name="source">That provider's history source.</param>
    /// <param name="from">First day to ask for, inclusive.</param>
    /// <param name="to">Last day to ask for, inclusive.</param>
    /// <param name="now">The instant to stamp a run with.</param>
    /// <param name="ct">Cancelled at shutdown.</param>
    private async ValueTask BackfillProviderAsync(
        SqliteSettingsStore scalars, IUsageHistoryService history, string providerId,
        IUsageHistorySource source, DateOnly from, DateOnly to, DateTimeOffset now,
        CancellationToken ct)
    {
        bool ran;

        try
        {
            IReadOnlyList<UsageDay> days = await source.GetHistoryAsync(from, to, ct)
                .ConfigureAwait(false);

            // Merging is the upsert's own single statement, never a decision repeated here.
            // These days carry the token figures — a backfill is the only source of one —
            // and a day's peak, which they know nothing about, is left exactly as the
            // rollup measured it.
            await history.UpsertDaysAsync(days, ct).ConfigureAwait(false);

            ran = days.Count > 0;

            if (ran)
            {
                AltimLog.Write(
                    "history",
                    "Backfilled " + days.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " days from " + providerId + ".");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type and nothing else, which is why this does not use the exception's
            // message: a provider store holds project names and file paths, and every
            // IOException the framework raises from inside one names the file it failed on.
            // The days it would have filled stay unknown, which is the honest square.
            AltimLog.Write("history", "Backfilling " + providerId + " failed", ex);
            ran = true;
        }

        if (!ran)
        {
            return;
        }

        try
        {
            await scalars.SetValueAsync(HistoryBackfill.LastRunKey(providerId),
                                        HistoryBackfill.FormatLastRun(now), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Costs one extra ask on the next pass and nothing else.
            AltimLog.Write("history", "Recording the backfill time for " + providerId + " failed", ex);
        }
    }

    /// <summary>
    /// Reads one maintenance scalar, treating an unreadable settings table as an absent key.
    /// </summary>
    /// <param name="scalars">The settings table.</param>
    /// <param name="key">The key to read.</param>
    /// <param name="ct">Cancelled at shutdown.</param>
    /// <returns>The stored text, or null when there is none or it could not be read.</returns>
    /// <remarks>
    /// An absent key means "never run", so a settings table that cannot be read costs one
    /// extra backfill rather than none at all. Not being able to remember when something
    /// last happened is no reason to stop doing it.
    /// </remarks>
    /// <summary>
    /// Whether Altim has ever shown itself to this user, and records that it has.
    /// </summary>
    /// <param name="ct">Cancels the read and the write.</param>
    /// <returns>True the first time this is asked on a machine, false every time after.</returns>
    /// <remarks>
    /// <para>
    /// The mark is written here rather than after the window opens, so a crash between the
    /// two does not greet the user again on every start. Showing the dashboard once too few
    /// costs somebody one visit to the tray; showing it once per launch for ever is the
    /// behaviour people uninstall over.
    /// </para>
    /// <para>
    /// A machine with no database gets false. There is nowhere to record the answer, so
    /// every launch would otherwise be a first run, and opening the dashboard on each of
    /// them is exactly the failure above. The tray message already says storage is
    /// unavailable.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> IsFirstRunAsync(CancellationToken ct)
    {
        if (_storage?.Scalars is not { } scalars)
        {
            return false;
        }

        if (await ReadScalarAsync(scalars, StartupWindows.FirstRunKey, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }

        try
        {
            await scalars.SetValueAsync(
                StartupWindows.FirstRunKey,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The mark could not be written, so the next launch will greet the user again.
            // That is better than not greeting them at all, and it is worth a line.
            AltimLog.Write("startup", "The first-run mark could not be written", ex);
        }

        return true;
    }

    private static async ValueTask<string?> ReadScalarAsync(SqliteSettingsStore scalars, string key,
                                                            CancellationToken ct)
    {
        try
        {
            return await scalars.GetValueAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The key is one of Altim's own constants, so naming it carries nothing of the
            // user's.
            AltimLog.Write("history", "Reading " + key + " failed", ex);
            return null;
        }
    }

    private async ValueTask ApplyAutoStartAsync(AltimSettings settings)
    {
        if (_platform is null)
        {
            return;
        }

        try
        {
            bool enabled = await _platform.AutoStart.IsEnabledAsync().ConfigureAwait(false);
            if (enabled == settings.LaunchAtLogin)
            {
                return;
            }

            await _platform.AutoStart.SetAsync(settings.LaunchAtLogin).ConfigureAwait(false);

            // Read back rather than assume: the contract says a request cannot override an
            // operating system level disable, and Task Manager's startup tab is one.
            bool applied = await _platform.AutoStart.IsEnabledAsync().ConfigureAwait(false);
            if (applied != settings.LaunchAtLogin)
            {
                _report.Add("Start with Windows is switched off by the system");
                AltimLog.Write("autostart", "The registration did not take; StartupApproved reports otherwise.");
            }
        }
        catch (Exception ex)
        {
            AltimLog.Write("autostart", "Applying the start-up registration failed", ex);
        }
    }

    /// <summary>
    /// Replaces the scheduler when a cadence setting changes, which is the only way to change
    /// one: its intervals are fixed at construction.
    /// </summary>
    /// <param name="settings">The settings as they now stand.</param>
    /// <remarks>
    /// <para>
    /// The hand-over order is the whole of this method's difficulty. The replacement is
    /// running before anything is pointed at it, the handle is re-pointed next so hints and
    /// passes reach the live instance from that moment, and only then is the old one
    /// disposed — so nothing is ever holding a scheduler that has stopped.
    /// </para>
    /// <para>
    /// It used to be the other way round, and two things captured the instance rather than
    /// the handle: the filesystem watchers and the first-readings pass. Changing Refresh
    /// therefore sent every later hint to a disposed scheduler, which drops them without
    /// complaint, and the meters fell back to the polling floor for the rest of the session.
    /// </para>
    /// </remarks>
    private async Task RebuildSchedulerAsync(AltimSettings settings)
    {
        MonitorScheduler? previous = _schedulers.Current;
        if (previous is null)
        {
            return;
        }

        MonitorSchedulerOptions wanted = MonitorSchedulerOptions.FromSettings(settings);
        if (wanted == previous.Options)
        {
            return;
        }

        try
        {
            var replacement = new MonitorScheduler(_providers, TimeProvider.System, wanted);
            replacement.UsageUpdated += OnUsageUpdated;
            replacement.ProviderFailed += OnProviderFailed;

            // The providers are holding the forwarding gate, so re-pointing it is the whole
            // of the hand-over; nothing has to be rebuilt to follow the new floor.
            _networkGate.Bind(replacement.NetworkGate);
            replacement.SetUiVisible(_windowOnScreen);
            replacement.Start();

            // Live before the old one stops, so a hint that arrives during the hand-over has
            // somewhere to go.
            _schedulers.Bind(replacement);

            previous.UsageUpdated -= OnUsageUpdated;
            previous.ProviderFailed -= OnProviderFailed;
            await previous.DisposeAsync().ConfigureAwait(false);

            AltimLog.Write("monitor", "Rebuilt the scheduler for a new cadence; hints follow it.");
        }
        catch (Exception ex)
        {
            AltimLog.Write("monitor", "Rebuilding the scheduler failed; monitoring has stopped", ex);
            _report.Add("Monitoring stopped; restart Altim");
        }
    }

    /// <summary>
    /// Whether a window is on screen. Dispatcher thread only; everything else reads
    /// <see cref="_windowOnScreen"/>.
    /// </summary>
    private bool IsAnyWindowOpen() => _popup?.IsOpen == true || _dashboard?.IsOpen == true;

    /// <summary>
    /// Raised on the dispatcher thread when either window is shown or hidden, which is the
    /// only place the question may be asked.
    /// </summary>
    private void OnWindowVisibilityChanged(object? sender, EventArgs e)
    {
        bool onScreen = IsAnyWindowOpen();
        _windowOnScreen = onScreen;
        _schedulers.Current?.SetUiVisible(onScreen);
        WatchCountdowns(onScreen);
    }

    /// <summary>
    /// Runs the countdown tick while something is on screen and not otherwise.
    /// </summary>
    /// <param name="onScreen">Whether either window is showing.</param>
    /// <remarks>
    /// The first tick is immediate rather than an interval away. The panel is built once and
    /// hidden rather than closed, so what it is carrying when it opens was measured whenever
    /// the last reading landed - which on a quiet machine can be hours ago. Opening it is
    /// exactly the moment the figures are read.
    /// </remarks>
    private void WatchCountdowns(bool onScreen)
    {
        if (!onScreen)
        {
            _countdowns?.Stop();
            return;
        }

        TickCountdowns();

        _countdowns ??= new DispatcherTimer(
            CountdownInterval, DispatcherPriority.Background, (_, _) => TickCountdowns());

        _countdowns.Start();
    }

    /// <summary>Re-measures the reset countdowns on whichever surfaces are up.</summary>
    private void TickCountdowns()
    {
        _popup?.ViewModel.RefreshCountdowns();
        _dashboard?.ViewModel?.RefreshCountdowns();
    }

    /// <summary>
    /// The panel asks for the window, and says which section it wants: the gear asks for
    /// Settings and a provider's own row asks for that provider, while the action at the
    /// foot of the panel asks for wherever the window opens by default.
    /// </summary>
    private void OnPopupOpenRequested(object? sender, string? section)
    {
        _popup?.Close("opened the dashboard");
        _dashboard?.Open(section);
    }

    private void OnTrayActivated(object? sender, TrayClickEventArgs e)
    {
        Altim.Core.Models.PixelRect? anchor = e.Anchor;

        // One press, one activation. The shell's duplicate callback is collapsed inside
        // WindowsTrayHost, where it belongs: it is a property of that host's message
        // contract, not of this application, and de-duplicating here left every other
        // consumer of ITrayHost to discover it for itself.

        // Raised on the tray host's own message loop thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (_popup is null)
            {
                // The icon is up before the panel is built. Remember the click rather than
                // dropping it; the panel opens the moment it exists.
                _activationPending = true;
                _pendingAnchor = anchor;
                return;
            }

            _popup.Toggle(anchor);
        });
    }

    private void OnMenuInvoked(object? sender, TrayMenuItemInvokedEventArgs e)
    {
        string id = e.ItemId;

        Dispatcher.UIThread.Post(() =>
        {
            switch (id)
            {
                case TrayController.OpenId:
                    _popup?.Close("menu");
                    _dashboard?.Open();
                    break;

                case TrayController.SettingsId:
                    _popup?.Close("menu");
                    _dashboard?.Open("Settings");
                    break;

                case TrayController.QuitId:
                    RequestShutdown();
                    break;

                default:
                    break;
            }
        });
    }

    private void OnSurfaceRequested(object? sender, EventArgs e)
    {
        // Another launch asked this instance to show itself. Anchor it the same way a click
        // would, so the panel appears where the user's eye already is.
        _ = Task.Run(async () =>
        {
            Altim.Core.Models.PixelRect? anchor = null;
            if (_platform?.Platform is { } platform)
            {
                try
                {
                    anchor = await platform.GetTrayAnchorAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AltimLog.Write("tray", "Reading the tray anchor failed", ex);
                }
            }

            Dispatcher.UIThread.Post(() => _popup?.Open(anchor));
        });
    }

    /// <summary>
    /// Rebuilds the tray menu when a degraded condition is recorded, which is the only
    /// surface a process with no main window is guaranteed to have.
    /// </summary>
    /// <remarks>
    /// Not every condition is known at start-up. The Linux notification service connects on
    /// its first message, so whether notifications work is not knowable until one is sent;
    /// building the menu once from what was known before the icon went up would leave the
    /// rest permanently unreported. Raised on whichever thread recorded it, so the rebuild
    /// is queued rather than awaited.
    /// </remarks>
    private void OnReportChanged(object? sender, EventArgs e)
    {
        TrayController? tray = _tray;
        if (tray is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await tray.RefreshMenuAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down; the menu belongs to a process that is closing.
            }
            catch (Exception ex)
            {
                AltimLog.Write("tray", "Rebuilding the menu for a new condition failed", ex);
            }
        });
    }

    private void OnSystemSuspending(object? sender, EventArgs e)
    {
        MonitorScheduler? scheduler = _schedulers.Current;
        if (scheduler is null)
        {
            return;
        }

        AltimLog.Write("monitor", "System suspending.");
        scheduler.Suspend();
    }

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        MonitorScheduler? scheduler = _schedulers.Current;
        if (scheduler is null)
        {
            return;
        }

        AltimLog.Write("monitor", "System resumed.");

        // A wake with no suspend before it is an ordinary outcome, not a bug: a modern
        // standby machine can sleep without sending the classic broadcast, and Altim may
        // have started after the machine went to sleep. MonitorScheduler.Resume does
        // nothing unless the scheduler is suspended — which is what stops a platform that
        // reports a wake twice refreshing twice — so entering the state here is what makes
        // the user's "refresh on resume" setting hold in the half-signal case too.
        if (!scheduler.IsSuspended)
        {
            scheduler.Suspend();
        }

        scheduler.Resume();
    }

    /// <summary>
    /// Carries a reduce-motion change from the platform to the interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform raises this on whichever thread heard about it - the tray message loop on
    /// Windows, a bus thread on Linux - and <see cref="Motion"/>'s subscribers are controls,
    /// so the hand-off is posted to the dispatcher. It is not awaited: the next frame is soon
    /// enough for a preference somebody has just changed in another application's window.
    /// </para>
    /// <para>
    /// The line is written from inside the posted work rather than before it, so that what
    /// reaches the log is "the interface has been told" and not "something intended to tell
    /// it". No test project binds to the composition root, which makes this line the only
    /// evidence that the chain from the platform to the interface is joined up, and evidence
    /// for the wrong half of it would be worse than none.
    /// </para>
    /// </remarks>
    private void OnMotionPreferenceChanged(object? sender, EventArgs e)
    {
        MotionPreference preference = _platform?.Motion.Current ?? MotionPreference.Unknown;

        Dispatcher.UIThread.Post(() =>
        {
            Motion.Set(preference);
            AltimLog.Write("motion", "Reduced motion changed: " + Describe(preference));
        });
    }

    private void OnPlatformThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_current.Theme == ThemePreference.System)
            {
                ApplyTheme(ThemePreference.System);
            }
        });

    private void OnUsageUpdated(object? sender, ProviderUsage e) => _pipeline?.Submit(e);

    private void OnProviderFailed(object? sender, ProviderFailure e) =>
        AltimLog.Write("provider", "Refreshing " + e.ProviderId + " failed", e.Exception);

    private void OnReadingsChanged(object? sender, IReadOnlyList<ProviderUsage> e)
    {
        TrayController? tray = _tray;
        if (tray is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _ = tray.UpdateTooltipAsync(e, _shutdown.Token).AsTask();
        }
        catch (ObjectDisposedException)
        {
            // A reading that finished as the tray host went away. The tooltip it was
            // about to write belongs to a process that is closing.
        }
    }

    private void OnSettingsChanged(object? sender, AltimSettings e)
    {
        _current = e;

        // Before anything else, and deliberately not on the dispatcher: the next scheduler
        // tick can be on a worker already, and the whole point of the setting is that
        // switching it off stops the next call rather than the next restart.
        _networkPolicy.Set(e.AllowNetworkCalls);

        Dispatcher.UIThread.Post(() =>
        {
            ApplyTheme(e.Theme);

            // The meter ticks are drawn at the configured thresholds, so the panel has to
            // hear about a threshold change even while it is hidden.
            _popup?.ViewModel.ApplySettings(e);
        });

        _ = Task.Run(async () =>
        {
            await ApplyAutoStartAsync(e).ConfigureAwait(false);
            await RebuildSchedulerAsync(e).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The session-end <em>query</em>: Windows asking whether the session may end, which the
    /// user can still answer no to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing irreversible happens here, and it used to.</b> This handler removed the
    /// tray icon and latched the shutdown flag, on the reasoning that a session end leaves no
    /// time for an orderly teardown. Both halves were wrong. Avalonia raises this from
    /// <c>WM_QUERYENDSESSION</c>, which is a question — sign-out can be cancelled at the
    /// confirmation screen, or by another application — and answering it is not the same as
    /// the process going away. Reproduced on Windows 11 26200 by sending the query to the
    /// running process: the icon went, the query was vetoed by
    /// <c>PopupHost</c> cancelling its own close, and what was left was a running Altim with
    /// no icon and, because the flag had latched, a Quit that did nothing.
    /// </para>
    /// <para>
    /// So the query gets a log line and no action. <see cref="OnExit"/> is where shutdown is
    /// certain, and that is where the teardown happens.
    /// </para>
    /// </remarks>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) =>
        AltimLog.Write("shutdown", "A session end was queried; allowing it.");

    /// <summary>
    /// Shutdown is certain: Avalonia has decided to exit and is about to stop the dispatcher.
    /// </summary>
    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) =>
        ShutdownSynchronously("the application is exiting");

    /// <summary>
    /// Tears everything down on the calling thread, bounded by
    /// <see cref="SynchronousShutdownTimeout"/>.
    /// </summary>
    /// <param name="cause">Why, for the log.</param>
    /// <remarks>
    /// <para>
    /// For the two paths that have nothing behind them: the session ending, and an unhandled
    /// exception on the dispatcher thread. Both run on that thread with the message loop
    /// about to stop, so handing the teardown to a worker and returning leaves a tray icon
    /// and an open database behind while the process exits around them.
    /// </para>
    /// <para>
    /// The icon goes first and only from here, because from here the process really is going
    /// away — the removal can no longer be regretted.
    /// </para>
    /// </remarks>
    internal void ShutdownSynchronously(string cause)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        AltimLog.Write("shutdown", "Tearing down synchronously: " + cause + ".");

        try
        {
            _platform?.Dispose();
        }
        catch (Exception ex)
        {
            AltimLog.Write("shutdown", "Removing the tray icon failed", ex);
        }

        // The windows next, here, on this thread. This is the dispatcher thread and it is
        // about to block on the rest of the teardown, and the teardown does not stay on it:
        // the first await inside DisposeAsync hands the continuation to the thread pool, and
        // a pool thread asking the dispatcher to close the windows would be waiting for a
        // frame that cannot run until the wait it is inside of is over. Measured as exactly
        // that deadlock before this line existed — the teardown reached the windows and sat
        // there until the budget ran out.
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                DisposeWindows();
            }
        }
        catch (Exception ex)
        {
            AltimLog.Write("shutdown", "Closing the windows failed", ex);
        }

        try
        {
            if (!DisposeAsync().AsTask().Wait(SynchronousShutdownTimeout))
            {
                AltimLog.Write("shutdown", "The teardown did not finish in time; exiting anyway.");
            }
        }
        catch (Exception ex)
        {
            AltimLog.Write("shutdown", "The teardown failed", ex);
        }
    }

    /// <summary>
    /// Quit, from the tray menu.
    /// </summary>
    /// <remarks>
    /// The latch is taken here rather than on the session-end query, which is what makes Quit
    /// still work after a sign-out the user backed out of. <see cref="OnExit"/> finds the
    /// latch already taken and does nothing, because this path has already done it.
    /// </remarks>
    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        AltimLog.Write("shutdown", "Quit was chosen from the tray menu.");

        _ = Task.Run(async () =>
        {
            await DisposeAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _lifetime.Shutdown());
        });
    }
}
