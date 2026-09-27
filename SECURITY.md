# Security

## Reporting something

**Use [Report a vulnerability](https://github.com/mpge/altim/security/advisories/new)**, which is
private between you and the maintainer. Please do not open a public issue for a security problem:
an issue is visible to everyone the moment you press the button, including to people who would use
it before there is anything to upgrade to.

Tell me what you did, what happened, and what you expected. A proof of concept helps and is never
required — a clear description of the flaw is worth more than a working exploit.

Altim is one unpaid maintainer, so there is no guaranteed response time and it would be dishonest
to print one. What I will do is acknowledge a report when I see it, tell you whether I agree it is
a problem, and say what I intend to do about it. If I decide not to fix something I will say that
too, and why, rather than leaving the report unanswered.

You are welcome to disclose publicly once a fix has shipped, or if I have gone quiet for 90 days.
Credit goes in the release notes unless you would rather it did not.

## What is worth reporting

Altim is a local desktop application. It has no server, no account and no telemetry, so the
interesting surface is what it reads, what it stores and the little it sends.

Especially worth reporting:

- **Anything that gets transcript content, prompts, filenames, repository contents or credentials
  off the machine.** Altim reads AI coding-agent transcripts, and the whole product position is
  that what it reads never leaves. A path by which it does is the most serious bug Altim can have.
- **Anything that reads outside the directories Altim is meant to read.** `PRIVACY.md` lists them.
- **Credential exposure.** Altim is designed never to read vendor credential files. Somewhere it
  does, or logs something that came from one, is a bug.
- **Code execution from data.** A malformed transcript, a hostile filename or a crafted database
  making Altim run something.
- **The update path.** Anything that causes Altim to fetch, trust or install something other than a
  genuine release from `github.com/mpge/altim`.
- **Local escalation.** File permissions on the database or the log, a writable install directory,
  a service or start-at-login entry that can be hijacked.

## What is already known, and is not a vulnerability

These are stated in the open because they are design limits, not discoveries:

- **Releases are not signed with a paid certificate.** Windows SmartScreen warns and macOS
  Gatekeeper refuses a downloaded copy until you right-click and Open. Verify a download against
  `SHA256SUMS.txt` on the release. See `docs/INSTALL.md`.
- **The database is not encrypted.** It sits in your user profile with the permissions your
  operating system gives it, and it holds usage figures — counts, times, model names — not
  transcript text. Anyone who can already read your home directory can read it, and can equally
  read the transcripts it was derived from.
- **Live Codex quota asks the installed Codex CLI**, over local IPC, and that CLI contacts OpenAI
  with credentials it already holds. Altim sees only the numbers, and the whole thing can be turned
  off in Settings. `PRIVACY.md` explains it.
- **Altim trusts the transcript files it reads** to the extent of parsing them. They are written by
  tools already running as you.

## Supported versions

The most recent release. Altim is at `0.x`: fixes go into the next release rather than into
patches of older ones.
