using Altim.UI.Formatting;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim dial: <see cref="Meter"/>'s cross section bent round a swept arc, drawn directly
/// rather than templated. It reports one level and how close it is to a ceiling.
/// </summary>
/// <remarks>
/// <para>
/// The band is the meter's, to the pixel: a <see cref="RailThickness"/> rail with fully round
/// ends, a <see cref="ScaleGap"/> gap, and a <see cref="ScaleDepth"/> row of graduations - the
/// same <see cref="FaceDepth"/> in all, and the same tokens. The rail is innermost and the
/// engraving outermost, so the scale stands on the far side of the rail from the figure, the
/// way a metric row puts the figure above the rail and the scale below it.
/// </para>
/// <para>
/// The sweep is <see cref="Sweep"/> degrees rather than a full circle, centred on the top, so
/// nothing used and the ceiling are two distinguishable ends rather than the same point. The
/// arc left open at the foot is where the words under the figure go.
/// </para>
/// <para>
/// The scale is <see cref="InstrumentScale"/>'s, not a second scheme: the interval halves at
/// 50 and again at 80, 0/50/100 run the full depth of the engraving, and a graduation is kept
/// only when it stands clear of the one before it along the arc. The span the pitch is
/// measured over is the arc length at the radius the graduations reach furthest in, which is
/// where two of them stand closest together - see <see cref="GraduationSpanFor"/>. <b>The
/// mapping from a level to an angle stays linear</b>; it is the graduations that crowd.
/// </para>
/// <para>
/// The threshold is an index rather than a colour change: the one mark that crosses the rail
/// and carries on through the engraving, and the only one drawn at <see cref="IndexWeight"/>
/// hairlines. Position, extent and weight all say what it is, so it survives being printed,
/// photocopied or read by somebody who cannot tell two greys apart.
/// </para>
/// <para>
/// A null <see cref="Value"/> is not zero. It renders the unavailable state - the rail as a
/// hairline outline, with no track, no sweep, no scale and no index - so an unreported
/// reading cannot be read as a reported nothing, and so there is no sweep sitting at the
/// bottom of the dial pretending to be one. The control measures the same either way, so a
/// reading arriving does not reflow the panel.
/// </para>
/// <para>
/// <b>Nothing here animates and no geometry is built per frame.</b> The two paths are cached
/// against the size and the level they were built for, and rebuilt only when one of those
/// moves. The dial's surface is the tray panel, which is laid out once at start-up and shown
/// rather than constructed, and which keeps taking readings while it is hidden: a transition
/// on this control would rebuild a path per frame for a panel nobody is looking at, and the
/// panel's whole budget is that opening it is a show rather than a build. The meter animates
/// because it lives on a window somebody already has open.
/// </para>
/// <para>
/// The engraving is radial, so its marks cannot be snapped to the pixel grid the way the
/// meter's vertical rules are - a line at 37 degrees lands where it lands. Their weight still
/// resolves to a whole number of device pixels through <see cref="Hairline"/>, so the whole
/// engraving is one weight rather than a different grey per mark, and the radii they run
/// between are snapped.
/// </para>
/// </remarks>
public sealed class Dial : Control
{
    /// <summary>The width and height of the dial's face, in device independent pixels.</summary>
    /// <remarks>
    /// Sized from the figure that sits inside it rather than picked. The widest reading is
    /// "100%", which is 95 wide at the type scale's Figure and whose ink stands 12 either
    /// side of the middle; the band is <see cref="FaceDepth"/> deep, so a face of 144 leaves
    /// a clear circle 116 across and the figure stands 9 clear of the rail at its widest. At
    /// 128 it stands 1, which reads as the figure touching the instrument.
    /// </remarks>
    public const double Size = 144d;

    /// <summary>How much of a circle the scale is swept over, in degrees.</summary>
    /// <remarks>
    /// Centred on the top, so the two ends sit symmetrically below the middle and the 120
    /// degrees left open at the foot carry the words under the figure. A full circle would
    /// put nothing used and the ceiling at the same place.
    /// </remarks>
    public const double Sweep = 240d;

    /// <inheritdoc cref="InstrumentScale.RailThickness" />
    public const double RailThickness = InstrumentScale.RailThickness;

    /// <inheritdoc cref="InstrumentScale.ScaleGap" />
    public const double ScaleGap = InstrumentScale.ScaleGap;

