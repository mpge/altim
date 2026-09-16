namespace Altim.UI.Tests.Fakes;

/// <summary>
/// A clock the tests own.
/// </summary>
/// <remarks>
/// The local zone is UTC so a greeting or a clock time reads the same on every machine that
/// runs the suite. Reset arithmetic and the greeting both depend on the local hour, and a
/// suite that passes only in one time zone is not a suite.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    /// <summary>Initializes the clock at an instant.</summary>
    /// <param name="utcNow">The instant the clock reads.</param>
    public TestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    /// <summary>The instant the clock reads. Settable, so a test can move time on.</summary>
    public DateTimeOffset UtcNow { get; set; }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
