namespace Altim.Core.Models;

/// <summary>
/// What one provider used on one local calendar day.
/// </summary>
/// <param name="ProviderId">The provider the day belongs to.</param>
/// <param name="Day">The user's local calendar day.</param>
/// <param name="Tokens">
/// The day's token components, or <see langword="null"/> when none were reported. A component
/// inside it may be null on its own; null means unreported and never zero.
/// </param>
/// <param name="PeakPercent">
/// The highest percentage any of that provider's windows reached that day, or
/// <see langword="null"/> when no window reported one. Never combined across providers.
/// </param>
/// <param name="Source">Whether Altim observed the day or read it from the provider's history.</param>
/// <param name="UpdatedAt">When the row was last written.</param>
public sealed record UsageDay(
    string ProviderId,
    DateOnly Day,
    TokenTotals? Tokens,
    double? PeakPercent,
    UsageDaySource Source,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The day's total token volume, or <see langword="null"/> when nothing was reported.
    /// A component that was not reported contributes nothing rather than zero.
    /// </summary>
    public long? TotalTokens
    {
        get
        {
            if (Tokens is null)
            {
                return null;
            }

            long? total = null;
            foreach (long? part in new[] { Tokens.Input, Tokens.Output, Tokens.CacheRead, Tokens.CacheWrite })
            {
                if (part is { } value)
                {
                    total = (total ?? 0) + value;
                }
            }

            return total;
        }
    }
}
