using System.Globalization;
using Altim.UI.History;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Altim.UI.Controls;

/// <summary>
/// One day of one provider's usage.
/// </summary>
/// <param name="Day">The user's local calendar day.</param>
/// <param name="Tokens">
/// The day's total token volume, or <see langword="null"/> when no figure was reported.
/// Null is unreported and never zero.
/// </param>
/// <param name="PeakPercent">
/// The highest percentage any of that provider's windows reached that day, carried for the
/// tooltip. Never combined across providers, and never drawn: the square's colour is token
/// volume and nothing else.
/// </param>
/// <param name="IsKnown">
/// Whether anything at all is known about the day. This, and not <paramref name="Tokens"/>,
/// decides whether the map draws an outline or a fill. A day Altim was not watching is
/// unknown; a day it was watching that reported no volume is known, and the two are
/// different squares.
/// </param>
/// <param name="Detail">
/// The words that go with the square: the tooltip a pointer brings up, and the help text an
/// assistive technology reads after the name. It is composed by whoever built the row rather
/// than here, because the square carries one total and one percentage while the words carry
/// the four components it was reported in, whether the day was observed or backfilled, and -
/// on the combined row - which providers the figure covers and which had nothing.
/// <see langword="null"/> means there is nothing to say, and no tip is shown.
/// </param>
public sealed record UsageMapCell(
    DateOnly Day,
    long? Tokens,
    double? PeakPercent,
    bool IsKnown,
    string? Detail = null);

/// <summary>
/// One labelled row of the map: a provider, or the combined row beneath them.
/// </summary>
/// <param name="Name">The row's label, set above its grid.</param>
/// <param name="Cells">
/// The days in the row, in any order. A day that is absent from this list is outside the
/// map's range rather than unknown within it, and nothing is drawn for it at all.
/// </param>
/// <param name="IsCombined">
/// Whether this row sums the rows above it. The map rules a separator above it, because a
/// total standing in the same stack as its parts reads as another provider.
/// </param>
public sealed record UsageMapRow(string Name, IReadOnlyList<UsageMapCell> Cells, bool IsCombined = false);

/// <summary>What sits under a point on the map.</summary>
/// <param name="RowIndex">The index into <see cref="UsageMap.Rows"/> the cell came from.</param>
/// <param name="Cell">The day under the point.</param>
/// <param name="Bounds">The cell's rectangle, in the map's own coordinates.</param>
public readonly record struct UsageMapHit(int RowIndex, UsageMapCell Cell, Rect Bounds);

/// <summary>
/// A calendar of daily usage: one labelled block per provider, one square per local
/// calendar day, weeks as columns and the days of a week read down a column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown and zero are different squares, and that is not negotiable.</b> A day nothing
/// is known about is a hairline outline with no fill: before install, outside a provider's
/// backfill reach, or a provider that was never installed. A day that is known to have used
/// nothing is the faintest fill in the ramp. Painting the first as the second is the
/// application claiming it watched a day it did not, and it is the one rendering detail the
/// design says may not be traded for a tidier grid. <see cref="UsageMapCell.IsKnown"/> is
/// the only authority on which of the two a square is.
/// </para>
/// <para>
/// A known day that reported no token figure is therefore still a fill, at level zero. The
/// outline means "we were not watching"; a day we were watching that has no volume to
/// report is the faintest square, which is exactly what level zero says.
/// </para>
/// <para>
/// The ramp is five steps of <c>TextPrimary</c> opacity, monochrome in both variants. A
/// provider's accent colour is identity and never data, so no square is ever tinted by
/// whose row it is in.
/// </para>
/// <para>
/// Levels come from <see cref="UsageMapScale"/> and are not computed here: the scale is
/// quantile based, because token counts span orders of magnitude and a linear ramp would
/// render the whole year as the palest square beside one black one. A caller that is
/// showing several maps hands the same <see cref="Scale"/> to all of them so the rows stay
/// comparable; a map given none ranks the rows it was given.
/// </para>
/// <para>
/// The map draws itself rather than hosting a control per day. A year across two providers
/// and a combined row is over a thousand cells, and a thousand <c>Border</c>s is a thousand
/// visuals, a thousand style applications and a thousand entries in the render tree, for
/// squares that never take focus and never animate. The cost of that is memory this
/// application has a measured budget for. The price is that the grid is a single element to
/// automation: see <see cref="HitTest"/>.
/// </para>
/// <para>
/// Edges are snapped to whole device pixels through <see cref="Hairline"/>, so the grid
/// stays even at 125% and 150% instead of drifting a fraction of a pixel per column until
/// some squares are 8 wide and others 9.
/// </para>
/// <para>
/// <see cref="Rows"/> is not frozen into a private copy, which is the one place this
/// deliberately differs from <see cref="UsageTape"/>. The tape freezes because it is handed
/// observable collections that are mutated in place. A map is rebuilt and assigned whole,
/// its rows and cells are immutable records, and a defensive copy of a year across three
/// rows is a second thousand objects held for a risk that is not present. A caller that
/// does mutate a list in place will not see the map repaint: assign a new list.
/// </para>
/// <para>
/// Because the grid is one element, the two ways a square is read are both arranged here
/// from <see cref="HitTest"/> and <see cref="CellBounds"/>. A pointer over a square opens
/// that day's <see cref="UsageMapCell.Detail"/> as a tooltip, and
/// <see cref="UsageMapAutomationPeer"/> gives an assistive technology one named child per
/// square. Neither is decoration: a year of unlabelled rectangles is silence to a screen
/// reader, and a square whose only figure is a shade of grey says nothing about what day it
/// is or what it counted.
/// </para>
/// </remarks>
public sealed class UsageMap : Control, ICustomHitTest
{
    /// <summary>How many steps the ramp has. The scale is built with the same number.</summary>
    public const int LevelCount = 5;

