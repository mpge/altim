using Altim.Core.Models;
using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One agent session, as a row on a provider page.
/// </summary>
/// <remarks>
/// Only the model name, the timings and the token counts are shown. Session identifiers, project
/// names and paths are not: PRIVACY.md says agent activity is shown live and never written down,
/// and there is nothing a user does with a session's identifier on screen.
/// </remarks>
public sealed class AgentSessionViewModel : ObservableObject
{
    /// <summary>The label used when a session does not name its model.</summary>
    public const string UnnamedSession = "Agent session";

    /// <summary>Initializes a session row.</summary>
    /// <param name="session">The session as the provider reported it.</param>
    /// <param name="timeProvider">The clock local times are read against.</param>
    public AgentSessionViewModel(AgentSession session, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Id = session.Id;
        Title = string.IsNullOrWhiteSpace(session.ModelId) ? UnnamedSession : session.ModelId;
        IsActive = session.IsActive;
        StatusText = session.IsActive ? "Active" : "Idle";
        StartedText = UsageFormat.ClockTime(session.StartedAt) is { } started
            ? string.Concat("Started ", started)
            : null;
        LastActivityText = UsageFormat.ClockTime(session.LastActivityAt) is { } active
            ? string.Concat("Last active ", active)
            : null;
        TokensText = UsageFormat.Tokens(session.Tokens);
    }

    /// <summary>The provider's own identifier for the session. Never shown.</summary>
    public string Id { get; }

    /// <summary>The model the session runs, or a plain fallback.</summary>
    public string Title { get; }

    /// <summary>Whether the session is working right now.</summary>
    public bool IsActive { get; }

    /// <summary>The single word the session's state appears as.</summary>
    public string StatusText { get; }

    /// <summary>When the session started, or null when it is not reported.</summary>
    public string? StartedText { get; }

    /// <summary>When the session was last active, or null when it is not reported.</summary>
    public string? LastActivityText { get; }

    /// <summary>The session's token count, or null when it is not reported.</summary>
    public string? TokensText { get; }

    /// <summary>Whether a token count exists to show.</summary>
    public bool HasTokens => TokensText is not null;
}
