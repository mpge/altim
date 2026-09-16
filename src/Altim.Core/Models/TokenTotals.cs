namespace Altim.Core.Models;

/// <summary>
/// Token counts for a provider or a session. Every component is independently
/// nullable because providers report different subsets of them.
/// </summary>
/// <param name="Input">
/// Uncached input tokens. <see langword="null"/> means the provider did not report
/// input tokens, which is not the same as reporting zero.
/// </param>
/// <param name="Output">
/// Generated output tokens. <see langword="null"/> means not reported.
/// </param>
/// <param name="CacheRead">
/// Tokens served from the prompt cache. <see langword="null"/> means not reported;
/// in particular it does not mean the cache went unused.
/// </param>
/// <param name="CacheWrite">
/// Tokens written into the prompt cache. <see langword="null"/> means not reported.
/// </param>
public sealed record TokenTotals(long? Input, long? Output, long? CacheRead, long? CacheWrite);
