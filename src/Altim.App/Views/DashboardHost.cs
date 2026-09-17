using Altim.App.Diagnostics;
using Altim.Core.Abstractions;
using Altim.UI.Services;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Controls;

namespace Altim.App.Views;

/// <summary>
/// The dashboard window: built when it is asked for and released when it is dismissed.
/// </summary>
/// <remarks>
/// The opposite lifetime to the popup, and deliberately so. The popup has to stay alive
/// because the process needs a top level; the dashboard is 960x680 of render surface that
/// nobody is looking at once it is closed, so it is constructed on demand and its view model
/// — which holds a provider row per provider, each subscribed to that provider — is disposed
/// with it.
/// </remarks>
internal sealed class DashboardHost : IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly IUsageHistoryService _history;
    private readonly ISettingsStore _settings;
    private readonly IStatusLineService _statusLine;
    private readonly TimeProvider _time;

    private DashboardWindow? _window;
    private DashboardViewModel? _viewModel;
    private bool _disposed;

    /// <summary>Creates the host.</summary>
    /// <param name="providers">The providers the dashboard reports on.</param>
    /// <param name="history">The store the history page reads.</param>
    /// <param name="settings">The seam the settings page round-trips through.</param>
    /// <param name="statusLine">The seam the settings page installs the status line through.</param>
    /// <param name="timeProvider">The clock every time on screen is measured against.</param>
    public DashboardHost(
        IReadOnlyList<IUsageProvider> providers,
        IUsageHistoryService history,
        ISettingsStore settings,
        IStatusLineService statusLine,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(statusLine);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _providers = providers;
        _history = history;
        _settings = settings;
        _statusLine = statusLine;
        _time = timeProvider;
    }

    /// <summary>Raised after the window has been shown.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the window has been closed and its view model released.</summary>
    public event EventHandler? Closed;

    /// <summary>True while the window exists.</summary>
    public bool IsOpen => _window is not null;

    /// <summary>
    /// Shows the dashboard, building it first if it is not already up, and brings an
    /// existing one to the front rather than opening a second.
    /// </summary>
    /// <param name="section">
    /// The sidebar row to select by title, or null to leave the default selection. Used by
    /// the tray menu's Settings entry, which is the same window on a different page.
    /// </param>
    public void Open(string? section = null)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_window is null)
            {
                _viewModel = new DashboardViewModel(_providers, _history, _settings, _statusLine, _time);
                _window = new DashboardWindow(_viewModel);
                _window.Closed += OnClosed;
                _window.Show();

                // Loading after Show so the window is on screen inside the frame budget and
                // the pages fill in behind it, rather than the click costing a history query
                // before anything is painted.
                _ = _viewModel.LoadAsync(CancellationToken.None);
                Opened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _window.Activate();
            }

            Select(section);
        }
        catch (Exception ex)
        {
            AltimLog.Write("dashboard", "Opening the dashboard failed", ex);
            Release();
        }
    }

    /// <summary>Closes the window if it is open.</summary>
    public void Close() => _window?.Close();

    /// <summary>Closes the window and releases its view model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        Release();
    }

    private void Select(string? section)
    {
        if (section is null || _viewModel is null)
        {
            return;
        }

        foreach (NavigationItemViewModel item in _viewModel.Sections)
        {
            if (string.Equals(item.Title, section, StringComparison.Ordinal))
            {
                _viewModel.SelectedSection = item;
                return;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Release();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Release()
    {
        if (_window is not null)
        {
            _window.Closed -= OnClosed;
            _window = null;
        }

        _viewModel?.Dispose();
        _viewModel = null;
    }
}
