using Altim.UI.Views;
using Avalonia.Controls;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Which requests to close the panel window are refused, and which one must never be.
/// </summary>
/// <remarks>
/// <para>
/// The panel is hidden and never closed for the whole life of the process, because a
/// tray-only application with no live top level cannot reach screen geometry. Anything that
/// asks the window to close therefore gets a hide instead — alt+F4 on a focused panel, for
/// instance.
/// </para>
/// <para>
/// <b>One caller must not be refused.</b> Avalonia asks every window to close while it is
/// answering the operating system's session-end query, and a cancelled close is the answer
/// it gives back: refusing there tells Windows that Altim is blocking sign-out. Reproduced
/// on Windows 11 26200 by sending <c>WM_QUERYENDSESSION</c> to the running process — the
/// query returned 0, which is a veto, and the shell's "this app is preventing you from
/// signing out" screen is what a user sees for it.
/// </para>
/// <para>
/// So the reason the window is being closed for decides the answer, and it is a pure
/// function over that reason — no window, no screen and no session in it.
/// </para>
/// </remarks>
public sealed class PopupCloseRuleTests
{
    /// <summary>
    /// The two reasons that mean the process is going away. Refusing either is what vetoed
    /// the session-end query.
    /// </summary>
    /// <param name="reason">The reason Avalonia reported.</param>
    [Theory]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    public void AShutdownClosesTheWindowRatherThanHidingIt(WindowCloseReason reason) =>
        Assert.Equal(PopupCloseAction.Close, PopupCloseRule.Decide(reason));

    /// <summary>
    /// Everything else is a dismissal of the panel, not the end of the process, and the
    /// window survives it.
    /// </summary>
    /// <param name="reason">The reason Avalonia reported.</param>
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing)]
    [InlineData(WindowCloseReason.OwnerWindowClosing)]
    [InlineData(WindowCloseReason.Undefined)]
    public void EveryOtherReasonHidesThePanelAndKeepsTheWindow(WindowCloseReason reason) =>
        Assert.Equal(PopupCloseAction.Hide, PopupCloseRule.Decide(reason));

    /// <summary>
    /// A reason this build has never heard of is not treated as a shutdown. Hiding is the
    /// recoverable answer: the panel goes away and the window is still there, whereas
    /// closing it on a guess costs the process its only top level.
    /// </summary>
    [Fact]
    public void AnUnknownReasonHides() =>
        Assert.Equal(PopupCloseAction.Hide, PopupCloseRule.Decide((WindowCloseReason)99));

    /// <summary>Every reason has a phrase, because the log line is how this is debugged.</summary>
    /// <param name="reason">The reason Avalonia reported.</param>
    [Theory]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.WindowClosing)]
    [InlineData(WindowCloseReason.OwnerWindowClosing)]
    [InlineData(WindowCloseReason.Undefined)]
    [InlineData((WindowCloseReason)99)]
    public void EveryReasonReadsAsASentence(WindowCloseReason reason) =>
        Assert.False(string.IsNullOrWhiteSpace(PopupCloseRule.Describe(reason)));
}
