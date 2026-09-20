# Audit backlog, 2026-09-18

Two audits: what is untested, and what stands between Altim and a first release. Everything below
was demonstrated or read out of the code, not inferred. Where something is reported rather than
verified, it says so.

**State 2026-09-20.** Every job green on Windows, Ubuntu and macOS, including both macOS
bundle jobs. The release pipeline has now run: it produced a complete draft with twelve
artefacts across three platforms, so `vpk pack`, `build-linux.sh`, the AppImage without FUSE,
the artifact round trip and `gh release create` are all observed to work. The draft is
`v0.0.1-rc1`, unpublished, so no public tag exists.

**State when this was written, 2026-09-18:** 1,610 tests green on Windows, Ubuntu and macOS. `main` was red —
the `macOS app bundle` job fails on every push. Zero tags, zero releases, zero artefacts ever
produced by CI.

---

## Do first

These are cheap, and each closes a class of problem rather than one instance.

- [x] **Stop writing third-party exception messages into the log.** Seven `ex.Message` sites in
      `src/`; two reach `altim.log` today via `PlatformStack.cs:287` and `:415`, the other five are
      one wiring line away. `PRIVACY.md` says logs carry exception **types**, and `AltimLog`'s own
      doc calls this "a defect rather than a decision". A Linux `IOException` here names
      `$XDG_RUNTIME_DIR/<uid>/bus`. Replace with `ExceptionSummary.Describe(ex)`, then a
      source-shape test forbidding `\b(ex|error|e)\.Message\b` in `src/`. **~30 min.**

- [x] **Test `JsonValues.IsIdentifier` / `ReadIdentifier`.** Zero tests. It is the single chokepoint
      between vendor files full of prompts, source and command output and 20+ string fields, and the
      shape tests cannot help because `SessionId`, `ModelId`, `LimitName`, `Label` and `StatusDetail`
      are allowlisted. `CodexRateLimitParserTests` already proves vendor text reaches a visible
      label. Loosen the character set or the 96-char cap and 1,610 tests stay green. **~1h.**

- [x] **Unred `main`.** Make the ad-hoc seal non-fatal and loosen the signature assertion in
      `verify-bundle.sh` for that path, so a DMG is finally produced as an artefact. You cannot tag
      off a red branch, and nobody has ever run Altim on a Mac. **~30 min.**

- [x] **Cut a throwaway prerelease tag and read the run.** `release.yml` is 332 lines of
      never-executed code guarding all three platforms — `vpk pack` on a runner, `build-linux.sh`
      end to end, AppImage without FUSE, the artifact round trip, `gh release create`. One run
      teaches more than a day of reading. Push `v0.0.1-rc1`, let it fail, delete the draft.
      **~20 min plus the run.**

- [x] **First run shows the user nothing.** `StartMinimised` defaults to `true`, so a fresh install
      opens no window and Windows 11 hides a new tray icon in the overflow. A stranger runs the
      installer and nothing visible happens. Open the dashboard once when the database is new.
      **~1h.**

---

## Before a first release

### Correctness and privacy

- [ ] **`altim.dev` is a parked domain listed for sale** (192.64.119.173, Namecheap). It is shown in
      the app's own About panel (`SettingsViewModel.Website`), the README, and the `.deb`'s homepage
      and `maintainer:` address. Either buy it or change the four references. **~10 min once decided.**

- [ ] **`TRADEMARKS.md` contradicts the code.** It says the vendor marks are "filled in a single ink
      so they inherit the interface's foreground colour" and are "not restyled, recoloured". They are
      recoloured — `Shared.axaml:47,52,57` fills each with a per-provider accent that differs by
      theme. That document is the first thing a vendor's counsel would read and the discrepancy is
      trivially checkable. Keep the remedy paragraph verbatim; it is the strongest mitigation here.

- [ ] **No third-party notices anywhere.** Clear unmet obligations: SQLitePCLRaw (Apache-2.0 §4(a),
      §4(d)), Skia and ANGLE (BSD-3 §2), FreeType's FTL credit inside `libSkiaSharp`, Inter's OFL
      text. `/usr/share/doc/altim/copyright` currently carries Altim's 21-line MIT notice while the
      package contains the whole .NET runtime — a Debian Policy §12.5 violation. Matters most on
      Windows, where AOT statically links everything into one binary and a recipient has no way to
      discover what is inside. Generate with `nuget-license`, hand-add the four, wire into all three
      packaging paths. **~1 day.**

