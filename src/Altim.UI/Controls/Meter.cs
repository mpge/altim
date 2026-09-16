using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Altim.UI.Controls;

/// <summary>
/// The Altim usage meter: a flat 6px rail with a hairline limit tick, drawn directly
/// rather than templated. It reports one level and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The rail is <see cref="RailHeight"/> tall with fully round ends, the track is the
/// <c>AltimMeterTrackBrush</c> token and the fill is <c>AltimMeterFillBrush</c>. The fill
/// stays that colour at every level: above a threshold it is the metric label that turns
/// <c>AltimStatusWarnBrush</c>, never the bar.
/// </para>
/// <para>
/// A null <see cref="Value"/> is not zero. It renders the unavailable state - the rail as
/// a hairline outline with no track and no fill - so an unreported metric cannot be read
/// as a reported nothing.
/// </para>
/// <para>
/// The fill animates for 180ms on an ease-out curve when the value moves from one reported
/// number to another. It does not animate on the first value, on becoming unavailable, or
/// on coming back from unavailable, because those are not a level changing.
/// </para>
/// <para>
/// A meter reports a level by its width, so a meter with no width reports nothing. It asks
/// for <see cref="MinimumWidth"/> at measure, which is what keeps it from collapsing to
/// zero in an auto sized grid column and vanishing without any layout error.
/// </para>
/// <para>
/// The threshold tick and the unavailable outline are hairlines, so both are snapped to
/// whole device pixels through <see cref="Hairline"/>: at 125% an unsnapped 1px rule is
/// spread over two rows of pixels at partial coverage and reads as a grey smear.
/// </para>
/// </remarks>
public sealed class Meter : Control
{
    /// <summary>The height of the rail in device independent pixels.</summary>
    public const double RailHeight = 6d;

    /// <summary>
    /// The narrowest a meter will measure to. It matches the <c>AltimMeterMinWidth</c>
    /// token, which is the popup's half width less one step on the spacing scale.
    /// </summary>
    public const double MinimumWidth = 48d;

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
    /// The configured threshold from 0 to 100, marked by a 1px tick, or null for no tick.
    /// </summary>
    public static readonly StyledProperty<double?> ThresholdProperty =
        AvaloniaProperty.Register<Meter, double?>(nameof(Threshold), coerce: CoercePercent);

    /// <summary>The rail behind the fill. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(TrackBrush));

    /// <summary>The fill. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(FillBrush));

    /// <summary>The threshold tick. Supplied by the control theme from tokens.</summary>
    public static readonly StyledProperty<IBrush?> ThresholdBrushProperty =
        AvaloniaProperty.Register<Meter, IBrush?>(nameof(ThresholdBrush));

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
            ThresholdBrushProperty);
    }

    /// <summary>Initializes a meter in the unavailable state.</summary>
    public Meter() => PseudoClasses.Set(UnavailablePseudoClass, true);

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

    /// <inheritdoc cref="ThresholdBrushProperty" />
    public IBrush? ThresholdBrush
    {
        get => GetValue(ThresholdBrushProperty);
        set => SetValue(ThresholdBrushProperty, value);
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
    /// The width of the fill as it is currently drawn, in device independent pixels.
    /// Zero while the metric is unavailable, and mid-transition while the fill is moving.
    /// </summary>
    public double RenderedFillWidth =>
        FillWidthFor(IsUnavailable ? null : DisplayValue, Bounds.Width);

    /// <summary>
    /// Maps a level to a fill width. The mapping is linear and has no minimum: one
    /// percent of a 200px rail is two pixels, not a token gesture toward visibility.
    /// </summary>
    /// <param name="value">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="width">The full width of the rail.</param>
    /// <returns>The fill width, which is zero for a null or non-positive width.</returns>
    public static double FillWidthFor(double? value, double width)
    {
        if (width <= 0d || double.IsNaN(width) || value is not { } level || double.IsNaN(level))
        {
            return 0d;
        }

        return width * (Math.Clamp(level, 0d, 100d) / 100d);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        double height = Math.Min(RailHeight, size.Height);
        if (size.Width <= 0d || height <= 0d)
        {
            return;
        }

        var rail = new Rect(0d, (size.Height - height) / 2d, size.Width, height);
        double radius = height / 2d;

        if (IsUnavailable)
        {
            RenderUnavailable(context, rail, radius);
            return;
        }

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

        if (Threshold is { } threshold && ThresholdBrush is { } tick)
        {
            double scale = Hairline.ScaleOf(this);
            double weight = Math.Min(Hairline.ThicknessFor(scale), rail.Width);
            double x = Math.Clamp(
                FillWidthFor(threshold, rail.Width),
                0d,
                Math.Max(0d, rail.Width - weight));

            context.FillRectangle(
                tick,
                new Rect(Hairline.SnapEdge(rail.X + x, scale), rail.Y, weight, rail.Height));
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

        return new Size(width, RailHeight);
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

            // A level only "changes" when it moves between two reported numbers. The
            // first value, and every crossing into or out of unavailable, lands flat.
            bool animate = _hasValueBeenSet && oldValue is not null && newValue is not null;
            _hasValueBeenSet = true;
            SetDisplayValue(newValue ?? 0d, animate);
        }
        else if (change.Property == ThresholdProperty)
        {
            UpdateAboveThreshold();
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

    private void RenderUnavailable(DrawingContext context, Rect rail, double radius)
    {
        // An outline, not an empty track: a track with no fill reads as a reported zero,
        // and DESIGN.md is explicit that an unreported metric is never shown as a zero.
        if (TrackBrush is not { } brush)
        {
            return;
        }

        double weight = Hairline.ThicknessFor(Hairline.ScaleOf(this));
        var outline = rail.Deflate(weight / 2d);
        if (outline.Width <= 0d || outline.Height <= 0d)
        {
            return;
        }

        double outlineRadius = Math.Max(0d, radius - (weight / 2d));
        context.DrawRectangle(null, new Pen(brush, weight), outline, outlineRadius, outlineRadius);
    }

    private void UpdateAboveThreshold()
    {
        bool above = Value is { } value && Threshold is { } threshold && value >= threshold;
        IsAboveThreshold = above;
        PseudoClasses.Set(AboveThresholdPseudoClass, above);
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
