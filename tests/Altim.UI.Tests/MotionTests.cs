using System.Text.RegularExpressions;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Accessibility;
using Altim.UI.Controls;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The ambient reduce-motion value the interface reads.
/// </summary>
/// <remarks>
/// In the motion collection: <c>Motion.Preference</c> is one ambient value for the whole
/// interface, and xUnit runs test classes in parallel, so a class that sets it decides what
/// a class running beside it sees. Restoring it afterwards is not enough. This failed on a
/// macOS runner and passed on Windows, which is scheduling choosing the outcome.
/// </remarks>
[Collection("Motion")]
public sealed class MotionValueTests
{
    /// <summary>
    /// Nothing animates until a platform has said motion is wanted. A composition root that
    /// never reported would otherwise leave the interface moving for somebody who asked it
    /// not to, which is the failure this default exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnknownPreferenceDoesNotAllowAnimation()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Unknown);

        Assert.Equal(MotionPreference.Unknown, Motion.Preference);
        Assert.False(Motion.AnimationsAllowed);
    }

    /// <summary>Only an explicit "motion is wanted" allows it.</summary>
    [Fact]
    public void OnlyFullMotionAllowsAnimation()
    {
        using (MotionScope.Of(MotionPreference.Full))
        {
            Assert.True(Motion.AnimationsAllowed);
        }

        using (MotionScope.Of(MotionPreference.Reduced))
        {
            Assert.False(Motion.AnimationsAllowed);
        }
    }

    /// <summary>
    /// A change is announced once, and a platform re-reporting the same answer announces
    /// nothing. Surfaces cut whatever they are animating when this fires, so a repeat would
    /// be a visible stutter on every unrelated system setting a person changes.
    /// </summary>
    [Fact]
    public void ChangedFiresOnAMoveAndNotOnARepeat()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Unknown);

        int raised = 0;
        void Count(object? sender, EventArgs e) => raised++;

        Motion.Changed += Count;
        try
        {
            Motion.Set(MotionPreference.Reduced);
            Motion.Set(MotionPreference.Reduced);
            Motion.Set(MotionPreference.Full);

            Assert.Equal(2, raised);
        }
        finally
        {
            Motion.Changed -= Count;
        }
    }
}