- [ ] **Network permission silently inverts when the database will not open.** `AllowNetworkCalls`
      defaults `true`, and the memory fallback is `AltimSettings.Default`, so a user who switched it
      **off** gets it **on** with no log line. A composition decision to state or change.

### Packaging

- [x] **macOS bundle shape.** The failure is at **sign** time, not verify — `codesign` classifies
      `Contents/MacOS/` as a nested-code location when it seals the bundle, so every file there must
      already be signable Mach-O and a managed `.dll` never can be. No verification flag helps; three
      attempts proved that. Fix: `PublishSingleFile=true` so only the host and 18 dylibs remain, and
      move `assets/` to `Contents/Resources/` (`MacOSTrayAssets.DiscoverAssetDirectory` already
      prefers it). **Unverified from here:** that `lipo` of two single-file hosts yields a working
      universal binary — .NET embeds the payload inside the Mach-O specifically so `codesign` works,
      which should make it safe, but nobody has observed it. Consider dropping "universal" and
      shipping per-architecture DMGs, which deletes the whole `lipo` layer.

- [ ] **No update mechanism on any platform.** Velopack is referenced for exactly one call —
      `VelopackApp.Build().Run()` for installer hooks. No `UpdateManager`, no feed URL, no
      `HttpClient` anywhere in `src/`. Ship 0.1.0 and every copy stays on 0.1.0 forever, including
      through the known-broken notification path. Windows is cheap: the feed already exists.
      **~half day, Windows only. macOS and Linux stay manual, which is normal.**

- [ ] **`Microsoft.WindowsAppSDK` is proprietary, pulls ~42MB, and the feature does not work.**
      Referenced for one API. `packaging/README.md` already states Windows notifications "do not work
      at all, on any machine". Its EULA requires downstream terms protective of Microsoft plus an
      indemnity, while `LICENSE` grants recipients unrestricted dealing — the only genuine licence
      incompatibility in the tree. Dropping it removes that, cuts the installer by roughly two
      thirds, and costs a broken feature. **~half day. A decision, not access.**

- [x] **Correct two docs that overstate CI.** `packaging/README.md` and `README.md` both say the
      macOS bundle is assembled and read back on every push. It has failed all 11 runs;
      `verify-bundle.sh` has never executed. `AGENTS.md` says docs are canonical, so this is the
      wrong kind of wrong.

- [ ] **README has no way for a stranger to install it.** No Download or Install section, no
      supported-OS floor, no note about the tray overflow, SmartScreen or Gatekeeper.

### Tests that would catch real bugs

- [ ] **`UsageTapePixelTests`** — every other drawn control has one. `UsageTapeSeries`'s own doc says
      "a null is a gap the line breaks across, never a zero", and the test claiming to guard it
      asserts only that the tape differs from an empty tape, which a tape drawing zeros also does.
      Add `DesignSystem.AssertRendersDifferently` to mirror `AssertRendersAlike` and apply per
      control. **~2h.** This is the same hole that, elsewhere, let a null-drawn-as-zero mutation turn
      exactly one test red out of 700.

- [ ] **`TrayController.BuildTooltip`** — pure, public, static, zero tests, and it is the tray's
      whole enforcement of "unavailable is not zero". It re-implements percent formatting instead of
      using `UsageFormat.Percent`, and truncates at a hard `[..127]` that can cut mid-number. Move it
      to `Altim.Core` (it already takes everything it needs) rather than creating `Altim.App.Tests`
      — a test project referencing `Altim.App` makes that build mandatory and breaks "run the app
      while running the suite" with MSB3027. **~1h.**

- [ ] **Fix 13 confirmed-vacuous tests.** Including `Assert.Equal(scale.LevelFor(7), scale.LevelFor(7))`
      (literally `x == x`, and the guard it names is covered by no test in the repo); a `SnapCentre`
      test that passes against an implementation ignoring its `thickness` argument entirely; a
      countdown test that greps `AltimRuntime.cs` as text and passes with the call site commented
      out; an automation-peer test whose every assertion is invariant under the translation it
      exists to check; and a dial positive-control that is `Assert.True(true)` on two of three
      iterations — exactly the two colours needing it. **~half day.**

- [ ] **`UsagePacing.Compare` with an expired carry-in.** Delete the `HoldsAt` guard — the class's
      own stated priority — and Altim subtracts a level from two windows ago and prints it as this
      window's pacing. Suite stays green. **~20 min.**

