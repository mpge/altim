namespace Altim.Providers.Codex;

/// <summary>
/// How the Codex CLI is authenticated, as the CLI itself reports it.
/// </summary>
/// <remarks>
/// Altim never opens <c>auth.json</c>. Authentication state is learned only from
/// <c>codex doctor --json</c>, which is redacted by design, and the answer is used for one
/// decision: whether the live quota call is worth making.
/// </remarks>
public enum CodexAuthMode
{
    /// <summary>
    /// The CLI did not say, or Altim could not tell. The live call is still attempted: an
    /// older CLI without a doctor report is not evidence of the wrong auth mode, and a call
    /// that is refused costs one process and is handled.
    /// </summary>
    Unknown = 0,

    /// <summary>ChatGPT subscription tokens. The live quota call is supported.</summary>
    ChatGpt = 1,

    /// <summary>
    /// An API key. The live quota call hard-errors under this mode, so it is skipped and
    /// the reader goes straight to the newest local snapshot.
    /// </summary>
    ApiKey = 2,

    /// <summary>Not signed in. Nothing to ask for.</summary>
    NotAuthenticated = 3,
}
