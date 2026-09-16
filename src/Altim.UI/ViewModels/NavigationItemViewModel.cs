namespace Altim.UI.ViewModels;

/// <summary>
/// Which mark a sidebar row carries.
/// </summary>
public enum NavigationIcon
{
    /// <summary>The Overview row: a house.</summary>
    Overview = 0,

    /// <summary>A provider row: that provider's own mark, in its own accent.</summary>
    Provider = 1,

    /// <summary>The History row: a clock.</summary>
    History = 2,

    /// <summary>The Settings row: a gear.</summary>
    Settings = 3,
}

/// <summary>
/// One sidebar row: a name, a 16px mark and the page it shows.
/// </summary>
/// <remarks>
/// The row says which mark it wants, not what the mark is. A geometry cannot be built before
/// Avalonia's rendering platform exists, so a view model that held one could not be
/// constructed by a plain unit test - or by anything else that runs before a window does. The
/// view turns these flags into style classes and the style supplies the path, which happens
/// when the row is already on screen.
/// </remarks>
public sealed class NavigationItemViewModel
{
    /// <summary>Initializes a sidebar row.</summary>
    /// <param name="page">The page the row shows.</param>
    /// <param name="icon">The mark the row carries.</param>
    /// <param name="isAnthropic">Whether a provider mark wears the Anthropic accent.</param>
    /// <param name="isOpenAI">Whether a provider mark wears the OpenAI accent.</param>
    public NavigationItemViewModel(
        IDashboardPage page,
        NavigationIcon icon = NavigationIcon.Overview,
        bool isAnthropic = false,
        bool isOpenAI = false)
    {
        ArgumentNullException.ThrowIfNull(page);

        Page = page;
        Icon = icon;
        IsAnthropic = isAnthropic;
        IsOpenAI = isOpenAI;
    }

    /// <summary>The page the row shows.</summary>
    public IDashboardPage Page { get; }

    /// <summary>The row's name.</summary>
    public string Title => Page.Title;

    /// <summary>The mark the row carries.</summary>
    public NavigationIcon Icon { get; }

    /// <summary>Whether the row's mark is a provider's own, rather than a line icon.</summary>
    public bool IsProviderMark => Icon == NavigationIcon.Provider;

    /// <summary>Whether the row carries the house.</summary>
    public bool IsOverviewIcon => Icon == NavigationIcon.Overview;

    /// <summary>Whether the row carries the clock.</summary>
    public bool IsHistoryIcon => Icon == NavigationIcon.History;

    /// <summary>Whether the row carries the gear.</summary>
    public bool IsSettingsIcon => Icon == NavigationIcon.Settings;

    /// <summary>Whether the mark wears the Anthropic accent.</summary>
    public bool IsAnthropic { get; }

    /// <summary>Whether the mark wears the OpenAI accent.</summary>
    public bool IsOpenAI { get; }
}
