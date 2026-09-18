using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Altim.UI.Controls;

/// <summary>
/// One row of a <see cref="DialLegend"/>: the mark, the name and the figure for one of the
/// dial's rings, and the two ends of the pairing between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row does not decide that it is marked.</b> Pointing at it, or arriving on it with
/// the keyboard, tells the <see cref="DialLegend"/>, which tells the <see cref="Dial"/>,
/// which resolves one answer and publishes it; the row then reads that answer back. So the
/// hairline round this row and the hairline round a band on the face are two drawings of one
/// fact rather than two facts that have to be kept in step.
/// </para>
/// <para>
/// <b>The mark is the face's own.</b> A hairline, in the engraving's ink, traced round the
/// row - the same mark the dial traces round the band it is reading. One gesture in two
/// places reads as one thing; a second treatment invented for the legend would read as two.
/// It is drawn <em>outside</em> the row's own bounds, the way the focus ring is, so marking a
/// row costs no height and nothing below it moves.
/// </para>
/// <para>
/// <b>Nothing here animates.</b> The mark says <em>which</em>, not how much, and it appears
/// and goes in one frame. That is the dial's rule and the legend keeps it, which is why there
/// is nothing for the reduce-motion setting to suppress.
/// </para>
/// <para>
/// <b>It is a focus stop, not only a hover target.</b> Hovering a row marks its band, so the
/// keyboard has to be able to do the same thing: an affordance a pointer has and a keyboard
/// does not is the one shape <c>docs/DESIGN.md</c> rules out. Focus draws the design system's
/// ring, which is drawn after the mark and covers it, so a focused row carries one indicator
/// rather than two.
/// </para>
/// </remarks>
public sealed class DialLegendRow : Decorator
{
    /// <summary>Set while this row is the one being read.</summary>
    public const string MarkedPseudoClass = ":marked";

    /// <summary>
    /// The ground the row stands on, which is <c>Transparent</c> rather than unset.
    /// </summary>
    /// <remarks>
    /// A ground is what makes the whole row answer a pointer. Without one only the ink does,
    /// and the gaps between the mark, the name and the figure would take the mark away as the
    /// pointer crossed them. <see cref="Border"/> would have supplied this, and seals its own
    /// <c>Render</c>; this row draws two marks outside its bounds, so it does the drawing.
    /// </remarks>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<DialLegendRow, IBrush?>(nameof(Background));

    /// <summary>The reading this row names.</summary>
    public static readonly StyledProperty<DialReading?> ReadingProperty =
        AvaloniaProperty.Register<DialLegendRow, DialReading?>(nameof(Reading));

    /// <summary>The hairline traced round the row being read. Supplied by the theme.</summary>
    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<DialLegendRow, IBrush?>(nameof(MarkBrush));

    /// <summary>The focus ring. Supplied by the theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty =
        AvaloniaProperty.Register<DialLegendRow, IBrush?>(nameof(FocusRingBrush));

    /// <summary>The focus ring's weight. Supplied by the theme from tokens.</summary>
    public static readonly StyledProperty<double> FocusRingWidthProperty =
        AvaloniaProperty.Register<DialLegendRow, double>(nameof(FocusRingWidth), 2d);

    /// <summary>How far the mark and the ring stand off the row.</summary>
    /// <remarks>
    /// One offset for both, because they are two weights drawn round one rectangle. Two
    /// offsets would let a focused row's ring sit somewhere its own mark does not.
    /// </remarks>
    public static readonly StyledProperty<double> FocusRingOffsetProperty =
        AvaloniaProperty.Register<DialLegendRow, double>(nameof(FocusRingOffset), 2d);

    /// <summary>The corner the mark and the ring take.</summary>
    /// <remarks>
    /// The system's smallest, 4, rather than the 8 a row usually takes. A legend line is 16
    /// tall, so the rectangle being marked is 21: at 8 that is a pill, and a pill's ends
    /// sweep back across the 14 wide circle this row's own mark is drawn as. At 4 the sides
    /// are straight where the circle stands and the two never cross.
    /// </remarks>
    public static readonly StyledProperty<double> FocusRingRadiusProperty =
        AvaloniaProperty.Register<DialLegendRow, double>(nameof(FocusRingRadius), 4d);

    private DialLegend? _legend;
    private bool _isMarked;

    static DialLegendRow()
    {
        AffectsRender<DialLegendRow>(
            BackgroundProperty,
            MarkBrushProperty,
            FocusRingBrushProperty,
            FocusRingWidthProperty,
            FocusRingOffsetProperty,
            FocusRingRadiusProperty);

        FocusableProperty.OverrideDefaultValue<DialLegendRow>(true);
    }

