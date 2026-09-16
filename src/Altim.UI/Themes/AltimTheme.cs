using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Altim.UI.Themes;

/// <summary>
/// The Altim design system as one resource dictionary: tokens, primitives, typography and
/// every control theme. This is the code behind for
/// <c>avares://Altim.UI/Themes/Generic.axaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// The application merges it into <c>Application.Resources</c>, which Avalonia searches
/// ahead of <c>Application.Styles</c>. That ordering is what lets the Altim control themes
/// win over the base theme's, whatever order the styles were added in:
/// </para>
/// <code>
/// Styles.Add(new SimpleTheme());
/// Resources.MergedDictionaries.Add(new AltimTheme());
/// </code>
/// <para>
/// Every colour token lives in a theme dictionary, so consumers must reach them with
/// <c>DynamicResource</c>. <c>StaticResource</c> has no theme variant to resolve against
/// and throws.
/// </para>
/// </remarks>
public partial class AltimTheme : ResourceDictionary
{
    /// <summary>Initializes and loads the design system dictionary.</summary>
    public AltimTheme() => AvaloniaXamlLoader.Load(this);
}
