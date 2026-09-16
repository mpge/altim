using Altim.Core.Abstractions;
using Altim.Core.Models;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Altim.Platform.Linux;

/// <summary>
/// The Linux panel presence, built on Avalonia's own <c>TrayIcon</c>, which on this platform
/// is a StatusNotifierItem published over D-Bus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> There is no Linux desktop in the development environment. Avalonia owns
/// the protocol implementation, so what is untested here is Altim's use of it rather than the
/// protocol itself.
/// </para>
/// <para>
/// <b>Why Avalonia's tray icon rather than a hand-written one, unlike Windows and macOS.</b>
/// Windows needed its own host to register at version 4 and get click coordinates; macOS
/// needed its own because Avalonia's has no click callback there. Neither reason applies on
/// Linux: StatusNotifierItem carries no coordinates for anyone, so there is nothing a
/// hand-written implementation could learn that this one cannot, and Avalonia's is already
/// fixed for the 12.1.0 defect that ARCHITECTURE.md pins the version for.
/// </para>
/// <para>
/// <b>There is no anchor, ever.</b> The StatusNotifierItem specification has no geometry in
/// it at all: the item is a D-Bus object, the panel draws it wherever it likes, and nothing
/// in the protocol reports where that was. <see cref="Clicked"/> therefore always carries a
/// null anchor, and the popup falls to the working-area corner. This is permanent, not a gap
/// waiting to be filled — see
/// <see cref="LinuxPlatformService.GetTrayAnchorAsync"/>.
/// </para>
/// <para>
/// <b>The menu is attached, not raised.</b> Avalonia's tray icon exposes a
/// <c>NativeMenu</c> the platform presents on secondary activation and offers no way to pop
/// one on demand, which is exactly the case <see cref="ITrayHost.ShowMenuAsync"/> documents:
/// the entries are attached and the desktop presents them on the next right click.
/// </para>
/// <para>
/// <b>Threading.</b> <c>TrayIcon</c> is an Avalonia object, so every touch is marshalled to
/// the UI thread. A caller already on it runs inline.
/// </para>
/// </remarks>
public sealed class LinuxTrayHost : ITrayHost
{
    private readonly string? _assetDirectory;
    private readonly List<(NativeMenuItem Item, EventHandler Handler)> _menuBindings = [];

    private TrayIcon? _icon;
    private IReadOnlyList<TrayMenuItem> _menuItems = [];
    private TrayIconVariant _variant = TrayIconVariant.Automatic;
    private string _tooltip;
    private string? _appliedIconPath;
    private string? _unavailableReason;
    private bool _desktopIsDark;
    private volatile bool _visible;
    private bool _disposed;

    /// <summary>
    /// Creates the host. Nothing is published to the panel until <see cref="ShowAsync"/> is
    /// called, which also means nothing requires Avalonia to be initialised before then.
    /// </summary>
    /// <param name="tooltip">Initial hover text.</param>
    /// <param name="assetDirectory">
    /// Directory holding the icon assets, or <see langword="null"/> to look for
    /// <c>assets/icons</c> beside the application and then up the directory tree.
    /// </param>
    public LinuxTrayHost(string tooltip = "Altim", string? assetDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        _tooltip = tooltip;
        _assetDirectory = assetDirectory ?? LinuxTrayAssets.DiscoverAssetDirectory(AppContext.BaseDirectory);
    }

    /// <inheritdoc />
    /// <remarks>The anchor is always null on Linux. See the class remarks.</remarks>
    public event EventHandler<TrayClickEventArgs>? Clicked;

    /// <inheritdoc />
    public event EventHandler<TrayMenuItemInvokedEventArgs>? MenuItemInvoked;

    /// <inheritdoc />
    public bool IsVisible => _visible;

    /// <summary>
    /// Why the panel presence is unavailable, or null when nothing has gone wrong. Set when
    /// the windowing backend has no StatusNotifierItem support, which is the case under a
    /// bare Wayland session with no XWayland and no host implementing the specification.
    /// </summary>
    public string? UnavailableReason => _unavailableReason;

    /// <summary>The directory the icon assets were found in, or null when none was.</summary>
    public string? AssetDirectory => _assetDirectory;