    /// <inheritdoc cref="InstrumentScale.ScaleDepth" />
    public const double ScaleDepth = InstrumentScale.ScaleDepth;

    /// <summary>The whole band, from the engraving's outer edge to the rail's inner edge.</summary>
    public const double FaceDepth = InstrumentScale.TotalDepth;

    /// <inheritdoc cref="InstrumentScale.IndexWeight" />
    public const double IndexWeight = InstrumentScale.IndexWeight;

    /// <summary>Set while <see cref="Value"/> is null.</summary>
    public const string UnavailablePseudoClass = ":unavailable";

    /// <summary>Set while the value is at or above <see cref="Threshold"/>.</summary>
    public const string AboveThresholdPseudoClass = ":above-threshold";

    /// <summary>
    /// The level from 0 to 100, or null when the provider does not report it. Values
    /// outside the range are clamped; NaN is treated as unavailable.
    /// </summary>
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<Dial, double?>(nameof(Value), coerce: CoercePercent);

    /// <summary>
    /// The configured threshold from 0 to 100, marked by the index, or null for no index.
    /// </summary>
    public static readonly StyledProperty<double?> ThresholdProperty =
        AvaloniaProperty.Register<Dial, double?>(nameof(Threshold), coerce: CoercePercent);

    /// <summary>The band behind the sweep. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(TrackBrush));

    /// <summary>The sweep. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(FillBrush));

    /// <summary>The graduations. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> ScaleBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(ScaleBrush));

    /// <summary>The threshold index. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> ThresholdBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(ThresholdBrush));

    /// <summary>The focus ring. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(FocusRingBrush));

    /// <summary>The focus ring's weight. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<double> FocusRingWidthProperty =
        AvaloniaProperty.Register<Dial, double>(nameof(FocusRingWidth), 2d);

    /// <summary>How far the focus ring stands off the face.</summary>
    public static readonly StyledProperty<double> FocusRingOffsetProperty =
        AvaloniaProperty.Register<Dial, double>(nameof(FocusRingOffset), 2d);

    /// <summary>True while a reported value is at or above a configured threshold.</summary>
    public static readonly DirectProperty<Dial, bool> IsAboveThresholdProperty =
        AvaloniaProperty.RegisterDirect<Dial, bool>(
            nameof(IsAboveThreshold),
            o => o.IsAboveThreshold);

    /// <summary>True while the reading is not reported.</summary>
    public static readonly DirectProperty<Dial, bool> IsUnavailableProperty =
        AvaloniaProperty.RegisterDirect<Dial, bool>(
            nameof(IsUnavailable),
            o => o.IsUnavailable);

    private bool _isAboveThreshold;
    private bool _isUnavailable = true;

    private Geometry? _band;
    private Geometry? _sweep;
    private Point _builtAround;
    private double _builtForRadius = double.NaN;
    private double _builtForLevel = double.NaN;

    static Dial()
    {
        AffectsRender<Dial>(
            ValueProperty,
            ThresholdProperty,
            TrackBrushProperty,
            FillBrushProperty,
            ScaleBrushProperty,
            ThresholdBrushProperty,
            FocusRingBrushProperty,
            FocusRingWidthProperty,
            FocusRingOffsetProperty);
    }

    /// <summary>Initializes a dial in the unavailable state.</summary>
    public Dial()
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
    /// What this dial reads, in words: the level, and the threshold it is measured against.
    /// It is the accessible name, and it is the tip a pointer and the keyboard both open.
    /// </summary>
    public string Reading => ReadingFor(Value, Threshold);

    /// <summary>
    /// The rail's path as it was last drawn, or null before the first render.
    /// </summary>
    /// <remarks>
    /// It is the same instance from one render to the next unless the face's radius or the
    /// level has moved, and <see cref="RenderedSweep"/> is rebuilt with it. That is the whole
    /// of this control's answer to standing on the surface with an opening budget: a dial that
    /// built a fresh path every time the panel repainted would spend that budget on a picture
    /// that had not changed.
    /// </remarks>
    public Geometry? RenderedBand => _band;

    /// <summary>
    /// The reading's path as it was last drawn, clipped to the rail.
    /// </summary>
    /// <remarks>
    /// Null when there is nothing to draw: an unreported reading, and a reading of nothing
    /// used. Those two are not the same picture - the first draws the rail as an outline and
    /// the second draws it filled - but neither of them puts a sweep on the dial, because a
    /// sweep of no length is not a reading of nothing.
    /// </remarks>
    public Geometry? RenderedSweep => _sweep;