- [ ] **Decide what `IsBestEffort` does on screen.** `MetricViewModel` computes and exposes it;
      nothing consumes it. So a prose-scraped figure and a documented one are byte-identical on
      screen — while `ClaudeUsageProvider` deliberately holds a stale summary on a failed `/usage`
      run "labelled best-effort either way", republished with **no reset instant** so it can never
      expire. Rule 2's only mitigation does not exist. **Half a day, mostly a design call.**

- [ ] **Two existing seams nobody used:** `LinuxAutoStartService` takes an autostart directory and
      `LinuxProcessMonitor` takes a `/proc` root — both injectable today, both testable on Windows
      with a temp directory. The autostart round-trip covers the rewrite-not-replace rule that
      currently has only string-level proof. **~2h for both.**

- [ ] **Icon assets are never asserted to exist.** Tests check `FileName(light, 32)` returns
      `"altim-white-32.png"` but nothing checks the file ships. A size added to an array with no PNG,
      or a PNG dropped from packaging, shows up only as a missing tray icon at run time. **~20 min.**

- [ ] **A Windows test project at the Windows TFM.** Every file in `Altim.Platform.Windows` is
      `#if WINDOWS`, and all four test projects are `net10.0`, so the facade they would bind to is a
      literally empty assembly — adding a reference changes nothing. With the right TFM,
      `WindowsAutoStartService.CommandLine` is immediately testable, and a broken quote there
      silently breaks startup for anyone with a space in their install path. **~1h, then free.**

- [ ] **Make `PlatformStack.CreateLinux/CreateMacOS` internal.** `ForeignPlatformStackTests` says
      outright that it copies the sequence by hand and therefore cannot catch a service added to one
      stack and not the other. **~20 min.**

---

## Later

- Extract and test `AltimRuntime`'s two real decisions — backfill run-stamping (stamp an empty
  answer and the map shows blank squares for 24 hours) and `OnSystemResumed`'s suspend/resume dance
  (get it wrong and a modern-standby laptop never refreshes on wake). By extraction into Core, not
  a new test project.
- `UsageReadings.DescribeAge`, and `AreEquivalent` for Claude and Gemini on a *moving* clock — the
  Codex test runs on a frozen one, so deleting the exclusion that is the class's reason to exist
  keeps it green.
- `setting` key allowlist test; `ParsedResultShapeTests.Types` completeness by reflection rather
  than a hand-maintained array; `DesignData` returning null outside design mode.
- Coverage tooling — not as a quality metric, but as a permanent way to find never-executed code.
  It would immediately print 0% for `Altim.App` and `Altim.Platform.Windows` (8,853 lines, ~19%).
- Installing Altim *before* the vendor CLI leaves a provider with no transcript roots until restart:
  `ResolveConfigRoots()` is captured once at construction and filters on `Directory.Exists`.
- Executable bit on the three packaging scripts; `SECURITY.md` and `CHANGELOG.md`; `win-arm64`;
  AppImage update information.
- Delete the unreachable `AbandonedMutexException` branch in `SingleInstance`.

## Doc corrections

- `AGENTS.md`'s layout table omits `Altim.Providers.Gemini`.
- `PRIVACY.md`'s Gemini sentence overstates the credential guarantee — `JsonlTailReader` swallows a
  failed open, so a reader that *did* touch the file would look identical. The real guarantee is
  `AFileOutsideAChatsDirectoryIsNeverRead`.
- Three doc comments describe "25/50/75/100 rules" where `UsageTape.Levels` is `[0, 50, 100]`.
- Verify the three Simple Icons per-icon licence fields; the project's own disclaimer says
  project-level CC0 does not imply every icon is CC0, and the repo asserts flat CC0 in three places.

## Needs money or hardware

A Mac (bundle shape, and to launch Altim even once); Apple Developer Program at $99/yr for
Developer ID and notarisation, without which a downloaded DMG is refused outright; somebody to
install the `.deb`, `.rpm` and AppImage on a real desktop, since no Linux artefact has ever been
installed by anyone; optionally a Windows code-signing certificate.

**A Windows-only 0.1.0 needs none of these** and is roughly three days: prerelease tag, first-run
visibility, drop or narrow the App SDK, notices file, `UpdateManager`, settle the domain, Install
section.
