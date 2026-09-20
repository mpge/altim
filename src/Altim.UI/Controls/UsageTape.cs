using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim history tape: hairline level lines at 0, 50 and 100 per cent, one line per
/// provider over them, and dates along the bottom.
/// </summary>
/// <remarks>
/// <para>
/// There is no charting dependency and no chart furniture. No fills, no gradients, no
/// legend box, no axis frame. Three rules is the fewest that still says which way is up:
/// the top is the limit, the bottom is nothing used, and the middle is half. Dots mark
/// individual samples only while a series has fewer than <see cref="DotSampleLimit"/>
/// points, because past that they stop being readable and start being texture.
/// </para>
/// <para>
/// The provider names can be set at the end of each line or handed to a legend outside the
/// control - <see cref="ShowSeriesNames"/> chooses. Inside a panel the legend sits under the
/// panel's own heading, where it reads as part of the heading rather than as two words
/// floating in the plot, and it is also the arrangement that survives two lines finishing at
/// the same level.
/// </para>
/// <para>
/// Time runs left to right and the newest sample is always at the right edge, so a series
/// of one sample is drawn at the right edge rather than stranded on the left.
/// </para>
/// <para>
/// <see cref="Series"/> is frozen on assignment. The tape deep copies what it is handed and
/// draws the copy, so the picture can never drift from the assignment that produced it, and
/// the value read back out cannot be mutated by anyone. If the assigned collection raises
/// <see cref="INotifyCollectionChanged"/> the tape subscribes and re-freezes on every
/// change, so an <c>ObservableCollection</c> mutated in place repaints. That is the whole
/// contract: the tape is a function of assignments and notifications, with no third state
/// in which it is showing half of a change. The subscription lasts only as long as the tape
/// is in a visual tree, because it is a reference from the collection to the control and
/// the collection outlives the view.
/// </para>
/// <para>
/// Level rules are snapped to whole device pixels through <see cref="Hairline"/>, and the
/// level labels are set in the tabular figures the rest of the system uses, so 25 and 100
/// line up under one another on a 125% display as well as on a 100% one.
/// </para>
/// </remarks>
public sealed class UsageTape : Control
{
    /// <summary>Sample dots are drawn only while a series has fewer points than this.</summary>
    public const int DotSampleLimit = 32;

    /// <summary>The levels the hairline rules are drawn at.</summary>
    public static readonly IReadOnlyList<double> Levels = [0d, 50d, 100d];

    private const double GutterGap = 6d;
    private const double LabelGap = 2d;
    private const double DefaultWidth = 320d;
    private const double DefaultHeight = 96d;

    /// <summary>The provider histories to draw. Null or empty renders the empty state.</summary>
    public static readonly StyledProperty<IReadOnlyList<UsageTapeSeries>?> SeriesProperty =
        AvaloniaProperty.Register<UsageTape, IReadOnlyList<UsageTapeSeries>?>(
            nameof(Series),
            coerce: Freeze);

    /// <summary>The brush for the <see cref="Levels"/> rules. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> LevelLineBrushProperty =
        AvaloniaProperty.Register<UsageTape, IBrush?>(nameof(LevelLineBrush));

    /// <summary>The primary series line. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> PrimaryLineBrushProperty =
        AvaloniaProperty.Register<UsageTape, IBrush?>(nameof(PrimaryLineBrush));

    /// <summary>The secondary series line. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> SecondaryLineBrushProperty =
        AvaloniaProperty.Register<UsageTape, IBrush?>(nameof(SecondaryLineBrush));

    /// <summary>Level labels and the empty sentence. Supplied by the control theme.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageTape, IBrush?>(nameof(LabelBrush));

    /// <summary>The one sentence shown when there is nothing to draw.</summary>
    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<UsageTape, string>(
            nameof(EmptyText),
            "No usage recorded yet. Altim starts collecting when an agent runs.");

    /// <summary>Whether the level rules are labelled outside the plot.</summary>
    public static readonly StyledProperty<bool> ShowLevelLabelsProperty =
        AvaloniaProperty.Register<UsageTape, bool>(nameof(ShowLevelLabels), true);