    /// <summary>
    /// Where the sweep begins, in degrees clockwise from the top. Nothing used stands here.
    /// </summary>
    public static double StartAngle => -Sweep / 2d;

    /// <summary>
    /// Where the sweep ends, in degrees clockwise from the top. The ceiling stands here.
    /// </summary>
    public static double EndAngle => Sweep / 2d;

    /// <summary>
    /// The angle a level is drawn at, in degrees clockwise from the top.
    /// </summary>
    /// <param name="level">The level from 0 to 100, or null when unavailable.</param>
    /// <returns>The angle, which is <see cref="StartAngle"/> for an unreported level.</returns>
    /// <remarks>
    /// Linear in the level, which is the same promise the meter's fill width makes: it is the
    /// graduations that crowd toward the ceiling, never the mapping.
    /// </remarks>
    public static double AngleFor(double? level) =>
        StartAngle + InstrumentScale.PositionFor(level, Sweep);

    /// <summary>The radius of the dial's face inside a given size.</summary>
    /// <param name="size">The room the control was given.</param>
    /// <returns>Half the shorter side, or zero when there is no room.</returns>
    public static double FaceRadiusFor(Size size) =>
        Math.Max(0d, Math.Min(size.Width, size.Height) / 2d);

    /// <summary>
    /// The span the scale is fitted to: the arc length the sweep covers at the radius the
    /// graduations reach furthest in, which is where two of them stand closest together.
    /// </summary>
    /// <param name="faceRadius">The radius of the face.</param>
    /// <returns>The span in device independent pixels, never negative.</returns>
    /// <remarks>
    /// Measuring at the outer edge instead would keep marks that are four pixels apart out
    /// there and less than that where they actually meet, which is the one thing
    /// <see cref="InstrumentScale.MinimumGraduationPitch"/> exists to prevent.
    /// </remarks>
    public static double GraduationSpanFor(double faceRadius) =>
        Math.Max(0d, faceRadius - ScaleDepth) * Sweep * Math.PI / 180d;

    /// <summary>The graduations a face of a given radius can carry, in order.</summary>
    /// <param name="faceRadius">The radius of the face.</param>
    /// <returns>The levels to draw, which is empty for a face of nothing.</returns>
    public static IReadOnlyList<double> GraduationsFor(double faceRadius) =>
        InstrumentScale.GraduationsFor(GraduationSpanFor(faceRadius));

    /// <inheritdoc cref="UsageFormat.InstrumentReading" />
    /// <param name="value">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>A sentence naming the level and what it is measured against.</returns>
    public static string ReadingFor(double? value, double? threshold) =>
        UsageFormat.InstrumentReading(value, threshold);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Size size = Bounds.Size;
        double radius = FaceRadiusFor(size);
        if (radius < FaceDepth)
        {
            // Below this there is no band to draw, only a blot. A dial that reported a level
            // as a blot would be reporting a level it cannot be read for.
            return;
        }

        double scale = Hairline.ScaleOf(this);
        var centre = new Point(
            Hairline.SnapEdge(size.Width / 2d, scale),
            Hairline.SnapEdge(size.Height / 2d, scale));

        double scaleOuter = Hairline.SnapEdge(radius, scale);
        double railOuter = Hairline.SnapEdge(radius - ScaleDepth - ScaleGap, scale);
        double railInner = Hairline.SnapEdge(railOuter - RailThickness, scale);

        EnsureGeometry(centre, railInner, railOuter, radius);

        if (IsUnavailable)
        {
            RenderUnavailable(context, scale);
        }
        else
        {
            if (TrackBrush is { } track && _band is { } band)
            {
                context.DrawGeometry(track, null, band);
            }

            if (FillBrush is { } fill && _sweep is { } swept && _band is { } clip)
            {
                // Clipped to the band for the same reason the meter clips its fill to the
                // rounded rail: the sweep's own ends are square, and the round end at nothing
                // used belongs to the rail rather than to the reading.
                using (context.PushGeometryClip(clip))
                {
                    context.DrawGeometry(fill, null, swept);
                }
            }

            if (ScaleBrush is { } engraving)
            {
                RenderScale(context, centre, radius, scaleOuter, scale, engraving);
            }

            if (Threshold is { } threshold && ThresholdBrush is { } index)
            {
                RenderIndex(context, centre, threshold, railInner, scaleOuter, scale, index);
            }
        }

