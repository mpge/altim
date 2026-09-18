using Avalonia;
using Avalonia.Controls;

namespace Altim.UI.Controls;

/// <summary>
/// The words beside a <see cref="Dial"/>: one row per ring, in the rings' own order, each
/// naming the provider that ring belongs to and printing its figure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The legend takes its rows from the face, not from a second binding.</b> It is handed
/// the dial and reads <see cref="Dial.Arcs"/>; a view that bound the legend to the same list
/// it handed the dial would be keeping two copies of one truth, and two copies can be handed
/// two lists. The order, the count and the ring numbers are then the face's by construction.
/// </para>
/// <para>
/// <b>One mark, one state.</b> Pointing at a row, or arriving on it with the keyboard, tells
/// the <em>dial</em> which reading is being read; the dial resolves that against its own
/// pointer and keyboard and publishes one answer in <see cref="Dial.Marked"/>; and every row
/// here marks itself from that answer. Nothing is mirrored. A row cannot light up while the
/// face traces a different band, because the row is not the thing that decided.
/// </para>
/// <para>
/// The legend arbitrates its own two inputs before telling the dial, so a pointer moving off
/// a row falls back to the row the keyboard is still on rather than to nothing. That is the
/// same shape of precedence the dial applies to its own two.
/// </para>
/// <para>
/// A face carrying one reading has no legend. <c>docs/DESIGN.md</c> puts one wherever a dial
/// carries <em>more than one</em> reading: with a single ring the words under the figure have
/// already named it, and a legend would be the same line printed twice with nothing else on
/// the face to tell it apart from.
/// </para>
/// </remarks>
public sealed class DialLegend : ItemsControl
{
    /// <summary>The dial this legend names the rings of.</summary>
    public static readonly StyledProperty<Dial?> FaceProperty =
        AvaloniaProperty.Register<DialLegend, Dial?>(nameof(Face));

    private readonly List<DialLegendRow> _rows = [];

    private Dial? _listening;
    private DialLegendRow? _pointed;
    private DialLegendRow? _arrived;

    static DialLegend()
    {
        // A templated control clips to its own bounds, and a row draws its mark and its focus
        // ring outside its own. Left on, the first row keeps its lower edge and loses its
        // upper one, which reads as a rule under the row rather than as the row being marked
        // - and every property involved still reads correctly while it happens.
        ClipToBoundsProperty.OverrideDefaultValue<DialLegend>(false);
    }

    /// <inheritdoc cref="FaceProperty" />
    public Dial? Face
    {
        get => GetValue(FaceProperty);
        set => SetValue(FaceProperty, value);
    }

    /// <summary>How many rows the legend is showing.</summary>
    public int RowCount => _rows.Count;

    /// <summary>
    /// The plain items control's own theme, which is where the presenter comes from.
    /// </summary>
    /// <remarks>
    /// A control theme is keyed on a type, and a derived type does not inherit one: without
    /// this the legend has no template, no items presenter, and lays out at nothing at all
    /// while every property on it still reads correctly.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FaceProperty)
        {
            Listen();
            TakeRowsFromTheFace();
        }
    }

    /// <summary>Takes note that a row is on screen, and marks it if it is the one.</summary>
    /// <param name="row">The row that arrived.</param>
    internal void Attach(DialLegendRow row)
    {
        if (!_rows.Contains(row))
        {
            _rows.Add(row);
        }
    }

    /// <summary>Forgets a row that has gone, and gives up anything it was holding.</summary>
    /// <param name="row">The row that left.</param>
    internal void Detach(DialLegendRow row)
    {
        _rows.Remove(row);

        bool held = ReferenceEquals(_pointed, row) || ReferenceEquals(_arrived, row);
        _pointed = ReferenceEquals(_pointed, row) ? null : _pointed;
        _arrived = ReferenceEquals(_arrived, row) ? null : _arrived;

        if (held)
        {
            Settle();
        }
    }

    /// <summary>Takes note that a pointer is on a row, or has left it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="on">True while the pointer is over it.</param>
    internal void Point(DialLegendRow row, bool on)
    {
        if (on)
        {
            _pointed = row;
        }
        else if (ReferenceEquals(_pointed, row))
        {
            _pointed = null;
        }
        else
        {
            return;
        }

        Settle();
    }

    /// <summary>Takes note that the keyboard is on a row, or has left it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="on">True while it has the focus.</param>
    internal void Arrive(DialLegendRow row, bool on)
    {
        if (on)
        {
            _arrived = row;
        }
        else if (ReferenceEquals(_arrived, row))
        {
            _arrived = null;
        }
        else
        {
            return;
        }

        Settle();
    }

    /// <summary>The reading the face says is being read, or null when none is.</summary>
    internal DialReading? Marked => Face?.Marked;

    /// <summary>Tells the face which of its readings this legend is naming.</summary>
    private void Settle() => Face?.Mark((_pointed ?? _arrived)?.Reading);

    /// <summary>Keeps every row in step with the one answer the face publishes.</summary>
    private void Refresh()
    {
        foreach (DialLegendRow row in _rows)
        {
            row.Refresh();
        }
    }

    private void TakeRowsFromTheFace()
    {
        IReadOnlyList<DialReading> arcs = Face?.Arcs ?? [];

        // Hidden rather than empty below two: a legend is what turns a ring back into a
        // provider, and with one ring the words under the figure have already done that.
        IsVisible = arcs.Count > 1;
        ItemsSource = arcs;
        Refresh();
    }

    private void Listen()
    {
        if (ReferenceEquals(_listening, Face))
        {
            return;
        }

        if (_listening is not null)
        {
            _listening.PropertyChanged -= OnFaceChanged;
        }

        _listening = Face;

        if (_listening is not null)
        {
            _listening.PropertyChanged += OnFaceChanged;
        }
    }

    private void OnFaceChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Dial.ArcsProperty)
        {
            TakeRowsFromTheFace();
        }
        else if (change.Property == Dial.MarkedProperty)
        {
            Refresh();
        }
    }
}
