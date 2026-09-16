#if WINDOWS

using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Windows.Interop;
using Microsoft.Win32;

namespace Altim.Platform.Windows;

/// <summary>
/// The Windows tray presence: a real <c>Shell_NotifyIcon</c> registration owned by a
/// message-only window on its own message loop thread.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does not use Avalonia's <c>TrayIcon</c>. Avalonia registers its
/// icon at version 0, so the click messages carry no screen coordinates, and it
/// exposes no way to ask the shell where the icon actually is. Both are needed here:
/// the popup is positioned against the icon rectangle, and the rectangle has to come
/// from <c>Shell_NotifyIconGetRect</c>.
/// </para>
/// <para>
/// Every shell call is made on the thread that owns the window, because
/// <c>Shell_NotifyIcon</c> associates the icon with the calling thread's window and
/// the shell posts the callback messages back to it.
/// </para>
/// <para>
/// The icon is registered at <c>NOTIFYICON_VERSION_4</c>. That changes the callback
/// contract: primary activation arrives as <c>NIN_SELECT</c> rather than
/// <c>WM_LBUTTONUP</c>, the cursor position is packed into <c>wParam</c>, and the icon
/// id into the high word of <c>lParam</c>.
/// </para>
/// <para>
/// Events are raised on the message loop thread. Subscribers marshal to their own
/// thread and must not block, because the tray stops responding while a handler runs.
/// </para>
/// </remarks>
public sealed class WindowsTrayHost : ITrayHost
{
    private const uint IconId = 1;

    /// <summary>The shell truncates at 128 characters including the terminator.</summary>
    private const int TooltipLimit = 127;

    private readonly TrayWindow _window;
    private readonly TrayIconLoader _icons;
    private readonly TrayOverflow _overflow = new(IconId);

    private IReadOnlyList<TrayMenuItem> _menuItems = [];
    private TrayIconVariant _variant = TrayIconVariant.Automatic;
    private string _tooltip;
    private IntPtr _icon;
    private bool _iconIsShared;
    private bool _iconIsLight;
    private uint _dpi;
    private volatile bool _added;
    private int _lastAnchorHResult;
    private bool _disposed;

    /// <summary>
    /// Creates the host. The window and its thread exist as soon as this returns; the
    /// icon itself is added by <see cref="ShowAsync"/>.
    /// </summary>
    /// <param name="tooltip">Initial hover text.</param>
    /// <param name="iconDirectory">
    /// Directory holding the icon assets, or <see langword="null"/> to look for
    /// <c>assets/icons</c> beside the application and then up the directory tree.
    /// </param>
    public WindowsTrayHost(string tooltip = "Altim", string? iconDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        _tooltip = tooltip;
        _icons = new TrayIconLoader(iconDirectory);
        _dpi = TrayIconLoader.CurrentTrayDpi();

        _window = new TrayWindow();
        _window.MessageReceived += OnMessage;

        // A DPI change moves the notification area to a different icon size, and a
        // theme change flips which glyph has contrast against it. Both arrive as
        // system events rather than as window messages, because a message-only window
        // is not in the broadcast set.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <inheritdoc />
    public event EventHandler<TrayClickEventArgs>? Clicked;

    /// <inheritdoc />
    public event EventHandler<TrayMenuItemInvokedEventArgs>? MenuItemInvoked;

    /// <inheritdoc />
    public bool IsVisible => _added;

    /// <summary>
    /// The <c>HRESULT</c> from the most recent <c>Shell_NotifyIconGetRect</c> call.
    /// Zero is success. Note that success does not mean the anchor was usable: the
    /// shell answers for an icon in the overflow too, with the chevron's rectangle.
    /// Exposed for diagnostics only.
    /// </summary>
    public int LastAnchorHResult => _lastAnchorHResult;

    /// <summary>
    /// The notification area icon size this host is currently rendering, in physical
    /// pixels, and the DPI it was chosen for.
    /// </summary>
    public (uint Dpi, int Size) IconMetrics => (_dpi, TrayIconLoader.MetricSizeForDpi(_dpi));

    /// <summary>The directory the icon assets were loaded from, or null when none was found.</summary>
    public string? IconDirectory => _icons.IconDirectory;

    /// <summary>The window pair this host runs on.</summary>
    internal TrayWindow Window => _window;

    /// <inheritdoc />
    public ValueTask ShowAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _window.InvokeAsync(AddIcon, ct);
    }

