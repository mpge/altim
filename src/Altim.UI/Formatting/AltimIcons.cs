using Avalonia.Media;

namespace Altim.UI.Formatting;

/// <summary>
/// The line icons the interface draws, on a 16px box.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than imported. Altim carries one bitmap, its own mark, and nothing else: an
/// icon set as an asset would need a second copy for the dark variant, would not follow the
/// ink colour, and would put artwork somebody has to license into the repository. Every
/// geometry here is stroked at <c>AltimIconStrokeThickness</c> in the ink of the surface it
/// stands on, which is what keeps one weight across the whole set.
/// </para>
/// <para>
/// The box is 16 by 16 and the shapes are inset from it, so an icon beside a 14px label sits
/// on the label's optical centre rather than overhanging it. A provider's own mark is not
/// here: it is identity rather than furniture and lives in <see cref="ProviderIdentity"/>.
/// </para>
/// <para>
/// As in <see cref="ProviderIdentity"/>, every path is held as a string and parsed on first
/// read. <see cref="Geometry.Parse"/> needs Avalonia's rendering platform, and a parse inside
/// a type initialiser throws - permanently, for the life of the process - the first time the
/// type is touched before the platform exists.
/// </para>
/// </remarks>
public static class AltimIcons
{
    /// <summary>Overview: a house, roof then body then door.</summary>
    public const string HomePath =
        "M 2.25,7 L 8,2 L 13.75,7 L 13.75,13.75 L 2.25,13.75 Z M 6.25,13.75 L 6.25,9.25 L 9.75,9.25 L 9.75,13.75";

    /// <summary>History: a clock face with its hands at a quarter past.</summary>
    public const string ClockPath =
        "M 1.75,8 A 6.25,6.25 0 1 0 14.25,8 A 6.25,6.25 0 1 0 1.75,8 M 8,4.25 L 8,8 L 10.75,9.75";

    /// <summary>Settings: a six toothed gear around an open centre.</summary>
    public const string SettingsPath =
        "M 6.21,3.33 L 6.54,1.15 L 9.46,1.15 L 9.79,3.33 A 5,5 0 0 1 11.15,4.11 L 13.2,3.32 "
        + "L 14.66,5.84 L 12.94,7.22 A 5,5 0 0 1 12.94,8.78 L 14.66,10.16 L 13.2,12.68 "
        + "L 11.15,11.89 A 5,5 0 0 1 9.79,12.67 L 9.46,14.85 L 6.54,14.85 L 6.21,12.67 "
        + "A 5,5 0 0 1 4.85,11.89 L 2.8,12.68 L 1.34,10.16 L 3.06,8.78 A 5,5 0 0 1 3.06,7.22 "
        + "L 1.34,5.84 L 2.8,3.32 L 4.85,4.11 A 5,5 0 0 1 6.21,3.33 Z "
        + "M 5.8,8 A 2.2,2.2 0 1 0 10.2,8 A 2.2,2.2 0 1 0 5.8,8 Z";

    /// <summary>The disclosure mark beside a name that opens a page of its own.</summary>
    public const string ChevronRightPath = "M 6.25,3.5 L 10.75,8 L 6.25,12.5";

    /// <summary>The mark on a control that opens a list under itself.</summary>
    public const string ChevronDownPath = "M 3.5,6.25 L 8,10.75 L 12.5,6.25";

    /// <summary>The mark on the action that leaves the panel for the window.</summary>
    public const string ArrowRightPath = "M 2.5,8 L 13,8 M 8.75,3.75 L 13,8 L 8.75,12.25";

    /// <summary>The tick inside the status icon when every provider answered.</summary>
    public const string CheckPath = "M 4.25,8.25 L 6.75,10.75 L 11.75,5.5";

    /// <summary>The bar and dot inside the status icon when one did not.</summary>
    public const string AlertPath = "M 8,4.25 L 8,9 M 8,11.5 L 8.01,11.5";

    private static readonly Lazy<Geometry> LazyHome = Lazily(HomePath);
    private static readonly Lazy<Geometry> LazyClock = Lazily(ClockPath);
    private static readonly Lazy<Geometry> LazySettings = Lazily(SettingsPath);
    private static readonly Lazy<Geometry> LazyChevronRight = Lazily(ChevronRightPath);
    private static readonly Lazy<Geometry> LazyChevronDown = Lazily(ChevronDownPath);
    private static readonly Lazy<Geometry> LazyArrowRight = Lazily(ArrowRightPath);
    private static readonly Lazy<Geometry> LazyCheck = Lazily(CheckPath);
    private static readonly Lazy<Geometry> LazyAlert = Lazily(AlertPath);

    /// <inheritdoc cref="HomePath" />
    public static Geometry Home => LazyHome.Value;

    /// <inheritdoc cref="ClockPath" />
    public static Geometry Clock => LazyClock.Value;

    /// <inheritdoc cref="SettingsPath" />
    public static Geometry Settings => LazySettings.Value;

    /// <inheritdoc cref="ChevronRightPath" />
    public static Geometry ChevronRight => LazyChevronRight.Value;

    /// <inheritdoc cref="ChevronDownPath" />
    public static Geometry ChevronDown => LazyChevronDown.Value;

    /// <inheritdoc cref="ArrowRightPath" />
    public static Geometry ArrowRight => LazyArrowRight.Value;

    /// <inheritdoc cref="CheckPath" />
    public static Geometry Check => LazyCheck.Value;

    /// <inheritdoc cref="AlertPath" />
    public static Geometry Alert => LazyAlert.Value;

    private static Lazy<Geometry> Lazily(string path) =>
        new(() => Geometry.Parse(path), LazyThreadSafetyMode.ExecutionAndPublication);
}