    /// <summary>
    /// Whether each provider's name is set at the end of its own line. False when a legend
    /// outside the control is naming them instead.
    /// </summary>
    public static readonly StyledProperty<bool> ShowSeriesNamesProperty =
        AvaloniaProperty.Register<UsageTape, bool>(nameof(ShowSeriesNames), true);

    /// <summary>
    /// The dates written along the bottom of the plot, evenly spaced from the left edge to
    /// the right. Empty draws no axis at all rather than an axis of nothing.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> AxisLabelsProperty =
        AvaloniaProperty.Register<UsageTape, IReadOnlyList<string>?>(nameof(AxisLabels));

    /// <summary>The size of the level labels and inline series names. Caption, 11px.</summary>
    public static readonly StyledProperty<double> CaptionFontSizeProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(CaptionFontSize), 11d);

    /// <summary>The series line weight. 1.5px.</summary>
    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(LineThickness), 1.5d);

    /// <summary>The sample dot diameter. 3px.</summary>
    public static readonly StyledProperty<double> DotDiameterProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(DotDiameter), 3d);

    private INotifyCollectionChanged? _observed;
    private bool _attached;

    static UsageTape()
    {
        AffectsRender<UsageTape>(
            SeriesProperty,
            LevelLineBrushProperty,
            PrimaryLineBrushProperty,
            SecondaryLineBrushProperty,
            LabelBrushProperty,
            EmptyTextProperty,
            ShowLevelLabelsProperty,
            ShowSeriesNamesProperty,
            AxisLabelsProperty,
            CaptionFontSizeProperty,
            LineThicknessProperty,
            DotDiameterProperty);

        AffectsRender<UsageTape>(TextElement.FontFeaturesProperty);
    }

    /// <inheritdoc cref="SeriesProperty" />
    public IReadOnlyList<UsageTapeSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <inheritdoc cref="LevelLineBrushProperty" />
    public IBrush? LevelLineBrush
    {
        get => GetValue(LevelLineBrushProperty);
        set => SetValue(LevelLineBrushProperty, value);
    }

    /// <inheritdoc cref="PrimaryLineBrushProperty" />
    public IBrush? PrimaryLineBrush
    {
        get => GetValue(PrimaryLineBrushProperty);
        set => SetValue(PrimaryLineBrushProperty, value);
    }

    /// <inheritdoc cref="SecondaryLineBrushProperty" />
    public IBrush? SecondaryLineBrush
    {
        get => GetValue(SecondaryLineBrushProperty);
        set => SetValue(SecondaryLineBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc cref="EmptyTextProperty" />
    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <inheritdoc cref="ShowLevelLabelsProperty" />
    public bool ShowLevelLabels
    {
        get => GetValue(ShowLevelLabelsProperty);
        set => SetValue(ShowLevelLabelsProperty, value);
    }

    /// <inheritdoc cref="ShowSeriesNamesProperty" />
    public bool ShowSeriesNames
    {
        get => GetValue(ShowSeriesNamesProperty);
        set => SetValue(ShowSeriesNamesProperty, value);
    }

    /// <inheritdoc cref="AxisLabelsProperty" />
    public IReadOnlyList<string>? AxisLabels
    {
        get => GetValue(AxisLabelsProperty);
        set => SetValue(AxisLabelsProperty, value);
    }

    /// <inheritdoc cref="CaptionFontSizeProperty" />
    public double CaptionFontSize
    {
        get => GetValue(CaptionFontSizeProperty);
        set => SetValue(CaptionFontSizeProperty, value);
    }

    /// <inheritdoc cref="LineThicknessProperty" />
    public double LineThickness
    {
        get => GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    /// <inheritdoc cref="DotDiameterProperty" />
    public double DotDiameter
    {
        get => GetValue(DotDiameterProperty);
        set => SetValue(DotDiameterProperty, value);
    }

    /// <summary>
    /// Maps a sample index to its horizontal position inside the plot. The last sample
    /// sits on the right edge, and a lone sample is the last sample.
    /// </summary>
    /// <param name="index">The zero based sample index.</param>
    /// <param name="count">The number of samples in the series.</param>
    /// <param name="plotWidth">The plot width.</param>
    /// <returns>The offset from the left edge of the plot.</returns>
    public static double XFor(int index, int count, double plotWidth)
    {
        if (count <= 1 || plotWidth <= 0d)
        {
            return plotWidth <= 0d ? 0d : plotWidth;
        }

        return plotWidth * index / (count - 1);
    }

    /// <summary>
    /// Maps a level to its vertical position inside the plot. Zero is the bottom edge and
    /// 100 is the top.
    /// </summary>
    /// <param name="level">The level from 0 to 100.</param>
    /// <param name="plotHeight">The plot height.</param>
    /// <returns>The offset from the top edge of the plot.</returns>
    public static double YFor(double level, double plotHeight) =>
        plotHeight * (1d - (Math.Clamp(level, 0d, 100d) / 100d));

    /// <summary>
    /// Pushes inline labels apart so two providers that end at a similar level do not set
    /// their names on top of one another.
    /// </summary>
    /// <remarks>
    /// Each label keeps the position its line asks for unless a nearer one has already
    /// taken the room. A forward pass over the labels in vertical order pushes overlaps
    /// down, and a backward pass pulls anything that ran past the bottom edge back up, so
    /// the set stays inside the plot and stays in the order the lines are in. When there is
    /// not enough room for every label they stack at the top rather than being drawn off
    /// the control.
    /// </remarks>
    /// <param name="tops">The wanted top offset of each label.</param>
    /// <param name="step">The smallest permitted distance between two tops.</param>
    /// <param name="minimum">The highest permitted top.</param>
    /// <param name="maximum">The lowest permitted top.</param>
    /// <returns>The placed tops, in the order they were given.</returns>
    public static IReadOnlyList<double> SpreadLabels(
        IReadOnlyList<double> tops,
        double step,
        double minimum,
        double maximum)
    {
        ArgumentNullException.ThrowIfNull(tops);

        int count = tops.Count;
        if (count == 0)
        {
            return [];
        }

        int[] order = [.. Enumerable.Range(0, count).OrderBy(i => tops[i])];
        double[] placed = new double[count];

        double running = double.NegativeInfinity;
        foreach (int index in order)
        {
            double top = Math.Max(Math.Max(tops[index], minimum), running);
            placed[index] = top;
            running = top + step;
        }

        running = double.PositiveInfinity;
        for (int i = count - 1; i >= 0; i--)
        {
            int index = order[i];
            double top = Math.Max(minimum, Math.Min(Math.Min(placed[index], maximum), running));
            placed[index] = top;
            running = top - step;
        }

        return placed;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0d || size.Height <= 0d)
        {
            return;
        }

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        double captionSize = Math.Max(1d, CaptionFontSize);
        IReadOnlyList<UsageTapeSeries> series = DrawableSeries();

        if (series.Count == 0)
        {
            // An empty state is a sentence, not an illustration. It is handed the control's
            // width to wrap inside: without one the sentence is laid out on a single line
            // and simply runs past the right edge, which reads as truncation.
            FormattedText sentence = Text(EmptyText, typeface, captionSize, LabelBrush);
            sentence.MaxTextWidth = size.Width;
            context.DrawText(sentence, new Point(0d, (size.Height - sentence.Height) / 2d));
            return;
        }

        double captionHeight = Text("0", typeface, captionSize, LabelBrush).Height;
        double pad = captionHeight / 2d;

        double leftGutter = 0d;
        if (ShowLevelLabels)
        {
            foreach (double level in Levels)
            {
                leftGutter = Math.Max(
                    leftGutter,
                    LevelText(LevelLabel(level), typeface, captionSize).Width);
            }

            leftGutter += GutterGap;
        }

        double rightGutter = 0d;
        if (ShowSeriesNames)
        {
            foreach (UsageTapeSeries s in series)
            {
                rightGutter = Math.Max(
                    rightGutter,
                    Text(s.Name, typeface, captionSize, LabelBrush).Width);
            }

            rightGutter += GutterGap;
        }

        IReadOnlyList<string> axis = AxisLabels ?? [];
        double bottomGutter = axis.Count > 1 ? captionHeight + GutterGap : 0d;

        var plot = new Rect(
            leftGutter,
            pad,
            size.Width - leftGutter - rightGutter,
            size.Height - (pad * 2d) - bottomGutter);

        if (plot.Width <= 0d || plot.Height <= 0d)
        {
            return;
        }

        RenderLevels(context, plot, typeface, captionSize);
        RenderAxis(context, axis, plot, typeface, captionSize);

        using (context.PushTransform(Matrix.CreateTranslation(plot.X, plot.Y)))
        {
            foreach (UsageTapeSeries s in series)
            {
                RenderSeriesLine(context, s, plot.Size);
            }
        }

        if (ShowSeriesNames)
        {
            RenderSeriesNames(context, series, plot, typeface, captionSize);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty)
        {
            Observe(change.GetNewValue<IReadOnlyList<UsageTapeSeries>?>());
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Observe(Series);

        // The collection is free to have changed while nobody was listening.
        Resync();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;

        // The handler is a reference from the collection to this control, and the
        // collection usually outlives the view: a tape that stayed subscribed after it
        // left the tree would keep the whole page alive.
        Observe(null);
    }

    /// <summary>
    /// Freezes whatever was assigned into a deep copy the tape owns, so nothing outside it
    /// can change what is drawn without the tape hearing about it.
    /// </summary>
    private static IReadOnlyList<UsageTapeSeries>? Freeze(
        AvaloniaObject sender,
        IReadOnlyList<UsageTapeSeries>? value)
    {
        _ = sender;
        return value switch
        {
            null => null,
            FrozenSeries frozen => frozen,
            _ => FrozenSeries.Of(value),
        };
    }

    private static string LevelLabel(double level) =>
        string.Concat(level.ToString("0", CultureInfo.CurrentCulture), "%");

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush? brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    /// <summary>The newest sample a series actually reported.</summary>
    private static (int Index, double Level)? LastReported(UsageTapeSeries series)
    {
        IReadOnlyList<double?> values = series.Values;
        for (int i = values.Count - 1; i >= 0; i--)
        {
            if (values[i] is { } level && !double.IsNaN(level))
            {
                return (i, level);
            }
        }

        return null;
    }

    /// <summary>
    /// A level label, set in the tabular figures the theme hands down. The rules are
    /// evenly spaced, so their labels have to be as well: proportional digits put the 1 of
    /// 100 on a different column from the 7 of 75.
    /// </summary>
    private FormattedText LevelText(string text, Typeface typeface, double size)
    {
        FormattedText label = Text(text, typeface, size, LabelBrush);
        if (TextElement.GetFontFeatures(this) is { } features)
        {
            label.SetFontFeatures(features);
        }

        return label;
    }

    private void Observe(IReadOnlyList<UsageTapeSeries>? value)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnSourceChanged;
            _observed = null;
        }

        if (_attached && value is FrozenSeries { Source: INotifyCollectionChanged observable })
        {
            _observed = observable;
            observable.CollectionChanged += OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resync();

    /// <summary>
    /// Takes the copy again. Re-freezing produces a new instance, which is a new value for
    /// the property, which is what makes AffectsRender repaint. SetCurrentValue rather than
    /// SetValue so a view that bound Series keeps its binding.
    /// </summary>
    private void Resync()
    {
        if (Series is FrozenSeries frozen)
        {
            SetCurrentValue(SeriesProperty, frozen.Source);
        }
    }

    private IReadOnlyList<UsageTapeSeries> DrawableSeries()
    {
        IReadOnlyList<UsageTapeSeries>? all = Series;
        if (all is null || all.Count == 0)
        {
            return [];
        }

        var drawable = new List<UsageTapeSeries>(all.Count);
        foreach (UsageTapeSeries s in all)
        {
            if (LastReported(s) is not null)
            {
                drawable.Add(s);
            }
        }

        return drawable;
    }

    private void RenderLevels(DrawingContext context, Rect plot, Typeface typeface, double captionSize)
    {
        if (LevelLineBrush is not { } rule)
        {
            return;
        }

        double scale = Hairline.ScaleOf(this);
        double weight = Hairline.ThicknessFor(scale);

        foreach (double level in Levels)
        {
            // Snapped to whole device pixels: an unsnapped rule at 125% is spread over two
            // rows at partial coverage and comes out a grey smear rather than a line.
            double centre = plot.Y + YFor(level, plot.Height);
            double top = Hairline.SnapCentre(centre, weight, scale);
            context.FillRectangle(rule, new Rect(plot.X, top, plot.Width, weight));

            if (!ShowLevelLabels)
            {
                continue;
            }

            FormattedText label = LevelText(LevelLabel(level), typeface, captionSize);
            context.DrawText(
                label,
                new Point(plot.X - GutterGap - label.Width, centre - (label.Height / 2d)));
        }
    }

    /// <summary>
    /// Writes the dates along the bottom. The first is left aligned on the plot's left edge
    /// and the last right aligned on its right edge, because a date centred on the edge
    /// hangs half of itself outside the control; the ones between are centred on their own
    /// position, which is where their samples are.
    /// </summary>
    /// <remarks>
    /// The caller decides how the span divides - whole days across a week - and the tape
    /// decides how many of those divisions there is room to write, because that depends on
    /// the width it was given, which the caller does not know. Where they do not all fit it
    /// writes every second or every third one rather than dropping whichever happens to
    /// collide: an axis of evenly spaced dates two days apart is a scale, and the same axis
    /// with one date missing from the middle of it looks like a defect.
    /// </remarks>
    private void RenderAxis(
        DrawingContext context,
        IReadOnlyList<string> axis,
        Rect plot,
        Typeface typeface,
        double captionSize)
    {
        if (axis.Count < 2)
        {
            return;
        }

        double widest = 0d;
        foreach (string text in axis)
        {
            widest = Math.Max(widest, LevelText(text, typeface, captionSize).Width);
        }

        int fits = Math.Max(1, (int)((plot.Width + GutterGap) / Math.Max(1d, widest + GutterGap)));
        int stride = Math.Max(1, (int)Math.Ceiling((double)axis.Count / fits));

        double top = plot.Bottom + GutterGap;
        double drawnTo = double.NegativeInfinity;

        for (int i = 0; i < axis.Count; i += stride)
        {
            FormattedText label = LevelText(axis[i], typeface, captionSize);
            double centre = plot.X + (plot.Width * i / (axis.Count - 1));
            double x = Math.Clamp(
                centre - (label.Width / 2d),
                plot.X,
                Math.Max(plot.X, plot.Right - label.Width));

            if (x < drawnTo)
            {
                continue;
            }

            context.DrawText(label, new Point(x, top));
            drawnTo = x + label.Width + GutterGap;
        }
    }

    private void RenderSeriesLine(DrawingContext context, UsageTapeSeries series, Size plot)
    {
        IBrush? brush = BrushFor(series.Emphasis);
        if (brush is null)
        {
            return;
        }

        IReadOnlyList<double?> values = series.Values;
        int count = values.Count;
        var pen = new Pen(brush, LineThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext figure = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < count; i++)
            {
                if (values[i] is not { } level || double.IsNaN(level))
                {
                    if (open)
                    {
                        figure.EndFigure(false);
                        open = false;
                    }

                    continue;
                }

                var point = new Point(XFor(i, count, plot.Width), YFor(level, plot.Height));
                if (open)
                {
                    figure.LineTo(point);
                }
                else
                {
                    figure.BeginFigure(point, false);
                    open = true;
                }
            }

            if (open)
            {
                figure.EndFigure(false);
            }
        }

        context.DrawGeometry(null, pen, geometry);

        if (count >= DotSampleLimit)
        {
            return;
        }

        double radius = DotDiameter / 2d;
        for (int i = 0; i < count; i++)
        {
            if (values[i] is not { } level || double.IsNaN(level))
            {
                continue;
            }

            var centre = new Point(XFor(i, count, plot.Width), YFor(level, plot.Height));
            context.DrawEllipse(brush, null, centre, radius, radius);
        }
    }

    /// <summary>
    /// Sets each provider name at the end of its own line. There is no legend box, so the
    /// names are the legend: two of them landing on the same row would make the tape
    /// unreadable exactly where it is carrying the most information.
    /// </summary>
    private void RenderSeriesNames(
        DrawingContext context,
        IReadOnlyList<UsageTapeSeries> series,
        Rect plot,
        Typeface typeface,
        double captionSize)
    {
        List<(FormattedText Label, double X, double Top)> labels = [];
        double tallest = 0d;

        foreach (UsageTapeSeries s in series)
        {
            if (BrushFor(s.Emphasis) is not { } brush || LastReported(s) is not { } last)
            {
                continue;
            }

            FormattedText label = Text(s.Name, typeface, captionSize, brush);
            double x = plot.X + XFor(last.Index, s.Values.Count, plot.Width) + GutterGap;
            double top = plot.Y + YFor(last.Level, plot.Height) - (label.Height / 2d);

            labels.Add((label, x, top));
            tallest = Math.Max(tallest, label.Height);
        }

        if (labels.Count == 0)
        {
            return;
        }

        IReadOnlyList<double> tops = SpreadLabels(
            [.. labels.Select(l => l.Top)],
            tallest + LabelGap,
            0d,
            Math.Max(0d, Bounds.Height - tallest));

        for (int i = 0; i < labels.Count; i++)
        {
            context.DrawText(labels[i].Label, new Point(labels[i].X, tops[i]));
        }
    }

    private IBrush? BrushFor(UsageTapeEmphasis emphasis) => emphasis switch
    {
        UsageTapeEmphasis.Secondary => SecondaryLineBrush,
        _ => PrimaryLineBrush,
    };

    /// <summary>
    /// The tape's own copy of a series list: a deep copy of what was assigned, with a
    /// reference back to the collection it was taken from so the tape can watch it.
    /// </summary>
    /// <remarks>
    /// It implements no mutating interface and nothing outside this class holds the array,
    /// so a caller cannot change the tape's data behind its back - the failure the tape
    /// previously had no defence against.
    /// </remarks>
    private sealed class FrozenSeries : IReadOnlyList<UsageTapeSeries>
    {
        private readonly UsageTapeSeries[] _series;

        private FrozenSeries(UsageTapeSeries[] series, IReadOnlyList<UsageTapeSeries> source)
        {
            _series = series;
            Source = source;
        }

        /// <summary>The collection this copy was taken from.</summary>
        public IReadOnlyList<UsageTapeSeries> Source { get; }

        /// <inheritdoc />
        public int Count => _series.Length;

        /// <inheritdoc />
        public UsageTapeSeries this[int index] => _series[index];

        /// <summary>Deep copies a series list. A null entry is not a series, so it is dropped.</summary>
        /// <param name="source">The collection to copy.</param>
        /// <returns>The frozen copy.</returns>
        public static FrozenSeries Of(IReadOnlyList<UsageTapeSeries> source)
        {
            var frozen = new List<UsageTapeSeries>(source.Count);
            foreach (UsageTapeSeries series in source)
            {
                if (series is not null)
                {
                    frozen.Add(series with { Values = [.. series.Values] });
                }
            }

            return new FrozenSeries([.. frozen], source);
        }

        /// <inheritdoc />
        public IEnumerator<UsageTapeSeries> GetEnumerator() =>
            ((IEnumerable<UsageTapeSeries>)_series).GetEnumerator();

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => _series.GetEnumerator();
    }
}
