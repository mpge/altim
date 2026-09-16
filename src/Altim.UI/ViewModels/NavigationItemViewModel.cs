using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One sidebar row: a name, an optional 16px mark and the page it shows.
/// </summary>
/// <remarks>
/// The mark is only set for a provider row, where it carries the provider accent. The other rows
/// stay plain, because DESIGN.md gives navigation no accent of its own.
/// </remarks>
public sealed class NavigationItemViewModel : ObservableObject
{
    /// <summary>Initializes a sidebar row.</summary>
    /// <param name="page">The page the row shows.</param>
    /// <param name="glyph">The 16px mark, or null for a row without one.</param>
    /// <param name="isAnthropic">Whether the mark wears the Anthropic accent.</param>
    /// <param name="isOpenAI">Whether the mark wears the OpenAI accent.</param>
    public NavigationItemViewModel(
        IDashboardPage page,
        Geometry? glyph = null,
        bool isAnthropic = false,
        bool isOpenAI = false)
    {
        ArgumentNullException.ThrowIfNull(page);

        Page = page;
        Glyph = glyph;
        IsAnthropic = isAnthropic;
        IsOpenAI = isOpenAI;
    }

    /// <summary>The page the row shows.</summary>
    public IDashboardPage Page { get; }

    /// <summary>The row's name.</summary>
    public string Title => Page.Title;

    /// <summary>The 16px mark, or null.</summary>
    public Geometry? Glyph { get; }

    /// <summary>Whether a mark exists to draw.</summary>
    public bool HasGlyph => Glyph is not null;

    /// <summary>Whether the mark wears the Anthropic accent.</summary>
    public bool IsAnthropic { get; }

    /// <summary>Whether the mark wears the OpenAI accent.</summary>
    public bool IsOpenAI { get; }
}
