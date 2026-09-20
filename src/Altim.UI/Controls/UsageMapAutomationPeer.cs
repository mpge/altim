using System.Globalization;
using Altim.UI.Formatting;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Altim.UI.Controls;

/// <summary>
/// What an assistive technology is handed in place of the map's drawn grid.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UsageMap"/> paints a thousand squares into one element rather than building a
/// thousand controls, which is the right trade for memory and the wrong one for accessibility:
/// a drawn rectangle has no name, no position and no existence as far as automation is
/// concerned, so the whole year reads as a single unlabelled shape. This peer restores it, by
/// synthesising one child per square from the rows the map was given.
/// </para>
/// <para>
/// The children are not visuals and are never laid out. Each one answers a name - the row, the
/// date and the day's value - the square's own words as help text, and the square's rectangle,
/// all read back from the map through <see cref="UsageMap.CellBounds"/> so the arithmetic that
/// places a square is stated once.
/// </para>
/// <para>
/// They are built on demand and thrown away whenever <see cref="UsageMap.Rows"/> is replaced.
/// A screen reader that never visits the map never pays for them.
/// </para>
/// <para>
/// <b>The keyboard's square is reported as this peer's name, and a change of square raises a
/// name-changed event.</b> The map is one focusable element, so the only thing the automation
/// root can ever answer "what has the focus" with is the map itself: there is no element
/// behind a square for it to name instead. Telling the client the children changed would make
/// a screen reader re-read a year of squares on every arrow key. Re-reading one string is the
/// notification that says "the selection moved", and it is the one a client acts on.
/// </para>
/// </remarks>
public sealed class UsageMapAutomationPeer : ControlAutomationPeer
{
    /// <summary>What the map is called when nothing else names it.</summary>
    public const string MapName = "Daily usage";

    private string? _announced;

    /// <summary>Initializes a peer over one map.</summary>
    /// <param name="owner">The map the peer speaks for.</param>
    public UsageMapAutomationPeer(UsageMap owner)
        : base(owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.PropertyChanged += OnOwnerPropertyChanged;
        owner.FocusedCellChanged += OnOwnerFocusedCellChanged;
        _announced = MapName;
    }

    private UsageMap Map => (UsageMap)Owner;

    /// <summary>
    /// The words one square is read out as: the row it is in, the day it is, and what that day
    /// counted.
    /// </summary>
    /// <param name="rowName">The row's label.</param>
    /// <param name="cell">The day.</param>
    /// <returns>A sentence naming the square.</returns>
    /// <remarks>
    /// A day nothing is known about says so rather than reading as a zero, which is the same
    /// distinction the squares themselves are drawn to keep.
    /// </remarks>
    public static string NameFor(string rowName, UsageMapCell cell)
    {
        ArgumentNullException.ThrowIfNull(rowName);
        ArgumentNullException.ThrowIfNull(cell);

        string value = (cell.IsKnown, cell.Tokens) switch
        {
            (false, _) => "no data",
            (true, { } tokens) => string.Concat(UsageFormat.Count(tokens), " tokens"),
            _ => "no token figures reported",
        };

        return string.Concat(
            rowName,
            ", ",
            cell.Day.ToString("D", CultureInfo.CurrentCulture),
            ", ",
            value);
    }

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(UsageMap);

    /// <inheritdoc />
    /// <remarks>
    /// While the keyboard is on a square, the map is called that square. Anything else would
    /// leave a client that asked the focused element what it is with the name of the whole
    /// grid, which is where it already was.
    /// </remarks>
    protected override string? GetNameCore() =>
        FocusedName() ?? (base.GetNameCore() is { Length: > 0 } named ? named : MapName);

    /// <inheritdoc />
    protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
    {
        if (Map.Rows is not { } rows)
        {
            return [];
        }

        List<AutomationPeer> squares = [];
        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not { } row)
            {
                continue;
            }

            foreach (UsageMapCell cell in row.Cells)
            {
                // A day the map draws nothing for is a day outside the picture. Naming it
                // would put a square in the reading order that is not on the screen.
                if (cell is not null && Map.CellBounds(index, cell.Day) is not null)
                {
                    squares.Add(new UsageMapCellAutomationPeer(this, index, row.Name, cell));
                }
            }
        }

        return squares;
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == UsageMap.RowsProperty)
        {
            InvalidateChildren();
        }
    }

    /// <summary>
    /// Tells the client the selection moved, by the one route the platform offers: the name
    /// of the element that actually holds the focus has changed.
    /// </summary>
    private void OnOwnerFocusedCellChanged(object? sender, EventArgs e)
    {
        string? previous = _announced;
        _announced = GetName();

        if (!string.Equals(previous, _announced, StringComparison.Ordinal))
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, previous, _announced);
        }
    }

    /// <summary>The focused square's words, or nothing when no square is focused.</summary>
    private string? FocusedName() =>
        Map is { FocusedCell: { } focused, Rows: { } rows }
        && focused.RowIndex >= 0
        && focused.RowIndex < rows.Count
        && rows[focused.RowIndex] is { } row
            ? NameFor(row.Name, focused.Cell)
            : null;
}

