#if WINDOWS

using Microsoft.Win32;

namespace Altim.Platform.Windows.Interop;

/// <summary>
/// Decides whether the notification icon is actually visible in the tray or is sitting
/// in the Windows 11 overflow flyout.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>Shell_NotifyIconGetRect</c> does not answer the question.
/// Measured on Windows 11 build 26200, with an icon the user has never promoted:
/// </para>
/// <list type="bullet">
/// <item><description>
/// flyout closed — the call returns <c>S_OK</c> and the rectangle of the <em>overflow
/// chevron</em>, which is indistinguishable by geometry from a promoted icon's own
/// rectangle;
/// </description></item>
/// <item><description>
/// flyout open — the call returns <c>S_OK</c> and the icon's rectangle inside the
/// flyout window, whose root window class is
/// <c>TopLevelWindowForOverflowXamlIsland</c>;
/// </description></item>
/// <item><description>
/// icon promoted — the call returns <c>S_OK</c> and a rectangle whose root window is
/// <c>Shell_TrayWnd</c>, the taskbar itself.
/// </description></item>
/// </list>
/// <para>
/// So the failure this class has to catch is the first case: a successful call that
/// hands back somebody else's rectangle. Two independent checks cover it — the
/// per-icon promotion state Windows 11 keeps under
/// <c>Control Panel\NotifyIconSettings</c>, and a hit test of the returned rectangle
/// against the overflow flyout window. Neither is load bearing on its own: the
/// registry key does not exist on Windows 10, and the hit test only sees the flyout
/// while it is open.
/// </para>
/// <para>
/// On Windows 11 the registry is decisive, including when it holds no entry for the
/// icon at all: the shell writes <c>IsPromoted</c> only when something promotes an
/// icon, so no entry means never promoted, and Windows 11 starts every new icon in the
/// overflow. On Windows 10 the key does not exist, the hit test is the only check, and
/// an icon whose rectangle is not inside a flyout is taken at face value — which is
/// what every tray application did before the flyout existed.
/// </para>
/// </remarks>
internal sealed unsafe class TrayOverflow
{
    private const string SettingsKeyPath = @"Control Panel\NotifyIconSettings";

    /// <summary>Windows 11 22H2 and later: the XAML overflow flyout.</summary>
    private const string OverflowFlyoutClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>Windows 10 and early Windows 11: the legacy overflow window.</summary>
    private const string LegacyOverflowClass = "NotifyIconOverflowWindow";

    private readonly string? _executablePath;
    private readonly uint _iconId;
    private string? _settingsSubKey;

    /// <summary>
    /// Creates the detector for one icon.
    /// </summary>
    /// <param name="iconId">The <c>uID</c> the icon was registered with.</param>
    /// <param name="executablePath">
    /// The executable Windows recorded the icon against, or <see langword="null"/> for
    /// the running process.
    /// </param>
    public TrayOverflow(uint iconId, string? executablePath = null)
    {
        _iconId = iconId;
        _executablePath = executablePath ?? Environment.ProcessPath;
    }

    /// <summary>
    /// True when the rectangle the shell returned does not belong to a visible icon.
    /// </summary>
    /// <param name="rect">The rectangle <c>Shell_NotifyIconGetRect</c> returned.</param>
    /// <returns>
    /// True when the icon is in the overflow, false when it is in the visible tray or
    /// when Windows will not say.
    /// </returns>
    public bool IsInOverflow(RECT rect)
    {
        if (IsPromoted() == false)
        {
            return true;
        }

        return RootWindowClassAt((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2) switch
        {
            OverflowFlyoutClass or LegacyOverflowClass => true,
            _ => false,
        };
    }

    /// <summary>
    /// Reads the promotion state Windows 11 records for this icon.
    /// </summary>
    /// <returns>
    /// True when the user, or Windows, has promoted the icon to the taskbar; false when
    /// it is in the overflow; <see langword="null"/> when this version of Windows keeps
    /// no such record, which is the case on Windows 10.
    /// </returns>
    /// <remarks>
    /// An entry that is missing altogether counts as not promoted. Windows 11 writes
    /// the entry the first time the shell sees an icon and only ever writes
    /// <c>IsPromoted</c> when something promotes it, so "no record" and "never promoted"
    /// are the same state — and a brand new icon on Windows 11 starts in the overflow.
    /// </remarks>
    public bool? IsPromoted()
    {
        if (string.IsNullOrEmpty(_executablePath))
        {
            return null;
        }

        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            if (settings is null)
            {
                return null;
            }

            if (_settingsSubKey is not null)
            {
                bool? cached = ReadPromotion(settings, _settingsSubKey);
                if (cached is not null)
                {
                    return cached;
                }

                _settingsSubKey = null;
            }

            foreach (string name in settings.GetSubKeyNames())
            {
                bool? promoted = ReadPromotion(settings, name);
                if (promoted is not null)
                {
                    _settingsSubKey = name;
                    return promoted;
                }
            }

            // The key exists, so this is Windows 11 and the shell keeps promotion
            // records — it simply has none for this icon yet. That is the state of
            // every icon Windows 11 has just put into the overflow.
            return false;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No record is the same answer as an unreadable record.
        }

        return null;
    }

    private static string RootWindowClassAt(int x, int y)
    {
        IntPtr window = NativeMethods.WindowFromPoint(new POINT { x = x, y = y });
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        IntPtr root = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = window;
        }

        char* buffer = stackalloc char[128];
        int length = NativeMethods.GetClassNameW(root, buffer, 128);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    /// <summary>
    /// Reads one <c>NotifyIconSettings</c> entry, if it is this icon's.
    /// </summary>
    /// <param name="settings">The open settings key.</param>
    /// <param name="name">The sub key to read.</param>
    /// <returns>
    /// The promotion state, or <see langword="null"/> when the entry belongs to another
    /// icon or another executable.
    /// </returns>
    private bool? ReadPromotion(RegistryKey settings, string name)
    {
        using RegistryKey? entry = settings.OpenSubKey(name, writable: false);
        if (entry?.GetValue("ExecutablePath") is not string path ||
            !string.Equals(path, _executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The entry is keyed by executable and icon id together, so a process with more
        // than one icon has one entry per icon.
        if (entry.GetValue("UID") is int uid && (uint)uid != _iconId)
        {
            return null;
        }

        // The value is absent until the shell has had a reason to write it, and an
        // absent value means the icon is in the overflow: that is the Windows 11
        // default for an icon the user has never promoted.
        return entry.GetValue("IsPromoted") is int promoted && promoted != 0;
    }
}

#endif
