namespace Altim.Core.Monitoring;

/// <summary>
/// A provider refresh that failed, carried to whoever does the logging. The reading the
/// user sees says "Unable to retrieve usage" and nothing else; the exception behind it
/// names file paths, commands and hosts, which belongs in a log file and never on screen.
/// </summary>
/// <param name="ProviderId">The provider whose refresh failed.</param>
/// <param name="Exception">
/// What went wrong, unmodified. Includes the timeout raised when a provider did not
/// answer inside <see cref="MonitorSchedulerOptions.ProviderTimeout"/>.
/// </param>
/// <param name="OccurredAt">When the failure was observed.</param>
public sealed record ProviderFailure(string ProviderId, Exception Exception, DateTimeOffset OccurredAt);
