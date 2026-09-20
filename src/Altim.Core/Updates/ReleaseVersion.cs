using System.Globalization;

namespace Altim.Core.Updates;

/// <summary>
/// A released version of Altim, parsed from a tag or an assembly version, and ordered
/// against another one.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the answer to "is there a newer version" must not be a string
/// comparison. <c>"0.10.0"</c> sorts before <c>"0.9.0"</c> as text, and a release feed that
/// told an installed copy to downgrade would be worse than no feed at all.
/// </para>
/// <para>
/// The grammar is the subset of SemVer2 the release workflow already enforces:
/// <c>major.minor.patch</c>, optionally followed by <c>-prerelease</c> and
/// <c>+build</c>. Build metadata is discarded, as SemVer requires — two builds of the same
/// version are the same version. A prerelease sorts <em>before</em> the release it leads to,
/// so <c>0.2.0-rc1</c> is older than <c>0.2.0</c>, which is the rule that keeps a release
/// candidate from being offered to somebody already running the final build.
/// </para>
/// <para>
/// Prerelease identifiers are compared the way SemVer says: dot-separated, numeric
/// identifiers numerically and below alphanumeric ones, and a shorter run of identifiers
/// before a longer one that starts the same way. Nothing here needs the full grammar's
/// leading-zero rules, so a numeric identifier is simply one made only of digits.
/// </para>
/// </remarks>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly string[] _prerelease;

    private ReleaseVersion(int major, int minor, int patch, string[] prerelease, string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
        Text = text;
    }

    /// <summary>The major component.</summary>
    public int Major { get; }

    /// <summary>The minor component.</summary>
    public int Minor { get; }

    /// <summary>The patch component.</summary>
    public int Patch { get; }

    /// <summary>True when this is a prerelease, which sorts before the same release.</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>
    /// The version as it was written, without any leading <c>v</c> and without build
    /// metadata. This is what goes on screen, so that a user comparing it with a release
    /// page sees the same characters.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Parses a version, accepting a leading <c>v</c> because that is how the tags are
    /// written.
    /// </summary>
    /// <param name="text">The text to parse. Null, empty or malformed gives null.</param>
    /// <returns>The version, or null when <paramref name="text"/> is not one.</returns>
    /// <remarks>
    /// Returning null rather than throwing is deliberate: the input is a tag read off a
    /// release page, so it is somebody else's data and being unparseable is an ordinary
    /// outcome rather than a bug. A caller that cannot parse a tag has learned that it
    /// does not know of a newer version, which is the safe answer.
    /// </remarks>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string body = text.Trim();
        if (body.StartsWith('v') || body.StartsWith('V'))
        {
            body = body[1..];
        }

        // Build metadata is not part of precedence, so it is dropped before anything else
        // looks at the string.
        int plus = body.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            body = body[..plus];
        }

        string[] prerelease = [];
        int dash = body.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            string tail = body[(dash + 1)..];
            if (tail.Length == 0)
            {
                return null;
            }

            prerelease = tail.Split('.');
            if (Array.Exists(prerelease, static part => part.Length == 0))
            {
                return null;
            }

            body = body[..dash];
        }

        string[] parts = body.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        if (!TryComponent(parts[0], out int major) ||
            !TryComponent(parts[1], out int minor) ||
            !TryComponent(parts[2], out int patch))
        {
            return null;
        }

        string canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{major}.{minor}.{patch}");
        if (prerelease.Length > 0)
        {
            canonical += "-" + string.Join('.', prerelease);
        }

        return new ReleaseVersion(major, minor, patch, prerelease, canonical);
    }

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int numeric = Major.CompareTo(other.Major);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0)
        {
            return numeric;
        }

        // A version with no prerelease outranks one with any, and that asymmetry is the
        // whole reason 0.2.0-rc1 is never offered to somebody on 0.2.0.
        if (_prerelease.Length == 0 && other._prerelease.Length == 0)
        {
            return 0;
        }

        if (_prerelease.Length == 0)
        {
            return 1;
        }

        if (other._prerelease.Length == 0)
        {
            return -1;
        }

        int shared = Math.Min(_prerelease.Length, other._prerelease.Length);
        for (int i = 0; i < shared; i++)
        {
            int part = CompareIdentifier(_prerelease[i], other._prerelease[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    /// <inheritdoc />
    public bool Equals(ReleaseVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, string.Join('.', _prerelease));

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>True when <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) < 0;

    /// <summary>True when <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) > 0;

    /// <summary>True when <paramref name="left"/> is not newer than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) <= 0;

    /// <summary>True when <paramref name="left"/> is not older than <paramref name="right"/>.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) >= 0;

    /// <summary>Equality by precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) == 0;

    /// <summary>Inequality by precedence.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) =>
        Compare(left, right) != 0;

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }

    private static bool TryComponent(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = IsNumeric(left);
        bool rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            // Long rather than int: an identifier is somebody else's text and a tag with
            // twenty digits in it should sort, not throw.
            bool leftFits = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out long l);
            bool rightFits = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long r);
            if (leftFits && rightFits)
            {
                return l.CompareTo(r);
            }

            // Both are digits and at least one overflowed, so length then text is the
            // numeric order.
            int byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
