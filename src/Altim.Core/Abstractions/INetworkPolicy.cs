namespace Altim.Core.Abstractions;

/// <summary>
/// Whether providers are currently permitted to make the one call each of them has that
/// leaves the machine.
/// </summary>
/// <remarks>
/// <para>
/// This is a live reading, not a construction-time flag, and that is the whole point of it
/// existing. Provider options are immutable records taken once in a constructor, so a
/// permission carried there is fixed for the life of the provider and a user switching the
/// setting off would go on reaching the vendor's CLI until Altim was restarted. Providers
/// hold one of these instead and ask it at the moment of the call.
/// </para>
/// <para>
/// It composes with, rather than replaces, the option: a provider makes the call only when
/// its own options allow it <em>and</em> the policy does. That keeps a test or an embedder
/// that constructed a provider with network calls switched off switched off, whatever the
/// user's setting says.
/// </para>
/// <para>
/// Separate from <see cref="IRefreshGate"/> on purpose. The gate answers "not yet", which is
/// temporary and worth saying so in a status line; this answers "not at all", which the user
/// chose and which the interface reports differently.
/// </para>
/// <para>Implementations are safe to read from any thread.</para>
/// </remarks>
public interface INetworkPolicy
{
    /// <summary>
    /// True when a provider may make a call that reaches the vendor. False is strict
    /// local-only mode: the provider reads its local files, reports what they contain, and
    /// says the figures are local rather than estimating the rest.
    /// </summary>
    bool AllowsNetworkCalls { get; }
}
