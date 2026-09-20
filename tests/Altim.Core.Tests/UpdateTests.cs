using System.Text;
using Altim.Core.Updates;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The three decisions behind "is there a newer Altim": what a version is, which of two is
/// newer, and when it is worth asking.
/// </summary>
/// <remarks>
/// Every one of these is a thing that would be silently wrong in production. A string
/// comparison offers 0.9.0 to somebody on 0.10.0; a prerelease that outranks its release
/// offers 0.2.0-rc1 to somebody on 0.2.0; a tag reader that matches the first
/// <c>tag_name</c> at any depth reads an asset's uploader instead of the release.
/// </remarks>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 0.0.1 ", 0, 0, 1)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void ParsesTheThreeComponents(string text, int major, int minor, int patch)
    {
        ReleaseVersion? version = ReleaseVersion.TryParse(text);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.False(version.IsPrerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("-1.2.3")]
    public void RefusesAnythingThatIsNotAVersion(string? text) =>
        Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public void DiscardsBuildMetadata()
    {
        // The SDK appends +<sha> to the informational version, so the running version
        // arrives in this shape on every real build.
        ReleaseVersion? stamped = ReleaseVersion.TryParse("0.2.0+1f3badc");
        ReleaseVersion? tagged = ReleaseVersion.TryParse("v0.2.0");

        Assert.NotNull(stamped);
        Assert.Equal(tagged, stamped);
        Assert.Equal("0.2.0", stamped.Text);
    }

    /// <summary>
    /// The defect this covers: a string comparison. "0.10.0" sorts before "0.9.0" as text,
    /// so an update check built on one would tell somebody running 0.10.0 to downgrade.
    /// </summary>
    [Fact]
    public void OrdersNumericallyRatherThanAsText()
    {
        ReleaseVersion? ten = ReleaseVersion.TryParse("0.10.0");
        ReleaseVersion? nine = ReleaseVersion.TryParse("0.9.0");

        Assert.True(string.CompareOrdinal("0.10.0", "0.9.0") < 0);
        Assert.True(ten > nine);
        Assert.True(nine < ten);
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0-rc1", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-rc1", "1.0.0-rc2")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1.1")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    public void OrdersOlderBeforeNewer(string older, string newer)
    {
        ReleaseVersion? left = ReleaseVersion.TryParse(older);
        ReleaseVersion? right = ReleaseVersion.TryParse(newer);

        Assert.True(left < right, older + " should be older than " + newer);
        Assert.True(right > left, newer + " should be newer than " + older);
        Assert.NotEqual(left, right);
    }

    /// <summary>
    /// The defect this covers: a prerelease that outranks the release it led to. The
    /// release workflow's own throwaway tag is 0.0.1-rc1, so this is not hypothetical —
    /// without the rule, everybody on 0.0.1 would be offered the candidate.
    /// </summary>
    [Fact]
    public void APrereleaseIsOlderThanItsRelease()
    {
        ReleaseVersion? candidate = ReleaseVersion.TryParse("0.0.1-rc1");
        ReleaseVersion? release = ReleaseVersion.TryParse("0.0.1");

        Assert.NotNull(candidate);
        Assert.NotNull(release);
        Assert.True(candidate.IsPrerelease);
        Assert.False(release.IsPrerelease);
        Assert.True(candidate < release);
    }

    [Fact]
    public void EqualVersionsCompareEqualWhicheverWayTheyWereWritten()
    {
        ReleaseVersion? left = ReleaseVersion.TryParse("v1.2.3");
        ReleaseVersion? right = ReleaseVersion.TryParse("1.2.3+abc");

        Assert.Equal(left, right);
        Assert.Equal(left!.GetHashCode(), right!.GetHashCode());
        Assert.True(left <= right);
        Assert.True(left >= right);
        Assert.False(left < right);
        Assert.False(left > right);
    }

    [Fact]
    public void ANullIsOlderThanAnyVersion()
    {
        ReleaseVersion? version = ReleaseVersion.TryParse("0.1.0");

        Assert.True(null < version);
        Assert.True(version > null);
        Assert.False(version == null);
    }

    [Fact]
    public void TextIsWhatGoesOnScreen()
    {
        Assert.Equal("1.2.3", ReleaseVersion.TryParse("v1.2.3")!.Text);
        Assert.Equal("1.2.3-rc.1", ReleaseVersion.TryParse("v1.2.3-rc.1+deadbee")!.Text);
    }
}

