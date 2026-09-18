using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Whether a provider belongs on the surfaces that report usage. Pure: no clock, no state,
/// same input gives the same output.
/// </summary>
/// <remarks>
/// <para>
/// Altim registers every provider it knows how to read, whether or not the machine has it.
/// A machine with one agent installed therefore had a panel listing three, two of them
/// saying nothing, which is clutter rather than information: a reader counting the rows
/// counts tools they do not have.
/// </para>
/// <para>
/// <strong>Only <see cref="ProviderStatus.NotDetected"/> hides a provider.</strong> That
/// status is the settled answer that the provider is not installed, or keeps its data
/// somewhere that does not exist here, and a settled absence is the one thing there is
/// nothing to report about.
/// </para>
/// <para>
/// <strong><see cref="ProviderStatus.Error"/> is never hidden</strong>, and that is the rule
/// this type exists to protect. An error means the provider is there and the last read of it
/// failed. Hiding it would turn a fault into a silence, which is the opposite of what a
/// monitor is for: the user would see a shorter list and no reason to think anything was
/// wrong. Everything else - detected, active, idle - is a provider with something to say.
/// </para>
/// <para>
/// <see cref="ProviderStatus.Unknown"/> is not hidden either, for a different reason. It
/// means the provider has not been probed yet, so hiding it would be a guess, and a guess
/// that resolves a moment later: the row would appear under the reader's pointer as the
/// first reading landed and push everything below it down. A provider that has not answered
/// yet is shown as it is shown at any other time it has no figures - its name, its mark, and
/// the sentence that says nothing was reported - and settles into place or out of the list
/// when the reading arrives.
/// </para>
/// <para>
/// This decides what the usage surfaces list: the tray panel's provider rows and the rings,
/// legend and reset lines built from them, and Overview's cards. It deliberately does not
/// decide three other things. <strong>The status line</strong> is aggregated over every
/// provider, because one sentence naming the absent one is what explains a short list, and
/// because it is what says "No providers detected" when the list is empty.
/// <strong>History</strong> is drawn for every provider, because a provider uninstalled
/// today still used something last week and hiding the line would erase the past rather than
/// tidy the present. <strong>Settings</strong> lists every provider with its own status
/// sentence, because that page is the inventory: it is where a reader goes to find out why
/// something is missing.
/// </para>
/// </remarks>
public static class ProviderVisibility
{
    /// <summary>Whether a provider in this state belongs on a surface that reports usage.</summary>
    /// <param name="status">The status the provider reported.</param>
    /// <returns>
    /// <see langword="false"/> only for <see cref="ProviderStatus.NotDetected"/>. An
    /// unrecognised value is shown, because the safe answer to "we do not know what this
    /// means" is to show it.
    /// </returns>
    public static bool IsShown(ProviderStatus status) => status != ProviderStatus.NotDetected;
}
