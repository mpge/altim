namespace Altim.Core.Usage;

/// <summary>
/// The family a limit window belongs to. Derived from the window length in minutes and
/// never from a provider slot name, because slot position has been observed to change
/// meaning between provider releases.
/// </summary>
public enum LimitWindowKind
{
    /// <summary>
    /// A length that matches none of the known families. The default, so an
    /// unrecognised window is never mistaken for a known one.
    /// </summary>
    Other = 0,

    /// <summary>Roughly 300 minutes. Shown as "Session".</summary>
    FiveHour = 1,

    /// <summary>Roughly 1440 minutes.</summary>
    Daily = 2,

    /// <summary>Roughly 10080 minutes.</summary>
    Weekly = 3,

    /// <summary>Roughly 43200 minutes.</summary>
    Monthly = 4,
}
