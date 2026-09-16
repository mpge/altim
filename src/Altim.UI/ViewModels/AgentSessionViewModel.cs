using Altim.Core.Models;
using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One agent session: a row on a provider page, and a row in Overview's activity panel.
/// </summary>
/// <remarks>
/// <para>
/// Only the provider, the model name, the timings and the token counts are shown. Session
/// identifiers, project names, file paths and commands are not: PRIVACY.md says agent activity
/// is shown live and never written down, and the providers are built so that the rest of it
/// cannot be obtained at all - process arguments are never read and the parsers reject any
/// string shaped like a path.
/// </para>
/// <para>
/// That is why <see cref="ChipText"/> is the model identifier and nothing else. The reference
/// puts the file being edited and the command being run in that chip; Altim cannot know either,
/// and the model a session is running is the one safe descriptor a provider does expose.
/// </para>
/// </remarks>
public sealed class AgentSessionViewModel : ObservableObject
{
    /// <summary>The label used when a session does not name its model.</summary>
    public const string UnnamedSession = "Agent session";

    /// <summary>Initializes a session row.</summary>
    /// <param name="session">The session as the provider reported it.</param>
    /// <param name="providerName">The provider that reported it, as shown.</param>
    /// <param name="timeProvider">The clock local and relative times are read against.</param>
    public AgentSessionViewModel(AgentSession session, string providerName, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Id = session.Id;
        ProviderName = providerName;
        IsAnthropic = ProviderIdentity.IsAnthropic(session.ProviderId);
        IsOpenAI = ProviderIdentity.IsOpenAI(session.ProviderId);
        LastActivityAt = session.LastActivityAt ?? session.StartedAt;
        Title = string.IsNullOrWhiteSpace(session.ModelId) ? UnnamedSession : session.ModelId;
        ChipText = string.IsNullOrWhiteSpace(session.ModelId) ? null : session.ModelId;
        IsActive = session.IsActive;
        StatusText = session.IsActive ? "Active" : "Idle";
        StartedText = UsageFormat.ClockTime(session.StartedAt) is { } started
            ? string.Concat("Started ", started)
            : null;
        LastActivityText = UsageFormat.ClockTime(session.LastActivityAt) is { } active
            ? string.Concat("Last active ", active)
            : null;
        TokensText = UsageFormat.Tokens(session.Tokens);
        RelativeText = UsageFormat.RelativeTime(session.LastActivityAt ?? session.StartedAt, timeProvider);

        // State, how long it has been going and what it has spent: the three things a
        // provider reports about a session that a person can act on.
        DetailText = UsageFormat.Join(
            StatusText,
            UsageFormat.Elapsed(session.StartedAt, timeProvider),
            TokensText) ?? StatusText;
    }

    /// <summary>The provider's own identifier for the session. Never shown.</summary>
    public string Id { get; }

    /// <summary>The provider that reported the session, as shown.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// When the session was last active, falling back to when it started. Not shown: it is
    /// what orders a list of sessions drawn from several providers at once.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; }

    /// <summary>Whether the row's mark wears the Anthropic accent.</summary>
    public bool IsAnthropic { get; }

    /// <summary>Whether the row's mark wears the OpenAI accent.</summary>
    public bool IsOpenAI { get; }

    /// <summary>The model the session runs, or a plain fallback.</summary>
    public string Title { get; }

    /// <summary>The model identifier, or null when the provider does not report one.</summary>
    public string? ChipText { get; }

    /// <summary>Whether a chip exists to show.</summary>
    public bool HasChip => ChipText is not null;

    /// <summary>Whether the session is working right now.</summary>
    public bool IsActive { get; }

    /// <summary>The single word the session's state appears as.</summary>
    public string StatusText { get; }

    /// <summary>The line under the provider's name: state, duration and tokens.</summary>
    public string DetailText { get; }

    /// <summary>How long ago the session was last active, or null when not reported.</summary>
    public string? RelativeText { get; }

    /// <summary>Whether a relative time exists to show.</summary>
    public bool HasRelativeText => RelativeText is not null;

    /// <summary>When the session started, or null when it is not reported.</summary>
    public string? StartedText { get; }

    /// <summary>When the session was last active, or null when it is not reported.</summary>
    public string? LastActivityText { get; }

    /// <summary>The session's token count, or null when it is not reported.</summary>
    public string? TokensText { get; }

    /// <summary>Whether a token count exists to show.</summary>
    public bool HasTokens => TokensText is not null;
}