    /// <summary>
    /// The rows to draw. <see langword="null"/> draws nothing at all, because a map that
    /// has not been given anything has not been told the history is empty either; an empty
    /// list, or rows in which no day is known, draws <see cref="EmptyText"/>.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<UsageMapRow>?> RowsProperty =
        AvaloniaProperty.Register<UsageMap, IReadOnlyList<UsageMapRow>?>(nameof(Rows));

    /// <summary>
    /// The scale that turns a day's tokens into a ramp level. <see langword="null"/> ranks
    /// the rows this map was given, which is right for a map standing on its own and wrong
    /// for one of several that must be read against each other.
    /// </summary>
    public static readonly StyledProperty<UsageMapScale?> ScaleProperty =
        AvaloniaProperty.Register<UsageMap, UsageMapScale?>(nameof(Scale));

    /// <summary>The hairline around a day nothing is known about. No fill goes with it.</summary>
    public static readonly StyledProperty<IBrush?> UnknownBrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(UnknownBrush));

    /// <summary>The faintest step: a day known to have used nothing.</summary>
    public static readonly StyledProperty<IBrush?> Level0BrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(Level0Brush));

    /// <summary>The second step of the ramp.</summary>
    public static readonly StyledProperty<IBrush?> Level1BrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(Level1Brush));

    /// <summary>The third step of the ramp.</summary>
    public static readonly StyledProperty<IBrush?> Level2BrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(Level2Brush));

    /// <summary>The fourth step of the ramp.</summary>
    public static readonly StyledProperty<IBrush?> Level3BrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(Level3Brush));

    /// <summary>The heaviest step of the ramp.</summary>
    public static readonly StyledProperty<IBrush?> Level4BrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(Level4Brush));

    /// <summary>Row names, month names and the empty sentence.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(LabelBrush));

    /// <summary>The rule above the combined row.</summary>
    public static readonly StyledProperty<IBrush?> SeparatorBrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(SeparatorBrush));

    /// <summary>The one sentence shown when not a single day is known.</summary>
    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<UsageMap, string>(
            nameof(EmptyText),
            "No daily usage recorded yet. The map fills in as agents run.");

    /// <summary>One day's square, in device independent pixels.</summary>
    public static readonly StyledProperty<double> CellSizeProperty =
        AvaloniaProperty.Register<UsageMap, double>(nameof(CellSize), 8d);

    /// <summary>The room between two squares.</summary>
    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<UsageMap, double>(nameof(CellGap), 2d);

    /// <summary>The room between two rows' grids.</summary>
    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<UsageMap, double>(nameof(RowGap), 12d);

    /// <summary>The size of the row names, the month names and the empty sentence.</summary>
    public static readonly StyledProperty<double> CaptionFontSizeProperty =
        AvaloniaProperty.Register<UsageMap, double>(nameof(CaptionFontSize), 11d);

    /// <summary>
    /// The ring round the square the keyboard is on. The design system's focus colour and
    /// nothing else: a platform default would be a second mechanism drawn beside this one.
    /// </summary>
    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty =
        AvaloniaProperty.Register<UsageMap, IBrush?>(nameof(FocusRingBrush));

    /// <summary>
    /// How thick that ring is, in device independent pixels. It is also the room the map
    /// reserves round its own grid, so the ring on an edge square is not cut in half by the
    /// control's own boundary.
    /// </summary>
    public static readonly StyledProperty<double> FocusRingWidthProperty =
        AvaloniaProperty.Register<UsageMap, double>(nameof(FocusRingWidth), 2d);

    /// <summary>Days in a week, which is the height of every block in squares.</summary>
    private const int DaysInWeek = 7;

    /// <summary>The room between a label and whatever it labels. One spacing step.</summary>
    private const double LabelGap = 4d;

    /// <summary>What a map with nothing to draw asks for, so it does not collapse.</summary>
    private const double DefaultWidth = 320d;

    private MapLayout? _layout;
    private UsageMapCell? _hovered;
    private UsageMapCell? _captioned;
    private Selection? _focused;

    static UsageMap()
    {
        AffectsRender<UsageMap>(
            RowsProperty,
            ScaleProperty,
            UnknownBrushProperty,
            Level0BrushProperty,
            Level1BrushProperty,
            Level2BrushProperty,
            Level3BrushProperty,
            Level4BrushProperty,
            LabelBrushProperty,
            SeparatorBrushProperty,
            EmptyTextProperty,
            FocusRingBrushProperty,
            FocusRingWidthProperty);

        AffectsMeasure<UsageMap>(
            RowsProperty,
            EmptyTextProperty,
            CellSizeProperty,
            CellGapProperty,
            RowGapProperty,
            CaptionFontSizeProperty,
            FocusRingWidthProperty);

        AffectsRender<UsageMap>(TextElement.FontFamilyProperty);
        AffectsMeasure<UsageMap>(TextElement.FontFamilyProperty);
    }

    /// <inheritdoc cref="RowsProperty" />
    public IReadOnlyList<UsageMapRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <inheritdoc cref="ScaleProperty" />
    public UsageMapScale? Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <inheritdoc cref="UnknownBrushProperty" />
    public IBrush? UnknownBrush
    {
        get => GetValue(UnknownBrushProperty);
        set => SetValue(UnknownBrushProperty, value);
    }

    /// <inheritdoc cref="Level0BrushProperty" />
    public IBrush? Level0Brush
    {
        get => GetValue(Level0BrushProperty);
        set => SetValue(Level0BrushProperty, value);
    }

    /// <inheritdoc cref="Level1BrushProperty" />
    public IBrush? Level1Brush
    {
        get => GetValue(Level1BrushProperty);
        set => SetValue(Level1BrushProperty, value);
    }

    /// <inheritdoc cref="Level2BrushProperty" />
    public IBrush? Level2Brush
    {
        get => GetValue(Level2BrushProperty);
        set => SetValue(Level2BrushProperty, value);
    }

    /// <inheritdoc cref="Level3BrushProperty" />
    public IBrush? Level3Brush
    {
        get => GetValue(Level3BrushProperty);
        set => SetValue(Level3BrushProperty, value);
    }

    /// <inheritdoc cref="Level4BrushProperty" />
    public IBrush? Level4Brush
    {
        get => GetValue(Level4BrushProperty);
        set => SetValue(Level4BrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc cref="SeparatorBrushProperty" />
    public IBrush? SeparatorBrush
    {
        get => GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <inheritdoc cref="EmptyTextProperty" />
    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <inheritdoc cref="CellSizeProperty" />
    public double CellSize
    {
        get => GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    /// <inheritdoc cref="CellGapProperty" />
    public double CellGap
    {
        get => GetValue(CellGapProperty);
        set => SetValue(CellGapProperty, value);
    }

    /// <inheritdoc cref="RowGapProperty" />
    public double RowGap
    {
        get => GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    /// <inheritdoc cref="CaptionFontSizeProperty" />
    public double CaptionFontSize
    {
        get => GetValue(CaptionFontSizeProperty);
        set => SetValue(CaptionFontSizeProperty, value);
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

    /// <summary>Raised when the square the keyboard is on changes, including when it is lost.</summary>
    /// <remarks>
    /// The map is one element, so the square the keyboard is on is not a focus change any
    /// framework can see. <see cref="UsageMapAutomationPeer"/> listens here to tell an
    /// assistive technology which day is being read.
    /// </remarks>
    public event EventHandler? FocusedCellChanged;

    /// <summary>
    /// The square the keyboard is on, or <see langword="null"/> when none is.
    /// </summary>
    /// <remarks>
    /// The rectangle is read back from <see cref="CellBounds"/> on every access rather than
    /// remembered, so a square that has moved - a different square size, a different display
    /// scaling - reports where it actually is.
    /// </remarks>
    public UsageMapHit? FocusedCell =>
        _focused is { } focus && CellBounds(focus.RowIndex, focus.Cell.Day) is { } bounds
            ? new UsageMapHit(focus.RowIndex, focus.Cell, bounds)
            : null;

    /// <summary>
    /// Where one day's square is, in the map's own coordinates.
    /// </summary>
    /// <param name="rowIndex">The index into <see cref="Rows"/>.</param>
    /// <param name="day">The day to find.</param>
    /// <returns>
    /// The square's rectangle, or <see langword="null"/> when that row draws no grid - it
    /// carried no cells, or no day anywhere on the map is known - or when the day is
    /// outside the range the map was given.
    /// </returns>
    /// <remarks>
    /// The arithmetic that places a square is the map's own, so the tooltip, the focus
    /// rectangle and the tests read it from here rather than each deriving it again and
    /// drifting from the picture.
    /// </remarks>
    public Rect? CellBounds(int rowIndex, DateOnly day)
    {
        MapLayout? layout = EnsureLayout();
        if (layout is null)
        {
            return null;
        }

        foreach (MapBlock block in layout.Blocks)
        {
            if (block.RowIndex == rowIndex)
            {
                int offset = day.DayNumber - layout.Start.DayNumber;
                return offset >= 0 && offset < block.Cells.Length && block.Cells[offset] is not null
                    ? RectFor(layout, block, offset)
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The day under a point.
    /// </summary>
    /// <param name="point">A position in the map's own coordinates.</param>
    /// <returns>The cell there, or <see langword="null"/> when the point is between squares.</returns>
    /// <remarks>
    /// The map is one element, so hover, keyboard focus and an assistive technology all
    /// have to be told which square they are on by asking. That is the cost of drawing the
    /// grid rather than building it out of controls, and it is paid here rather than by a
    /// caller re-deriving the layout.
    /// </remarks>
    public UsageMapHit? HitTest(Point point)
    {
        MapLayout? layout = EnsureLayout();
        if (layout is null)
        {
            return null;
        }

        double x = point.X - layout.Inset;
        if (x < 0d)
        {
            return null;
        }

        int column = (int)Math.Floor(x / layout.Pitch);
        if (column < 0 || column >= layout.Weeks || x - (column * layout.Pitch) >= CellSize)
        {
            return null;
        }

        foreach (MapBlock block in layout.Blocks)
        {
            double inside = point.Y - block.GridTop;
            if (inside < 0d || inside >= DaysInWeek * layout.Pitch)
            {
                continue;
            }

            int weekday = (int)Math.Floor(inside / layout.Pitch);
            if (inside - (weekday * layout.Pitch) >= CellSize)
            {
                return null;
            }

            int offset = (column * DaysInWeek) + weekday;
            return offset < block.Cells.Length && block.Cells[offset] is { } cell
                ? new UsageMapHit(block.RowIndex, cell, RectFor(layout, block, offset))
                : null;
        }

        return null;
    }

    /// <summary>
    /// Whether a point counts as being on the map at all.
    /// </summary>
    /// <param name="point">A position in the map's own coordinates.</param>
    /// <returns>Whether the map takes the pointer there.</returns>
    /// <remarks>
    /// The whole rectangle, not only the squares. A control that drew nothing in the gaps
    /// between days would take the pointer on a square and lose it a pixel later, so the
    /// tooltip would flicker its way across a week. Taking the whole area means the gaps are
    /// handled the same way as the squares: <see cref="HitTest"/> answers nothing there, and
    /// the tip closes rather than the pointer leaving the control entirely.
    /// </remarks>
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        double captionSize = Math.Max(1d, CaptionFontSize);

        if (EnsureLayout() is not { } layout)
        {
            // Nothing is known. A map that was never given rows says nothing at all,
            // because it has not been told the history is empty; one that was given rows
            // with no known day in them says so in a sentence, rather than drawing a grid
            // of empty squares pretending to be a map.
            if (Rows is null)
            {
                return;
            }

            FormattedText sentence = Text(EmptyText, typeface, captionSize, LabelBrush);
            sentence.MaxTextWidth = Math.Max(1d, Bounds.Width);
            context.DrawText(sentence, new Point(0d, Math.Max(0d, (Bounds.Height - sentence.Height) / 2d)));
            return;
        }

        double scale = Hairline.ScaleOf(this);
        double weight = Hairline.ThicknessFor(scale);
        UsageMapScale ramp = Scale ?? layout.OwnScale;

        RenderMonths(context, layout, typeface, captionSize);

        foreach (MapBlock block in layout.Blocks)
        {
            if (!double.IsNaN(block.SeparatorY) && SeparatorBrush is { } rule)
            {
                context.FillRectangle(
                    rule,
                    new Rect(0d, Hairline.SnapCentre(block.SeparatorY, weight, scale), layout.Size.Width, weight));
            }

            context.DrawText(
                Text(block.Name, typeface, captionSize, LabelBrush),
                new Point(layout.Inset, block.LabelTop));

            for (int offset = 0; offset < block.Cells.Length; offset++)
            {
                if (block.Cells[offset] is not { } cell)
                {
                    continue;
                }

                RenderCell(context, RectFor(layout, block, offset), cell, ramp, weight);
            }
        }

        // Last, so the ring stands on top of the squares it runs between rather than under
        // whichever of them happened to be drawn after it.
        if (_focused is { } focus
            && FocusRingBrush is { } ring
            && CellBounds(focus.RowIndex, focus.Cell.Day) is { } square)
        {
            RenderFocusRing(context, square, ring, scale);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (EnsureLayout() is { } layout)
        {
            return layout.Size;
        }

        // The sentence, or the room it would take: a map still loading must not collapse to
        // nothing and then shove the page down when its rows arrive.
        double width = double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width;
        FormattedText sentence = Text(
            EmptyText,
            new Typeface(TextElement.GetFontFamily(this)),
            Math.Max(1d, CaptionFontSize),
            LabelBrush);
        sentence.MaxTextWidth = Math.Max(1d, width);

        return new Size(width, sentence.Height);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One peer for the map with one synthesised child per square. The grid is drawn rather
    /// than built out of controls, so without this an assistive technology is handed a single
    /// rectangle where a year of days is.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new UsageMapAutomationPeer(this);

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);
        Hover(HitTest(e.GetPosition(this))?.Cell);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Focus arrives on the first square the map <em>knows</em> something about, not on the
    /// first square it draws. A row usually opens with days from before the provider's
    /// backfill could reach, and landing on one of those would read out a day Altim was never
    /// watching as though it were a reading.
    /// </remarks>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        if (_focused is null && FirstKnown() is { } first)
        {
            Select(first);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The selection goes with the focus. A ring left standing on a control nobody is on, and
    /// a tip left open beside it, would caption whatever the reader moved to next.
    /// </remarks>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Select(null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Up and down move a day, left and right move a week, because weeks are the columns and
    /// the days of a week read down one. Home and End go to the ends of what the row knows
    /// rather than to the ends of the grid it was drawn on.
    /// </para>
    /// <para>
    /// <b>None of them leave the row, and every one of them is marked handled even when
    /// nothing moved.</b> An arrow that fell off the end of a block would carry the reader
    /// into the next provider's year without saying so. An arrow that moved nothing and was
    /// then left unhandled does the damage from the other end: it goes on up the tree, and on
    /// the History page the map sits inside a horizontally scrolling <c>ScrollViewer</c>, so
    /// the whole year would slide sideways under a reader who asked for the next day. Ctrl and
    /// an arrow is the way between providers, and it is a different key because it is a
    /// different question.
    /// </para>
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnKeyDown(e);

        if (e.Handled
            || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End)
            || _focused is not { } focus
            || EnsureLayout() is not { } layout)
        {
            return;
        }

        bool between = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        Selection? next = e.Key switch
        {
            Key.Up when between => Sibling(layout, focus, -1),
            Key.Down when between => Sibling(layout, focus, 1),
            Key.Up => Step(layout, focus, -1),
            Key.Down => Step(layout, focus, 1),
            Key.Left => Step(layout, focus, -DaysInWeek),
            Key.Right => Step(layout, focus, DaysInWeek),
            Key.Home => Edge(layout, focus, oldest: true),
            _ => Edge(layout, focus, oldest: false),
        };

        if (next is not null)
        {
            Select(next);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Puts the keyboard on one square, focusing the map if it is not focused already.
    /// </summary>
    /// <param name="rowIndex">The index into <see cref="Rows"/>.</param>
    /// <param name="day">The day to land on.</param>
    /// <returns>Whether that square exists and could be focused.</returns>
    /// <remarks>
    /// The grid is drawn rather than built out of controls, so an assistive technology asking
    /// to focus a square has nothing to call <c>Focus</c> on. This is what it calls instead.
    /// </remarks>
    public bool TryFocusCell(int rowIndex, DateOnly day)
    {
        if (EnsureLayout() is not { } layout
            || BlockFor(layout, rowIndex) is not { } block
            || CellAt(block, day.DayNumber - layout.Start.DayNumber) is not { } cell
            || !Focus(NavigationMethod.Unspecified))
        {
            return false;
        }

        Select(new Selection(rowIndex, cell));
        return true;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RowsProperty
            || change.Property == CellSizeProperty
            || change.Property == CellGapProperty
            || change.Property == RowGapProperty
            || change.Property == CaptionFontSizeProperty
            || change.Property == FocusRingWidthProperty
            || change.Property == TextElement.FontFamilyProperty)
        {
            _layout = null;

            // Whatever the pointer or the keyboard was on belonged to the picture that has
            // just been replaced. Leaving either up would caption a new square with an old
            // day, and leaving the ring up would point at a square that has moved.
            Select(null);
            Hover(null);
        }

        if (change.Property == RowsProperty)
        {
            // A map with no day to land on must not offer itself for focus. A hosted window
            // can hand focus to the only focusable control in it, and an empty map that took
            // it would paint a ring over the sentence it is there to show.
            Focusable = AnyKnown(Rows);
        }
    }

    /// <summary>Whether any row carries a day the map knows something about.</summary>
    /// <param name="rows">The rows to look through, which may be null.</param>
    /// <remarks>
    /// Deliberately cheap and deliberately not the layout. Focusability is decided the moment
    /// the rows are assigned, and the layout measures text, which needs a font manager that
    /// does not exist until the control is in a tree.
    /// </remarks>
    private static bool AnyKnown(IReadOnlyList<UsageMapRow>? rows)
    {
        if (rows is null)
        {
            return false;
        }

        foreach (UsageMapRow row in rows)
        {
            if (row is null)
            {
                continue;
            }

            foreach (UsageMapCell cell in row.Cells)
            {
                if (cell is { IsKnown: true })
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One block's square at an offset from the grid's first day, if there is one.</summary>
    private static UsageMapCell? CellAt(MapBlock block, int offset) =>
        offset >= 0 && offset < block.Cells.Length ? block.Cells[offset] : null;

    /// <summary>The block one row was drawn as, if that row drew one.</summary>
    private static MapBlock? BlockFor(MapLayout layout, int rowIndex)
    {
        foreach (MapBlock block in layout.Blocks)
        {
            if (block.RowIndex == rowIndex)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>
    /// The square a number of days away in the same row, or nothing when that is off the end
    /// of it. Nothing, rather than the next row's square: the rows are separate years.
    /// </summary>
    private static Selection? Step(MapLayout layout, Selection from, int days) =>
        BlockFor(layout, from.RowIndex) is { } block
        && CellAt(block, from.Cell.Day.DayNumber - layout.Start.DayNumber + days) is { } cell
            ? new Selection(block.RowIndex, cell)
            : null;

    /// <summary>The oldest or newest day the row knows something about.</summary>
    private static Selection? Edge(MapLayout layout, Selection from, bool oldest)
    {
        if (BlockFor(layout, from.RowIndex) is not { } block)
        {
            return null;
        }

        for (int step = 0; step < block.Cells.Length; step++)
        {
            int offset = oldest ? step : block.Cells.Length - 1 - step;
            if (block.Cells[offset] is { IsKnown: true } cell)
            {
                return new Selection(block.RowIndex, cell);
            }
        }

        return null;
    }

    /// <summary>
    /// The same day in the row above or below, so a keyboard reader can reach a provider
    /// other than the first. A row that does not carry that day at all answers with its
    /// oldest square rather than with nothing.
    /// </summary>
    private static Selection? Sibling(MapLayout layout, Selection from, int direction)
    {
        int at = -1;
        for (int index = 0; index < layout.Blocks.Count; index++)
        {
            if (layout.Blocks[index].RowIndex == from.RowIndex)
            {
                at = index;
                break;
            }
        }

        int target = at + direction;
        if (at < 0 || target < 0 || target >= layout.Blocks.Count)
        {
            return null;
        }

        MapBlock block = layout.Blocks[target];
        if (CellAt(block, from.Cell.Day.DayNumber - layout.Start.DayNumber) is { } same)
        {
            return new Selection(block.RowIndex, same);
        }

        foreach (UsageMapCell? cell in block.Cells)
        {
            if (cell is not null)
            {
                return new Selection(block.RowIndex, cell);
            }
        }

        return null;
    }

    /// <summary>The first square, in reading order, that the map knows something about.</summary>
    private Selection? FirstKnown()
    {
        if (EnsureLayout() is not { } layout)
        {
            return null;
        }

        foreach (MapBlock block in layout.Blocks)
        {
            foreach (UsageMapCell? cell in block.Cells)
            {
                if (cell is { IsKnown: true })
                {
                    return new Selection(block.RowIndex, cell);
                }
            }
        }

        return null;
    }

    /// <summary>Moves the keyboard to one square, or off the grid entirely.</summary>
    /// <param name="next">The square to land on, or <see langword="null"/> for none.</param>
    private void Select(Selection? next)
    {
        bool same = _focused is { } was
            ? next is { } now && was.RowIndex == now.RowIndex && ReferenceEquals(was.Cell, now.Cell)
            : next is null;

        if (same)
        {
            return;
        }

        _focused = next;
        InvalidateVisual();
        Caption();
        FocusedCellChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows one square's words, or takes them away.
    /// </summary>
    /// <param name="cell">The square under the pointer, or <see langword="null"/> for none.</param>
    /// <remarks>
    /// The tip is opened here rather than left to the hover service. That service arms itself
    /// when <c>ToolTip.Tip</c> stops being null, and by the time the map knows which square
    /// the pointer is on the pointer has already entered - so a tip merely set would not
    /// appear until the pointer left the map and came back onto the same square.
    /// </remarks>
    private void Hover(UsageMapCell? cell)
    {
        if (ReferenceEquals(cell, _hovered))
        {
            return;
        }

        _hovered = cell;
        Caption();
    }

    /// <summary>
    /// Shows the words belonging to whichever square is being read, or takes them away.
    /// </summary>
    /// <remarks>
    /// The pointer wins while it is on a square, and the keyboard's square is what is left
    /// when it is not. Both arrive here rather than each setting a tip of its own, and both
    /// show <see cref="UsageMapCell.Detail"/> itself rather than a second rendering of the
    /// same day, so hover and focus cannot drift into two descriptions of one square.
    /// </remarks>
    private void Caption()
    {
        UsageMapCell? cell = _hovered ?? _focused?.Cell;
        if (ReferenceEquals(cell, _captioned))
        {
            return;
        }

        _captioned = cell;
        ToolTip.SetTip(this, cell?.Detail);
        ToolTip.SetIsOpen(this, cell?.Detail is { Length: > 0 });
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush? brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    /// <summary>One square's rectangle, snapped so the whole grid lands on device pixels.</summary>
    private Rect RectFor(MapLayout layout, MapBlock block, int offset)
    {
        double scale = Hairline.ScaleOf(this);
        double rawX = layout.Inset + ((offset / DaysInWeek) * layout.Pitch);
        double rawY = block.GridTop + ((offset % DaysInWeek) * layout.Pitch);

        double left = Hairline.SnapEdge(rawX, scale);
        double top = Hairline.SnapEdge(rawY, scale);

        return new Rect(
            left,
            top,
            Math.Max(1d, Hairline.SnapEdge(rawX + CellSize, scale) - left),
            Math.Max(1d, Hairline.SnapEdge(rawY + CellSize, scale) - top));
    }

    /// <summary>
    /// One day. Unknown is an outline and nothing else; everything else is a fill from the
    /// ramp. The two branches never meet, which is the point.
    /// </summary>
    private void RenderCell(
        DrawingContext context,
        Rect cell,
        UsageMapCell day,
        UsageMapScale ramp,
        double weight)
    {
        if (!day.IsKnown)
        {
            if (UnknownBrush is { } outline)
            {
                double inner = Math.Max(0d, cell.Height - (weight * 2d));
                context.FillRectangle(outline, new Rect(cell.X, cell.Y, cell.Width, weight));
                context.FillRectangle(outline, new Rect(cell.X, cell.Bottom - weight, cell.Width, weight));
                context.FillRectangle(outline, new Rect(cell.X, cell.Y + weight, weight, inner));
                context.FillRectangle(outline, new Rect(cell.Right - weight, cell.Y + weight, weight, inner));
            }

            return;
        }

        // A known day with no figure to report is still a day we were watching, so it takes
        // the faintest fill rather than the outline that would claim we were not.
        int level = ramp.LevelFor(day.Tokens);
        if (BrushFor(level == UsageMapScale.Unknown ? 0 : level) is { } fill)
        {
            context.FillRectangle(fill, cell);
        }
    }

    /// <summary>
    /// The ring round the focused square, drawn as four bands in the gaps beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The design system's ring is 2px offset 2px outside the control. A day is 8px with 2px
    /// between it and the next one, so a ring held 2px clear would be painted on top of the
    /// neighbouring days and the focused square would read as a three by three block. The
    /// ring therefore hugs the square and fills the gap that is already there, which is the
    /// same weight of line in the same colour, sitting where there is room for it.
    /// </para>
    /// <para>
    /// Both edges are snapped through <see cref="Hairline"/> against the square's own snapped
    /// rectangle, so the band is a whole number of device pixels at 125% and 150% instead of
    /// a grey smear on two of its four sides.
    /// </para>
    /// </remarks>
    private void RenderFocusRing(DrawingContext context, Rect cell, IBrush brush, double scale)
    {
        double wanted = Math.Max(0d, FocusRingWidth);
        if (wanted <= 0d)
        {
            return;
        }

        double left = Hairline.SnapEdge(cell.X - wanted, scale);
        double top = Hairline.SnapEdge(cell.Y - wanted, scale);
        double right = Hairline.SnapEdge(cell.Right + wanted, scale);
        double bottom = Hairline.SnapEdge(cell.Bottom + wanted, scale);

        context.FillRectangle(brush, new Rect(left, top, right - left, cell.Y - top));
        context.FillRectangle(brush, new Rect(left, cell.Bottom, right - left, bottom - cell.Bottom));
        context.FillRectangle(brush, new Rect(left, cell.Y, cell.X - left, cell.Height));
        context.FillRectangle(brush, new Rect(cell.Right, cell.Y, right - cell.Right, cell.Height));
    }

    private IBrush? BrushFor(int level) => level switch
    {
        <= 0 => Level0Brush,
        1 => Level1Brush,
        2 => Level2Brush,
        3 => Level3Brush,
        _ => Level4Brush,
    };

    /// <summary>
    /// Month names along the top, one where a month starts. Where two would collide the
    /// later one is dropped rather than overprinted: a scale with a name missing reads as a
    /// wider month, while two names on top of each other reads as a defect.
    /// </summary>
    private void RenderMonths(DrawingContext context, MapLayout layout, Typeface typeface, double captionSize)
    {
        if (layout.MonthBandHeight <= 0d)
        {
            return;
        }

        double drawnTo = double.NegativeInfinity;
        int previous = -1;

        for (int column = 0; column < layout.Weeks; column++)
        {
            DateOnly top = layout.Start.AddDays(column * DaysInWeek);
            if (top.Month == previous)
            {
                continue;
            }

            previous = top.Month;

            double x = layout.Inset + (column * layout.Pitch);
            if (x < drawnTo)
            {
                continue;
            }

            FormattedText label = Text(
                top.ToString("MMM", CultureInfo.CurrentCulture),
                typeface,
                captionSize,
                LabelBrush);

            context.DrawText(label, new Point(x, 0d));
            drawnTo = x + label.Width + LabelGap;
        }
    }

    /// <summary>
    /// The layout, worked out on first use and kept until something that moves a square
    /// changes. It is built lazily rather than on assignment because it measures text, and
    /// text measurement needs a font manager: every caller here is a layout pass, a render
    /// or a hit test on a live control, by which time there is one.
    /// </summary>
    private MapLayout? EnsureLayout() => _layout ??= BuildLayout();

    private MapLayout? BuildLayout()
    {
        IReadOnlyList<UsageMapRow>? rows = Rows;
        if (rows is null)
        {
            return null;
        }

        DateOnly first = DateOnly.MaxValue;
        DateOnly last = DateOnly.MinValue;
        bool anyKnown = false;
        bool anyCell = false;

        foreach (UsageMapRow row in rows)
        {
            if (row is null)
            {
                continue;
            }

            foreach (UsageMapCell cell in row.Cells)
            {
                if (cell is null)
                {
                    continue;
                }

                anyCell = true;
                anyKnown |= cell.IsKnown;
                first = cell.Day < first ? cell.Day : first;
                last = cell.Day > last ? cell.Day : last;
            }
        }

        // Not one provider has a single day of data. That is a sentence, not a grid of
        // empty squares: a map of nothing but outlines would look like a year Altim
        // watched and found empty.
        if (!anyCell || !anyKnown)
        {
            return null;
        }

        // Columns are weeks, so the grid starts on the first day of the week the earliest
        // day falls in. Which day that is belongs to the user's calendar, like the days
        // themselves.
        var culture = CultureInfo.CurrentCulture;
        int back = ((int)first.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + DaysInWeek) % DaysInWeek;
        DateOnly start = first.AddDays(-back);
        int weeks = ((last.DayNumber - start.DayNumber) / DaysInWeek) + 1;

        double cellSize = Math.Max(1d, CellSize);
        double pitch = cellSize + Math.Max(0d, CellGap);
        double gap = Math.Max(0d, RowGap);

        // Room for the focus ring on the squares at the edges of the grid. The grid otherwise
        // begins and ends exactly on a square, so a ring round the first column, the last
        // column or the bottom row would be drawn outside the control's own rectangle - where
        // a parent is free to clip it away, which is a ring that exists everywhere except at
        // the four places a reader arrives first.
        double inset = Math.Max(0d, FocusRingWidth);

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        double captionSize = Math.Max(1d, CaptionFontSize);
        double captionHeight = Text("0", typeface, captionSize, LabelBrush).Height;

        double widest = (weeks * pitch) - Math.Max(0d, CellGap);
        double y = captionHeight + LabelGap;
        double monthBand = y;

        List<MapBlock> blocks = [];
        List<long> values = [];

        for (int index = 0; index < rows.Count; index++)
        {
            UsageMapRow? row = rows[index];
            if (row is null || row.Cells.Count == 0)
            {
                // A row with no days contributes nothing and takes no room. Naming an
                // empty strip would be furniture standing in for data.
                continue;
            }

            double separatorY = double.NaN;
            if (blocks.Count > 0)
            {
                if (row.IsCombined)
                {
                    separatorY = y + gap;
                    y += gap * 2d;
                }
                else
                {
                    y += gap;
                }
            }

            double labelTop = y;
            y += captionHeight + LabelGap;

            var cells = new UsageMapCell?[weeks * DaysInWeek];
            foreach (UsageMapCell cell in row.Cells)
            {
                if (cell is null)
                {
                    continue;
                }

                int offset = cell.Day.DayNumber - start.DayNumber;
                if (offset >= 0 && offset < cells.Length)
                {
                    cells[offset] = cell;
                }

                if (cell is { IsKnown: true, Tokens: { } tokens })
                {
                    values.Add(tokens);
                }
            }

            blocks.Add(new MapBlock
            {
                RowIndex = index,
                Name = row.Name,
                LabelTop = labelTop,
                GridTop = y,
                SeparatorY = separatorY,
                Cells = cells,
            });

            y += (DaysInWeek * pitch) - Math.Max(0d, CellGap);
            widest = Math.Max(widest, Text(row.Name, typeface, captionSize, LabelBrush).Width);
        }

        return blocks.Count == 0
            ? null
            : new MapLayout
            {
                Start = start,
                Weeks = weeks,
                Pitch = pitch,
                Inset = inset,
                MonthBandHeight = monthBand,
                Blocks = blocks,
                OwnScale = UsageMapScale.From(values, LevelCount),
                Size = new Size(inset + widest + inset, y + inset),
            };
    }

    /// <summary>Where every block and every square goes, worked out once per assignment.</summary>
    private sealed class MapLayout
    {
        /// <summary>The first day of the week the grid's first column covers.</summary>
        public required DateOnly Start { get; init; }

        /// <summary>How many week columns the grid has.</summary>
        public required int Weeks { get; init; }

        /// <summary>One square plus one gap.</summary>
        public required double Pitch { get; init; }

        /// <summary>
        /// The room kept clear round the grid so a focus ring on an edge square is drawn
        /// inside the control rather than over whatever is next to it.
        /// </summary>
        public required double Inset { get; init; }

        /// <summary>The height of the month names and the room under them.</summary>
        public required double MonthBandHeight { get; init; }

        /// <summary>One block per row that had days, in the order the rows were given.</summary>
        public required IReadOnlyList<MapBlock> Blocks { get; init; }

        /// <summary>
        /// The ranking of the days on this map, used when the caller supplied no scale.
        /// </summary>
        public required UsageMapScale OwnScale { get; init; }

        /// <summary>The map's natural size.</summary>
        public required Size Size { get; init; }
    }

    /// <summary>
    /// The square the keyboard is on: which row it came from, and the square itself.
    /// </summary>
    /// <param name="RowIndex">The index into <see cref="Rows"/>.</param>
    /// <param name="Cell">The day.</param>
    /// <remarks>
    /// The rectangle is not kept here. A square moves whenever the theme, the scaling or the
    /// rows change, and a remembered rectangle would go on pointing at where it used to be.
    /// </remarks>
    private readonly record struct Selection(int RowIndex, UsageMapCell Cell);

    /// <summary>One row's grid.</summary>
    private sealed class MapBlock
    {
        /// <summary>The index into <see cref="Rows"/> this block was built from.</summary>
        public required int RowIndex { get; init; }

        /// <summary>The row's label.</summary>
        public required string Name { get; init; }

        /// <summary>The top of the label.</summary>
        public required double LabelTop { get; init; }

        /// <summary>The top of the grid.</summary>
        public required double GridTop { get; init; }

        /// <summary>Where the rule above a combined row goes, or NaN when there is none.</summary>
        public required double SeparatorY { get; init; }

        /// <summary>
        /// The row's days, indexed by their distance from <see cref="MapLayout.Start"/>. A
        /// null entry is a day the row was not given, which is outside the map rather than
        /// unknown inside it, and nothing is drawn for it.
        /// </summary>
        public required UsageMapCell?[] Cells { get; init; }
    }
}
