# Backlog

Open work, in the order it matters. Everything here was demonstrated or read out of the code
rather than inferred; where something is reported rather than verified it says so.

**State, 2026-09-20, evening.** 1,816 tests green locally, 0 warnings. The release pipeline has
run end to end three times today and the last one produced a complete draft with **twenty**
assets across five targets: Windows x64 and arm64 on separate Velopack channels, both macOS
architectures, and the AppImage, `.deb` and `.rpm`. Windows arm64 and the Intel macOS DMG both
built for the first time. No public tag exists yet, and nothing is signed.

---

## Before a first release

- [x] **No third-party notices anywhere.** Clear unmet obligations: SQLitePCLRaw (Apache-2.0
      §4(a), §4(d)), Skia and ANGLE (BSD-3 §2), FreeType's FTL credit inside `libSkiaSharp`,
      Inter's OFL text. `/usr/share/doc/altim/copyright` currently carries Altim's 21-line MIT
      notice while the package contains the whole .NET runtime — a Debian Policy §12.5 violation.
      Matters most on Windows, where AOT statically links everything into one binary and a
      recipient has no way to discover what is inside. Generate with `nuget-license`, hand-add the
      four, wire into all three packaging paths.

- [x] **Network permission silently inverts when the database will not open.** `AllowNetworkCalls`
      defaults `true`, and the memory fallback is `AltimSettings.Default`, so a user who switched
      it **off** gets it **on** with no log line.

- [x] **No update mechanism on any platform.** Velopack is referenced for exactly one call —
      `VelopackApp.Build().Run()` — so installs happen and updates never do. `UpdateManager` is
      never constructed and no feed URL exists.

- [x] **`Microsoft.WindowsAppSDK` is proprietary, pulls ~42MB, and the feature does not work.**
      Narrowed to `Microsoft.WindowsAppSDK.Foundation`. The umbrella was putting
      `onnxruntime.dll` and `DirectML.dll` into every publish — 38.5MB of inference runtime.
      win-x64 shipped payload: 92.1MB in 37 files → 70.6MB in 78. Notification registration
      logs the same line before and after on a machine with no complete Windows App Runtime.

- [x] **Intel macOS build is not published.** It builds in CI; the release job does not attach it.

- [x] **`win-arm64` is not built at all.**

## Tests that would catch real bugs

- [x] **`UsageTapePixelTests`** — every other drawn control has one. `UsageTapeSeries`'s own doc
      says "a null is a gap the line breaks across, never a zero", and the test claiming to guard
      it asserts only that the tape differs from an empty tape, which a tape drawing zeros also
      does. Add `DesignSystem.AssertRendersDifferently` to mirror `AssertRendersAlike` and apply
      per control. This is the same hole that, elsewhere, let a null-drawn-as-zero mutation turn
      exactly one test red out of 700.

- [x] **`TrayController.BuildTooltip`** — pure, public, static, zero tests, and it is the tray's
      whole enforcement of "unavailable is not zero". It re-implements percent formatting instead
      of using `UsageFormat.Percent`, and truncates at a hard `[..127]` that can cut mid-number.
      Move it to `Altim.Core` (it already takes everything it needs) rather than creating
      `Altim.App.Tests` — a test project referencing `Altim.App` makes that build mandatory and
      breaks "run the app while running the suite" with MSB3027.

- [x] **Fix 13 confirmed-vacuous tests.** Including `Assert.Equal(scale.LevelFor(7),
      scale.LevelFor(7))` (literally `x == x`, and the guard it names is covered by no test in the
      repo); a `SnapCentre` test that passes against an implementation ignoring its `thickness`
      argument entirely; a countdown test that greps `AltimRuntime.cs` as text and passes with the
      call site commented out; an automation-peer test whose every assertion is invariant under the
      translation it exists to check; and a dial positive-control that is `Assert.True(true)` on
      two of three iterations — exactly the two colours needing it.

- [x] **`UsagePacing.Compare` with an expired carry-in.** Delete the `HoldsAt` guard — the class's
      own stated priority — and Altim subtracts a level from two windows ago and prints it as this
      window's pacing. Suite stays green.

- [x] **Decide what `IsBestEffort` does on screen.** One caption per provider card when any of
      its figures was derived, and the row itself carries the longer sentence on hover and to a
      screen reader. Same shape as `ShowsLocalOnlyNotice`. A badge on every derived row would be
      furniture on a card where most rows are derived, which is the Claude case. The other half —
      a stale summary that could never expire — is fixed separately: each figure now expires
      against the window it measures.

- [x] **Two existing seams nobody used:** `LinuxAutoStartService` takes an autostart directory and
      `LinuxProcessMonitor` takes a `/proc` root — both injectable today, both testable on Windows
      with a temp directory. The autostart round-trip covers the rewrite-not-replace rule that
      currently has only string-level proof.

- [x] **Icon assets are never asserted to exist.** Tests check `FileName(light, 32)` returns
      `"altim-white-32.png"` but nothing checks the file ships. A size added to an array with no
      PNG, or a PNG dropped from packaging, shows up only as a missing tray icon at run time.