    /// <inheritdoc />
    public ValueTask HideAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _window.InvokeAsync(RemoveIcon, ct);
    }

    /// <inheritdoc />
    public ValueTask SetTooltipAsync(string tooltip, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _window.InvokeAsync(
            () =>
            {
                _tooltip = tooltip.Length > TooltipLimit ? tooltip[..TooltipLimit] : tooltip;
                if (_added)
                {
                    ModifyIcon(tooltipOnly: true);
                }
            },
            ct);
    }

    /// <inheritdoc />
    public ValueTask SetIconAsync(TrayIconVariant variant, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _window.InvokeAsync(
            () =>
            {
                _variant = variant;
                ReloadIcon();
            },
            ct);
    }

    /// <summary>
    /// Stores the entries the icon's own secondary activation presents, without presenting
    /// a menu now.
    /// </summary>
    /// <param name="items">
    /// The entries, in order, flat. Use <see cref="TrayMenuItem.Separator"/> for a rule.
    /// </param>
    /// <param name="ct">Cancels the call before the entries are stored.</param>
    /// <remarks>
    /// A right click is handled inside this host, on the message loop thread, because
    /// tracking a menu is synchronous and modal and there is no useful event to raise first.
    /// It presents whatever entries were stored last, and <see cref="ShowMenuAsync"/> is the
    /// only thing that stores them — which would mean a composition root had to pop a menu
    /// at start-up before the first right click could work. This stores without presenting.
    /// </remarks>
    public ValueTask SetMenuAsync(IReadOnlyList<TrayMenuItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _window.InvokeAsync(() => _menuItems = items, ct);
    }

    /// <inheritdoc />
    public ValueTask ShowMenuAsync(IReadOnlyList<TrayMenuItem> items, PixelRect? anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The menu is tracked modally on the message loop thread, so the work item
        // reports completion as soon as the menu is up rather than when it closes.
        var handed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _window.Post(() =>
        {
            _menuItems = items;
            try
            {
                TrackMenu(items, anchor, handed);
            }
            catch (Exception ex)
            {
                _ = handed.TrySetException(ex);
            }
        });

        return new ValueTask(handed.Task);
    }

    /// <summary>
    /// Removes the icon, destroys the windows and stops the message loop. After this
    /// returns the notification area holds no ghost icon, because the removal happens
    /// while the owning window is still alive.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        try
        {
            // Blocking is correct here: the icon must be gone before the window is.
            _window.InvokeAsync(RemoveIcon).AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop already stopped; the icon dies with the window.
        }

        _window.MessageReceived -= OnMessage;
        _window.Dispose();

        if (_icon != IntPtr.Zero && !_iconIsShared)
        {
            _ = NativeMethods.DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;

        _icons.Dispose();
    }

    /// <summary>
    /// Asks the shell where the icon is.
    /// </summary>
    /// <returns>
    /// The icon rectangle in physical pixels; the click or cursor position when the
    /// shell reports the icon with no usable geometry; or <see langword="null"/> when
    /// the icon is not individually visible, which covers both a refused query and an
    /// icon inside the Windows 11 overflow flyout — see <see cref="TrayOverflow"/> for
    /// why those are not the same thing. A null is the caller's signal to drop to the
    /// next positioning tier, never a rectangle at the origin.
    /// </returns>
    internal unsafe PixelRect? ResolveAnchor(POINT? clickPoint)
    {
        var identifier = default(NOTIFYICONIDENTIFIER);
        identifier.cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER);
        identifier.hWnd = _window.IconWindow;
        identifier.uID = IconId;

        RECT rect;
        int hr = NativeMethods.Shell_NotifyIconGetRect(&identifier, &rect);
        _lastAnchorHResult = hr;

        if (hr != NativeMethods.S_OK)
        {
            return null;
        }

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width > 0 && height > 0)
        {
            // A successful call is not proof that the icon is visible. For an icon in
            // the Windows 11 overflow the shell answers with the overflow chevron's
            // rectangle, which would anchor the popup to somebody else's button.
            return _overflow.IsInOverflow(rect) ? null : new PixelRect(rect.left, rect.top, width, height);
        }

        // The shell answered but has no geometry to give. The click coordinates from
        // the version 4 callback are the next best anchor, then the live cursor.
        POINT point;
        if (clickPoint is { } known)
        {
            point = known;
        }
        else if (!NativeMethods.GetCursorPos(&point))
        {
            return null;
        }

        return new PixelRect(point.x, point.y, 1, 1);
    }

    private static uint MenuFlags(TrayMenuItem item)
    {
        uint flags = NativeMethods.MF_STRING;
        if (!item.IsEnabled)
        {
            flags |= NativeMethods.MF_GRAYED;
        }

        if (item.IsChecked)
        {
            flags |= NativeMethods.MF_CHECKED;
        }

        return flags;
    }

    private unsafe void AddIcon()
    {
        if (_added)
        {
            return;
        }

        EnsureIcon();

        if (!TryAdd())
        {
            // NIM_ADD fails when the shell still holds a registration for this window
            // and id. That happens after a TaskbarCreated broadcast the shell sent
            // without actually discarding the icons, and it used to leave the host
            // believing it had no icon while one was still in the tray. Drop whatever
            // is registered and add once more.
            NOTIFYICONDATAW stale = NewIconData(0);
            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, &stale);

            if (!TryAdd())
            {
                // Explorer is not ready. The TaskbarCreated broadcast brings us back.
                return;
            }
        }

        // Version 4 must be requested after the icon exists, and only then do clicks
        // carry screen coordinates.
        NOTIFYICONDATAW version = NewIconData(0);
        version.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, &version);

        _added = true;
    }

    private unsafe bool TryAdd()
    {
        NOTIFYICONDATAW data = NewIconData(
            NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        return NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, &data);
    }

    private unsafe void RemoveIcon()
    {
        // Unconditional on purpose. Shell_NotifyIcon simply reports false when there is
        // nothing to remove, whereas trusting a cached flag is how an application ends
        // up destroying the owning window with the icon still registered — which is
        // exactly what leaves a ghost icon in the tray until the user hovers over it.
        NOTIFYICONDATAW data = NewIconData(0);
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, &data);
        _added = false;
    }

    private unsafe void ModifyIcon(bool tooltipOnly)
    {
        uint flags = tooltipOnly
            ? NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP
            : NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;

        NOTIFYICONDATAW data = NewIconData(flags);
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, &data);
    }

    private unsafe NOTIFYICONDATAW NewIconData(uint flags)
    {
        var data = default(NOTIFYICONDATAW);
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.hWnd = _window.IconWindow;
        data.uID = IconId;
        data.uFlags = flags;
        data.uCallbackMessage = TrayWindow.WmTrayCallback;
        data.hIcon = _icon;

        ReadOnlySpan<char> tip = _tooltip.Length > TooltipLimit ? _tooltip.AsSpan(0, TooltipLimit) : _tooltip;
        for (int i = 0; i < tip.Length; i++)
        {
            data.szTip[i] = tip[i];
        }

        data.szTip[tip.Length] = 0;
        return data;
    }

    private void EnsureIcon()
    {
        if (_icon == IntPtr.Zero)
        {
            ReloadIcon();
        }
    }

    private void ReloadIcon()
    {
        IntPtr previous = _icon;
        bool previousShared = _iconIsShared;

        _iconIsLight = TrayIconLoader.UsesLightGlyph(_variant);
        _icon = _icons.Load(_iconIsLight, TrayIconLoader.MetricSizeForDpi(_dpi), out _iconIsShared);

        if (_added)
        {
            ModifyIcon(tooltipOnly: false);
        }

        if (previous != IntPtr.Zero && previous != _icon && !previousShared)
        {
            _ = NativeMethods.DestroyIcon(previous);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _window.Post(RefreshForDpi);

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Desktop)
        {
            _window.Post(RefreshForDpi);
        }
    }

    private void RefreshForDpi()
    {
        if (_disposed)
        {
            return;
        }

        uint dpi = TrayIconLoader.CurrentTrayDpi();
        bool dpiMoved = dpi != _dpi;
        _dpi = dpi;

        // Reloaded on a theme change too, because Automatic picks its glyph from the
        // taskbar's light or dark setting. Nothing is decoded when neither the size nor
        // the glyph actually moved: these notifications arrive for wallpaper and colour
        // changes as well, and this process is supposed to be idle.
        if (dpiMoved || TrayIconLoader.UsesLightGlyph(_variant) != _iconIsLight)
        {
            ReloadIcon();
        }
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Message == TrayWindow.WmTrayCallback && message.Window == _window.IconWindow)
        {
            OnTrayCallback(message);
            return;
        }

        if (message.Message == _window.TaskbarCreatedMessage && _window.TaskbarCreatedMessage != 0)
        {
            // Explorer restarted and every notification icon went with it. The
            // registration is rebuilt from scratch, including the version 4 request.
            _added = false;
            RefreshForDpi();
            AddIcon();
            return;
        }

        if (message.Message == NativeMethods.WM_CLOSE && message.Window == _window.IconWindow)
        {
            RemoveIcon();
        }
    }

    private void OnTrayCallback(WindowMessage message)
    {
        uint notification = (uint)(message.LParam.ToInt64() & 0xFFFF);
        var point = new POINT
        {
            x = NativeMethods.LowInt16(message.WParam),
            y = NativeMethods.HighInt16(message.WParam),
        };

        switch (notification)
        {
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
            case NativeMethods.WM_LBUTTONUP:
                Clicked?.Invoke(this, new TrayClickEventArgs(ResolveAnchor(point)));
                break;

            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                if (_menuItems.Count > 0)
                {
                    TrackMenu(_menuItems, ResolveAnchor(point), completion: null);
                }

                break;

            default:
                break;
        }
    }

    private unsafe void TrackMenu(IReadOnlyList<TrayMenuItem> items, PixelRect? anchor, TaskCompletionSource? completion)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            _ = completion?.TrySetResult();
            return;
        }

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                TrayMenuItem item = items[i];
                if (item.IsSeparator)
                {
                    _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                fixed (char* label = item.Label)
                {
                    _ = NativeMethods.AppendMenuW(menu, MenuFlags(item), (UIntPtr)(i + 1), label);
                }
            }

            POINT at;
            if (anchor is { } rect)
            {
                at = new POINT { x = rect.X, y = rect.Y };
            }
            else if (!NativeMethods.GetCursorPos(&at))
            {
                at = default;
            }

            // Windows requires the owner to be foreground, or the menu never closes
            // when the user clicks elsewhere. The message-only window cannot be
            // foreground, which is the second reason the hidden top-level exists.
            _ = NativeMethods.SetForegroundWindow(_window.ShellWindow);
            _ = completion?.TrySetResult();

            uint flags = NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY |
                         NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_LEFTALIGN |
                         NativeMethods.TPM_BOTTOMALIGN;

            int command = NativeMethods.TrackPopupMenuEx(menu, flags, at.x, at.y, _window.ShellWindow, IntPtr.Zero);

            // Documented dance: without this the menu leaves the shell in a state
            // where the next click is swallowed.
            _ = NativeMethods.PostMessageW(_window.ShellWindow, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= items.Count)
            {
                TrayMenuItem chosen = items[command - 1];
                if (!chosen.IsSeparator && chosen.IsEnabled)
                {
                    MenuItemInvoked?.Invoke(this, new TrayMenuItemInvokedEventArgs(chosen.Id));
                }
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
            _ = completion?.TrySetResult();
        }
    }
}

#endif
