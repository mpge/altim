using Altim.UI.Formatting;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim usage meter: a flat rail read against a graduated instrument scale, drawn
/// directly rather than templated. It reports one level and how close it is to a ceiling.
/// </summary>
/// <remarks>
/// <para>
/// The rail is <see cref="RailHeight"/> tall with fully round ends, the track is the
/// <c>AltimMeterTrackBrush</c> token and the fill is <c>AltimMeterFillBrush</c>. The fill
/// stays that colour at every level: above a threshold it is the metric label that turns
/// <c>AltimStatusWarnBrush</c>, never the bar.
/// </para>
/// <para>
/// Under the rail is the scale: hairline graduations whose intervals halve twice on the way
/// to the ceiling, so the tape tightens exactly where a reading starts to matter. The
/// intervals are stated by <see cref="Graduations"/> and the arithmetic is in
/// <see cref="GraduationsFor"/> rather than in the renderer, so what is drawn can be
/// asserted without reading pixels. <b>The bar itself stays linear</b>: it is the graduations
/// that crowd, never the mapping from a level to a position, because a rail whose scale
/// stretched would misreport every level on it.
/// </para>
/// <para>
/// The threshold is an index rather than a colour change: the one mark that crosses the rail
/// and carries on through the scale, and the only one drawn at <see cref="IndexWeight"/>
/// hairlines. Position, extent and weight all say what it is, so it survives being printed,
/// photocopied or read by somebody who cannot tell two greys apart. The words that go with
/// it are in <see cref="ReadingFor"/>, which is what the tip and the accessible name both
/// read, because a marker nobody can name is a marker only a sighted mouse user can use.
/// </para>
/// <para>
/// A null <see cref="Value"/> is not zero. It renders the unavailable state - the rail as a
/// hairline outline, with no track, no fill, no scale and no index - so an unreported metric
/// cannot be read as a reported nothing. The scale is the apparatus for reading a level, and
/// there is no level: drawing it would furnish the empty rail with everything except a
/// figure. The control still measures to its full height, so a metric arriving does not
/// shove the page down.
/// </para>
/// <para>
/// The fill animates for 180ms on an ease-out curve when the value moves from one reported
/// number to another. It does not animate on the first value, on becoming unavailable, or
/// on coming back from unavailable, because those are not a level changing. Nothing else in
/// the control moves.
/// </para>
/// <para>
/// A meter reports a level by its width, so a meter with no width reports nothing. It asks
/// for <see cref="MinimumWidth"/> at measure, which is what keeps it from collapsing to
/// zero in an auto sized grid column and vanishing without any layout error.
/// </para>
/// <para>
/// The graduations, the index and the unavailable outline are hairlines, so all of them are
/// snapped to whole device pixels through <see cref="Hairline"/>: at 125% an unsnapped 1px
/// rule is spread over two rows of pixels at partial coverage and reads as a grey smear.
/// </para>
/// </remarks>
public sealed class Meter : Control
{
    /// <summary>The height of the rail in device independent pixels.</summary>
    public const double RailHeight = InstrumentScale.RailThickness;

    /// <summary>The gap between the rail and the graduations under it.</summary>
    public const double ScaleGap = InstrumentScale.ScaleGap;

    /// <summary>The height of the scale: the length of a major graduation.</summary>
    public const double ScaleHeight = InstrumentScale.ScaleDepth;

    /// <summary>The whole control: the rail, the gap and the scale under it.</summary>
    public const double TotalHeight = InstrumentScale.TotalDepth;

    /// <summary>
    /// The narrowest a meter will measure to. It matches the <c>AltimMeterMinWidth</c>
    /// token, which is the popup's half width less one step on the spacing scale.
    /// </summary>
    public const double MinimumWidth = 48d;

    /// <inheritdoc cref="InstrumentScale.MinimumGraduationPitch" />
    public const double MinimumGraduationPitch = InstrumentScale.MinimumGraduationPitch;

    /// <inheritdoc cref="InstrumentScale.IndexWeight" />
    public const double IndexWeight = InstrumentScale.IndexWeight;

