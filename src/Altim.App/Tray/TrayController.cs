using System.Globalization;
using System.Text;
using Altim.App.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Usage;
#if WINDOWS
using Altim.Platform.Windows;
#endif
using Altim.Providers;
using Altim.Providers.Claude;
using Altim.Providers.Codex;

namespace Altim.App.Tray;

/// <summary>
/// Everything the tray icon does: the icon itself, the tooltip, the native menu and the two
/// gestures.
/// </summary>
/// <remarks>
/// <para>
/// A left click asks for the panel and carries the icon's rectangle when the host knows it.
/// A right click raises the native menu, which is Open Altim, Settings and Quit, plus one
/// disabled line for anything that is degraded — an uninstalled provider, a notification
/// platform that refused registration, a database that could not be opened. That list is the
/// only place a tray-only process can honestly report those, so it is not decoration.
/// </para>
/// <para>
/// The icon variant is left on <c>Automatic</c>. The Windows host already picks the dark or
/// light glyph from the taskbar's own setting and reloads it when that changes, and the
/// choice is about contrast against the tray, not about the application's theme.
/// </para>
/// </remarks>
internal sealed class TrayController : IDisposable
{
    /// <summary>The identifiers the native menu raises.</summary>
    public const string OpenId = "open";

    /// <summary>The identifier of the Settings entry.</summary>
    public const string SettingsId = "settings";

    /// <summary>The identifier of the Quit entry.</summary>
    public const string QuitId = "quit";

    /// <summary>The shell truncates a tooltip at 128 characters including the terminator.</summary>
    private const int TooltipLimit = 127;

    private readonly ITrayHost _host;
    private readonly StartupReport _report;
    private string _tooltip = "Altim";
    private bool _disposed;

    /// <summary>Creates the controller over a tray host.</summary>
    /// <param name="host">The host, owned by the platform service and disposed with it.</param>
    /// <param name="report">The degraded conditions the menu reports.</param>
    public TrayController(ITrayHost host, StartupReport report)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(report);

        _host = host;
        _report = report;

        _host.Clicked += OnClicked;
        _host.MenuItemInvoked += OnMenuItemInvoked;
    }

    /// <summary>Raised on primary activation, carrying the icon rectangle when it is known.</summary>
    public event EventHandler<TrayClickEventArgs>? Activated;

    /// <summary>Raised when the user picks an entry from the native menu.</summary>
    public event EventHandler<TrayMenuItemInvokedEventArgs>? MenuInvoked;

    /// <summary>True while the icon is in the tray, including in the Windows 11 overflow.</summary>
    public bool IsVisible => _host.IsVisible;

    /// <summary>Adds the icon and attaches the menu.</summary>
    /// <param name="ct">Cancels the call.</param>
    public async ValueTask ShowAsync(CancellationToken ct)
    {
        await _host.ShowAsync(ct).ConfigureAwait(false);
        await RefreshMenuAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the native menu, which is how a condition detected after start-up — a
    /// provider that stopped answering, a write that failed — reaches the user.
    /// </summary>
    /// <param name="ct">Cancels the call.</param>
    public async ValueTask RefreshMenuAsync(CancellationToken ct)
    {
        List<TrayMenuItem> items = [];

        foreach (string issue in _report.Issues)
        {
            // Disabled, so it reads as a notice rather than as something to click. The id is
            // non-empty so it is not mistaken for a separator, and a disabled entry raises
            // no event.
            items.Add(new TrayMenuItem("issue:" + issue, issue, IsEnabled: false));
        }

        if (items.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
        }

        items.Add(new TrayMenuItem(OpenId, "Open Altim"));
        items.Add(new TrayMenuItem(SettingsId, "Settings"));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(QuitId, "Quit"));

        try
        {
#if WINDOWS
            if (_host is WindowsTrayHost windows)
            {
                // Stores the entries without presenting them, so the first right click on
                // the icon already has a menu.
                await windows.SetMenuAsync(items, ct).ConfigureAwait(false);
                return;
            }
#endif

            // The documented behaviour for a host that cannot raise a menu on demand: the
            // entries are attached and the platform presents them on secondary activation.
            await _host.ShowMenuAsync(items, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AltimLog.Write("tray", "Attaching the native menu failed", ex);
        }
    }

    /// <summary>
    /// Rewrites the hover text from the current readings.
    /// </summary>
    /// <param name="readings">Every provider's current reading.</param>
    /// <param name="ct">Cancels the call.</param>
    public async ValueTask UpdateTooltipAsync(IReadOnlyList<ProviderUsage> readings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readings);

        string tooltip = BuildTooltip(readings);
        if (string.Equals(tooltip, _tooltip, StringComparison.Ordinal))
        {
            return;
        }

        _tooltip = tooltip;

        try
        {
            await _host.SetTooltipAsync(tooltip, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AltimLog.Write("tray", "Setting the tooltip failed", ex);
        }
    }

    /// <summary>Stops listening. The host itself belongs to the platform service.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Clicked -= OnClicked;
        _host.MenuItemInvoked -= OnMenuItemInvoked;
    }

    /// <summary>
    /// The hover text: the application name, then one line per provider carrying the metric
    /// closest to its limit.
    /// </summary>
    /// <param name="readings">Every provider's current reading.</param>
    /// <remarks>
    /// Nothing is invented. A provider that reported no usable percentage says so, a failed
    /// reading says it is unavailable, and neither is rendered as a zero.
    /// </remarks>
    public static string BuildTooltip(IReadOnlyList<ProviderUsage> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (readings.Count == 0)
        {
            return Truncate("Altim\nNo providers configured");
        }

        var text = new StringBuilder("Altim");

        foreach (ProviderUsage reading in readings)
        {
            string name = DisplayNameFor(reading.ProviderId);

            if (reading.Status == ProviderStatus.Error)
            {
                _ = text.Append('\n').Append(name).Append(" unavailable");
                continue;
            }

            UsageOverview overview = UsageAggregator.Aggregate([reading]);
            if (overview.WorstMetric is { UsedPercent: { } percent } metric)
            {
                _ = text.Append('\n').Append(name).Append(' ')
                    .Append(metric.Label).Append(' ')
                    .Append(percent.ToString("0", CultureInfo.CurrentCulture)).Append('%');
            }
            else
            {
                _ = text.Append('\n').Append(name).Append(" not reported");
            }
        }

        return Truncate(text.ToString());
    }

    private static string DisplayNameFor(string providerId) => providerId switch
    {
        ProviderIds.Claude => ClaudeProviderInfo.DisplayName,
        ProviderIds.Codex => CodexProviderInfo.DisplayName,
        _ => providerId,
    };

    private static string Truncate(string text) =>
        text.Length > TooltipLimit ? text[..TooltipLimit] : text;

    private void OnClicked(object? sender, TrayClickEventArgs e) => Activated?.Invoke(this, e);

    private void OnMenuItemInvoked(object? sender, TrayMenuItemInvokedEventArgs e) => MenuInvoked?.Invoke(this, e);
}
