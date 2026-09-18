using Altim.Core.Models;
using Altim.UI.Accessibility;

namespace Altim.UI.Tests;

/// <summary>
/// Holds <see cref="Motion"/> at one preference for the length of a test and puts it back
/// afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The preference is ambient, the way the theme variant is: one value the whole interface
/// agrees on, pushed in from outside the visual tree. A test that set it and walked away
/// would decide what the next test renders, so every test that needs a particular answer
/// takes one of these and every test that does not gets the default, which is
/// <see cref="MotionPreference.Unknown"/> and animates nothing.
/// </para>
/// <para>
/// Restoring rather than clearing, because nesting one scope inside another is a reasonable
/// thing for a test to want and a scope that reset to unknown would break it.
/// </para>
/// </remarks>
internal sealed class MotionScope : IDisposable
{
    private readonly MotionPreference _previous;

    private MotionScope(MotionPreference previous) => _previous = previous;

    /// <summary>Sets the preference and returns the scope that restores it.</summary>
    /// <param name="preference">What the platform is pretending to report.</param>
    public static MotionScope Of(MotionPreference preference)
    {
        var scope = new MotionScope(Motion.Preference);
        Motion.Set(preference);
        return scope;
    }

    /// <summary>Puts the preference back to whatever it was.</summary>
    public void Dispose() => Motion.Set(_previous);
}