- [x] **A Windows test project at the Windows TFM.** Every file in `Altim.Platform.Windows` is
      `#if WINDOWS`, and all four test projects are `net10.0`, so the facade they would bind to is
      a literally empty assembly — adding a reference changes nothing. With the right TFM,
      `WindowsAutoStartService.CommandLine` is immediately testable, and a broken quote there
      silently breaks startup for anyone with a space in their install path.

- [x] **~~Make `PlatformStack.CreateLinux/CreateMacOS` internal.~~ Won't do, and the gap is closed
      another way.** Internal would need a test project to reference `Altim.App`, which
      `ARCHITECTURE.md` forbids and which is what lets the suite run while Altim is running. It
      would also weaken the test: both methods catch constructor exceptions into a startup report,
      so a throw and an unsupported service would stop being distinguishable, which is
      `ForeignPlatformStackTests`' whole purpose. `PlatformStackParityTests` covers the real gap —
      a service in two branches and not the third — and kills that mutation where all three of the
      existing tests pass it.

## Found while doing the above

- **`UsageMapViewModelTests.ConstructionReadsNothingAndASlowStoreDoesNotBlockIt` is a
  load-dependent flake.** It fails under a loaded machine and passes in isolation. Seen twice.
  Its wait budget has since been raised to 60 seconds.
- **`AltimDatabaseTests.DisposingWaitsForAWriteThatIsAlreadyInFlight` failed once on
  `windows-latest`** and does not reproduce locally over repeated runs. Its assertion could not
  say why, so it now names which of three things happened: the gate did not hold the shutdown,
  `Dispose` threw on the thread pool, or the test's own token fired before `Task.Run` scheduled
  anything. The next failure should diagnose itself. Nothing has been changed in
  `AltimDatabase` on the strength of a single unreproduced failure.
- **`altim-white-512.png` and `altim-template-512.png` are 1024x1024.** Waste rather than a
  broken promise — neither is installed into a size-named directory and the choosers round up
  and downscale. Rename to `-1024` and add the size, or re-render at 512.
- **`build-linux.sh`'s `chmod 0755` is a no-op on Git Bash over NTFS**, so cross-building the
  `.deb` from Windows leaves `/usr/lib/altim/Altim` at 0664. The mitigation its own comment
  describes only works on a real Linux host.
- **`UsageFormat.Percent` and `TrayTooltip` format percentages independently**, as do
  `UsageFormat.NoProviders` and `UsageAggregator.DescribeStatus`. They agree today. The
  single-implementation fix is for the UI's formatter to delegate to the Core helper.

## Later

- Extract and test `AltimRuntime`'s two real decisions — backfill run-stamping (stamp an empty
  answer and the map shows blank squares for 24 hours) and `OnSystemResumed`'s suspend/resume
  dance (get it wrong and a modern-standby laptop never refreshes on wake). By extraction into
  Core, not a new test project.
- `UsageReadings.DescribeAge`, and `AreEquivalent` for Claude and Gemini on a *moving* clock — the
  Codex test runs on a frozen one, so deleting the exclusion that is the class's reason to exist
  keeps it green.
- `setting` key allowlist test; `ParsedResultShapeTests.Types` completeness by reflection rather
  than a hand-maintained array; `DesignData` returning null outside design mode.
- Coverage tooling — not as a quality metric, but as a permanent way to find never-executed code.
  It would immediately print 0% for `Altim.App` and `Altim.Platform.Windows` (8,853 lines, ~19%).
- Installing Altim *before* the vendor CLI leaves a provider with no transcript roots until
  restart: `ResolveConfigRoots()` is captured once at construction and filters on
  `Directory.Exists`.
- Executable bit on the three packaging scripts; `SECURITY.md` and `CHANGELOG.md`; AppImage update
  information.
- Delete the unreachable `AbandonedMutexException` branch in `SingleInstance`.

## Doc corrections

- ~~`AGENTS.md`'s layout table omits `Altim.Providers.Gemini`.~~ Done.
- ~~`PRIVACY.md`'s Gemini sentence overstates the credential guarantee.~~ Done: the paragraph now
  names the scoping rule and the two tests that enforce it, and says why a test that made the
  credential files unreadable would have proved nothing.
- ~~Doc comments describing "25/50/75/100 rules" where `UsageTape.Levels` is `[0, 50, 100]`.~~
  Done; one occurrence was left, not three.
- ~~Verify the three Simple Icons per-icon licence fields.~~ Done, and it found something.
  `Claude` and `Google Gemini` carry no per-icon `license` field, so the project CC0 waiver is
  what applies to them — 223 of 3,461 icons carry an override and neither of those does.
  **`OpenAI` is not in the set at all any more**: added July 2020, removed November 2025 in
  simple-icons#13944 because nobody obtained OpenAI's permission to keep it. Altim's path was
  traced from it while it was there. `ProviderIdentity.cs`, `docs/DESIGN.md` and `TRADEMARKS.md`
  now say so. `docs/DESIGN.md` also still carried the "not recoloured" claim that TRADEMARKS.md
  had already been corrected for; fixed in the same pass.

## Needs money or hardware

A Mac (bundle shape, and to launch Altim even once); Apple Developer Program at $99/yr for
Developer ID and notarisation, without which a downloaded DMG is refused outright; somebody to
install the `.deb`, `.rpm` and AppImage on a real desktop, since no Linux artefact has ever been
installed by anyone; optionally a Windows code-signing certificate.