/// <summary>
/// Whether the interface actually holds still, rather than whether a flag says it should.
/// </summary>
/// <remarks>
/// <para>
/// A transition attached to a control is the mechanism every animation in this stack uses,
/// Altim's own and the base theme's alike, so the question "is anything on this window
/// animating" has an answer the object graph can give: does any visual carry
/// <see cref="Animatable.Transitions"/>. These tests ask it of the real dashboard, on every
/// page, after driving the one control that animates.
/// </para>
/// <para>
/// The pixel proof that a suppressed meter really arrives at the new level in the frame it
/// is told about it is in <see cref="MeterPixelTests"/>. This suite is the sweep: it is what
/// catches an animation Altim did not write and <c>docs/DESIGN.md</c> does not mention.
/// </para>
/// </remarks>
/// <remarks>
/// In the motion collection: <c>Motion.Preference</c> is one ambient value for the whole
/// interface, and xUnit runs test classes in parallel, so a class that sets it decides what
/// a class running beside it sees. Restoring it afterwards is not enough. This failed on a
/// macOS runner and passed on Windows, which is scheduling choosing the outcome.
/// </remarks>
[Collection("Motion")]
public sealed class MotionSuppressionTests
{
    /// <summary>
    /// Nothing anywhere in the dashboard animates while motion is reduced, on any page.
    /// </summary>
    [AvaloniaFact]
    public async Task NoVisualInTheDashboardCarriesATransitionUnderReducedMotion()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Reduced);
        using DashboardViewModel dashboard = Dashboard();
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard) { Width = 1000d, Height = 720d };
        List<string> animated = [];
        int metersDriven = 0;

        Surface.Render(window, w =>
        {
            for (int page = 0; page < dashboard.Sections.Count; page++)
            {
                dashboard.SelectedSection = dashboard.Sections[page];
                Dispatcher.UIThread.RunJobs();

                metersDriven += DriveMeters(w);
                Dispatcher.UIThread.RunJobs();

                animated.AddRange(Animating(w));
            }
        });

        // The sweep has to have had something to find, or it is a test that passes because
        // it looked at an empty window.
        Assert.True(metersDriven > 0, "No meter was found to drive, so nothing was proved.");
        Assert.True(
            animated.Count == 0,
            "These are animating while the machine asks for reduced motion: "
                + string.Join(", ", animated));
    }

    /// <summary>
    /// The same sweep finds the meter's transition when motion is allowed, which is what
    /// makes the assertion above mean something. Without this, a meter that had stopped
    /// animating altogether, or a walk that never reached one, would pass just as quietly.
    /// </summary>
    [AvaloniaFact]
    public async Task TheSameSweepFindsTheMetersTransitionWhenMotionIsAllowed()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Full);
        using DashboardViewModel dashboard = Dashboard();
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard) { Width = 1000d, Height = 720d };
        List<string> animated = [];

        Surface.Render(window, w =>
        {
            Assert.True(DriveMeters(w) > 0, "No meter was found to drive.");
            Dispatcher.UIThread.RunJobs();
            animated.AddRange(Animating(w));
        });

        Assert.Contains(nameof(Meter), animated);
    }

    /// <summary>
    /// The tray panel holds still too. It carries no meters - the panel reports its figures
    /// as text - so this is a sweep for anything the control themes might bring with them.
    /// </summary>
    [AvaloniaFact]
    public void NoVisualInTheTrayPanelCarriesATransition()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Reduced);
        using var panel = new PopupViewModel(
            [new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude"))],
            new TestClock(Readings.Now),
            AltimSettings.Default);

        panel.Providers[0].Apply(Readings.Healthy("claude"));

        List<string> animated = [];
        Surface.Show(new PopupView { DataContext = panel }, w => animated.AddRange(Animating(w)), width: 320d);

        Assert.True(
            animated.Count == 0,
            "These are animating in the tray panel: " + string.Join(", ", animated));
    }

    /// <summary>
    /// Every meter on screen is given a second reported level, which is the only thing that
    /// makes one animate.
    /// </summary>
    /// <param name="root">The window to search.</param>
    /// <returns>How many meters were driven.</returns>
    private static int DriveMeters(Visual root)
    {
        int driven = 0;

        foreach (Meter meter in Surface.Visible<Meter>(root))
        {
            if (meter.Value is not { } value)
            {
                continue;
            }

            meter.Value = value >= 50d ? value - 20d : value + 20d;
            driven++;
        }

        return driven;
    }

    /// <summary>Every visual below a root that is carrying a transition, by type name.</summary>
    /// <param name="root">Where to look.</param>
    private static IEnumerable<string> Animating(Visual root)
    {
        foreach (Visual visual in root.GetVisualDescendants())
        {
            if (visual is Animatable { Transitions.Count: > 0 })
            {
                yield return visual.GetType().Name;
            }
        }
    }

    private static DashboardViewModel Dashboard()
    {
        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
        ];

        return new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new FakeStatusLineService(),
            new TestClock(Readings.Now));
    }
}

/// <summary>
/// The rule that keeps the next animation honest: anything in the interface that declares
/// one has to ask whether it is allowed to run it.
/// </summary>
/// <remarks>
/// <para>
/// The sweep above reads a window that has been built. This reads the source, because the
/// two catch different mistakes: a transition on a control that no test happens to render
/// reaches nobody's assertion and reaches every user.
/// </para>
/// <para>
/// It is deliberately a rule about <em>declaring</em> motion rather than a list of files.
/// A list would be satisfied by deleting an entry.
/// </para>
/// </remarks>
public sealed partial class MotionSourceShapeTests
{
    /// <summary>Anything that declares a transition or a key-framed animation.</summary>
    [GeneratedRegex(@"\bTransitions\b|\bDoubleTransition\b|\bBrushTransition\b|\bCrossFade\b|\bKeyFrame\b|<Animation\b|Style\.Animations")]
    private static partial Regex DeclaresMotion { get; }

