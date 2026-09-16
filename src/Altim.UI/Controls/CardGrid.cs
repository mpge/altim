using Avalonia;
using Avalonia.Controls;

namespace Altim.UI.Controls;

/// <summary>
/// The Overview grid: equal columns with a gap between them, dropping to fewer columns when
/// there is no longer room for a card of a readable width.
/// </summary>
/// <remarks>
/// <para>
/// A card is a fixed amount of reading: a provider name, two or three figures with their
/// meters, and a footer. Below <see cref="MinimumColumnWidth"/> the figures start colliding
/// with the labels beside them and the meters collapse towards their floor, so the grid takes
/// a column away rather than letting every card in the row become unreadable at once. That is
/// the whole behaviour - there is no breakpoint list and no width the caller has to know.
/// </para>
/// <para>
/// Every cell in a row is arranged at the row's tallest height, so two cards side by side are
/// the same height whether one of them reports two windows and the other three. A panel that
/// ended higher than the one beside it would read as a mistake rather than as a difference in
/// what the providers reported.
/// </para>
/// </remarks>
public sealed class CardGrid : Panel
{
    /// <summary>The narrowest a column may be before the grid takes one away.</summary>
    public static readonly StyledProperty<double> MinimumColumnWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(MinimumColumnWidth), 320d);

    /// <summary>The most columns the grid will ever use, however wide it is.</summary>
    public static readonly StyledProperty<int> MaximumColumnsProperty =
        AvaloniaProperty.Register<CardGrid, int>(nameof(MaximumColumns), 2);

    /// <summary>The gap between two columns.</summary>
    public static readonly StyledProperty<double> ColumnGapProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(ColumnGap), 24d);

    /// <summary>The gap between two rows.</summary>
    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(RowGap), 24d);

    private int _columns = 1;

    static CardGrid() =>
        AffectsMeasure<CardGrid>(
            MinimumColumnWidthProperty,
            MaximumColumnsProperty,
            ColumnGapProperty,
            RowGapProperty);

    /// <inheritdoc cref="MinimumColumnWidthProperty" />
    public double MinimumColumnWidth
    {
        get => GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    /// <inheritdoc cref="MaximumColumnsProperty" />
    public int MaximumColumns
    {
        get => GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    /// <inheritdoc cref="ColumnGapProperty" />
    public double ColumnGap
    {
        get => GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <inheritdoc cref="RowGapProperty" />
    public double RowGap
    {
        get => GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    /// <summary>How many columns the last measure settled on.</summary>
    public int Columns => _columns;

    /// <summary>
    /// How many columns fit in a width. Public because it is the whole rule, and a rule
    /// worth a test is worth being able to test without a layout pass.
    /// </summary>
    /// <param name="width">The width available.</param>
    /// <param name="minimumColumnWidth">The narrowest a column may be.</param>
    /// <param name="maximumColumns">The most columns to use.</param>
    /// <param name="gap">The gap between columns.</param>
    /// <returns>At least one column, and never more than <paramref name="maximumColumns"/>.</returns>
    public static int ColumnsFor(double width, double minimumColumnWidth, int maximumColumns, double gap)
    {
        int ceiling = Math.Max(1, maximumColumns);
        if (double.IsNaN(width) || double.IsInfinity(width) || minimumColumnWidth <= 0d)
        {
            return ceiling;
        }

        for (int columns = ceiling; columns > 1; columns--)
        {
            double available = width - (gap * (columns - 1));
            if (available / columns >= minimumColumnWidth)
            {
                return columns;
            }
        }

        return 1;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _columns = ColumnsFor(availableSize.Width, MinimumColumnWidth, MaximumColumns, ColumnGap);

        double columnWidth = ColumnWidth(availableSize.Width);
        double total = 0d;
        double rowHeight = 0d;
        int inRow = 0;
        int rows = 0;

        foreach (Control child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            if (++inRow != _columns)
            {
                continue;
            }

            total += rowHeight + (rows > 0 ? RowGap : 0d);
            rows++;
            rowHeight = 0d;
            inRow = 0;
        }

        if (inRow > 0)
        {
            total += rowHeight + (rows > 0 ? RowGap : 0d);
        }

        double width = double.IsInfinity(availableSize.Width)
            ? (columnWidth * _columns) + (ColumnGap * (_columns - 1))
            : availableSize.Width;

        return new Size(width, total);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double columnWidth = ColumnWidth(finalSize.Width);
        double top = 0d;
        double rowHeight = 0d;
        int inRow = 0;
        int index = 0;

        // Two passes over each row: the first finds the row's height, the second arranges
        // every cell in it at that height, which is what makes the cards line up.
        while (index < Children.Count)
        {
            rowHeight = 0d;
            inRow = 0;
            for (int i = index; i < Children.Count && inRow < _columns; i++, inRow++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            for (int i = 0; i < inRow; i++)
            {
                Children[index + i].Arrange(new Rect(
                    i * (columnWidth + ColumnGap),
                    top,
                    columnWidth,
                    rowHeight));
            }

            index += inRow;
            top += rowHeight + RowGap;
        }

        return finalSize;
    }

    private double ColumnWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return MinimumColumnWidth;
        }

        double available = width - (ColumnGap * (_columns - 1));
        return Math.Max(0d, available / _columns);
    }
}
