using Altim.Core.Accessibility;
using Altim.Core.Models;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The decision an unknown forces, taken once and in one place.
/// </summary>
/// <remarks>
/// A surface either animates or it does not, so the three states the operating system can be
/// in have to become two somewhere. That somewhere is <see cref="MotionPolicy"/> and nothing
/// else, which is why this is the only test in the repository that asserts what an unknown
/// motion preference does.
/// </remarks>
public sealed class MotionPolicyTests
{
    /// <summary>A machine that asked for reduced motion gets none.</summary>
    [Fact]
    public void ReducedDoesNotAnimate() =>
        Assert.False(MotionPolicy.AllowsAnimation(MotionPreference.Reduced));

    /// <summary>A machine that says it wants motion is the only case that gets it.</summary>
    [Fact]
    public void FullAnimates() =>
        Assert.True(MotionPolicy.AllowsAnimation(MotionPreference.Full));

    /// <summary>
    /// A machine Altim could not ask gets none either. Animating for somebody who asked for
    /// stillness and could not be heard is a mistake that can make them ill; withholding a
    /// 180ms ease from somebody who never asked is one that cannot.
    /// </summary>
    [Fact]
    public void UnknownDoesNotAnimate() =>
        Assert.False(MotionPolicy.AllowsAnimation(MotionPreference.Unknown));

    /// <summary>
    /// A value nobody set claims nothing. A zero that meant <see cref="MotionPreference.Full"/>
    /// would turn every uninitialised field, every default-constructed record and every
    /// forgotten assignment into an assertion that this user wants motion.
    /// </summary>
    [Fact]
    public void TheDefaultValueIsUnknown() =>
        Assert.Equal(MotionPreference.Unknown, default(MotionPreference));

    /// <summary>
    /// A value the enum does not define is not read as permission. Nothing produces one
    /// today; a later platform mapping that returned a number out of range would.
    /// </summary>
    [Fact]
    public void AnUndefinedValueDoesNotAnimate() =>
        Assert.False(MotionPolicy.AllowsAnimation((MotionPreference)42));

    /// <summary>
    /// The mapping all three platforms share: a platform that said animations are wanted,
    /// one that said they are not, and one that did not answer.
    /// </summary>
    /// <remarks>
    /// Windows' <c>SPI_GETCLIENTAREAANIMATION</c> and Linux's <c>enable-animations</c> feed
    /// this directly; macOS asks the opposite question and inverts its answer first. It is
    /// asserted here because it is the only part of those three files that can be run on a
    /// machine that is not the platform in question.
    /// </remarks>
    /// <param name="animationsEnabled">What the platform reported, or null for no answer.</param>
    /// <param name="expected">The preference that should come out.</param>
    [Theory]
    [InlineData(true, MotionPreference.Full)]
    [InlineData(false, MotionPreference.Reduced)]
    [InlineData(null, MotionPreference.Unknown)]
    public void TheSharedPlatformMappingKeepsAMissingAnswerMissing(
        bool? animationsEnabled, MotionPreference expected) =>
        Assert.Equal(expected, MotionPolicy.FromAnimationsEnabled(animationsEnabled));
}
