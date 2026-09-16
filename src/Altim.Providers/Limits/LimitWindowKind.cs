namespace Altim.Providers.Limits;

/// <summary>
/// A limit window, classified by how long it runs for.
/// </summary>
/// <remarks>
/// Classification is by length and never by the slot a provider reported the window in.
/// The verified drift that forces this: one account's <c>codex</c> family reported a
/// 300-minute window in its primary slot in 2025-12 and a 10,080-minute window in the
/// same slot in 2026-09. Code that trusted the slot would have relabelled a weekly
/// allowance as a five-hour one and shown the user a number that meant nothing.
/// </remarks>
public enum LimitWindowKind
{
    /// <summary>The length matches no window Altim recognises. The label is derived from the length itself.</summary>
    Unknown = 0,

    /// <summary>About 300 minutes: the rolling session window.</summary>
    FiveHour = 1,

    /// <summary>About 1,440 minutes.</summary>
    Daily = 2,

    /// <summary>About 10,080 minutes.</summary>
    Weekly = 3,

    /// <summary>About 43,200 minutes.</summary>
    Monthly = 4,
}