/// <summary>Reading a release tag out of GitHub's own response shape.</summary>
public class ReleaseFeedTests
{
    /// <summary>
    /// Trimmed from a real <c>releases/latest</c> response. The nesting is the point: an
    /// asset carries a <c>name</c> and an <c>uploader</c> object, so a reader that matched
    /// a key at any depth would have somewhere wrong to land.
    /// </summary>
    private const string Response = """
        {
          "url": "https://api.github.com/repos/mpge/altim/releases/1",
          "assets": [
            {
              "name": "Altim-win-Setup.exe",
              "uploader": { "login": "mpge", "tag_name": "v99.0.0" }
            }
          ],
          "tag_name": "v0.2.0",
          "draft": false,
          "prerelease": false
        }
        """;

    [Fact]
    public void ReadsTheTopLevelTag()
    {
        ReleaseVersion? version = ReleaseFeed.ReadLatestTag(Encoding.UTF8.GetBytes(Response));

        Assert.NotNull(version);
        Assert.Equal("0.2.0", version.Text);
    }

    /// <summary>
    /// The defect this covers: a shape-blind search for "tag_name". The fixture puts a
    /// v99.0.0 inside an asset's uploader, which is where one would land, and 99.0.0 is
    /// newer than anything Altim will ship for a while — so the mistake would look like a
    /// working update check right up until it offered a release that does not exist.
    /// </summary>
    [Fact]
    public void IgnoresAMatchingKeyNestedInsideAnAsset()
    {
        ReleaseVersion? version = ReleaseFeed.ReadLatestTag(Encoding.UTF8.GetBytes(Response));

        Assert.NotEqual(ReleaseVersion.TryParse("99.0.0"), version);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tag_name": null}""")]
    [InlineData("""{"tag_name": 3}""")]
    [InlineData("""{"tag_name": "latest"}""")]
    [InlineData("""{"message": "Not Found"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    // Truncated mid-value: the field is there and its content is not.
    [InlineData("""{"tag_name": "v1.0""")]
    public void AnythingUnreadableIsNoNewerVersion(string body) =>
        Assert.Null(ReleaseFeed.ReadLatestTag(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void ReadsATagWithAPrereleaseSuffix()
    {
        ReleaseVersion? version = ReleaseFeed.ReadLatestTag(
            Encoding.UTF8.GetBytes("""{"tag_name": "v0.0.1-rc1"}"""));

        Assert.NotNull(version);
        Assert.True(version.IsPrerelease);
        Assert.Equal("0.0.1-rc1", version.Text);
    }
}

/// <summary>When Altim is allowed to ask.</summary>
public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverWhenTheUserTurnedItOff()
    {
        Assert.False(UpdateSchedule.IsDue(
            enabled: false,
            startedAt: Start,
            lastCheckedAt: null,
            now: Start + TimeSpan.FromDays(7)));
    }

    /// <summary>
    /// The defect this covers: a check on the start-up path. The cold-start budget is 800ms
    /// to a tray icon and a DNS lookup behind a captive portal takes far longer, so a check
    /// that ran at zero seconds would put a network request between the user and their icon.
    /// </summary>
    [Fact]
    public void NotWhileTheProcessIsStillSettling()
    {
        Assert.False(UpdateSchedule.IsDue(true, Start, null, Start));
        Assert.False(UpdateSchedule.IsDue(true, Start, null, Start + TimeSpan.FromSeconds(119)));
        Assert.True(UpdateSchedule.IsDue(true, Start, null, Start + UpdateSchedule.Settle));
    }

    [Fact]
    public void OnceADayAfterThat()
    {
        DateTimeOffset checkedAt = Start + UpdateSchedule.Settle;

        Assert.False(UpdateSchedule.IsDue(true, Start, checkedAt, checkedAt + TimeSpan.FromHours(23)));
        Assert.True(UpdateSchedule.IsDue(true, Start, checkedAt, checkedAt + UpdateSchedule.Interval));
    }

    /// <summary>
    /// The defect this covers: a stamp in the future. A laptop that resumes and then
    /// corrects its clock leaves one, and treating it as "not due" would suspend checks
    /// until real time caught up — which for a clock that was hours fast means a day of
    /// silence with no way to tell.
    /// </summary>
    [Fact]
    public void AClockThatWentBackwardsDoesNotSuspendChecking()
    {
        DateTimeOffset stampedInTheFuture = Start + TimeSpan.FromHours(6);

        Assert.True(UpdateSchedule.IsDue(
            enabled: true,
            startedAt: Start,
            lastCheckedAt: stampedInTheFuture,
            now: Start + UpdateSchedule.Settle));
    }
}
