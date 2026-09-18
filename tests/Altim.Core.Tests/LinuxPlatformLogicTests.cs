using Altim.Core.Models;
using Altim.Platform.Linux;
using Altim.Platform.Linux.DBus;
using Altim.Platform.Linux.Desktop;
using Altim.Platform.Linux.Processes;
using Tmds.DBus.Protocol;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>XDG base directory resolution, including the rule that catches people out.</summary>
public sealed class XdgDirectoriesTests
{
    [Fact]
    public void AnAbsoluteEnvironmentValueWins() =>
        Assert.Equal("/custom/config", XdgDirectories.Resolve("/custom/config", "/home/u", ".config"));

    [Fact]
    public void ARelativeEnvironmentValueIsIgnoredRatherThanResolved() =>
        // The specification says a relative value is invalid. Resolving it against the
        // process directory would put the autostart entry wherever Altim was launched from.
        Assert.Equal("/home/u/.config", XdgDirectories.Resolve("relative/config", "/home/u", ".config"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetVariableFallsBackToHome(string? value) =>
        Assert.Equal("/home/u/.config", XdgDirectories.Resolve(value, "/home/u", ".config"));

    [Fact]
    public void ATrailingSeparatorIsTrimmed() =>
        Assert.Equal("/custom/config", XdgDirectories.Resolve("/custom/config/", "/home/u", ".config"));

    [Fact]
    public void NoHomeAndNoVariableMeansNowhereToWrite() =>
        Assert.Null(XdgDirectories.Resolve(null, null, ".config"));

    [Fact]
    public void AutostartHangsOffTheConfigHome() =>
        Assert.Equal("/home/u/.config/autostart", XdgDirectories.AutostartDirectory("/home/u/.config"));

    [Fact]
    public void NoConfigHomeMeansNoAutostartDirectory() =>
        Assert.Null(XdgDirectories.AutostartDirectory(null));
}

/// <summary>
/// The autostart desktop entry: what is written, what counts as enabled, and what a toggle
/// must leave alone.
/// </summary>
public sealed class DesktopEntryTests
{
    private const string Minimal = """
        [Desktop Entry]
        Type=Application
        Name=Altim
        Exec="/opt/altim/Altim"
        """;

    [Fact]
    public void AFreshEntryIsEnabled() =>
        Assert.True(DesktopEntry.IsEnabled(DesktopEntry.Build("Altim", "Monitor usage", "/opt/altim/Altim")));

    [Fact]
    public void AFreshEntryNamesTheExecutable() =>
        Assert.Contains(
            "Exec=\"/opt/altim/Altim\"",
            DesktopEntry.Build("Altim", "Monitor usage", "/opt/altim/Altim"),
            StringComparison.Ordinal);

    [Fact]
    public void AnEntryWithNoOpinionRuns() => Assert.True(DesktopEntry.IsEnabled(Minimal));

    [Fact]
    public void HiddenTrueDisables() =>
        Assert.False(DesktopEntry.IsEnabled(Minimal + "\nHidden=true\n"));

    [Fact]
    public void TheGnomeKeyAlsoDisables() =>
        // GNOME Tweaks writes this instead of Hidden, and an entry a user switched off there
        // must not be reported as enabled.
        Assert.False(DesktopEntry.IsEnabled(Minimal + "\nX-GNOME-Autostart-enabled=false\n"));

    [Fact]
    public void AMissingFileIsNotEnabled() => Assert.False(DesktopEntry.IsEnabled(null));

    [Fact]
    public void DisablingSetsBothKeysWhenNeitherIsPresent()
    {
        string disabled = DesktopEntry.SetHidden(Minimal, hidden: true);

        Assert.Contains("Hidden=true", disabled, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-enabled=false", disabled, StringComparison.Ordinal);
        Assert.False(DesktopEntry.IsEnabled(disabled));
    }

    [Fact]
    public void DisablingDoesNotDeleteTheRestOfTheFile()
    {
        string disabled = DesktopEntry.SetHidden(Minimal, hidden: true);

        Assert.Contains("Type=Application", disabled, StringComparison.Ordinal);
        Assert.Contains("Exec=\"/opt/altim/Altim\"", disabled, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingAgainIsTheExactInverse()
    {
        string entry = DesktopEntry.Build("Altim", "Monitor usage", "/opt/altim/Altim");
        string roundTripped = DesktopEntry.SetHidden(DesktopEntry.SetHidden(entry, hidden: true), hidden: false);

        Assert.True(DesktopEntry.IsEnabled(roundTripped));
    }

    [Fact]
    public void KeysAPackagerAddedSurviveAToggle()
    {
        const string customised = """
            [Desktop Entry]
            Type=Application
            Name=Altim
            Exec="/opt/altim/Altim"
            OnlyShowIn=GNOME;
            X-GNOME-Autostart-Delay=10
            """;

        string disabled = DesktopEntry.SetHidden(customised, hidden: true);

        Assert.Contains("OnlyShowIn=GNOME;", disabled, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-Delay=10", disabled, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheDesktopEntryGroupIsRead()
    {
        // A desktop action may carry its own Hidden key. It says nothing about whether the
        // application starts.
        const string withAction = """
            [Desktop Entry]
            Type=Application
            Name=Altim
            Exec="/opt/altim/Altim"

            [Desktop Action quit]
            Name=Quit
            Hidden=true
            """;

        Assert.True(DesktopEntry.IsEnabled(withAction));
    }

    [Fact]
    public void OnlyTheDesktopEntryGroupIsWritten()
    {
        const string withAction = """
            [Desktop Entry]
            Type=Application
            Name=Altim
            Exec="/opt/altim/Altim"
            Hidden=false

            [Desktop Action quit]
            Name=Quit
            Hidden=false
            """;

        string disabled = DesktopEntry.SetHidden(withAction, hidden: true);
        string[] hiddenLines = [.. disabled.Split('\n').Where(l => l.StartsWith("Hidden=", StringComparison.Ordinal))];

        Assert.Equal(["Hidden=true", "Hidden=false"], hiddenLines);
    }

    [Fact]
    public void AnOrdinaryPathIsQuotedAndOtherwiseUntouched() =>
        Assert.Equal("\"/opt/altim/Altim\"", DesktopEntry.QuoteExec("/opt/altim/Altim"));

    [Fact]
    public void APathWithASpaceStaysOneArgument() =>
        Assert.Equal("\"/opt/my apps/Altim\"", DesktopEntry.QuoteExec("/opt/my apps/Altim"));

    [Fact]
    public void ADollarSignIsEscapedTwice() =>
        // Once for the Exec quoting rules and once for the desktop file's own value escaping,
        // which is what the specification means by "escape backslashes twice".
        Assert.Equal("\"/opt/a\\\\$b/Altim\"", DesktopEntry.QuoteExec("/opt/a$b/Altim"));

    [Fact]
    public void ALineBreakIsDroppedRatherThanWritten() =>
        Assert.Equal("\"/opt/altim\"", DesktopEntry.QuoteExec("/opt/\naltim"));
}

/// <summary>Reading <c>/proc</c> without ever opening a command line.</summary>
public sealed class ProcFileSystemTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("1234", 1234)]
    public void ANumericEntryIsAProcess(string name, int expected)
    {
        Assert.True(ProcFileSystem.TryParseProcessId(name, out int processId));
        Assert.Equal(expected, processId);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("meminfo")]
    [InlineData("12a")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-1")]
    public void EverythingElseInProcIsSkipped(string? name) =>
        Assert.False(ProcFileSystem.TryParseProcessId(name, out _));

    [Fact]
    public void TheTrailingNewlineIsStripped() =>
        Assert.Equal("claude", ProcFileSystem.NormaliseComm("claude\n"));

    [Fact]
    public void NothingReadsAsAnEmptyName() => Assert.Equal(string.Empty, ProcFileSystem.NormaliseComm(null));

    [Fact]
    public void AnExactNameMatches() => Assert.True(ProcFileSystem.MatchesWatchedName("claude", "claude"));

    [Fact]
    public void MatchingIsCaseInsensitive() => Assert.True(ProcFileSystem.MatchesWatchedName("Claude", "claude"));

    [Fact]
    public void ADifferentNameDoesNotMatch() => Assert.False(ProcFileSystem.MatchesWatchedName("codex", "claude"));

    [Fact]
    public void ATruncatedNameStillMatchesTheLongNameItCameFrom() =>
        // The kernel caps comm at fifteen characters, so a longer executable name can only
        // ever be seen truncated.
        Assert.True(ProcFileSystem.MatchesWatchedName("verylongname123", "verylongname1234567"));

    [Fact]
    public void AShortNameIsNotTreatedAsAPrefix() =>
        // "claude" is six characters, so it was not truncated, so it is not the first six
        // characters of something longer.
        Assert.False(ProcFileSystem.MatchesWatchedName("claude", "claudecodehelperxyz"));

    [Theory]
    [InlineData(1970)]
    [InlineData(1)]
    public void AnImplausibleStampMeansUnknownRatherThanNineteenSeventy(int year) =>
        Assert.Null(ProcFileSystem.NormaliseStartTime(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void ARealStampComesBackAsUtc()
    {
        var stamp = new DateTime(2026, 9, 16, 8, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(stamp, TimeSpan.Zero), ProcFileSystem.NormaliseStartTime(stamp));
    }
}

/// <summary>Panel icon selection, and the guess it rests on.</summary>
public sealed class LinuxTrayAssetsTests
{
    [Theory]
    [InlineData(TrayIconVariant.Automatic, true, true)]
    [InlineData(TrayIconVariant.Automatic, false, false)]
    [InlineData(TrayIconVariant.Light, false, true)]
    [InlineData(TrayIconVariant.Dark, true, false)]
    public void AutomaticFollowsTheDesktopAndAnExplicitVariantDoesNot(
        TrayIconVariant variant, bool desktopIsDark, bool expectLight) =>
        Assert.Equal(expectLight, LinuxTrayAssets.UsesLightGlyph(variant, desktopIsDark));

    [Theory]
    [InlineData(false, 32, 32)]
    [InlineData(true, 33, 44)]
    [InlineData(true, 1024, 512)]
    [InlineData(false, 1, 16)]
    public void TheAssetChosenIsNeverSmallerThanAsked(bool light, int wanted, int expected) =>
        Assert.Equal(expected, LinuxTrayAssets.ChooseAssetSize(light, wanted));

    [Fact]
    public void TheLightGlyphIsTheWhiteRendering() =>
        Assert.Equal("altim-white-32.png", LinuxTrayAssets.FileName(light: true, 32));

    [Fact]
    public void TheDarkGlyphIsThePlainRendering() =>
        Assert.Equal("altim-32.png", LinuxTrayAssets.FileName(light: false, 32));
}

/// <summary>The appearance portal's value, including the shape it actually arrives in.</summary>
public sealed class PortalAppearanceTests
{
    [Theory]
    [InlineData(PortalAppearance.PreferDark, true)]
    [InlineData(PortalAppearance.PreferLight, false)]
    [InlineData(PortalAppearance.NoPreference, false)]
    [InlineData(99u, false)]
    public void OnlyPreferDarkIsDark(uint scheme, bool expected) =>
        // An unrecognised value means light. Guessing dark from a number the specification
        // has not defined yet would flip the whole UI on a desktop Altim has not met.
        Assert.Equal(expected, PortalAppearance.IsDark(scheme));

    [Fact]
    public void APlainNumberIsRead()
    {
        Assert.True(PortalAppearance.TryReadColorScheme(VariantValue.UInt32(1), out uint scheme));
        Assert.Equal(PortalAppearance.PreferDark, scheme);
    }

    [Fact]
    public void ANestedVariantIsUnwrapped()
    {
        // org.freedesktop.portal.Settings.Read returns a variant, and for the appearance
        // namespace the value inside it is itself a variant. ReadOne returns it unwrapped.
        // Both shapes have to work without a version check.
        VariantValue doubled = VariantValue.Variant(VariantValue.Variant(VariantValue.UInt32(1)));

        Assert.True(PortalAppearance.TryReadColorScheme(doubled, out uint scheme));
        Assert.Equal(PortalAppearance.PreferDark, scheme);
    }

    [Fact]
    public void SomeOtherShapeMeansNoPreferenceRatherThanAnError()
    {
        Assert.False(PortalAppearance.TryReadColorScheme(VariantValue.String("dark"), out uint scheme));
        Assert.Equal(PortalAppearance.NoPreference, scheme);
    }
}

/// <summary>
/// The animation preference the portal carries, including the shapes that mean "this desktop
/// has not said".
/// </summary>
public sealed class PortalAnimationsTests
{
    [Fact]
    public void AnimationsEnabledIsFullMotion() =>
        Assert.Equal(MotionPreference.Full, PortalAnimations.ToPreference(true));

    [Fact]
    public void AnimationsDisabledIsReducedMotion() =>
        Assert.Equal(MotionPreference.Reduced, PortalAnimations.ToPreference(false));

    [Fact]
    public void APortalThatDidNotAnswerIsUnknownRatherThanAnimated() =>
        // A KDE or Sway session has not said motion is wanted; it has said nothing, and the
        // difference has to survive all the way to the renderer.
        Assert.Equal(MotionPreference.Unknown, PortalAnimations.ToPreference(null));

    [Fact]
    public void APlainBooleanIsRead()
    {
        Assert.True(PortalAnimations.TryRead(VariantValue.Bool(false), out bool enabled));
        Assert.False(enabled);
    }

    [Fact]
    public void ANestedVariantIsUnwrapped()
    {
        // Read returns a variant whose contents are themselves a variant; ReadOne and
        // SettingChanged can hand the value over unwrapped. Both shapes have to work.
        VariantValue doubled = VariantValue.Variant(VariantValue.Variant(VariantValue.Bool(false)));

        Assert.True(PortalAnimations.TryRead(doubled, out bool enabled));
        Assert.False(enabled);
    }

    [Fact]
    public void SomeOtherShapeIsNotReadAsPermissionToAnimate()
    {
        // A portal answering with a number where the specification says boolean has told
        // Altim nothing. The caller turns a false here into Unknown, never into Full.
        Assert.False(PortalAnimations.TryRead(VariantValue.UInt32(1), out bool enabled));
        Assert.False(enabled);
    }
}

/// <summary>The bus addresses and match rules, which are easy to get subtly wrong and silent when they are.</summary>
public sealed class DBusServicesTests
{
    [Fact]
    public void TheSleepRuleNarrowsToLogindsSignal()
    {
        MatchRule rule = DBusServices.PrepareForSleepRule();

        Assert.Equal(MessageType.Signal, rule.Type);
        Assert.Equal("org.freedesktop.login1", rule.Sender);
        Assert.Equal("/org/freedesktop/login1", rule.Path);
        Assert.Equal("org.freedesktop.login1.Manager", rule.Interface);
        Assert.Equal("PrepareForSleep", rule.Member);
    }

    [Fact]
    public void TheAppearanceRuleFiltersOnTheNamespace()
    {
        MatchRule rule = DBusServices.AppearanceChangedRule();

        Assert.Equal(MessageType.Signal, rule.Type);
        Assert.Equal("org.freedesktop.portal.Desktop", rule.Sender);
        Assert.Equal("/org/freedesktop/portal/desktop", rule.Path);
        Assert.Equal("org.freedesktop.portal.Settings", rule.Interface);
        Assert.Equal("SettingChanged", rule.Member);

        // Without this the process is woken for every portal setting any application changes.
        Assert.Equal("org.freedesktop.appearance", rule.Arg0);
    }

    [Fact]
    public void TheAnimationsRuleFiltersOnGnomesOwnNamespace()
    {
        MatchRule rule = DBusServices.AnimationsChangedRule();

        Assert.Equal(MessageType.Signal, rule.Type);
        Assert.Equal("org.freedesktop.portal.Desktop", rule.Sender);
        Assert.Equal("/org/freedesktop/portal/desktop", rule.Path);
        Assert.Equal("org.freedesktop.portal.Settings", rule.Interface);
        Assert.Equal("SettingChanged", rule.Member);

        // There is no freedesktop key for motion, so this one is GNOME's by name. Pointing
        // it at org.freedesktop.appearance would subscribe successfully and then never carry
        // an animation change at all.
        Assert.Equal("org.gnome.desktop.interface", rule.Arg0);
        Assert.Equal("enable-animations", DBusServices.EnableAnimationsKey);
    }
}

/// <summary>The arguments handed to <c>Notify</c>, and how a tag becomes a replacement id.</summary>
public sealed class FreedesktopNotificationTests
{
    [Fact]
    public void TheRequestCarriesTheTitleAndBodyAndNothingElse()
    {
        var notification = new Core.Models.Notification(
            "Session usage reached 80%", "Resets in 42 minutes", "claude", "claude:five_hour");

        FreedesktopNotificationRequest request =
            FreedesktopNotificationRequest.From(notification, "Altim", "altim");

        Assert.Equal("Altim", request.AppName);
        Assert.Equal("Session usage reached 80%", request.Summary);
        Assert.Equal("Resets in 42 minutes", request.Body);
        Assert.Equal("altim", request.DesktopEntry);
        Assert.Equal(0u, request.ReplacesId);
        Assert.Equal(FreedesktopNotificationRequest.DefaultExpireTimeout, request.ExpireTimeout);
    }

    [Fact]
    public void AKnownTagReplacesRatherThanStacks()
    {
        var notification = new Core.Models.Notification("Title", "Body", "claude", "claude:five_hour");

        FreedesktopNotificationRequest request =
            FreedesktopNotificationRequest.From(notification, "Altim", "altim", appIcon: null, replacesId: 17);

        Assert.Equal(17u, request.ReplacesId);
    }

    [Fact]
    public void TheSignatureIsTheOneTheDaemonDeclares() =>
        Assert.Equal("susssasa{sv}i", FreedesktopNotificationRequest.NotifySignature);

    [Fact]
    public void AnUntaggedNotificationNeverReplacesAnything()
    {
        var map = new NotificationReplacementMap();
        map.Remember(null, 5);

        Assert.Equal(0u, map.Resolve(null));
    }

    [Fact]
    public void ATaggedNotificationRemembersItsId()
    {
        var map = new NotificationReplacementMap();
        map.Remember("claude:five_hour", 9);

        Assert.Equal(9u, map.Resolve("claude:five_hour"));
        Assert.Equal(0u, map.Resolve("codex:weekly"));
    }

    [Fact]
    public void AZeroIdIsNotRecorded()
    {
        // Zero is the "post a new one" sentinel, so remembering it would be indistinguishable
        // from never having posted.
        var map = new NotificationReplacementMap();
        map.Remember("tag", 4);
        map.Remember("tag", 0);

        Assert.Equal(4u, map.Resolve("tag"));
    }
}
