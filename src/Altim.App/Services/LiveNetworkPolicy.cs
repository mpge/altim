using Altim.Core.Abstractions;

namespace Altim.App.Services;

/// <summary>
/// The user's "allow network calls" setting, as providers read it: now, not as it was when
/// they were constructed.
/// </summary>
/// <remarks>
/// <para>
/// The same shape, and for the same reason, as <see cref="SchedulerNetworkGate"/>. Providers
/// take their options once, in their constructor, and those options are immutable records;
/// a permission carried there is fixed for the life of the provider. Rebuilding both
/// providers on a settings change would mean discarding their incremental scan cursors and
/// re-walking session stores measured at 28.3 GB, so the permission moves behind one level
/// of indirection instead and the providers stay exactly where they are.
/// </para>
/// <para>
/// Reads and writes are volatile rather than locked: this is a single flag, a provider reads
/// it on the tick that is about to make the call, and a change that lands a microsecond
/// after that read is honoured by the next tick a second later.
/// </para>
/// </remarks>
internal sealed class LiveNetworkPolicy : INetworkPolicy
{
    private volatile bool _allowed = true;

    /// <inheritdoc />
    public bool AllowsNetworkCalls => _allowed;

    /// <summary>Applies the stored setting.</summary>
    /// <param name="allowed">True when providers may reach the vendor.</param>
    public void Set(bool allowed) => _allowed = allowed;
}
