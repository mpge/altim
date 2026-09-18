using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace Altim.UI.Controls;

/// <summary>
/// What an assistive technology is handed in place of the dial's drawn face.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dial"/> draws itself rather than assembling a template, so without a peer it is
/// a rectangle: no name, no reading, and nothing for a client that asks what has the focus to
/// answer with. That omission was shipped once already, on the meter, and is the reason
/// <see cref="MeterAutomationPeer"/> exists; this peer is its twin, so the two instruments in
/// the design system are announced the same way.
/// </para>
/// <para>
/// A view that names the dial through <c>AutomationProperties.Name</c> is not overruled: the
/// name it set is read first and the reading follows it, so "Session (Claude Code)" becomes
/// "Session (Claude Code), 62% used, threshold 80%". That matters more here than on the
/// meter, because the dial shows one window out of several and the name is what says which.
/// </para>
/// <para>
/// <b>There is deliberately no range value pattern.</b> That pattern carries a
/// <see cref="double"/>, and this control's whole position is that an unreported reading is
/// not a zero: a client reading the value of a dial with nothing to report would be handed
/// one, with no way to say the difference. The reading says "Not reported by this provider"
/// in words instead, which is the same thing the face says by drawing an outline rather than
/// a sweep sitting at the bottom of the scale.
/// </para>
/// </remarks>
public sealed class DialAutomationPeer : ControlAutomationPeer
{
    private string? _announced;

    /// <summary>Initializes a peer over one dial.</summary>
    /// <param name="owner">The dial the peer speaks for.</param>
    public DialAutomationPeer(Dial owner)
        : base(owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        owner.PropertyChanged += OnOwnerPropertyChanged;
        _announced = owner.Reading;
    }

    private Dial Gauge => (Dial)Owner;

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.ProgressBar;

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(Dial);

    /// <inheritdoc />
    protected override string? GetNameCore() =>
        base.GetNameCore() is { Length: > 0 } named
            ? string.Concat(named, ", ", Gauge.Reading)
            : Gauge.Reading;

    /// <summary>
    /// Tells the client the reading changed, by the one route the platform offers for an
    /// element with no value pattern: the name of the element has changed.
    /// </summary>
    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != Dial.ValueProperty && change.Property != Dial.ThresholdProperty)
        {
            return;
        }

        string? previous = _announced;
        _announced = GetName();

        if (!string.Equals(previous, _announced, StringComparison.Ordinal))
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, previous, _announced);
        }
    }
}