        if (IsFocused && FocusRingBrush is { } ring)
        {
            RenderFocusRing(context, centre, radius, ring);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // A dial has an intrinsic size the way a gauge has a bezel: it is read by its angles,
        // and a dial stretched to a column would report the same level at a different shape
        // on every surface. It never asks for more room than it was offered.
        double width = double.IsNaN(availableSize.Width)
            ? Size
            : Math.Min(Size, availableSize.Width);
        double height = double.IsNaN(availableSize.Height)
            ? Size
            : Math.Min(Size, availableSize.Height);

        return new Size(width, height);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The face is drawn rather than templated, so there is no part to carry a name: without
    /// a peer the dial is a rectangle with no name, no value and no place in the reading
    /// order. See <see cref="DialAutomationPeer"/>.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new DialAutomationPeer(this);

    /// <inheritdoc />
    /// <remarks>
    /// Focus opens the same tip a pointer opens, because it is the same detail: the level and
    /// the threshold the index stands at. The words beside the dial print the level and name
    /// the window; the threshold is the one thing only the dial knows, and a dial that
    /// answered only a pointer would keep it from anybody using a keyboard.
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
            double? newValue = change.GetNewValue<double?>();

            IsUnavailable = newValue is null;
            PseudoClasses.Set(UnavailablePseudoClass, newValue is null);
            UpdateAboveThreshold();
            UpdateReading();
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

    /// <summary>A point on the face, from a radius and an angle clockwise from the top.</summary>
    private static Point Polar(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(
            centre.X + (radius * Math.Sin(radians)),
            centre.Y - (radius * Math.Cos(radians)));
    }

    /// <summary>
    /// One band of the face: the arc between two radii, from one angle to another, with
    /// either end square or rounded off.
    /// </summary>
    /// <remarks>
    /// The traversal runs clockwise along the outer edge, across the far end, back along the
    /// inner edge and across the near one, so every arc but the inner edge is drawn clockwise.
    /// A rounded end is a semicircle on the band's own thickness, which is what gives the
    /// rail the same fully round ends the meter's has.
    /// </remarks>
    private static Geometry BuildBand(
        Point centre,
        double inner,
        double outer,
        double from,
        double to,
        bool roundStart,
        bool roundEnd)
    {
        double cap = (outer - inner) / 2d;
        bool large = to - from > 180d;
        var outerSize = new Size(outer, outer);
        var innerSize = new Size(inner, inner);
        var capSize = new Size(cap, cap);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(Polar(centre, outer, from), isFilled: true);
            path.ArcTo(Polar(centre, outer, to), outerSize, 0d, large, SweepDirection.Clockwise);

            if (roundEnd)
            {
                path.ArcTo(Polar(centre, inner, to), capSize, 0d, false, SweepDirection.Clockwise);
            }
            else
            {
                path.LineTo(Polar(centre, inner, to));
            }

            path.ArcTo(
                Polar(centre, inner, from),
                innerSize,
                0d,
                large,
                SweepDirection.CounterClockwise);

            if (roundStart)
            {
                path.ArcTo(Polar(centre, outer, from), capSize, 0d, false, SweepDirection.Clockwise);
            }

            path.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>Whether there is anything to reveal that the words beside it do not print.</summary>
    private bool RevealsDetail => Value is not null && Threshold is not null;

    /// <summary>
    /// Rebuilds the two paths, and only when the size or the level they were built for has
    /// moved. A path per frame on the one surface with an opening budget is how that budget
    /// is lost.
    /// </summary>
    private void EnsureGeometry(Point centre, double railInner, double railOuter, double radius)
    {
        double level = Value ?? double.NaN;
        if (_band is not null
            && centre.Equals(_builtAround)
            && radius.Equals(_builtForRadius)
            && (level.Equals(_builtForLevel) || (double.IsNaN(level) && double.IsNaN(_builtForLevel))))
        {
            return;
        }

        // The centre is part of the key, not just the radius. A control laid out wider without
        // getting any taller keeps the same face and moves it, and a path cached on the radius
        // alone would then be drawn where the face used to be.
        _builtAround = centre;
        _builtForRadius = radius;
        _builtForLevel = level;

        // The rail's round ends bulge past the angles its centreline runs between, so the
        // centreline stops a cap short of each end and the caps land exactly on nothing used
        // and on the ceiling. That is what the meter's rounded rail does: the leftmost pixel
        // of the rail is level zero, not level zero less a corner radius.
        double centreRadius = (railInner + railOuter) / 2d;
        double capDegrees = centreRadius <= 0d
            ? 0d
            : Math.Min(Sweep / 2d, (railOuter - railInner) / 2d / centreRadius * 180d / Math.PI);

        _band = BuildBand(
            centre,
            railInner,
            railOuter,
            StartAngle + capDegrees,
            EndAngle - capDegrees,
            roundStart: true,
            roundEnd: true);

        double reading = AngleFor(Value);
        _sweep = reading > StartAngle
            ? BuildBand(
                centre,
                railInner,
                railOuter,
                StartAngle,
                reading,
                roundStart: false,
                roundEnd: false)
            : null;
    }

    private void RenderUnavailable(DrawingContext context, double scale)
    {
        // An outline, not an empty band: a band with no sweep reads as a reported zero, and
        // DESIGN.md is explicit that an unreported reading is never shown as a zero.
        if (TrackBrush is not { } brush || _band is not { } band)
        {
            return;
        }

        context.DrawGeometry(null, new Pen(brush, Hairline.ThicknessFor(scale)), band);
    }

    /// <summary>
    /// The graduations: a hairline per level, hung from the outer edge of the face inward,
    /// full depth at a major and half depth at the rest.
    /// </summary>
    private void RenderScale(
        DrawingContext context,
        Point centre,
        double radius,
        double outer,
        double scale,
        IBrush brush)
    {
        var pen = new Pen(brush, Hairline.ThicknessFor(scale));
        double major = Hairline.SnapEdge(outer - ScaleDepth, scale);
        double minor = Hairline.SnapEdge(outer - (ScaleDepth / 2d), scale);

        foreach (double level in GraduationsFor(radius))
        {
            double degrees = AngleFor(level);
            double inner = InstrumentScale.IsMajorGraduation(level) ? major : minor;

            context.DrawLine(pen, Polar(centre, inner, degrees), Polar(centre, outer, degrees));
        }
    }

    /// <summary>
    /// The threshold index: the one mark that crosses the rail and runs the whole depth of
    /// the face, at twice a hairline. Nothing else on the dial does either, which is what
    /// tells it apart from a graduation without asking anybody to compare two greys.
    /// </summary>
    private void RenderIndex(
        DrawingContext context,
        Point centre,
        double threshold,
        double railInner,
        double outer,
        double scale,
        IBrush brush)
    {
        var pen = new Pen(brush, Hairline.ThicknessFor(scale) * IndexWeight);
        double degrees = AngleFor(threshold);

        context.DrawLine(pen, Polar(centre, railInner, degrees), Polar(centre, outer, degrees));
    }

    /// <summary>
    /// The design system's ring - 2px, offset 2px, outside the control - drawn round rather
    /// than square, because the thing it is marking is round. A rounded rectangle held two
    /// pixels clear of a 144 circle stands two off it at the sides and nearly thirty at the
    /// corners, which reads as a box somebody drew round the dial rather than as the dial
    /// being focused.
    /// </summary>
    private void RenderFocusRing(DrawingContext context, Point centre, double radius, IBrush brush)
    {
        double weight = Math.Max(0d, FocusRingWidth);
        if (weight <= 0d)
        {
            return;
        }

        // The pen strokes astride the circle it is given, so that circle is the middle of the
        // ring: the offset plus half the weight out from the face.
        double ring = radius + Math.Max(0d, FocusRingOffset) + (weight / 2d);

        context.DrawEllipse(null, new Pen(brush, weight), centre, ring, ring);
    }

    private void UpdateAboveThreshold()
    {
        bool above = Value is { } value && Threshold is { } threshold && value >= threshold;
        IsAboveThreshold = above;
        PseudoClasses.Set(AboveThresholdPseudoClass, above);
    }

    /// <summary>
    /// Keeps the tip and the tab order in step with what the dial has to say.
    /// </summary>
    /// <remarks>
    /// The dial is a tab stop exactly when it reveals something the words beside it do not
    /// already print: the threshold. A dial with no threshold, or with nothing to measure
    /// against one, would be a stop on the way to the next control that read out the figure
    /// standing inside it.
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
}
