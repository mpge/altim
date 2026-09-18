using Altim.UI.Formatting;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim dial: <see cref="Meter"/>'s cross section bent round a swept arc, drawn directly
/// rather than templated. It reports one level per provider and how close each is to its own
/// ceiling.
/// </summary>
/// <remarks>
/// <para>
/// <b>One face, one sweep per provider, concentric.</b> The rings share the face, the
/// graduation tape and the angular mapping because they share a <i>unit</i>: how much of that
/// provider's own allowance is gone. Nothing is combined. Two vendors' percentages are
/// proportions of two different, undisclosed allowances, so summing, averaging or weighting
/// them would produce an authoritative looking figure that measures nothing, and neither
/// vendor publishes the allowance that would make one meaningful.
/// </para>
/// <para>
/// The band is the meter's, to the pixel, when there is one reading: a
/// <see cref="RailThickness"/> rail with fully round ends, a <see cref="ScaleGap"/> gap, and a
/// <see cref="ScaleDepth"/> row of graduations - the same <see cref="FaceDepth"/> in all, and
/// the same tokens. The rail is innermost and the engraving outermost, so the scale stands on
/// the far side of the rail from the figure, the way a metric row puts the figure above the
/// rail and the scale below it. Further readings add rings <em>inward</em> at
/// <see cref="RingThicknessFor"/>, so the engraving never moves and two faces are still read
/// against the same tape.
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
/// where two of them stand closest together - see <see cref="GraduationSpanFor"/>. It does not
/// depend on how many rings the face is carrying. <b>The mapping from a level to an angle
/// stays linear</b>; it is the graduations that crowd.
/// </para>
/// <para>
/// The threshold is an index rather than a colour change: the one mark that crosses the rail
/// and carries on through the engraving, and the only one drawn at <see cref="IndexWeight"/>
/// hairlines. Position, extent and weight all say what it is, so it survives being printed,
/// photocopied or read by somebody who cannot tell two greys apart.
/// </para>
/// <para>
/// <b>The sweep is banded.</b> Its colour is a property of where it has reached - normal,
/// caution, past the threshold - rather than a repaint of the whole instrument, and both
/// boundaries land on a mark the face draws anyway: the full depth graduation at
/// <see cref="InstrumentScale.HalfWay"/> and the threshold index. Take every hue away and the
/// arc's length, those two marks and the figure still say the same thing. See
/// <see cref="DialBands"/>.
/// </para>
/// <para>
/// A reading with no value is not zero. It renders the unavailable state - its ring as a
/// hairline outline, with no track and no sweep - so an unreported reading cannot be read as a
/// reported nothing, and so there is no sweep sitting at the bottom of the dial pretending to
/// be one. When <em>no</em> reading reports anything the engraving and the index go too: the
/// scale is the apparatus for reading a level and there is no level. The control measures the
/// same either way, so a reading arriving does not reflow the panel.
/// </para>
/// <para>
/// <b>Nothing here animates and no geometry is built per frame.</b> The paths are cached
/// against the size and the readings they were built for, and rebuilt only when one of those
/// moves. The dial's first surface is the tray panel, which is laid out once at start-up and
/// shown rather than constructed, and which keeps taking readings while it is hidden: a
/// transition on this control would rebuild a path per frame for a panel nobody is looking at,
/// and the panel's whole budget is that opening it is a show rather than a build. The meter
/// animates because it lives on a window somebody already has open.
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
    /// 128 it stands 1, which reads as the figure touching the instrument. A second and a
    /// third ring take that clearance to 5 and to 4, which is why three is the most this face
    /// carries: see <see cref="FaceDepthFor"/>.
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

    /// <summary>The whole band for one reading, from the engraving's outer edge to the rail's
    /// inner edge.</summary>
    public const double FaceDepth = InstrumentScale.TotalDepth;

    /// <inheritdoc cref="InstrumentScale.IndexWeight" />
    public const double IndexWeight = InstrumentScale.IndexWeight;

    /// <summary>The clear space between one ring and the next.</summary>
    /// <remarks>
    /// The same 2 the rail already stands off the engraving. Two rings with no gap read as
    /// one thick rail with a seam in it.
    /// </remarks>
    public const double RingGap = ScaleGap;

    /// <summary>How thick each ring is when the face carries two readings.</summary>
    /// <remarks>
    /// The thickest ring that still leaves the widest figure 5 clear of the innermost rail:
    /// two rings of 5 with a 2 between them is a 12 stack, which with the engraving and its
    /// gap is 18 deep and leaves a clear circle 108 across.
    /// </remarks>
    public const double PairedRingThickness = 5d;

    /// <summary>The thinnest a ring is ever drawn.</summary>
    /// <remarks>
    /// A hairline is one device independent pixel, so three is a band with a pixel of ink
    /// either side of its middle: below that the ring stops reading as a band carrying a
    /// colour and starts reading as a rule.
    /// </remarks>
    public const double MinimumRingThickness = 3d;

    /// <summary>The widest mark a legend draws for a ring, in device independent pixels.</summary>
    public const double LegendMarkSize = 14d;

    /// <summary>How much smaller each ring's legend mark is than the one outside it.</summary>
    public const double LegendMarkStep = 4d;

    /// <summary>Set while no reading on the face reports a level.</summary>
    public const string UnavailablePseudoClass = ":unavailable";

    /// <summary>Set while a reading is at or above its threshold.</summary>
    public const string AboveThresholdPseudoClass = ":above-threshold";

    /// <summary>
    /// The level from 0 to 100, or null when the provider does not report it. Values
    /// outside the range are clamped; NaN is treated as unavailable.
    /// </summary>
    /// <remarks>
    /// The single reading case, which is what a provider card's dial uses: one provider, one
    /// window, one ring. Ignored while <see cref="Readings"/> carries anything.
    /// </remarks>
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<Dial, double?>(nameof(Value), coerce: CoercePercent);

    /// <summary>
    /// The configured threshold from 0 to 100, marked by the index, or null for no index.
    /// </summary>
    public static readonly StyledProperty<double?> ThresholdProperty =
        AvaloniaProperty.Register<Dial, double?>(nameof(Threshold), coerce: CoercePercent);

    /// <summary>
    /// One reading per provider, outermost first. Overrides <see cref="Value"/> when it
    /// carries anything.
    /// </summary>
    /// <remarks>
    /// The two properties are one picture seen from two surfaces rather than two sources of
    /// truth: a card heads itself with one provider's window and sets
    /// <see cref="Value"/>; the tray panel reports every provider at once and sets this. The
    /// control works from <see cref="Arcs"/>, which is whichever of them was supplied.
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<DialReading>?> ReadingsProperty =
        AvaloniaProperty.Register<Dial, IReadOnlyList<DialReading>?>(nameof(Readings));

    /// <summary>The band behind a sweep. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(TrackBrush));

    /// <summary>The sweep below the caution band. Supplied by the control theme.</summary>
    public static readonly StyledProperty<IBrush?> NormalBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(NormalBrush));

    /// <summary>The stretch of sweep inside the caution band. Supplied by the control theme.</summary>
    public static readonly StyledProperty<IBrush?> CautionBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(CautionBrush));

    /// <summary>The stretch of sweep past the threshold. Supplied by the control theme.</summary>
    public static readonly StyledProperty<IBrush?> ExceededBrushProperty =
        AvaloniaProperty.Register<Dial, IBrush?>(nameof(ExceededBrush));

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

    /// <summary>True while a reported reading is at or above its configured threshold.</summary>
    public static readonly DirectProperty<Dial, bool> IsAboveThresholdProperty =
        AvaloniaProperty.RegisterDirect<Dial, bool>(
            nameof(IsAboveThreshold),
            o => o.IsAboveThreshold);

    /// <summary>True while nothing on the face is reported.</summary>
    public static readonly DirectProperty<Dial, bool> IsUnavailableProperty =
        AvaloniaProperty.RegisterDirect<Dial, bool>(
            nameof(IsUnavailable),
            o => o.IsUnavailable);

    /// <summary>What the face is drawing, whichever property supplied it.</summary>
    /// <remarks>
    /// Raised rather than merely readable so a legend can take its rows from the face
    /// itself. A legend bound to the same list the view handed the dial would be a second
    /// copy of the truth, and two copies can be handed two lists.
    /// </remarks>
    public static readonly DirectProperty<Dial, IReadOnlyList<DialReading>> ArcsProperty =
        AvaloniaProperty.RegisterDirect<Dial, IReadOnlyList<DialReading>>(
            nameof(Arcs),
            o => o.Arcs);

    /// <summary>The one reading being read, or null while none is.</summary>
    /// <remarks>
    /// <b>One answer, resolved in one place.</b> A pointer on a ring, the keyboard on a ring
    /// and a legend row naming one all arrive here through <see cref="Settle"/>, so the
    /// hairline on the face and a mark on anything standing beside it cannot end up naming
    /// two different readings.
    /// </remarks>
    public static readonly DirectProperty<Dial, DialReading?> MarkedProperty =
        AvaloniaProperty.RegisterDirect<Dial, DialReading?>(nameof(Marked), o => o.Marked);

    private bool _isAboveThreshold;
    private bool _isUnavailable = true;

    private IReadOnlyList<DialReading> _arcs = [];
    private Ring[] _rings = [];
    private IReadOnlyList<DialReading>? _builtForArcs;
    private Point _builtAround;
    private double _builtForRadius = double.NaN;

    private int? _hovered;
    private int? _keyboard;
    private int? _named;
    private int? _at;
    private DialReading? _marked;
    private string? _captioned;

    static Dial()
    {
        AffectsRender<Dial>(
            ValueProperty,
            ThresholdProperty,
            ReadingsProperty,
            TrackBrushProperty,
            NormalBrushProperty,
            CautionBrushProperty,
            ExceededBrushProperty,
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
        RebuildArcs();
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

    /// <inheritdoc cref="ReadingsProperty" />
    public IReadOnlyList<DialReading>? Readings
    {
        get => GetValue(ReadingsProperty);
        set => SetValue(ReadingsProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="NormalBrushProperty" />
    public IBrush? NormalBrush
    {
        get => GetValue(NormalBrushProperty);
        set => SetValue(NormalBrushProperty, value);
    }

    /// <inheritdoc cref="CautionBrushProperty" />
    public IBrush? CautionBrush
    {
        get => GetValue(CautionBrushProperty);
        set => SetValue(CautionBrushProperty, value);
    }

    /// <inheritdoc cref="ExceededBrushProperty" />
    public IBrush? ExceededBrush
    {
        get => GetValue(ExceededBrushProperty);
        set => SetValue(ExceededBrushProperty, value);
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
    /// What the dial actually draws: <see cref="Readings"/> when it carries anything, and
    /// otherwise the one reading <see cref="Value"/> and <see cref="Threshold"/> describe.
    /// </summary>
    /// <remarks>
    /// Rebuilt when one of those properties moves rather than per read, because the geometry
    /// cache is keyed on this list's identity: a list rebuilt per render would rebuild every
    /// path per render.
    /// </remarks>
    public IReadOnlyList<DialReading> Arcs => _arcs;

    /// <summary>
    /// What this dial reads, in words: every reading on it, each named, with the level and the
    /// threshold it is measured against. It is the accessible name.
    /// </summary>
    /// <remarks>
    /// Every ring, not only the one the figure in the middle belongs to. A client handed only
    /// the highest reading would be handed a dial with fewer sweeps on it than the face has.
    /// </remarks>
    public string Reading => string.Join(". ", _arcs.Select(static arc => arc.Words));

    /// <summary>
    /// What a pointer, the keyboard and a legend row reveal: the ring being read, when its
    /// window rolls over, and what the bands mean, or every ring at once when no one ring is
    /// being read.
    /// </summary>
    /// <remarks>
    /// Deliberately more than <see cref="Reading"/>. The extra sentence explains a colour
    /// code, which is exactly the thing a screen reader does not receive and a sighted reader
    /// has to be told once; putting it in the accessible name would read it out on every
    /// announcement to the one audience it cannot help.
    /// </remarks>
    public string? Detail => RevealsDetail ? DetailFor(_at) : null;

    /// <summary>Which ring a pointer is over, or null when it is over none.</summary>
    public int? HoveredRing => _hovered;

    /// <summary>Which ring the keyboard is on, or null when the dial is not focused.</summary>
    public int? FocusedRing => _keyboard;

    /// <inheritdoc cref="MarkedProperty" />
    public DialReading? Marked
    {
        get => _marked;
        private set => SetAndRaise(MarkedProperty, ref _marked, value);
    }

    /// <summary>Which ring is marked, or null when none is.</summary>
    /// <remarks>
    /// The list position, not <see cref="DialReading.Ring"/>: the face draws the list in the
    /// order it was handed, and a reading numbered differently from where it stands would
    /// send the mark to the wrong band.
    /// </remarks>
    public int? MarkedRing => _at;

    /// <summary>
    /// The outermost ring's path as it was last drawn, or null before the first render.
    /// </summary>
    /// <remarks>
    /// It is the same instance from one render to the next unless the face's radius or the
    /// readings have moved, and <see cref="RenderedSweep"/> is rebuilt with it. That is the
    /// whole of this control's answer to standing on the surface with an opening budget: a
    /// dial that built a fresh path every time the panel repainted would spend that budget on
    /// a picture that had not changed.
    /// </remarks>
    public Geometry? RenderedBand => _rings.Length == 0 ? null : _rings[0].Band;

    /// <summary>
    /// The outermost reading's path as it was last drawn, clipped to its ring.
    /// </summary>
    /// <remarks>
    /// Null when there is nothing to draw: an unreported reading, and a reading of nothing
    /// used. Those two are not the same picture - the first draws the ring as an outline and
    /// the second draws it filled - but neither of them puts a sweep on the dial, because a
    /// sweep of no length is not a reading of nothing. The banded stretches are drawn over
    /// this one rather than beside it, so there is no seam where two colours meet.
    /// </remarks>
    public Geometry? RenderedSweep => _rings.Length == 0 ? null : _rings[0].Sweep;

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

    /// <summary>How thick each ring is drawn when the face carries a given number.</summary>
    /// <param name="count">How many readings the face carries.</param>
    /// <returns>The thickness in device independent pixels.</returns>
    /// <remarks>
    /// One reading keeps the meter's own rail, to the pixel, because a card's meters and its
    /// dial are read against one another. Two share a stack that still leaves the widest
    /// figure clear of the innermost ring. Three take the thinnest ring that still reads as a
    /// band, and every radius stays a whole number so no ring is snapped a pixel thinner than
    /// the one outside it.
    /// </remarks>
    public static double RingThicknessFor(int count) => count switch
    {
        <= 1 => RailThickness,
        2 => PairedRingThickness,
        _ => MinimumRingThickness,
    };

    /// <summary>
    /// The whole band for a given number of readings, from the engraving's outer edge to the
    /// innermost ring's inner edge.
    /// </summary>
    /// <param name="count">How many readings the face carries.</param>
    /// <returns>The depth in device independent pixels.</returns>
    /// <remarks>
    /// 14 for one, 18 for two and 19 for three, which on a 144 face leave the figure standing
    /// 9, 5 and 4 clear of the innermost ring. A fourth ring would take that to less than
    /// nothing: the face carries three, and a fourth provider is where either the face or the
    /// figure inside it has to give way.
    /// </remarks>
    public static double FaceDepthFor(int count)
    {
        int rings = Math.Max(1, count);
        return ScaleDepth
            + ScaleGap
            + (RingThicknessFor(rings) * rings)
            + (RingGap * (rings - 1));
    }

    /// <summary>
    /// The span the scale is fitted to: the arc length the sweep covers at the radius the
    /// graduations reach furthest in, which is where two of them stand closest together.
    /// </summary>
    /// <param name="faceRadius">The radius of the face.</param>
    /// <returns>The span in device independent pixels, never negative.</returns>
    /// <remarks>
    /// Measuring at the outer edge instead would keep marks that are four pixels apart out
    /// there and less than that where they actually meet, which is the one thing
    /// <see cref="InstrumentScale.MinimumGraduationPitch"/> exists to prevent. It does not
    /// depend on the ring count: the engraving is the face's, not a ring's.
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

    /// <summary>
    /// Names one of this face's readings from somewhere off the face - a legend row being
    /// pointed at or arrived on - or stops naming one.
    /// </summary>
    /// <param name="reading">
    /// The reading to name, which has to be one this face carries, or null to stop.
    /// </param>
    /// <remarks>
    /// <para>
    /// The mark is still <em>resolved</em> rather than assigned: this is one of the three
    /// ways a reading can be asked for, and <see cref="Settle"/> decides between them. That
    /// is what stops a legend and the face disagreeing - there is one answer, and the legend
    /// reads it back out of <see cref="Marked"/> rather than keeping its own.
    /// </para>
    /// <para>
    /// A reading this face does not carry is ignored rather than clearing the mark. A legend
    /// holding a different list cannot speak for this face, and blanking the face on its word
    /// would hide that mistake instead of leaving it where somebody can see it.
    /// </para>
    /// </remarks>
    public void Mark(DialReading? reading)
    {
        int? at = null;
        if (reading is not null)
        {
            at = IndexOf(reading);
            if (at is null)
            {
                return;
            }
        }

        if (_named == at)
        {
            return;
        }

        _named = at;
        Settle();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Size size = Bounds.Size;
        double radius = FaceRadiusFor(size);
        if (radius < FaceDepthFor(_arcs.Count))
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

        EnsureGeometry(centre, radius, scale);

        if (IsUnavailable)
        {
            foreach (Ring ring in _rings)
            {
                RenderOutline(context, ring, scale);
            }
        }
        else
        {
            foreach (Ring ring in _rings)
            {
                RenderRing(context, ring, scale);
            }

            if (ScaleBrush is { } engraving)
            {
                RenderScale(context, centre, radius, scaleOuter, scale, engraving);
            }

            if (ThresholdBrush is { } index)
            {
                RenderIndexes(context, centre, scaleOuter, scale, index);
            }
        }

        if (_at is { } read && ThresholdBrush is { } marker)
        {
            RenderRead(context, read, scale, marker);
        }

        if (IsFocused && FocusRingBrush is { } focusRing)
        {
            RenderFocusRing(context, centre, radius, focusRing);
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
    /// A ring pointed at is a ring named. The tip is opened here rather than left to the
    /// hover service, which arms itself when the tip stops being null: by the time the dial
    /// knows which ring the pointer is on the pointer has already entered, so a tip merely
    /// set would not appear until the pointer left the face and came back.
    /// </remarks>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);
        Hover(RingAt(e.GetPosition(this)));
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Focus opens the same tip a pointer opens, on the same ring the legend reads first,
    /// because it is the same detail. The words beside the dial print the level and name the
    /// window; which ring belongs to whom, the threshold, and what the colours mean are the
    /// things only the dial knows, and a dial that answered only a pointer would keep all
    /// three from anybody using a keyboard.
    /// </remarks>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        Read(_arcs.Count == 0 ? null : 0);
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        Read(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The arrows step between the rings, outward and inward, and stop at the ends rather
    /// than wrapping. They are marked handled only on a face carrying more than one reading:
    /// a card's single dial stands inside the Overview's scrolling area, and one that
    /// swallowed an arrow key would stop the page scrolling to say nothing.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnKeyDown(e);

        if (e.Handled
            || _arcs.Count < 2
            || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End))
        {
            return;
        }

        int at = _keyboard ?? 0;
        int next = e.Key switch
        {
            Key.Up or Key.Left => at - 1,
            Key.Down or Key.Right => at + 1,
            Key.Home => 0,
            _ => _arcs.Count - 1,
        };

        Read(Math.Clamp(next, 0, _arcs.Count - 1));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty
            || change.Property == ThresholdProperty
            || change.Property == ReadingsProperty)
        {
            RebuildArcs();
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

    /// <summary>Whether anything on the face reports a level.</summary>
    private bool AnyReported
    {
        get
        {
            foreach (DialReading arc in _arcs)
            {
                if (arc.Value is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether there is anything to reveal that the words beside it do not print.</summary>
    /// <remarks>
    /// On a single reading that is the threshold, as it always was: a dial with nothing to
    /// measure against one is not a stop on the way to the next control. On a face carrying
    /// several it is always something, <b>including when nothing on it is reported</b>: the
    /// rings are all outlines then and nothing but their words says which provider each one
    /// belongs to, and "not reported by this provider" is an answer rather than an absence.
    /// A ring that could be marked but not named would be the one thing on this face that
    /// pointed at nothing.
    /// </remarks>
    private bool RevealsDetail => _arcs.Count switch
    {
        0 => false,
        1 => _arcs[0].Value is not null && _arcs[0].Threshold is not null,
        _ => true,
    };

    /// <summary>
    /// Rebuilds the list the control draws from, and everything that reads off it.
    /// </summary>
    private void RebuildArcs()
    {
        IReadOnlyList<DialReading> arcs = Readings is { Count: > 0 } supplied
            ? supplied
            : [new DialReading(0, string.Empty, Value, Threshold)];

        SetAndRaise(ArcsProperty, ref _arcs, arcs);

        if (_hovered >= _arcs.Count)
        {
            _hovered = null;
        }

        if (_keyboard >= _arcs.Count)
        {
            _keyboard = null;
        }

        if (_named >= _arcs.Count)
        {
            _named = null;
        }

        IsUnavailable = !AnyReported;
        PseudoClasses.Set(UnavailablePseudoClass, IsUnavailable);
        UpdateAboveThreshold();

        // The readings themselves have been replaced, so the mark is resolved again against
        // the list that is actually on the face rather than left pointing into the last one.
        Settle();
        UpdateReading();
    }

    /// <summary>
    /// Rebuilds the rings, and only when the size or the readings they were built for have
    /// moved. A path per frame on the one surface with an opening budget is how that budget
    /// is lost.
    /// </summary>
    private void EnsureGeometry(Point centre, double radius, double scale)
    {
        if (_rings.Length == _arcs.Count
            && ReferenceEquals(_builtForArcs, _arcs)
            && centre.Equals(_builtAround)
            && radius.Equals(_builtForRadius))
        {
            return;
        }

        // The centre is part of the key, not just the radius. A control laid out wider without
        // getting any taller keeps the same face and moves it, and a path cached on the radius
        // alone would then be drawn where the face used to be.
        _builtForArcs = _arcs;
        _builtAround = centre;
        _builtForRadius = radius;

        int count = _arcs.Count;
        double thickness = RingThicknessFor(count);
        double top = radius - ScaleDepth - ScaleGap;
        var rings = new Ring[count];

        for (int i = 0; i < count; i++)
        {
            DialReading arc = _arcs[i];
            double outer = Hairline.SnapEdge(top - (i * (thickness + RingGap)), scale);
            double inner = Hairline.SnapEdge(outer - thickness, scale);

            // The rail's round ends bulge past the angles its centreline runs between, so the
            // centreline stops a cap short of each end and the caps land exactly on nothing
            // used and on the ceiling. That is what the meter's rounded rail does: the
            // leftmost pixel of the rail is level zero, not level zero less a corner radius.
            double centreRadius = (inner + outer) / 2d;
            double cap = centreRadius <= 0d
                ? 0d
                : Math.Min(Sweep / 2d, (outer - inner) / 2d / centreRadius * 180d / Math.PI);

            rings[i] = new Ring
            {
                Inner = inner,
                Outer = outer,
                Band = BuildBand(
                    centre,
                    inner,
                    outer,
                    StartAngle + cap,
                    EndAngle - cap,
                    roundStart: true,
                    roundEnd: true),
                Sweep = Stretch(centre, inner, outer, 0d, arc.Value),
                Caution = Stretch(centre, inner, outer, DialBands.CautionFrom(arc.Threshold), arc.Value),
                Exceeded = Stretch(centre, inner, outer, DialBands.ExceededFrom(arc.Threshold), arc.Value),
                Reported = arc.Value is not null,
                Threshold = arc.Threshold,
            };
        }

        _rings = rings;
    }

    /// <summary>
    /// One stretch of a reading, from a level to wherever the reading got to, or null when
    /// the reading never got that far.
    /// </summary>
    /// <remarks>
    /// Each stretch runs from its own band's boundary all the way to the reading rather than
    /// to the next boundary, and they are drawn outermost band last. Abutting stretches would
    /// leave a hairline of track showing wherever two antialiased square ends met; drawn this
    /// way each later colour covers the one before it and the only end that is ever seen is
    /// the reading's own.
    /// </remarks>
    private static Geometry? Stretch(
        Point centre,
        double inner,
        double outer,
        double? from,
        double? level)
    {
        if (from is not { } start || level is not { } reading || reading <= start)
        {
            return null;
        }

        return BuildBand(
            centre,
            inner,
            outer,
            AngleFor(start),
            AngleFor(reading),
            roundStart: false,
            roundEnd: false);
    }

    private void RenderOutline(DrawingContext context, Ring ring, double scale)
    {
        // An outline, not an empty band: a band with no sweep reads as a reported zero, and
        // DESIGN.md is explicit that an unreported reading is never shown as a zero.
        if (TrackBrush is { } brush)
        {
            context.DrawGeometry(null, new Pen(brush, Hairline.ThicknessFor(scale)), ring.Band);
        }
    }

    private void RenderRing(DrawingContext context, Ring ring, double scale)
    {
        if (!ring.Reported)
        {
            RenderOutline(context, ring, scale);
            return;
        }

        if (TrackBrush is { } track)
        {
            context.DrawGeometry(track, null, ring.Band);
        }

        // Clipped to the band for the same reason the meter clips its fill to the rounded
        // rail: a stretch's own ends are square, and the round end at nothing used belongs to
        // the rail rather than to the reading.
        using (context.PushGeometryClip(ring.Band))
        {
            Paint(context, NormalBrush, ring.Sweep);
            Paint(context, CautionBrush, ring.Caution);
            Paint(context, ExceededBrush, ring.Exceeded);
        }
    }

    private static void Paint(DrawingContext context, IBrush? brush, Geometry? stretch)
    {
        if (brush is not null && stretch is not null)
        {
            context.DrawGeometry(brush, null, stretch);
        }
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
    /// The threshold indexes: the marks that cross a ring and run on to the face's edge, at
    /// twice a hairline. Nothing else on the dial does either, which is what tells one apart
    /// from a graduation without asking anybody to compare two greys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One per <em>distinct</em> threshold rather than one per ring, drawn from the innermost
    /// ring that measures against it. Two providers sharing a threshold share the one mark,
    /// which is the picture a single reading has always drawn; two that do not get one mark
    /// each, each starting at its own ring.
    /// </para>
    /// <para>
    /// A ring with nothing reported carries no index and none is drawn across it. The index
    /// says where <em>this reading</em> is measured against a threshold, and a reading nobody
    /// reported has nothing to measure: ink laid across an outlined ring would be the one
    /// thing on an unreported ring that looked like a reading.
    /// </para>
    /// </remarks>
    private void RenderIndexes(
        DrawingContext context,
        Point centre,
        double outer,
        double scale,
        IBrush brush)
    {
        var pen = new Pen(brush, Hairline.ThicknessFor(scale) * IndexWeight);

        for (int i = 0; i < _rings.Length; i++)
        {
            if (!_rings[i].Reported || _rings[i].Threshold is not { } threshold)
            {
                continue;
            }

            bool deeper = false;
            for (int j = i + 1; j < _rings.Length; j++)
            {
                deeper |= _rings[j].Reported
                    && _rings[j].Threshold is { } other
                    && other.Equals(threshold);
            }

            if (deeper)
            {
                continue;
            }

            double degrees = AngleFor(threshold);
            context.DrawLine(pen, Polar(centre, _rings[i].Inner, degrees), Polar(centre, outer, degrees));
        }
    }

    /// <summary>
    /// Marks the ring being read: a hairline traced round its whole band, in the engraving's
    /// own ink.
    /// </summary>
    /// <remarks>
    /// Static, because it says <em>which</em> rather than how much, and because the surface
    /// this control heads goes on repainting while nobody is watching it. It is drawn round
    /// the ring rather than over the reading so it does not touch the sweep's own length, and
    /// it is the same mark whether a pointer, the keyboard or a legend row asked for it - a
    /// <see cref="DialLegend"/> traces the row it names with the same hairline in the same
    /// ink, so the two ends of the pairing light up as one gesture.
    /// </remarks>
    private void RenderRead(DrawingContext context, int read, double scale, IBrush brush)
    {
        if (read < 0 || read >= _rings.Length)
        {
            return;
        }

        context.DrawGeometry(null, new Pen(brush, Hairline.ThicknessFor(scale)), _rings[read].Band);
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

    /// <summary>Which ring a point on the face lies on, or null for none.</summary>
    /// <remarks>
    /// Generous by half a gap either side, so the clear space between two rings belongs to
    /// the nearer of them and the engraving belongs to the ring under it: a pointer a pixel
    /// off a 3 thick ring is pointing at that ring. Inside the innermost ring is the figure's
    /// room and belongs to no ring, and so is anything outside the arc the sweep is drawn
    /// over.
    /// </remarks>
    private int? RingAt(Point point)
    {
        if (_rings.Length == 0)
        {
            return null;
        }

        Size size = Bounds.Size;
        double dx = point.X - (size.Width / 2d);
        double dy = point.Y - (size.Height / 2d);
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        double bearing = Math.Atan2(dx, -dy) * 180d / Math.PI;

        if (bearing < StartAngle || bearing > EndAngle)
        {
            return null;
        }

        for (int i = 0; i < _rings.Length; i++)
        {
            double outer = i == 0 ? FaceRadiusFor(size) : _rings[i].Outer + (RingGap / 2d);
            double inner = _rings[i].Inner - (i == _rings.Length - 1 ? 0d : RingGap / 2d);

            if (distance <= outer && distance >= inner)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Takes note of which ring a pointer is on, and says what it reads.</summary>
    private void Hover(int? ring)
    {
        if (_hovered == ring)
        {
            return;
        }

        _hovered = ring;
        Settle();
    }

    /// <summary>Takes note of which ring the keyboard is on, and says what it reads.</summary>
    private void Read(int? ring)
    {
        if (_keyboard == ring)
        {
            return;
        }

        _keyboard = ring;
        Settle();
    }

    /// <summary>
    /// Resolves the three ways a reading can be asked for into the one answer everything
    /// draws from.
    /// </summary>
    /// <remarks>
    /// A pointer on a ring wins, because it is the most direct thing anybody can do to this
    /// face; a legend row being pointed at or arrived on comes next; and the keyboard's own
    /// ring is what is left, so letting go of a legend row falls back to the ring the
    /// keyboard is still standing on rather than to nothing. Written once, here: the face's
    /// hairline and a legend's mark both read <see cref="Marked"/>, so there is no second
    /// copy of the answer to drift out of step with this one.
    /// </remarks>
    private void Settle()
    {
        int? at = _hovered ?? _named ?? _keyboard;
        DialReading? reading = at is { } index && index >= 0 && index < _arcs.Count
            ? _arcs[index]
            : null;

        _at = reading is null ? null : at;

        if (SetAndRaise(MarkedProperty, ref _marked, reading))
        {
            Caption();
        }
    }

    /// <summary>Where a reading stands on this face, or null when it is not on it.</summary>
    private int? IndexOf(DialReading reading)
    {
        for (int i = 0; i < _arcs.Count; i++)
        {
            if (ReferenceEquals(_arcs[i], reading))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Shows the words belonging to whichever ring is being read, or takes them away.
    /// </summary>
    /// <remarks>
    /// The pointer wins while it is on a ring and the keyboard's ring is what is left when it
    /// is not, so hover and focus cannot drift into two descriptions of one face. The tip is
    /// kept current either way; it is only <em>opened</em> while something is actually
    /// reading the dial.
    /// </remarks>
    private void Caption()
    {
        string? words = Detail;
        if (!string.Equals(words, _captioned, StringComparison.Ordinal))
        {
            _captioned = words;
            ToolTip.SetTip(this, words);
        }

        // Opened rather than merely set, for the reason the usage map opens its own: the
        // hover service arms itself when the tip stops being null, and by then the pointer
        // has already entered, so a tip only set would not appear until it left and came
        // back onto the same ring.
        ToolTip.SetIsOpen(this, words is { Length: > 0 } && (IsPointerOver || IsFocused));
        InvalidateVisual();
    }

    /// <summary>The words one ring reads, or every ring's when none is singled out.</summary>
    private string DetailFor(int? ring) =>
        ring is { } at && at >= 0 && at < _arcs.Count
            ? _arcs[at].Detailed
            : string.Join(" ", _arcs.Select(static arc => arc.Detailed));

    private void UpdateAboveThreshold()
    {
        bool above = false;
        foreach (DialReading arc in _arcs)
        {
            above |= arc.Value is { } value && arc.Threshold is { } threshold && value >= threshold;
        }

        IsAboveThreshold = above;
        PseudoClasses.Set(AboveThresholdPseudoClass, above);
    }

    /// <summary>
    /// Keeps the tip and the tab order in step with what the dial has to say.
    /// </summary>
    /// <remarks>
    /// The dial is a tab stop exactly when it reveals something the words beside it do not
    /// already print. A single dial with no threshold, or with nothing to measure against one,
    /// would be a stop on the way to the next control that read out the figure standing inside
    /// it; a face carrying several rings always has which ring is whose to add.
    /// </remarks>
    private void UpdateReading()
    {
        Focusable = RevealsDetail;
        _captioned = Detail;
        ToolTip.SetTip(this, _captioned);

        if (_captioned is null)
        {
            ToolTip.SetIsOpen(this, false);
        }
    }

    /// <summary>One ring's paths and the facts the renderer needs about it.</summary>
    private sealed class Ring
    {
        /// <summary>The ring's inner radius, snapped.</summary>
        public required double Inner { get; init; }

        /// <summary>The ring's outer radius, snapped.</summary>
        public required double Outer { get; init; }

        /// <summary>The whole ring, with the rail's round ends.</summary>
        public required Geometry Band { get; init; }

        /// <summary>Nothing used to the reading, or null when there is no sweep to draw.</summary>
        public required Geometry? Sweep { get; init; }

        /// <summary>The caution boundary to the reading, or null when it never got there.</summary>
        public required Geometry? Caution { get; init; }

        /// <summary>The threshold to the reading, or null when it never got there.</summary>
        public required Geometry? Exceeded { get; init; }

        /// <summary>Whether this reading has a level at all.</summary>
        public required bool Reported { get; init; }

        /// <summary>The level this ring's index stands at, or null when it has none.</summary>
        public required double? Threshold { get; init; }
    }
}