/// <summary>
/// One square of the map, as automation sees it.
/// </summary>
/// <remarks>
/// There is no control behind this and there is deliberately no attempt to invent one. The
/// peer exists to carry a name, a description and a rectangle for something that was drawn,
/// and it answers everything else the way a label answers it: not interactive, and with no
/// children of its own. It is keyboard focusable, though, because the map is: every square in
/// a focusable map can be reached with the arrow keys, and the one the keyboard is on says so.
/// </remarks>
internal sealed class UsageMapCellAutomationPeer : AutomationPeer
{
    private readonly UsageMapAutomationPeer _map;
    private readonly int _rowIndex;
    private readonly string _rowName;
    private readonly UsageMapCell _cell;
    private AutomationPeer? _parent;

    /// <summary>Initializes a peer over one square.</summary>
    /// <param name="map">The map's own peer, which is this one's parent.</param>
    /// <param name="rowIndex">The index into the map's rows the square came from.</param>
    /// <param name="rowName">The row's label.</param>
    /// <param name="cell">The day.</param>
    public UsageMapCellAutomationPeer(UsageMapAutomationPeer map, int rowIndex, string rowName, UsageMapCell cell)
    {
        _map = map;
        _rowIndex = rowIndex;
        _rowName = rowName;
        _cell = cell;
        _parent = map;
    }

    private UsageMap Map => (UsageMap)_map.Owner;

    /// <inheritdoc />
    protected override void BringIntoViewCore()
    {
        // The whole map is one element and never scrolls a square of its own accord.
    }

    /// <inheritdoc />
    protected override string? GetAcceleratorKeyCore() => null;

    /// <inheritdoc />
    protected override string? GetAccessKeyCore() => null;

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    /// <inheritdoc />
    protected override string? GetAutomationIdCore() => null;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Read back from the map rather than remembered, so a square that has moved - a different
    /// square size from the theme, a different scaling - reports where it actually is.
    /// </para>
    /// <para>
    /// <b>There are two steps from a square to the screen and both are needed.</b> The map
    /// answers in its own coordinates. Up the tree to the window is where the map's own
    /// position inside that window is added, and
    /// <see cref="ControlAutomationPeer.ToScreen"/> takes window coordinates out to screen
    /// ones - it does not do the first step, which is why the peer for the map itself
    /// transforms before calling it. Handing it a square's map coordinates skipped that and
    /// named every square short by wherever the map happened to sit: 540 points, for a panel
    /// centred in the dashboard window.
    /// </para>
    /// </remarks>
    protected override Rect GetBoundingRectangleCore()
    {
        if (Map.CellBounds(_rowIndex, _cell.Day) is not { } bounds
            || TopLevel.GetTopLevel(Map) is not { } window
            || Map.TransformToVisual(window) is not { } toWindow)
        {
            return default;
        }

        return _map.ToScreen(bounds.TransformToAABB(toWindow)) ?? default;
    }

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(UsageMapCell);

    /// <inheritdoc />
    protected override string? GetHelpTextCore() => _cell.Detail;

    /// <inheritdoc />
    protected override AutomationPeer? GetLabeledByCore() => null;

    /// <inheritdoc />
    protected override string? GetNameCore() => UsageMapAutomationPeer.NameFor(_rowName, _cell);

    /// <inheritdoc />
    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];

    /// <inheritdoc />
    protected override AutomationPeer? GetParentCore() => _parent;

    /// <inheritdoc />
    protected override bool HasKeyboardFocusCore() =>
        Map is { IsFocused: true, FocusedCell: { } focused }
        && focused.RowIndex == _rowIndex
        && focused.Cell.Day == _cell.Day;

    /// <inheritdoc />
    protected override bool IsContentElementCore() => true;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;

    /// <inheritdoc />
    protected override bool IsEnabledCore() => true;

    /// <inheritdoc />
    protected override bool IsKeyboardFocusableCore() => Map.Focusable;

    /// <inheritdoc />
    /// <remarks>
    /// The square is paint and the map is the focusable element, so this focuses the map and
    /// puts its keyboard selection on this day.
    /// </remarks>
    protected override void SetFocusCore() => Map.TryFocusCell(_rowIndex, _cell.Day);

    /// <inheritdoc />
    protected override bool ShowContextMenuCore() => false;

    /// <inheritdoc />
    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent ?? _map;
        return true;
    }
}
