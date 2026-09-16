using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.MacOS.Interop;

namespace Altim.Platform.MacOS;

/// <summary>
/// The macOS menu bar presence: a real <c>NSStatusItem</c> driven through
/// <c>objc_msgSend</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified. None of the interop in this class has ever run.</b> There is no macOS
/// host in the development environment. What follows is written from Apple's headers and
/// the Objective-C ABI, is isolated behind <see cref="ITrayHost"/> exactly so it can be
/// replaced, and degrades to "no tray" rather than throwing when anything is missing.
/// ARCHITECTURE.md carries the same warning as a named risk.
/// </para>
/// <para>
/// <b>Why not Avalonia's <c>TrayIcon</c>.</b> On macOS Avalonia's tray icon can only
/// present a native menu: its native interface exposes no click callback and no handle to
/// the status item, so there is no way to learn that the icon was clicked or where it is.
/// Altim's whole interaction model is a panel positioned against the icon, so the status
/// item has to be owned here. Avalonia's tray icon plus a native menu remains the
/// documented fallback if this proves unworkable on a real Mac.
/// </para>
/// <para>
/// <b>Threading.</b> Every <c>NSStatusItem</c> touch is marshalled to the Cocoa main thread
/// through <see cref="ObjCCallbackTarget"/>, which is why every mutator on
/// <see cref="ITrayHost"/> is asynchronous. A caller already on the main thread runs
/// inline.
/// </para>
/// <para>
/// <b>Clicks.</b> The status item's <c>menu</c> property is deliberately left nil, because
/// setting it makes AppKit swallow every click and present the menu — including the left
/// click that is supposed to open the panel. Instead the button's action is wired to this
/// host and <c>sendActionOn:</c> is widened to right mouse up, and the handler asks
/// <c>NSApp.currentEvent</c> which button it was. A secondary click assigns the menu just
/// long enough to present it and then clears it again.
/// </para>
/// </remarks>
public sealed class MacOSTrayHost : ITrayHost, IObjCCallbackSink
{
    /// <summary>The menu bar truncates long tooltips itself; this keeps the string sane.</summary>
    private const int TooltipLimit = 255;

    private readonly ObjCCallbackTarget? _target;
    private readonly string? _assetDirectory;
    private readonly Lock _stateGate = new();

    private IReadOnlyList<TrayMenuItem> _menuItems = [];
    private IntPtr _statusItem;
    private IntPtr _button;
    private IntPtr _image;
    private IntPtr _menu;
    private string _tooltip;
    private int _imagePixelSize;
    private volatile bool _visible;
    private bool _disposed;

    /// <summary>
    /// Creates the host. Nothing is added to the menu bar until <see cref="ShowAsync"/> is
    /// called.
    /// </summary>
    /// <param name="tooltip">Initial hover text.</param>
    /// <param name="assetDirectory">
    /// Directory holding <c>altim-template-*.png</c>, or <see langword="null"/> to discover
    /// it from the bundle and then by walking up from the application directory.
    /// </param>
    public MacOSTrayHost(string tooltip = "Altim", string? assetDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        _tooltip = Truncate(tooltip);
        _assetDirectory = assetDirectory ??
            MacOSTrayAssets.DiscoverAssetDirectory(Environment.ProcessPath, AppContext.BaseDirectory);

        // Null when the Objective-C runtime is not there, which is every non-macOS host.
        // The host then answers every call as a no-op and reports IsVisible false.
        _target = OperatingSystem.IsMacOS() ? ObjCCallbackTarget.TryCreate(this) : null;
    }

    /// <inheritdoc />
    public event EventHandler<TrayClickEventArgs>? Clicked;

    /// <inheritdoc />
    public event EventHandler<TrayMenuItemInvokedEventArgs>? MenuItemInvoked;

    /// <inheritdoc />
    public bool IsVisible => _visible;

    /// <summary>
    /// True when the Objective-C runtime was reachable and the callback class registered.
    /// False means Altim runs without a menu bar presence; it is never an error.
    /// </summary>
    public bool IsSupported => _target is not null;

    /// <summary>The directory the template assets were found in, or null when none was.</summary>
    public string? AssetDirectory => _assetDirectory;

    /// <summary>
    /// The callback target, so the platform service can share one main-thread pump and one
    /// notification observer object with the tray.
    /// </summary>
    internal ObjCCallbackTarget? Target => _target;