    /// <inheritdoc />
    public ValueTask ShowAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Invoke(
            () =>
            {
                if (!EnsureIcon())
                {
                    return;
                }

                _icon!.IsVisible = true;
                _visible = true;
            },
            ct);
    }

    /// <inheritdoc />
    public ValueTask HideAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Invoke(
            () =>
            {
                if (_icon is not null)
                {
                    _icon.IsVisible = false;
                }

                _visible = false;
            },
            ct);
    }

    /// <inheritdoc />
    public ValueTask SetTooltipAsync(string tooltip, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Invoke(
            () =>
            {
                _tooltip = tooltip;
                if (_icon is not null)
                {
                    _icon.ToolTipText = tooltip;
                }
            },
            ct);
    }

    /// <inheritdoc />
    public ValueTask SetIconAsync(TrayIconVariant variant, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Invoke(
            () =>
            {
                _variant = variant;
                ApplyIcon();
            },
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="anchor"/> is ignored, and so is the idea of presenting now: Avalonia's
    /// tray icon can only attach a menu for the desktop to present on secondary activation.
    /// The call completes once the entries are attached.
    /// </remarks>
    public ValueTask ShowMenuAsync(
        IReadOnlyList<TrayMenuItem> items, PixelRect? anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Invoke(
            () =>
            {
                _menuItems = items;
                ApplyMenu(items);
            },
            ct);
    }

    /// <summary>
    /// Tells the host what the desktop's colour scheme is, so
    /// <see cref="TrayIconVariant.Automatic"/> can pick a glyph with contrast against the
    /// panel.
    /// </summary>
    /// <param name="dark">True when the appearance portal reports a dark preference.</param>
    /// <param name="ct">Cancels the call before the icon is swapped.</param>
    /// <remarks>
    /// Called by <see cref="LinuxPlatformService"/> when the portal answers, which is
    /// normally shortly <em>after</em> start-up rather than during it. The panel's own
    /// background is not knowable; see <see cref="LinuxTrayAssets"/> for why this is the best
    /// available signal and why it is a guess.
    /// </remarks>
    public ValueTask SetDesktopColorSchemeAsync(bool dark, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        return Invoke(
            () =>
            {
                if (_desktopIsDark == dark)
                {
                    return;
                }

                _desktopIsDark = dark;
                ApplyIcon();
            },
            ct);
    }

    /// <summary>Removes the item from the panel.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visible = false;

        TrayIcon? icon = _icon;
        _icon = null;

        if (icon is null)
        {
            return;
        }

        void Teardown()
        {
            icon.Clicked -= OnClicked;
            DetachMenuHandlers();
            icon.IsVisible = false;
            icon.Dispose();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Teardown();
            return;
        }

        try
        {
            // Blocking is correct: the item must leave the panel before the process does, or
            // the host is left drawing a slot for an object that no longer answers.
            Dispatcher.UIThread.InvokeAsync(Teardown).GetTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or TaskCanceledException)
        {
            // The dispatcher has already shut down; the item dies with the connection.
        }
    }

    private static ValueTask Invoke(Action work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            work();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Dispatcher.UIThread.InvokeAsync(work).GetTask());
    }

    private bool EnsureIcon()
    {
        if (_icon is not null)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            // This assembly is compiled into every build of the solution, because there is no
            // net10.0-linux target framework to hide it behind. Creating an Avalonia TrayIcon
            // off Linux would publish a real icon through some other backend, so the guard is
            // here rather than only in the composition root.
            _unavailableReason = "The Linux tray host only publishes a StatusNotifierItem on Linux.";
            return false;
        }

        try
        {
            var icon = new TrayIcon { ToolTipText = _tooltip };
            icon.Clicked += OnClicked;
            _icon = icon;
        }
        catch (Exception ex)
        {
            // No StatusNotifierItem support in this session. Altim runs without a panel
            // presence; ITrayHost's contract is that this is reported, not thrown.
            _unavailableReason = ex.Message;
            return false;
        }

        ApplyIcon();
        ApplyMenu(_menuItems);
        return true;
    }

    private void ApplyIcon()
    {
        if (_icon is null || _assetDirectory is null)
        {
            return;
        }

        bool light = LinuxTrayAssets.UsesLightGlyph(_variant, _desktopIsDark);
        string? path = LinuxTrayAssets.Resolve(_assetDirectory, light, LinuxTrayAssets.PreferredPixelSize);
        if (path is null || string.Equals(path, _appliedIconPath, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _icon.Icon = new WindowIcon(path);
            _appliedIconPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            // A missing or unreadable asset leaves whatever icon is already published. The
            // panel slot stays rather than disappearing.
            _unavailableReason = ex.Message;
        }
    }

    private void ApplyMenu(IReadOnlyList<TrayMenuItem> items)
    {
        // An empty list is what the host holds between ShowAsync and the first
        // RefreshMenuAsync. Publishing an empty NativeMenu in that window would give the
        // panel a menu with nothing in it, so nothing is published until there is something
        // to put in it.
        if (_icon is null || items.Count == 0)
        {
            return;
        }

        DetachMenuHandlers();

        var menu = new NativeMenu();

        for (int index = 0; index < items.Count; index++)
        {
            TrayMenuItem entry = items[index];
            if (entry.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(entry.Label)
            {
                IsEnabled = entry.IsEnabled,
                IsChecked = entry.IsChecked,
                ToggleType = entry.IsChecked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
            };

            // The id is captured per entry, so the handler does not depend on the list still
            // being the one the menu was built from.
            string id = entry.Id;
            EventHandler handler = (_, _) => MenuItemInvoked?.Invoke(this, new TrayMenuItemInvokedEventArgs(id));

            item.Click += handler;
            _menuBindings.Add((item, handler));
            menu.Add(item);
        }

        _icon.Menu = menu;
    }

    private void DetachMenuHandlers()
    {
        // NativeMenuItem holds its handler and the handler holds this host, so a menu rebuilt
        // on every refresh would otherwise keep every previous menu alive.
        foreach ((NativeMenuItem item, EventHandler handler) in _menuBindings)
        {
            item.Click -= handler;
        }

        _menuBindings.Clear();
    }

    private void OnClicked(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Null, always: StatusNotifierItem carries no geometry. The caller drops to the
        // working-area corner.
        Clicked?.Invoke(this, new TrayClickEventArgs(null));
    }
}