    /// <summary>How long the fill takes to travel to a new value.</summary>
    public static readonly TimeSpan FillDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Set while <see cref="Value"/> is null.</summary>
    public const string UnavailablePseudoClass = ":unavailable";

    /// <summary>Set while the value is at or above <see cref="Threshold"/>.</summary>
    public const string AboveThresholdPseudoClass = ":above-threshold";

    /// <summary>
    /// The level from 0 to 100, or null when the provider does not report it. Values
    /// outside the range are clamped; NaN is treated as unavailable.
    /// </summary>
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<Meter, double?>(nameof(Value), coerce: CoercePercent);

    /// <summary>
    /// The configured threshold from 0 to 100, marked by the index, or null for no index.
    /// </summary>
    public static readonly StyledProperty<double?> ThresholdProperty =
        AvaloniaProperty.Register<Meter, double?>(nameof(Threshold), coerce: CoercePercent);

    /// <summary>The rail behind the fill. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(TrackBrush));

    /// <summary>The fill. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(FillBrush));

    /// <summary>The graduations. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> ScaleBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(ScaleBrush));

    /// <summary>The threshold index. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> ThresholdBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(ThresholdBrush));

    /// <summary>The focus ring. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(FocusRingBrush));

    /// <summary>The focus ring's weight. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<double> FocusRingWidthProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(FocusRingWidth), 2d);

