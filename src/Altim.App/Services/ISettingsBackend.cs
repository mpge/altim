using Altim.Core.Settings;

namespace Altim.App.Services;

/// <summary>
/// Where the one settings record actually lives.
/// </summary>
/// <remarks>
/// <c>Altim.Storage</c>'s <c>SqliteSettingsStore</c> is the real implementation and
/// <see cref="MemorySettingsBackend"/> is what a machine with an unreadable database falls
/// back to. Both are wrapped by <see cref="SettingsGateway"/>, which is the single
/// <c>ISettingsStore</c> the interface sees.
/// </remarks>
internal interface ISettingsBackend
{
    /// <summary>Reads the stored settings, falling back to defaults for anything unset.</summary>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<AltimSettings> GetAsync(CancellationToken ct);

    /// <summary>Writes the whole settings record.</summary>
    /// <param name="settings">The values to persist.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask SaveAsync(AltimSettings settings, CancellationToken ct);
}
