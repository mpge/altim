using Altim.Core.Settings;
using Altim.UI.Services;

namespace Altim.App.Services;

/// <summary>
/// The adapter between the interface's <see cref="ISettingsStore"/> and whatever is
/// actually storing settings, and the one place a settings change is announced.
/// </summary>
/// <remarks>
/// <para>
/// <c>Altim.UI</c> declares <see cref="ISettingsStore"/> because it must not reference
/// <c>Altim.Storage</c>; the shape is deliberately the same as
/// <c>SqliteSettingsStore</c>'s, so the adapter is a forward plus a notification.
/// </para>
/// <para>
/// The notification is the point. The settings page has no apply button: a toggle saves
/// itself, and several things outside the page have to move with it — the application theme,
/// the Run key registration, the scheduler's cadence, and the thresholds the popup's meters
/// tick at. Making the save the event source means none of them can be missed, including
/// when the save comes from somewhere other than the settings page.
/// </para>
/// </remarks>
internal sealed class SettingsGateway : ISettingsStore
{
    private readonly ISettingsBackend _backend;
    private readonly Lock _gate = new();
    private AltimSettings _current = AltimSettings.Default;

    /// <summary>Creates the gateway over a backend.</summary>
    /// <param name="backend">Where settings are read from and written to.</param>
    public SettingsGateway(ISettingsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    /// <summary>
    /// Raised after the stored settings have changed, on whichever thread saved them.
    /// Subscribers that touch the interface marshal themselves.
    /// </summary>
    public event EventHandler<AltimSettings>? Changed;

    /// <summary>The last settings read or written. <c>AltimSettings.Default</c> until the first read.</summary>
    public AltimSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<AltimSettings> GetAsync(CancellationToken ct)
    {
        AltimSettings settings = await _backend.GetAsync(ct).ConfigureAwait(false);
        Publish(settings);
        return settings;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A failed write is not swallowed: the settings page reports "Settings could not be
    /// saved" from the exception, and swallowing it here would replace that with a control
    /// that silently forgets.
    /// </remarks>
    public async ValueTask SaveAsync(AltimSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _backend.SaveAsync(settings, ct).ConfigureAwait(false);
        Publish(settings);
    }

    private void Publish(AltimSettings settings)
    {
        lock (_gate)
        {
            if (_current == settings)
            {
                return;
            }

            _current = settings;
        }

        Changed?.Invoke(this, settings);
    }
}