    /// <summary>
    /// Every file in the interface or the composition root that declares motion also reads
    /// the platform preference. There is one such file today; the test is here for the
    /// second one.
    /// </summary>
    [Fact]
    public void EverySourceThatDeclaresMotionAsksWhetherItIsAllowed()
    {
        List<string> offences = [];
        int declaring = 0;

        foreach (string file in InterfaceSource())
        {
            string text = File.ReadAllText(file);
            if (!DeclaresMotion.IsMatch(text))
            {
                continue;
            }

            declaring++;

            if (!text.Contains("Motion.AnimationsAllowed", StringComparison.Ordinal))
            {
                offences.Add(Path.GetFileName(file));
            }
        }

        Assert.True(declaring > 0, "No source declares any motion, so this rule watched nothing.");
        Assert.True(
            offences.Count == 0,
            "These declare an animation without consulting the operating system's reduce"
                + " motion setting: "
                + string.Join(", ", offences));
    }

    /// <summary>
    /// Every subscription to the preference is given back in the same file.
    /// </summary>
    /// <remarks>
    /// <see cref="Motion.Changed"/> is static, so a control that subscribes and never
    /// unsubscribes is kept alive by it for the life of the process along with the window it
    /// was in. Nothing at runtime reports that, and no behavioural test can see it: a
    /// detached control reverts to the value the handler would have given it anyway, so a
    /// leaked subscription and a released one look identical from outside. It is caught here
    /// instead, in the source, which is the same bargain the <c>StaticResource</c> rule makes.
    /// </remarks>
    [Fact]
    public void EverySubscriptionToThePreferenceIsGivenBack()
    {
        List<string> offences = [];
        int subscribing = 0;

        foreach (string file in InterfaceSource())
        {
            string text = File.ReadAllText(file);
            int added = Occurrences(text, "Motion.Changed +=");
            int removed = Occurrences(text, "Motion.Changed -=");

            if (added == 0)
            {
                continue;
            }

            subscribing++;

            if (added != removed)
            {
                offences.Add($"{Path.GetFileName(file)}: {added} subscribed, {removed} released");
            }
        }

        Assert.True(subscribing > 0, "Nothing subscribes to the preference, so this rule watched nothing.");
        Assert.True(
            offences.Count == 0,
            "A static event holds on to whatever subscribes to it until it is released: "
                + string.Join(", ", offences));
    }

    /// <summary>
    /// Altim's own settings do not carry a motion preference. The operating system owns this
    /// answer; a second copy of it here could only ever disagree with the first, and the
    /// person would have no way of telling which one the interface was obeying.
    /// </summary>
    [Fact]
    public void TheSettingsPageDoesNotOfferAMotionSettingOfItsOwn()
    {
        foreach (string file in SettingsSource())
        {
            string text = File.ReadAllText(file);

            Assert.DoesNotContain("MotionPreference", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ReduceMotion", text, StringComparison.Ordinal);
        }
    }

    /// <summary>How many times one exact string appears.</summary>
    /// <param name="text">The source to search.</param>
    /// <param name="needle">The string to count.</param>
    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Every C# and XAML file in the UI project and the composition root.</summary>
    /// <remarks>
    /// Build output is skipped by name rather than by hoping it is absent: the generated
    /// XAML behind an <c>.axaml</c> lands in <c>obj</c> and would be scanned twice.
    /// </remarks>
    private static IEnumerable<string> InterfaceSource()
    {
        string separator = Path.DirectorySeparatorChar.ToString();

        foreach (string project in new[] { "Altim.UI", "Altim.App" })
        {
            string directory = Path.Combine(SourceDirectory(), project);

            foreach (string pattern in new[] { "*.cs", "*.axaml" })
            {
                foreach (string file in Directory
                             .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                             .Where(f => !f.Contains(separator + "bin" + separator, StringComparison.Ordinal)
                                         && !f.Contains(separator + "obj" + separator, StringComparison.Ordinal))
                             .Order())
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>The settings page, its view model and the stored settings record.</summary>
    private static IEnumerable<string> SettingsSource()
    {
        yield return Path.Combine(SourceDirectory(), "Altim.UI", "Views", "SettingsView.axaml");
        yield return Path.Combine(SourceDirectory(), "Altim.UI", "ViewModels", "SettingsViewModel.cs");
        yield return Path.Combine(SourceDirectory(), "Altim.UI", "ViewModels", "SettingsOptions.cs");
        yield return Path.Combine(SourceDirectory(), "Altim.Core", "Settings", "AltimSettings.cs");
    }

    /// <summary>
    /// Walks up from the test binary to <c>src</c>. The runner's working directory is not the
    /// repository and a relative path from it would break the moment the suite ran elsewhere.
    /// </summary>
    private static string SourceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Altim.UI")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not find src from " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
