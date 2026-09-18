using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The rule deciding which providers a usage surface lists.
/// </summary>
/// <remarks>
/// Every status is named here rather than a representative few, because the whole of this
/// rule is which side of the line each one falls on, and a test that only checked the hidden
/// side would pass against an implementation that hid everything.
/// </remarks>
public sealed class ProviderVisibilityTests
{
    /// <summary>
    /// A provider that is simply not installed is the only thing hidden. It is a settled
    /// answer with nothing to report, and a row for it is a tool the reader does not have.
    /// </summary>
    [Fact]
    public void AProviderThatIsNotInstalledIsNotListed() =>
        Assert.False(ProviderVisibility.IsShown(ProviderStatus.NotDetected));

    /// <summary>
    /// <b>A provider whose last reading failed stays listed.</b> This is the assertion the
    /// rule exists for. An error means the provider is here and Altim could not read it,
    /// so hiding it would turn a fault into a silence: the panel would be one row shorter
    /// and nothing on it would say why. It is the opposite of what a monitor is for.
    /// </summary>
    [Fact]
    public void AProviderWhoseReadingFailedStaysListed() =>
        Assert.True(ProviderVisibility.IsShown(ProviderStatus.Error));

    /// <summary>
    /// A provider that has not been probed yet stays listed. Hiding it would be a guess,
    /// and a guess that resolves a moment later: the row would arrive under the reader's
    /// pointer as the first reading landed and push everything below it down.
    /// </summary>
    [Fact]
    public void AProviderThatHasNotBeenProbedYetStaysListed() =>
        Assert.True(ProviderVisibility.IsShown(ProviderStatus.Unknown));

    /// <summary>A provider that is here and readable is listed, whatever it is doing.</summary>
    /// <param name="status">The status to rank.</param>
    [Theory]
    [InlineData(ProviderStatus.Detected)]
    [InlineData(ProviderStatus.Active)]
    [InlineData(ProviderStatus.Idle)]
    public void AProviderThatIsHereIsListed(ProviderStatus status) =>
        Assert.True(ProviderVisibility.IsShown(status));

    /// <summary>
    /// A value the enum does not define is listed. The safe answer to "we do not know what
    /// this means" is to show it: showing one row too many is clutter, and hiding one is a
    /// provider disappearing with no explanation anywhere.
    /// </summary>
    [Fact]
    public void AStatusNobodyDefinedIsListed() =>
        Assert.True(ProviderVisibility.IsShown((ProviderStatus)99));

    /// <summary>
    /// Exactly one of the six defined statuses is hidden, which is what stops the rule
    /// quietly growing a second exception.
    /// </summary>
    [Fact]
    public void OnlyOneDefinedStatusIsHidden()
    {
        List<ProviderStatus> hidden = [];
        foreach (ProviderStatus status in Enum.GetValues<ProviderStatus>())
        {
            if (!ProviderVisibility.IsShown(status))
            {
                hidden.Add(status);
            }
        }

        Assert.Equal([ProviderStatus.NotDetected], hidden);
    }
}