    /// <inheritdoc cref="BackgroundProperty" />
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <inheritdoc cref="ReadingProperty" />
    public DialReading? Reading
    {
        get => GetValue(ReadingProperty);
        set => SetValue(ReadingProperty, value);
    }

    /// <inheritdoc cref="MarkBrushProperty" />
    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    /// <inheritdoc cref="FocusRingBrushProperty" />
    public IBrush? FocusRingBrush
    {
        get => GetValue(FocusRingBrushProperty);
        set => SetValue(FocusRingBrushProperty, value);
    }

    /// <inheritdoc cref="FocusRingWidthProperty" />
    public double FocusRingWidth
    {
        get => GetValue(FocusRingWidthProperty);
        set => SetValue(FocusRingWidthProperty, value);
    }

    /// <inheritdoc cref="FocusRingOffsetProperty" />
    public double FocusRingOffset
    {
        get => GetValue(FocusRingOffsetProperty);
        set => SetValue(FocusRingOffsetProperty, value);
    }

    /// <inheritdoc cref="FocusRingRadiusProperty" />
    public double FocusRingRadius
    {
        get => GetValue(FocusRingRadiusProperty);
        set => SetValue(FocusRingRadiusProperty, value);
    }

    /// <summary>Whether the face says this row is the one being read.</summary>
    public bool IsMarked => _isMarked;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        base.Render(context);

        var row = new Rect(default, Bounds.Size);

        // Drawn even when it is fully transparent: it is the draw operation that makes the
        // whole row answer a pointer, not the colour.
        if (Background is { } ground)
        {
            context.FillRectangle(ground, row);
        }

        if (_isMarked && MarkBrush is { } ink)
        {
            Trace(context, row, ink, Hairline.ThicknessFor(Hairline.ScaleOf(this)));
        }

        // Second, so that on a focused row the design system's ring covers its own hairline:
        // they stand off by the same offset and the ring is the heavier of the two, so one
        // indicator is drawn where two would otherwise sit a pixel apart.
        if (IsFocused && FocusRingBrush is { } ring)
        {
            Trace(context, row, ring, Math.Max(0d, FocusRingWidth));
        }
    }

    /// <summary>
    /// Keeps the row in step with the one answer the face publishes.
    /// </summary>
    internal void Refresh() =>
        SetMarked(Reading is { } reading
            && _legend?.Marked is { } marked
            && ReferenceEquals(reading, marked));

    /// <inheritdoc />
    /// <remarks>
    /// The row is a focus stop with no template and nothing but text inside it, so its own
    /// peer would be announced as a panel. The name the view sets is what a client hears;
    /// this keeps the row in the reading order as one thing rather than three.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _legend = this.FindAncestorOfType<DialLegend>();
        _legend?.Attach(this);
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _legend?.Detach(this);
        _legend = null;
        SetMarked(false);
    }

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _legend?.Point(this, true);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _legend?.Point(this, false);
    }

    /// <inheritdoc />
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _legend?.Arrive(this, true);
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _legend?.Arrive(this, false);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ReadingProperty)
        {
            Refresh();
        }
        else if (change.Property == IsFocusedProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>
    /// One outline round the row, standing off it rather than inside it.
    /// </summary>
    /// <remarks>
    /// The pen strokes astride the rectangle it is given, so that rectangle is the middle of
    /// the line: the offset plus half the weight out from the row. Drawn outside the row's
    /// bounds, which is why the theme turns clipping off.
    /// </remarks>
    private void Trace(DrawingContext context, Rect row, IBrush brush, double weight)
    {
        if (weight <= 0d)
        {
            return;
        }

        Rect at = row.Inflate(Math.Max(0d, FocusRingOffset) + (weight / 2d));
        double radius = Math.Max(0d, FocusRingRadius);

        context.DrawRectangle(null, new Pen(brush, weight), at, radius, radius);
    }

    private void SetMarked(bool marked)
    {
        if (_isMarked == marked)
        {
            return;
        }

        _isMarked = marked;
        PseudoClasses.Set(MarkedPseudoClass, marked);
        InvalidateVisual();
    }

    /// <summary>What an assistive technology is handed in place of the row.</summary>
    /// <remarks>
    /// A decorated container is announced as nothing at all by default, which on a focus stop
    /// means arriving somewhere silent. It is one item of a list here, and the name the view
    /// sets on the row is the whole reading, so the row is announced once rather than as
    /// three separate strings.
    /// </remarks>
    private sealed class Peer(DialLegendRow owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(DialLegendRow);
    }
}
