using Altim.Core.Settings;

namespace Altim.UI.Services;

/// <summary>
/// Reads and writes the one settings record the interface edits.
/// </summary>
/// <remarks>
/// <para>
/// <c>Altim.Core</c> defines <see cref="AltimSettings"/> but no abstraction for loading or
/// saving it, and <c>Altim.UI</c> must not reference <c>Altim.Storage</c>. So the seam is
/// declared here, deliberately shaped like <c>SqliteSettingsStore</c>'s two methods, and the
/// composition root adapts the storage type onto it.
/// </para>
/// <para>
/// Both calls may touch the disk, so both are asynchronous and view models never invoke them
/// on the dispatcher thread.
/// </para>
/// </remarks>
public interface ISettingsStore
{
    /// <summary>Reads the stored settings, falling back to defaults for anything unset.</summary>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<AltimSettings> GetAsync(CancellationToken ct);

    /// <summary>Writes the whole settings record.</summary>
    /// <param name="settings">The values to persist.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask SaveAsync(AltimSettings settings, CancellationToken ct);
}
