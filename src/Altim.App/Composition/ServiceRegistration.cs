using Altim.App.Diagnostics;
using Altim.App.Services;
using Altim.Core.Abstractions;
using Altim.Core.Monitoring;
using Altim.Core.Settings;
using Altim.Providers.Claude;
using Altim.Providers.Codex;
using Altim.Storage;
using Altim.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Altim.App.Composition;

/// <summary>
/// The container. Every registration is an explicit factory, so nothing in Altim is
/// constructed by reflection and the whole graph survives trimming and Native AOT.
/// </summary>
/// <remarks>
/// <para>
/// The container is built after storage and settings have been read, not before, because
/// the scheduler takes its cadences from the loaded settings and its cadences are fixed at
/// construction.
/// </para>
/// <para>
/// Network permission does <em>not</em> work that way and deliberately so. It is a live
/// reading through <see cref="LiveNetworkPolicy"/> rather than a value baked into each
/// provider's options, because the setting can be changed while Altim runs and a permission
/// frozen at start-up would go on calling the vendor until the next restart.
/// </para>
/// <para>
/// Providers are registered as <see cref="IUsageProvider"/> so that
/// <c>GetServices&lt;IUsageProvider&gt;()</c> is the whole list, which is what makes adding
/// a provider a one-line change here and no change at all in the interface.
/// </para>
/// </remarks>
internal static class ServiceRegistration
{
    /// <summary>Builds the container.</summary>
    /// <param name="report">The degraded conditions collected so far.</param>
    /// <param name="platform">The native stack: tray, notifications, autostart, processes.</param>
    /// <param name="storage">The database and everything built on it.</param>
    /// <param name="settings">The settings seam the interface binds to.</param>
    /// <param name="loaded">The settings as they were read at start-up.</param>
    /// <param name="networkGate">
    /// The gate handed to both providers, forwarding to the live scheduler's rate limiter.
    /// </param>
    /// <param name="networkPolicy">
    /// The live "may Altim reach the vendor" flag, also handed to both providers. The
    /// caller keeps it in step with the stored setting.
    /// </param>
    public static ServiceProvider Build(
        StartupReport report,
        PlatformStack platform,
        StorageStack storage,
        SettingsGateway settings,
        AltimSettings loaded,
        SchedulerNetworkGate networkGate,
        LiveNetworkPolicy networkPolicy)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(networkGate);
        ArgumentNullException.ThrowIfNull(networkPolicy);

        var services = new ServiceCollection();

        // Clock and diagnostics.
        _ = services.AddSingleton(TimeProvider.System);
        _ = services.AddSingleton(report);

        // Storage. The database itself is registered when it opened; the history service and
        // the settings backend are registered either way, because their degraded forms
        // satisfy the same contracts.
        if (storage.Database is { } database)
        {
            _ = services.AddSingleton(database);
        }

        if (storage.NotificationState is { } notificationState)
        {
            _ = services.AddSingleton(notificationState);
        }

        if (storage.Retention is { } retention)
        {
            _ = services.AddSingleton(retention);
        }

        _ = services.AddSingleton(storage.History);
        _ = services.AddSingleton(settings);
        _ = services.AddSingleton<ISettingsStore>(settings);

        // Platform. The tray host is reached through IPlatformService and is owned by it, so
        // it is deliberately not registered separately: two owners is how a tray icon ends
        // up disposed twice or not at all.
        if (platform.Platform is { } platformService)
        {
            _ = services.AddSingleton(platformService);
        }

        if (platform.Processes is { } processes)
        {
            _ = services.AddSingleton(processes);
        }

        _ = services.AddSingleton(platform.Notifications);
        _ = services.AddSingleton(platform.AutoStart);

        // The reduce-motion reading. Registered like the rest of the native stack even though
        // the interface reaches it through Altim.UI's ambient value rather than through the
        // container: the composition root is what bridges the two, and a diagnostic asking
        // what this machine reported should find the service here with everything else.
        _ = services.AddSingleton(platform.Motion);
        _ = services.AddSingleton(networkGate);
        _ = services.AddSingleton<IRefreshGate>(networkGate);
        _ = services.AddSingleton(networkPolicy);
        _ = services.AddSingleton<INetworkPolicy>(networkPolicy);

        // Providers. Both are handed the forwarding network gate, which is what rate limits
        // Claude's headless usage summary and Codex's app-server call; nothing else in the
        // process supplies one, so without it those calls would run on every refresh.
        _ = services.AddSingleton<IUsageProvider>(_ => new ClaudeUsageProvider(
            options: ClaudeOptions.Default,
            processMonitor: platform.Processes,
            timeProvider: TimeProvider.System,
            networkGate: networkGate,
            networkPolicy: networkPolicy));

        _ = services.AddSingleton<IUsageProvider>(_ => new CodexUsageProvider(
            options: CodexOptions.Default,
            processMonitor: platform.Processes,
            timeProvider: TimeProvider.System,
            networkGate: networkGate,
            networkPolicy: networkPolicy));

        // The scheduler. Its cadences are fixed at construction, so a later change to the
        // refresh interval replaces the instance; see AltimRuntime.RebuildSchedulerAsync,
        // and SchedulerNetworkGate for why the providers survive that.
        _ = services.AddSingleton(provider => new MonitorScheduler(
            provider.GetServices<IUsageProvider>(),
            provider.GetRequiredService<TimeProvider>(),
            MonitorSchedulerOptions.FromSettings(loaded)));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false,
        });
    }
}