    /// <summary>How far the focus ring stands off the control.</summary>
    public static readonly StyledProperty<double> FocusRingOffsetProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(FocusRingOffset), 2d);

    /// <summary>The focus ring's corner radius.</summary>
    public static readonly StyledProperty<double> FocusRingRadiusProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(FocusRingRadius), 8d);

    /// <summary>
    /// The level the fill is currently drawn at. It follows <see cref="Value"/> through
    /// the fill transition and is the number <see cref="RenderedFillWidth"/> is derived
    /// from. Views bind <see cref="Value"/>; this exists so the transition has something
    /// to animate.
    /// </summary>
    public static readonly StyledProperty<double> DisplayValueProperty =
        AvaloniaProperty.Register<Meter, double>(nameof(DisplayValue));

    /// <summary>True while a reported value is at or above a configured threshold.</summary>
    public static readonly DirectProperty<Meter, bool> IsAboveThresholdProperty =
        AvaloniaProperty.RegisterDirect<Meter, bool>(
            nameof(IsAboveThreshold),
            o => o.IsAboveThreshold);

    /// <summary>True while the metric is not reported.</summary>
    public static readonly DirectProperty<Meter, bool> IsUnavailableProperty =
        AvaloniaProperty.RegisterDirect<Meter, bool>(
            nameof(IsUnavailable),
            o => o.IsUnavailable);

    /// <inheritdoc cref="InstrumentScale.Graduations" />
    /// <remarks>
    /// The meter does not own this scheme. <see cref="InstrumentScale"/> does, and the dial
    /// reads against the same one, because a card's meters and the panel's dial are a set to
    /// be read against one another.
    /// </remarks>
    public static IReadOnlyList<double> Graduations => InstrumentScale.Graduations;

    private bool _isAboveThreshold;
    private bool _isUnavailable = true;
    private bool _hasValueBeenSet;

    static Meter()
    {
        AffectsRender<Meter>(
            DisplayValueProperty,
            ValueProperty,
            ThresholdProperty,
            TrackBrushProperty,
            FillBrushProperty,
            ScaleBrushProperty,
            ThresholdBrushProperty,
            FocusRingBrushProperty,
            FocusRingWidthProperty,
            FocusRingOffsetProperty,
            FocusRingRadiusProperty);
    }

    /// <summary>Initializes a meter in the unavailable state.</summary>
    public Meter()
    {
        PseudoClasses.Set(UnavailablePseudoClass, true);
        UpdateReading();
    }

    /// <inheritdoc cref="ValueProperty" />
    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="ThresholdProperty" />
    public double? Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="FillBrushProperty" />
    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <inheritdoc cref="ScaleBrushProperty" />
    public IBrush? ScaleBrush
    {
        get => GetValue(ScaleBrushProperty);
        set => SetValue(ScaleBrushProperty, value);
    }

    /// <inheritdoc cref="ThresholdBrushProperty" />
    public IBrush? ThresholdBrush
    {
        get => GetValue(ThresholdBrushProperty);
        set => SetValue(ThresholdBrushProperty, value);
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

    /// <inheritdoc cref="DisplayValueProperty" />
    public double DisplayValue
    {
        get => GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    /// <inheritdoc cref="IsAboveThresholdProperty" />
    public bool IsAboveThreshold
    {
        get => _isAboveThreshold;
        private set => SetAndRaise(IsAboveThresholdProperty, ref _isAboveThreshold, value);
    }

    /// <inheritdoc cref="IsUnavailableProperty" />
    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set => SetAndRaise(IsUnavailableProperty, ref _isUnavailable, value);
    }

    /// <summary>
    /// What this meter reads, in words: the level, and the threshold it is measured against.
    /// It is the accessible name, and it is the tip a pointer and the keyboard both open.
    /// </summary>
    public string Reading => ReadingFor(Value, Threshold);

    /// <summary>
    /// The width of the fill as it is currently drawn, in device independent pixels.
    /// Zero while the metric is unavailable, and mid-transition while the fill is moving.
    /// </summary>
    public double RenderedFillWidth =>
        FillWidthFor(IsUnavailable ? null : DisplayValue, Bounds.Width);

    /// <summary>
    /// Maps a level to a fill width: <see cref="InstrumentScale.PositionFor"/> with the
    /// rail's width as the span. The mapping is linear and has no minimum - one percent of a
    /// 200px rail is two pixels, not a token gesture toward visibility.
    /// </summary>
    /// <param name="value">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="width">The full width of the rail.</param>
    /// <returns>The fill width, which is zero for a null or non-positive width.</returns>
    public static double FillWidthFor(double? value, double width) =>
        InstrumentScale.PositionFor(value, width);

    /// <inheritdoc cref="InstrumentScale.IsMajorGraduation" />
    /// <param name="level">The level from 0 to 100.</param>
    /// <returns>True at nothing used, half way and the ceiling.</returns>
    public static bool IsMajorGraduation(double level) => InstrumentScale.IsMajorGraduation(level);

    /// <inheritdoc cref="InstrumentScale.GraduationsFor" />
    /// <param name="width">The rail width in device independent pixels, which is the span
    /// the scale is fitted to.</param>
    /// <returns>The levels to draw, which is empty for a width of nothing.</returns>
    public static IReadOnlyList<double> GraduationsFor(double width) =>
        InstrumentScale.GraduationsFor(width);

    /// <summary>
    /// What a reading says in words.
    /// </summary>
    /// <param name="value">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>A sentence naming the level and what it is measured against.</returns>
    /// <remarks>
    /// An unreported metric says so, and never reads as a zero - the same distinction the
    /// drawn states keep. The threshold is named in words because the index marking it is a
    /// mark on a scale: a reader who cannot see it, and a reader who can see it but cannot
    /// tell which level it stands at, both need the number said. The wording is
    /// <see cref="UsageFormat.InstrumentReading"/> so the meter and the dial say the same
    /// thing about the same reading.
    /// </remarks>
    public static string ReadingFor(double? value, double? threshold) =>
        UsageFormat.InstrumentReading(value, threshold);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var size = Bounds.Size;
        double scale = Hairline.ScaleOf(this);
        double content = Math.Min(TotalHeight, size.Height);
        double railHeight = Math.Min(RailHeight, content);
        if (size.Width <= 0d || railHeight <= 0d)
        {
            return;
        }

        double top = Hairline.SnapEdge((size.Height - content) / 2d, scale);
        var rail = new Rect(0d, top, size.Width, railHeight);
        double radius = railHeight / 2d;

        // The scale hangs off the bottom of the rail and is what is given up first when the
        // control is squeezed shorter than it asked for: a rail with no scale still reports
        // a level, a scale with no rail reports nothing.
        double scaleTop = Hairline.SnapEdge(rail.Bottom + ScaleGap, scale);
        double scaleBottom = Math.Max(scaleTop, Hairline.SnapEdge(top + content, scale));

        if (IsUnavailable)
        {
            RenderUnavailable(context, rail, radius, scale);
        }
        else
        {
            if (TrackBrush is { } track)
            {
                context.DrawRectangle(track, null, rail, radius, radius);
            }

            double fill = RenderedFillWidth;
            if (fill > 0d && FillBrush is { } fillBrush)
            {
                using (context.PushClip(new RoundedRect(rail, radius)))
                {
                    context.FillRectangle(fillBrush, new Rect(rail.X, rail.Y, fill, rail.Height));
                }
            }

            if (ScaleBrush is { } graduation)
            {
                RenderScale(context, rail, scaleTop, scaleBottom, graduation, scale);
            }

            if (Threshold is { } threshold && ThresholdBrush is { } index)
            {
                RenderIndex(context, rail, scaleBottom, threshold, index, scale);
            }
        }

        if (IsFocused && FocusRingBrush is { } ring)
        {
            RenderFocusRing(context, new Rect(0d, top, size.Width, content), ring);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // Asking for a width is the whole point: a meter that measured to zero would be
        // laid out at zero in an auto sized column, draw nothing, and report no error.
        // It never asks for more room than it was offered, so it cannot force an overflow.
        double width = double.IsNaN(availableSize.Width)
            ? MinimumWidth
            : Math.Min(MinimumWidth, availableSize.Width);

        // The height is the same whether or not there is anything to report, so a metric
        // arriving or going away does not reflow the page around it.
        return new Size(width, TotalHeight);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The rail is drawn rather than templated, so there is no part to carry a name: without
    /// a peer the meter is a rectangle with no name, no value and no place in the reading
    /// order. See <see cref="MeterAutomationPeer"/>.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new MeterAutomationPeer(this);

    /// <inheritdoc />
    /// <remarks>
    /// Focus opens the same tip a pointer opens, because it is the same detail: the level and
    /// the threshold the index stands at. A meter that only answered a pointer would keep the
    /// one thing it knows that the row does not print away from anybody using a keyboard.
    /// </remarks>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        ToolTip.SetIsOpen(this, RevealsDetail);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            double? oldValue = change.GetOldValue<double?>();
            double? newValue = change.GetNewValue<double?>();

            IsUnavailable = newValue is null;
            PseudoClasses.Set(UnavailablePseudoClass, newValue is null);
            UpdateAboveThreshold();
            UpdateReading();

            // A level only "changes" when it moves between two reported numbers. The
            // first value, and every crossing into or out of unavailable, lands flat.
            bool animate = _hasValueBeenSet && oldValue is not null && newValue is not null;
            _hasValueBeenSet = true;
            SetDisplayValue(newValue ?? 0d, animate);
        }
        else if (change.Property == ThresholdProperty)
        {
            UpdateAboveThreshold();
            UpdateReading();
        }
    }

    private static double? CoercePercent(AvaloniaObject sender, double? value)
    {
        _ = sender;
        if (value is not { } level || double.IsNaN(level))
        {
            return null;
        }

        return Math.Clamp(level, 0d, 100d);
    }

    /// <summary>Whether there is anything to reveal that the row around it does not print.</summary>
    private bool RevealsDetail => Value is not null && Threshold is not null;

    private void RenderUnavailable(DrawingContext context, Rect rail, double radius, double scale)
    {
        // An outline, not an empty track: a track with no fill reads as a reported zero,
        // and DESIGN.md is explicit that an unreported metric is never shown as a zero.
        if (TrackBrush is not { } brush)
        {
            return;
        }

        double weight = Hairline.ThicknessFor(scale);
        var outline = rail.Deflate(weight / 2d);
        if (outline.Width <= 0d || outline.Height <= 0d)
        {
            return;
        }

        double outlineRadius = Math.Max(0d, radius - (weight / 2d));
        context.DrawRectangle(null, new Pen(brush, weight), outline, outlineRadius, outlineRadius);
    }

    /// <summary>
    /// The graduations: a hairline per level, hung from the top of the scale, full height at
    /// a major and half height at the rest.
    /// </summary>
    private void RenderScale(
        DrawingContext context,
        Rect rail,
        double scaleTop,
        double scaleBottom,
        IBrush brush,
        double scale)
    {
        if (scaleBottom <= scaleTop)
        {
            return;
        }

        double weight = Math.Min(Hairline.ThicknessFor(scale), rail.Width);
        double half = Hairline.SnapEdge(scaleTop + ((scaleBottom - scaleTop) / 2d), scale);

        foreach (double level in GraduationsFor(rail.Width))
        {
            double x = Math.Clamp(
                FillWidthFor(level, rail.Width),
                0d,
                Math.Max(0d, rail.Width - weight));
            double bottom = IsMajorGraduation(level) ? scaleBottom : half;

            context.FillRectangle(
                brush,
                new Rect(
                    Hairline.SnapEdge(rail.X + x, scale),
                    scaleTop,
                    weight,
                    Math.Max(0d, bottom - scaleTop)));
        }
    }

    /// <summary>
    /// The threshold index: the one mark that crosses the rail and runs the whole height of
    /// the control, at twice a hairline. Nothing else in the meter does either, which is what
    /// tells it apart from a graduation without asking anybody to compare two greys.
    /// </summary>
    private void RenderIndex(
        DrawingContext context,
        Rect rail,
        double scaleBottom,
        double threshold,
        IBrush brush,
        double scale)
    {
        double weight = Math.Min(Hairline.ThicknessFor(scale) * IndexWeight, rail.Width);
        double x = Math.Clamp(
            FillWidthFor(threshold, rail.Width),
            0d,
            Math.Max(0d, rail.Width - weight));

        context.FillRectangle(
            brush,
            new Rect(
                Hairline.SnapEdge(rail.X + x, scale),
                rail.Y,
                weight,
                Math.Max(rail.Height, scaleBottom - rail.Y)));
    }

    /// <summary>
    /// The design system's ring: 2px, offset 2px, outside the control's own bounds. The
    /// control does not clip to them, and the theme nulls the framework's dashed adorner, so
    /// there is one ring rather than two.
    /// </summary>
    private void RenderFocusRing(DrawingContext context, Rect content, IBrush brush)
    {
        double weight = Math.Max(0d, FocusRingWidth);
        if (weight <= 0d)
        {
            return;
        }

        // The pen strokes astride the rectangle it is given, so the rectangle is the middle
        // of the ring: the offset plus half the weight out from the control.
        Rect ring = content.Inflate(Math.Max(0d, FocusRingOffset) + (weight / 2d));
        double radius = Math.Max(0d, FocusRingRadius);

        context.DrawRectangle(null, new Pen(brush, weight), ring, radius, radius);
    }

    private void UpdateAboveThreshold()
    {
        bool above = Value is { } value && Threshold is { } threshold && value >= threshold;
        IsAboveThreshold = above;
        PseudoClasses.Set(AboveThresholdPseudoClass, above);
    }

    /// <summary>
    /// Keeps the tip and the tab order in step with what the meter has to say.
    /// </summary>
    /// <remarks>
    /// The meter is a tab stop exactly when it reveals something the row around it does not
    /// already print: the threshold. A meter with no threshold, or with nothing to measure
    /// against it, would be a stop on the way to the next control that read out the figure
    /// standing beside it. Hover and focus open the same tip for the same reason, so neither
    /// way of reading the meter is told something the other is not.
    /// </remarks>
    private void UpdateReading()
    {
        bool reveals = RevealsDetail;

        Focusable = reveals;
        ToolTip.SetTip(this, reveals ? Reading : null);

        if (!reveals)
        {
            ToolTip.SetIsOpen(this, false);
        }
    }

    private void SetDisplayValue(double value, bool animate)
    {
        if (animate)
        {
            Transitions ??=
            [
                new DoubleTransition
                {
                    Property = DisplayValueProperty,
                    Duration = FillDuration,
                    Easing = new CubicEaseOut(),
                },
            ];

            DisplayValue = value;
            return;
        }

        // Detaching the transitions for the assignment is what keeps an entrance from
        // animating: Avalonia would otherwise animate the very first value from zero.
        Transitions? transitions = Transitions;
        Transitions = null;
        DisplayValue = value;
        Transitions = transitions;
    }
}
