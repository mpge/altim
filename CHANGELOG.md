# Changelog

Notable changes, newest first. Versions follow [SemVer](https://semver.org); Altim is at `0.x`,
so anything may still move.

Each release's page carries the downloads, their checksums, and a plain statement of what is and
is not signed.

## 0.1.0 — unreleased

The first published release. Everything below already existed and was tested; what is new is that
you can install it.

### What it does

- Sits in the tray (Windows), menu bar (macOS) or status area (Linux) and shows session and weekly
  usage against each provider's limits, time to the next reset, token counts where a provider
  exposes them, and whether an agent is working right now.
- Keeps local history: 24 hours, 7 days, 30 days, and a year as a calendar map with one square per
  day per provider.
- Reads Claude Code, OpenAI Codex and Google Gemini CLI. Every figure traces to a documented local
  source in `PROVIDERS.md`.
- **Never invents a number.** What cannot be read reliably is shown as unavailable, and that is
  kept distinct from zero. Gemini CLI is the plain case: it holds real remaining quota only in a
  running process's memory, so Altim reports its tokens and no percentage at all.
- Local-first. Prompts, source, repository contents, conversation contents, commands and filenames
  never leave the machine. One request of Altim's own, once a day, asks GitHub whether a newer
  version exists, and it is a switch in Settings. `PRIVACY.md` is the full account.

### Installing

Five targets: Windows x64 and arm64 (installer and portable), macOS Apple silicon and Intel
(`.dmg`), and Linux x64 (AppImage, `.deb`, `.rpm`). `docs/INSTALL.md` has the per-platform steps.

Both Windows installers update themselves. Every other way of installing tells you a newer version
exists and leaves the upgrade to you.

### Known limits

- **Nothing is signed with a paid certificate.** Windows SmartScreen warns that the publisher is
  unknown; macOS Gatekeeper refuses a downloaded copy until you right-click and choose **Open**.
  Both are expected, both are documented, and every release carries `SHA256SUMS.txt`.
- **The macOS and Linux builds are unverified.** They build and their tests pass on every platform,
  and Altim runs daily on Windows, but nobody has yet run it on a Mac or a Linux desktop. The tray
  icon, notifications and start-at-login are untested there.
- **Windows notifications need a complete Windows App Runtime.** Where it is absent or partial,
  Altim runs normally and shows no toasts.
- Cursor and GitHub Copilot are planned, not present.
