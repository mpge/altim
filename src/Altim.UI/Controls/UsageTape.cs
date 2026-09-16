using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim history tape: hairline level lines at 25, 50, 75 and 100, one line per
/// provider over them, and the provider name set at the end of its own line.
/// </summary>
/// <remarks>
/// <para>
/// There is no charting dependency and no chart furniture. No fills, no gradients, no
/// legend box, no axis frame, no grid below 25. Dots mark individual samples only while a
/// series has fewer than <see cref="DotSampleLimit"/> points, because past that they stop
/// being readable and start being texture.
/// </para>
/// <para>
/// Time runs left to right and the newest sample is always at the right edge, so a series
/// of one sample is drawn at the right edge rather than stranded on the left.
/// </para>
/// <para>
/// <see cref="Series"/> is compared by reference for invalidation. Mutating a list in
/// place will not repaint; assign a new list.
/// </para>
/// </remarks>
public sealed class UsageTape : Control
{
    /// <summary>Sample dots are drawn only while a series has fewer points than this.</summary>
    public const int DotSampleLimit = 32;

    /// <summary>The levels the hairline rules are drawn at.</summary>
    public static readonly IReadOnlyList<double> Levels = [25d, 50d, 75d, 100d];

    private const double GutterGap = 6d;
    private const double DefaultWidth = 320d;
    private const double DefaultHeight = 96d;

    /// <summary>The provider histories to draw. Null or empty renders the empty state.</summary>
    public static readonly StyledProperty<IReadOnlyList<UsageTapeSeries>?> SeriesProperty =
        AvaloniaProperty.Register<UsageTape, IReadOnlyList<UsageTapeSeries>?>(nameof(Series));

    /// <summary>The 25/50/75/100 rules. Supplied by the control theme from tokens.</summary>
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

    /// <summary>The size of the level labels and inline series names. Caption, 11px.</summary>
    public static readonly StyledProperty<double> CaptionFontSizeProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(CaptionFontSize), 11d);

    /// <summary>The series line weight. 1.5px.</summary>
    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(LineThickness), 1.5d);

    /// <summary>The sample dot diameter. 3px.</summary>
    public static readonly StyledProperty<double> DotDiameterProperty =
        AvaloniaProperty.Register<UsageTape, double>(nameof(DotDiameter), 3d);

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
            CaptionFontSizeProperty,
            LineThicknessProperty,
            DotDiameterProperty);
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
            // An empty state is a sentence, not an illustration.
            var sentence = Text(EmptyText, typeface, captionSize, LabelBrush);
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
                    Text(LevelLabel(level), typeface, captionSize, LabelBrush).Width);
            }

            leftGutter += GutterGap;
        }

        double rightGutter = 0d;
        foreach (UsageTapeSeries s in series)
        {
            rightGutter = Math.Max(
                rightGutter,
                Text(s.Name, typeface, captionSize, LabelBrush).Width);
        }

        rightGutter += GutterGap;

        var plot = new Rect(
            leftGutter,
            pad,
            size.Width - leftGutter - rightGutter,
            size.Height - (pad * 2d));

        if (plot.Width <= 0d || plot.Height <= 0d)
        {
            return;
        }

        RenderLevels(context, plot, typeface, captionSize);

        using (context.PushTransform(Matrix.CreateTranslation(plot.X, plot.Y)))
        {
            foreach (UsageTapeSeries s in series)
            {
                RenderSeriesLine(context, s, plot.Size);
            }
        }

        foreach (UsageTapeSeries s in series)
        {
            RenderSeriesName(context, s, plot, typeface, captionSize);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height;
        return new Size(width, height);
    }

    private static string LevelLabel(double level) =>
        level.ToString("0", CultureInfo.CurrentCulture);

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush? brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

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
            if (s is null)
            {
                continue;
            }

            foreach (double? value in s.Values)
            {
                if (value.HasValue && !double.IsNaN(value.Value))
                {
                    drawable.Add(s);
                    break;
                }
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

        var pen = new Pen(rule, 1d);
        foreach (double level in Levels)
        {
            // Half pixel offset so a one pixel rule lands on one row of pixels.
            double y = Math.Round(plot.Y + YFor(level, plot.Height)) + 0.5d;
            context.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));

            if (!ShowLevelLabels)
            {
                continue;
            }

            var label = Text(LevelLabel(level), typeface, captionSize, LabelBrush);
            context.DrawText(
                label,
                new Point(plot.X - GutterGap - label.Width, y - (label.Height / 2d)));
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

    private void RenderSeriesName(
        DrawingContext context,
        UsageTapeSeries series,
        Rect plot,
        Typeface typeface,
        double captionSize)
    {
        IBrush? brush = BrushFor(series.Emphasis);
        if (brush is null)
        {
            return;
        }

        IReadOnlyList<double?> values = series.Values;
        int count = values.Count;
        for (int i = count - 1; i >= 0; i--)
        {
            if (values[i] is not { } level || double.IsNaN(level))
            {
                continue;
            }

            var name = Text(series.Name, typeface, captionSize, brush);
            double x = plot.X + XFor(i, count, plot.Width) + GutterGap;
            double y = plot.Y + YFor(level, plot.Height) - (name.Height / 2d);
            context.DrawText(name, new Point(x, y));
            return;
        }
    }

    private IBrush? BrushFor(UsageTapeEmphasis emphasis) => emphasis switch
    {
        UsageTapeEmphasis.Secondary => SecondaryLineBrush,
        _ => PrimaryLineBrush,
    };
}
