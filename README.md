<div align="center">
  <img src="assets/brand/altim-logo-source.png" alt="Altim" width="160">

  **AI usage, at a glance.**

  [altim.dev](https://altim.dev)
</div>

---

Altim is a lightweight, local-first desktop monitor for AI coding agent usage. It sits in
your system tray (Windows), menu bar (macOS) or status area (Linux) and answers one
question: **what are my AI coding tools consuming right now?**

> **Status: early development.** The architecture and provider data sources are being
> established first — see [ARCHITECTURE.md](ARCHITECTURE.md) and [PROVIDERS.md](PROVIDERS.md).

## What it shows

- Session and weekly usage against each provider's limits
- Time until the next reset
- Token counts where a provider exposes them
- Whether an agent is working right now, and when it was last active
- Local usage history over 24 hours, 7 days and 30 days

## Providers

| Provider | Status |
|---|---|
| Claude / Claude Code | in development |
| OpenAI Codex | in development |
| Gemini CLI, Cursor, GitHub Copilot | planned |

Altim never invents numbers. Every metric is traced to a documented local source in
[PROVIDERS.md](PROVIDERS.md), and anything that cannot be read reliably is shown as
unavailable rather than estimated.

## Platforms

Windows, macOS and Linux, from one Avalonia UI codebase with native platform services
for tray/menu-bar behaviour, notifications, autostart and power events.

## Privacy

Local-first by default. Altim reads usage metadata only, stores it in a local SQLite
database, and never transmits prompts, source code, repository contents, conversation
contents, commands or filenames anywhere. See [PRIVACY.md](PRIVACY.md).

## Building

Requirements and build instructions land with the first implementation milestone.

## License

MIT — see [LICENSE](LICENSE).