    /// <inheritdoc />
    public ValueTask ShowAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Invoke(
            () =>
            {
                EnsureStatusItem();
                if (_statusItem == IntPtr.Zero)
                {
                    return;
                }

                ObjC.SendVoidBool(_statusItem, ObjC.Selector("setVisible:"), 1);
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
                if (_statusItem != IntPtr.Zero)
                {
                    ObjC.SendVoidBool(_statusItem, ObjC.Selector("setVisible:"), 0);
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
                _tooltip = Truncate(tooltip);
                ApplyTooltip();
            },
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every variant is accepted and the value is ignored. The menu bar icon is a template
    /// image that the system tints from the wallpaper behind it, so there is nothing to
    /// swap — see <see cref="MacOSTrayAssets"/>.
    /// </remarks>
    public ValueTask SetIconAsync(TrayIconVariant variant, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Invoke(ApplyImage, ct);
    }

    /// <summary>
    /// Stores the entries a secondary click should present, without presenting one now.
    /// </summary>
    /// <param name="items">The entries, in order, flat.</param>
    /// <param name="ct">Cancels the call before the entries are stored.</param>
    /// <remarks>
    /// The mirror of <c>WindowsTrayHost.SetMenuAsync</c>: a right click is handled inside
    /// this host, and it needs entries before the first one arrives.
    /// </remarks>
    public ValueTask SetMenuAsync(IReadOnlyList<TrayMenuItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Invoke(() => StoreMenu(items), ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>This presents the menu immediately.</b> Unlike the Linux host — where Avalonia's
    /// tray icon can only attach entries for the desktop to present later — macOS can raise a
    /// menu on demand, so this does. A caller that only wants to <em>store</em> the entries,
    /// which is what a composition root does at start-up, must call
    /// <see cref="SetMenuAsync"/> instead; calling this one there would pop a menu open on
    /// launch with nobody having clicked anything. <c>WindowsTrayHost</c> has exactly the same
    /// split for exactly the same reason.
    /// </para>
    /// <para>
    /// <paramref name="anchor"/> is ignored. AppKit positions a status item's menu under the
    /// item itself and offers no way to place it anywhere else, which is the correct
    /// behaviour here anyway.
    /// </para>
    /// </remarks>
    public ValueTask ShowMenuAsync(
        IReadOnlyList<TrayMenuItem> items, PixelRect? anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Presenting is modal on the main thread, so the call reports completion once the
        // menu has been handed over rather than when the user dismisses it — the contract
        // ITrayHost documents.
        var handed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_target is null)
        {
            StoreMenu(items);
            return ValueTask.CompletedTask;
        }

        _target.Post(() =>
        {
            StoreMenu(items);
            try
            {
                PresentMenu(handed);
            }
            finally
            {
                _ = handed.TrySetResult();
            }
        });

        return new ValueTask(handed.Task);
    }

    /// <summary>
    /// Removes the status item from the menu bar and releases everything retained.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visible = false;

        ObjCCallbackTarget? target = _target;
        if (target is null)
        {
            return;
        }

        // Blocking is correct: the status item must leave the menu bar before the callback
        // target that its action points at is released.
        try
        {
            target.InvokeAsync(RemoveStatusItem).AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The main run loop is already gone; the item dies with the process.
        }

        target.Dispose();
    }

    void IObjCCallbackSink.OnStatusItemClicked(IntPtr sender)
    {
        if (_disposed)
        {
            return;
        }

        if (IsSecondaryClick())
        {
            PresentMenu(completion: null);
            return;
        }

        Clicked?.Invoke(this, new TrayClickEventArgs(GetAnchor()));
    }

    void IObjCCallbackSink.OnMenuItemClicked(IntPtr sender)
    {
        if (_disposed || sender == IntPtr.Zero)
        {
            return;
        }

        long tag = ObjC.SendLong(sender, ObjC.Selector("tag"));
        IReadOnlyList<TrayMenuItem> items = _menuItems;
        if (tag < 0 || tag >= items.Count)
        {
            return;
        }

        TrayMenuItem item = items[(int)tag];
        if (item.IsSeparator || !item.IsEnabled)
        {
            return;
        }

        MenuItemInvoked?.Invoke(this, new TrayMenuItemInvokedEventArgs(item.Id));
    }

    void IObjCCallbackSink.OnNotification(IntPtr notification)
    {
        // The tray host observes nothing itself; the platform service owns the notification
        // centres. Present because one callback target serves both.
    }

    /// <summary>
    /// The status item's rectangle, in physical pixels, top-left origin.
    /// </summary>
    /// <returns>
    /// The rectangle, or <see langword="null"/> when there is no status item, when the
    /// button has no window yet, or when the geometry is not usable — for instance while
    /// the item is hidden because the menu bar has run out of room, which is macOS'
    /// equivalent of the Windows overflow.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The anchor comes from the status <em>button's window</em>, not from the button: a
    /// status item lives in its own small window whose frame is exactly the item's slot in
    /// the menu bar, and that frame is already in global screen coordinates.
    /// </para>
    /// <para>
    /// <b>Unverified</b>, like everything else that touches AppKit here. The Y flip and the
    /// pixel scaling it depends on are in <see cref="MacOSCoordinates"/> and are tested.
    /// </para>
    /// </remarks>
    internal PixelRect? GetAnchor()
    {
        if (_button == IntPtr.Zero)
        {
            return null;
        }

        IntPtr window = ObjC.Send(_button, ObjC.Selector("window"));
        if (window == IntPtr.Zero)
        {
            return null;
        }

        CGRect frame = ObjC.SendRect(window, ObjC.Selector("frame"));
        double scale = ObjC.SendDouble(window, ObjC.Selector("backingScaleFactor"));
        double primaryHeight = PrimaryScreenHeight();

        return MacOSCoordinates.ToPixelRect(
            frame.Origin.X, frame.Origin.Y, frame.Size.Width, frame.Size.Height, primaryHeight, scale);
    }

    /// <summary>
    /// Height in points of the display Cocoa measures its global coordinates from.
    /// </summary>
    /// <returns>Zero when no screen can be read, which makes the anchor null.</returns>
    private static double PrimaryScreenHeight()
    {
        IntPtr screenClass = ObjC.Class("NSScreen");
        if (screenClass == IntPtr.Zero)
        {
            return 0;
        }

        IntPtr screens = ObjC.Send(screenClass, ObjC.Selector("screens"));
        if (screens == IntPtr.Zero || ObjC.SendUnsignedLong(screens, ObjC.Selector("count")) == 0)
        {
            return 0;
        }

        IntPtr primary = ObjC.SendIndex(screens, ObjC.Selector("objectAtIndex:"), 0);
        if (primary == IntPtr.Zero)
        {
            return 0;
        }

        return ObjC.SendRect(primary, ObjC.Selector("frame")).Size.Height;
    }

    /// <summary>
    /// True when the click that is being handled was a right click or a control-click.
    /// </summary>
    private static bool IsSecondaryClick()
    {
        IntPtr application = ObjC.Class("NSApplication");
        if (application == IntPtr.Zero)
        {
            return false;
        }

        IntPtr app = ObjC.Send(application, ObjC.Selector("sharedApplication"));
        IntPtr currentEvent = app == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(app, ObjC.Selector("currentEvent"));
        if (currentEvent == IntPtr.Zero)
        {
            return false;
        }

        if (ObjC.SendLong(currentEvent, ObjC.Selector("type")) == ObjC.EventTypeRightMouseUp)
        {
            return true;
        }

        nuint modifiers = ObjC.SendUnsignedLong(currentEvent, ObjC.Selector("modifierFlags"));
        return (modifiers & ObjC.EventModifierFlagControl) != 0;
    }

    private static string Truncate(string value) =>
        value.Length > TooltipLimit ? value[..TooltipLimit] : value;

    private ValueTask Invoke(Action work, CancellationToken ct)
    {
        ObjCCallbackTarget? target = _target;
        if (target is null)
        {
            // No Objective-C runtime: accept the call and do nothing, which is what
            // "degrades, never throws" means for a host with no menu bar to write to.
            return ValueTask.CompletedTask;
        }

        return target.InvokeAsync(work, ct);
    }

    private void StoreMenu(IReadOnlyList<TrayMenuItem> items)
    {
        lock (_stateGate)
        {
            _menuItems = items;
        }

        RebuildMenu(items);
    }

    private void EnsureStatusItem()
    {
        if (_statusItem != IntPtr.Zero || _target is null)
        {
            return;
        }

        IntPtr statusBarClass = ObjC.Class("NSStatusBar");
        if (statusBarClass == IntPtr.Zero)
        {
            return;
        }

        IntPtr bar = ObjC.Send(statusBarClass, ObjC.Selector("systemStatusBar"));
        if (bar == IntPtr.Zero)
        {
            return;
        }

        IntPtr item = ObjC.SendDoubleArgument(
            bar, ObjC.Selector("statusItemWithLength:"), ObjC.VariableStatusItemLength);
        if (item == IntPtr.Zero)
        {
            return;
        }

        // statusItemWithLength: hands back an object owned by the status bar's autorelease
        // pool. Retaining it is what keeps the item in the menu bar for the life of Altim.
        _statusItem = ObjC.Retain(item);
        _button = ObjC.Send(_statusItem, ObjC.Selector("button"));

        if (_button != IntPtr.Zero)
        {
            ObjC.Send(_button, ObjC.Selector("setTarget:"), _target.Handle);
            ObjC.Send(_button, ObjC.Selector("setAction:"), ObjCCallbackTarget.StatusClickedSelector);

            // Without this only a left click fires the action, and the right click that is
            // supposed to raise the menu is never seen at all.
            ObjC.SendVoidMask(
                _button,
                ObjC.Selector("sendActionOn:"),
                ObjC.EventMaskLeftMouseUp | ObjC.EventMaskRightMouseUp);
        }

        ApplyImage();
        ApplyTooltip();
    }

    private void RemoveStatusItem()
    {
        if (_statusItem != IntPtr.Zero)
        {
            IntPtr statusBarClass = ObjC.Class("NSStatusBar");
            IntPtr bar = statusBarClass == IntPtr.Zero
                ? IntPtr.Zero
                : ObjC.Send(statusBarClass, ObjC.Selector("systemStatusBar"));

            if (_button != IntPtr.Zero)
            {
                // Drop the action before the target is released, so a click that is already
                // in flight cannot reach a freed object.
                ObjC.Send(_button, ObjC.Selector("setTarget:"), IntPtr.Zero);
                ObjC.Send(_button, ObjC.Selector("setAction:"), IntPtr.Zero);
            }

            if (bar != IntPtr.Zero)
            {
                ObjC.Send(bar, ObjC.Selector("removeStatusItem:"), _statusItem);
            }

            ObjC.Release(_statusItem);
        }

        _statusItem = IntPtr.Zero;
        _button = IntPtr.Zero;

        ObjC.Release(_image);
        _image = IntPtr.Zero;

        ObjC.Release(_menu);
        _menu = IntPtr.Zero;
    }

    private void ApplyTooltip()
    {
        if (_button == IntPtr.Zero)
        {
            return;
        }

        IntPtr text = ObjC.CreateString(_tooltip);
        try
        {
            ObjC.Send(_button, ObjC.Selector("setToolTip:"), text);
        }
        finally
        {
            ObjC.Release(text);
        }
    }

    private void ApplyImage()
    {
        if (_button == IntPtr.Zero || _assetDirectory is null)
        {
            return;
        }

        double scale = 1d;
        IntPtr window = ObjC.Send(_button, ObjC.Selector("window"));
        if (window != IntPtr.Zero)
        {
            double reported = ObjC.SendDouble(window, ObjC.Selector("backingScaleFactor"));
            if (double.IsFinite(reported) && reported > 0)
            {
                scale = reported;
            }
        }

        int wanted = MacOSTrayAssets.PixelSizeForScale(scale);
        if (_image != IntPtr.Zero && wanted == _imagePixelSize)
        {
            return;
        }

        string? path = MacOSTrayAssets.Resolve(_assetDirectory, wanted);
        if (path is null)
        {
            return;
        }

        IntPtr imageClass = ObjC.Class("NSImage");
        if (imageClass == IntPtr.Zero)
        {
            return;
        }

        IntPtr allocated = ObjC.Send(imageClass, ObjC.Selector("alloc"));
        IntPtr file = ObjC.CreateString(path);
        IntPtr image;
        try
        {
            image = ObjC.Send(allocated, ObjC.Selector("initWithContentsOfFile:"), file);
        }
        finally
        {
            ObjC.Release(file);
        }

        if (image == IntPtr.Zero)
        {
            return;
        }

        // The whole point of the macOS icon: the menu bar owns the colour, not Altim.
        ObjC.SendVoidBool(image, ObjC.Selector("setTemplate:"), 1);
        ObjC.SendVoidSize(
            image,
            ObjC.Selector("setSize:"),
            new CGSize(MacOSTrayAssets.MenuBarPointSize, MacOSTrayAssets.MenuBarPointSize));

        ObjC.Send(_button, ObjC.Selector("setImage:"), image);

        IntPtr previous = _image;
        _image = image;
        _imagePixelSize = wanted;
        ObjC.Release(previous);
    }

    private void RebuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        if (_target is null)
        {
            return;
        }

        IntPtr menuClass = ObjC.Class("NSMenu");
        IntPtr itemClass = ObjC.Class("NSMenuItem");
        if (menuClass == IntPtr.Zero || itemClass == IntPtr.Zero)
        {
            return;
        }

        IntPtr menu = ObjC.Send(ObjC.Send(menuClass, ObjC.Selector("alloc")), ObjC.Selector("init"));
        if (menu == IntPtr.Zero)
        {
            return;
        }

        // AppKit greys an entry out on its own when nothing validates it, which would make
        // every disabled-by-design notice indistinguishable from a live command.
        ObjC.SendVoidBool(menu, ObjC.Selector("setAutoenablesItems:"), 0);

        IntPtr emptyKey = ObjC.CreateString(string.Empty);
        try
        {
            for (int index = 0; index < items.Count; index++)
            {
                TrayMenuItem entry = items[index];
                if (entry.IsSeparator)
                {
                    IntPtr separator = ObjC.Send(itemClass, ObjC.Selector("separatorItem"));
                    if (separator != IntPtr.Zero)
                    {
                        ObjC.Send(menu, ObjC.Selector("addItem:"), separator);
                    }

                    continue;
                }

                IntPtr title = ObjC.CreateString(entry.Label);
                IntPtr allocated = ObjC.Send(itemClass, ObjC.Selector("alloc"));
                IntPtr menuItem;
                try
                {
                    menuItem = ObjC.Send(
                        allocated,
                        ObjC.Selector("initWithTitle:action:keyEquivalent:"),
                        title,
                        ObjCCallbackTarget.MenuClickedSelector,
                        emptyKey);
                }
                finally
                {
                    ObjC.Release(title);
                }

                if (menuItem == IntPtr.Zero)
                {
                    continue;
                }

                ObjC.Send(menuItem, ObjC.Selector("setTarget:"), _target.Handle);
                ObjC.SendVoidBool(menuItem, ObjC.Selector("setEnabled:"), entry.IsEnabled ? (byte)1 : (byte)0);
                ObjC.SendVoidLong(
                    menuItem,
                    ObjC.Selector("setState:"),
                    entry.IsChecked ? ObjC.ControlStateOn : ObjC.ControlStateOff);

                // The tag is the index into the list this menu was built from, which is what
                // the click handler maps back to an id.
                ObjC.SendVoidLong(menuItem, ObjC.Selector("setTag:"), index);

                ObjC.Send(menu, ObjC.Selector("addItem:"), menuItem);
                ObjC.Release(menuItem);
            }
        }
        finally
        {
            ObjC.Release(emptyKey);
        }

        IntPtr previousMenu = _menu;
        _menu = menu;
        ObjC.Release(previousMenu);
    }

    private void PresentMenu(TaskCompletionSource? completion)
    {
        if (_statusItem == IntPtr.Zero || _menu == IntPtr.Zero || _button == IntPtr.Zero)
        {
            _ = completion?.TrySetResult();
            return;
        }

        // Assign, click, clear. Leaving the menu assigned would make AppKit present it for
        // the left click too, and the panel would never open.
        ObjC.Send(_statusItem, ObjC.Selector("setMenu:"), _menu);
        _ = completion?.TrySetResult();

        try
        {
            ObjC.Send(_button, ObjC.Selector("performClick:"), IntPtr.Zero);
        }
        finally
        {
            ObjC.Send(_statusItem, ObjC.Selector("setMenu:"), IntPtr.Zero);
        }
    }
}
