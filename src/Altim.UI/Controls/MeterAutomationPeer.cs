using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace Altim.UI.Controls;

/// <summary>
/// What an assistive technology is handed in place of the meter's drawn rail.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Meter"/> draws itself rather than assembling a template, so without a peer it
/// is a rectangle: no name, no reading, and nothing for a client that asks what has the
/// focus to answer with. This peer gives it the one thing it knows -
/// <see cref="Meter.Reading"/> - and keeps it current.
/// </para>
/// <para>
/// A view that names the meter through <c>AutomationProperties.Name</c> is not overruled:
/// the name it set is read first and the reading follows it, so "Session" becomes
/// "Session, 62% used, threshold 80%". A meter nobody has named reads as the reading alone,
/// because the figure and the label beside it are the row's own elements and a screen reader
/// reaches them on their own.
/// </para>
/// <para>
/// <b>There is deliberately no range value pattern.</b> That pattern carries a
/// <see cref="double"/>, and this control's whole position is that an unreported metric is
/// not a zero: a client reading the value of a meter with nothing to report would be handed
/// one, with no way to say the difference. The reading says "Not reported by this provider"
/// in words instead, which is the same thing the rail says by drawing an outline rather than
/// an empty track.
/// </para>
/// </remarks>
public sealed class MeterAutomationPeer : ControlAutomationPeer
{
    private string? _announced;

    /// <summary>Initializes a peer over one meter.</summary>
    /// <param name="owner">The meter the peer speaks for.</param>
    public MeterAutomationPeer(Meter owner)
        : base(owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        owner.PropertyChanged += OnOwnerPropertyChanged;
        _announced = owner.Reading;
    }

    private Meter Gauge => (Meter)Owner;

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.ProgressBar;

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(Meter);

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
        if (change.Property != Meter.ValueProperty && change.Property != Meter.ThresholdProperty)
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
