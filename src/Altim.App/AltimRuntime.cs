using System.Diagnostics;
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
    /// <summary>
    /// How close together two tray activations have to be to count as one click. Long
    /// enough to absorb the shell's duplicate message, short enough that a user deliberately
    /// clicking twice still gets two answers.
    /// </summary>
    private const long ActivationCoalescingMilliseconds = 350;

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly StartupReport _report = new();
    private readonly SchedulerNetworkGate _networkGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DateTimeOffset _processStarted;

    private SingleInstance? _instance;
    private PlatformStack? _platform;
    private StorageStack? _storage;
    private SettingsGateway? _settings;
    private ServiceProvider? _services;
    private MonitorScheduler? _scheduler;
    private ProviderHintWatcher? _hints;
    private UsagePipeline? _pipeline;
    private TrayController? _tray;
    private PopupHost? _popup;
    private DashboardHost? _dashboard;
    private IReadOnlyList<IUsageProvider> _providers = [];

    private AltimSettings _current = AltimSettings.Default;
    private Altim.Core.Models.PixelRect? _pendingAnchor;
    private long _lastActivation;
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

        _platform = PlatformStack.Create(_report);

        if (_platform.Platform is { } platform)
        {
            _tray = new TrayController(platform.Tray, _report);
            _tray.Activated += OnTrayActivated;
            _tray.MenuInvoked += OnMenuInvoked;
            platform.SystemResumed += OnSystemResumed;
            platform.ThemeChanged += OnPlatformThemeChanged;

            _ = ShowTrayAsync();
        }

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

        if (_scheduler is not null)
        {
            _scheduler.UsageUpdated -= OnUsageUpdated;
            _scheduler.ProviderFailed -= OnProviderFailed;

            try
            {
                await _scheduler.DisposeAsync().ConfigureAwait(false);
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
            platform.SystemResumed -= OnSystemResumed;
            platform.ThemeChanged -= OnPlatformThemeChanged;
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
                _report.Add("The tray icon could not be added");
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

            _services = ServiceRegistration.Build(_report, _platform!, _storage, _settings, loaded, _networkGate);
            _providers = [.. _services.GetServices<IUsageProvider>()];

            _scheduler = _services.GetRequiredService<MonitorScheduler>();
            _networkGate.Bind(_scheduler.NetworkGate);
            _scheduler.UsageUpdated += OnUsageUpdated;
            _scheduler.ProviderFailed += OnProviderFailed;

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

            await Dispatcher.UIThread.InvokeAsync(() => BuildWindows(loaded)).GetTask().ConfigureAwait(false);

            _scheduler.Start();

            // Filesystem hints are the event-driven half of monitoring; the 60-second floor
            // covers anything they miss, which is why failing to arm one is not fatal.
            _hints = ProviderHintWatcher.Create(_scheduler, BuildProviderIdSet());

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

    private void BuildWindows(AltimSettings settings)
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

        _dashboard = new DashboardHost(_providers, _storage!.History, _settings!, TimeProvider.System);
        _dashboard.Opened += OnWindowVisibilityChanged;
        _dashboard.Closed += OnWindowVisibilityChanged;

        // Fills the panel with the current readings while it is still hidden, which is what
        // lets the first open be a position and a show rather than a read.
        _ = popup.LoadAsync(CancellationToken.None);

        if (_activationPending)
        {
            _activationPending = false;
            _popup.Open(_pendingAnchor);
        }
    }

    private async ValueTask DisposeWindowsAsync()
    {
        if (_popup is null && _dashboard is null)
        {
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
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
            });
        }
        catch (Exception ex)
        {
            AltimLog.Write("shutdown", "Closing the windows failed", ex);
        }
    }

    private void ApplyTheme(ThemePreference preference)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = VariantFor(preference);
        }
    }

    private async Task FirstReadingsAsync(CancellationToken ct)
    {
        MonitorScheduler? scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        try
        {
            await scheduler.RefreshAllAsync(ct).ConfigureAwait(false);
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

    private async Task MaintainAsync(CancellationToken ct)
    {
        if (_storage?.Retention is not { } retention)
        {
            return;
        }

        try
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
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Retention is housekeeping. Failing it costs disk, never a reading.
            AltimLog.Write("storage", "Retention pass failed", ex);
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

    private async Task RebuildSchedulerAsync(AltimSettings settings)
    {
        MonitorScheduler? previous = _scheduler;
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
            previous.UsageUpdated -= OnUsageUpdated;
            previous.ProviderFailed -= OnProviderFailed;
            await previous.DisposeAsync().ConfigureAwait(false);

            var replacement = new MonitorScheduler(_providers, TimeProvider.System, wanted);
            replacement.UsageUpdated += OnUsageUpdated;
            replacement.ProviderFailed += OnProviderFailed;

            _scheduler = replacement;

            // The providers are holding the forwarding gate, so re-pointing it is the whole
            // of the hand-over; nothing has to be rebuilt to follow the new floor.
            _networkGate.Bind(replacement.NetworkGate);
            replacement.SetUiVisible(IsAnyWindowOpen());
            replacement.Start();

            AltimLog.Write("monitor", "Rebuilt the scheduler for a new cadence.");
        }
        catch (Exception ex)
        {
            AltimLog.Write("monitor", "Rebuilding the scheduler failed; monitoring has stopped", ex);
            _report.Add("Monitoring stopped; restart Altim");
        }
    }

    private bool IsAnyWindowOpen() => _popup?.IsOpen == true || _dashboard?.IsOpen == true;

    private void OnWindowVisibilityChanged(object? sender, EventArgs e) =>
        _scheduler?.SetUiVisible(IsAnyWindowOpen());

    private void OnPopupOpenRequested(object? sender, EventArgs e)
    {
        _popup?.Close("opened the dashboard");
        _dashboard?.Open();
    }

    private void OnTrayActivated(object? sender, TrayClickEventArgs e)
    {
        Altim.Core.Models.PixelRect? anchor = e.Anchor;

        // Measured on Windows 11 26200: one left click on a version 4 icon produces two
        // activations, NIN_SELECT and then WM_LBUTTONUP about 25ms later, and the tray host
        // raises Clicked for both. Toggling on each one opens the panel and closes it again
        // before it has been drawn, which reads as "the tray icon does nothing". Two
        // activations this close together are one click.
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastActivation, now);
        if (previous != 0 && now - previous < ActivationCoalescingMilliseconds)
        {
            return;
        }

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

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        // Nothing in the platform contract reports the machine going to sleep — IPlatformService
        // carries SystemResumed and no counterpart — so the scheduler's suspended state is
        // entered and left here, at the wake, which is the only moment this process hears
        // about. Resume honours the user's "refresh on resume" setting and coalesces, so a
        // wake that reports twice still produces one refresh.
        MonitorScheduler? scheduler = _scheduler;
        if (scheduler is null)
        {
            return;
        }

        AltimLog.Write("monitor", "System resumed.");
        scheduler.Suspend();
        scheduler.Resume();
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

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // The session is ending and there is no time for an orderly teardown. Remove the
        // icon synchronously, because the one thing that must not survive this process is a
        // ghost in the notification area.
        if (Interlocked.Exchange(ref _shuttingDown, 1) == 0)
        {
            _platform?.Dispose();
        }
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await DisposeAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _lifetime.Shutdown());
        });
    }
}
