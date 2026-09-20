# Packaging

Everything needed to turn a build of Altim into something a person can install, on
all three platforms, by hand or from CI.

| Platform | Artefacts | Tool | Updates |
|---|---|---|---|
| Windows | `Altim-win-Setup.exe`, `Altim-win-Portable.zip`, `*.nupkg` + `RELEASES` | [Velopack](https://velopack.io) 1.2.0 | in-app, delta |
| Linux | `Altim-<version>-x86_64.AppImage`, `altim_<version>_amd64.deb`, `altim-<version>.x86_64.rpm` | [appimagetool](https://github.com/AppImage/appimagetool) 1.9.1, [nfpm](https://nfpm.goreleaser.com) 2.47.0 | none |
| macOS | `Altim-<version>-arm64.dmg`, `Altim-<version>-x64.dmg`, `Altim.app` (zipped) | `iconutil`, `hdiutil` | none |

`.github/workflows/release.yml` runs all three on a `v*` tag and leaves a **draft**
release for a human to publish. The scripts below are what that workflow calls, so
a local run and a CI run produce the same thing.

### What has actually been run

| | Status |
|---|---|
| Windows: publish, pack, install, run, uninstall | **verified on Windows 11 26200** |
| Windows: delta generation across two versions | **verified** — 0.1.0 → 0.1.1 produced a 0.09MB delta against a 40.03MB full package |
| Linux: `dotnet publish -r linux-x64` | **verified** (cross-published from Windows with `-p:AltimPortableBuild=true`) |
| Linux: `.deb` and `.rpm` from `nfpm.yaml` | **verified** — nfpm is a Go binary and runs on Windows; both packages were built and their control metadata read back |
| Linux: AppImage | **never executed** — `appimagetool` is itself an AppImage |
| Linux: installing or running any of the three | **never executed** |
| macOS: `dotnet build` and the full test suite | **run on every push** — `build.yml` has a `macos-latest` job; Apple silicon, so this is also the only arm64 run of the suite |
| macOS: `build-macos.sh`, the `.app` and the DMG | **has never completed once.** The `macos-bundle` job failed all eleven times it ran, every one of them at the `codesign` bundle seal, so no `.app`, no DMG and no artefact has ever existed, and `verify-bundle.sh` has never read a bundle back. See "The bundle seal" below. |
| macOS: installing, launching or signing anything | **never executed** — no Apple Developer account, and a runner has no menu bar |
| `release.yml`, `build.yml` | `actionlint` clean as of the last run that had it. The macOS changes of 2026-09-19 have not been through it; they do parse as YAML. |
| `THIRD-PARTY-NOTICES.md` reaching the Windows artefacts | **verified on Windows 11 26200** - generated from all three publishes, packed with `vpk` 1.2.0, and read back out of `Altim-win-Portable.zip` (`current/`) and `Altim-0.1.0-full.nupkg` (`lib/app/`), 644,168 bytes and SHA-256 identical to the copy in the repository. |
| `/usr/share/doc/altim/copyright` reaching the `.deb` | **verified** - `build-linux.sh` assembled `dist/.copyright`, nfpm 2.47.0 built the package, and the `data.tar.xz` was read back: one entry at `./usr/share/doc/altim/copyright`, 647,049 bytes, mode 0644, carrying the OFL, Apache-2.0, the FTL, Skia's BSD-3, SQLite's dedication and Altim's own MIT. The `.rpm` was built from the same configuration and not opened. |
| The AppImage and macOS copies | **never executed**, like everything else in those two rows. They are wired the same way and `bash -n` passes. |
| `build-linux.sh` | `shellcheck --severity=style` clean |
| `build-macos.sh`, `verify-bundle.sh` | `bash -n` only. shellcheck was not installed on the machine they were last changed on, so the row above does not cover them. |
| The notices changes of 2026-09-20, in all three scripts | `bash -n` only. shellcheck is still not installed here, so the `build-linux.sh` row above does **not** extend to them. |

Lint-clean is not the same as correct. The macOS rows used to say a bundle was
produced on every push and read back; that was never true, and it is corrected
above. What is true today is that the scripts have been read, `bash -n` checked,
and exercised as far as a Windows machine reaches: the `osx-arm64` single-file
publish, the bundle assembly and the Mach-O-only check all run here. Everything
that needs `sips`, `iconutil`, `codesign`, `ditto` or `hdiutil` is unobserved.

Nobody has launched Altim on a Mac, nothing is signed with a real identity, and
nothing is notarised. Nothing that installs or launches on Linux has been
observed working either.

`build.yml` uploads the macOS bundle as an artefact with a fourteen-day
retention, and that upload now runs even when a step above it failed, because a
red job with nothing attached is exactly what the first eleven runs were.

---

## Windows

### By hand

```powershell
./packaging/windows/build-windows.ps1 -Version 0.1.0
```

Output lands in `dist/windows/win-x64/`. The script publishes and then packs; pass
`-SkipPublish` to re-pack an existing `dist/.publish/win-x64` while iterating.

**Prerequisites**

- .NET 10 SDK.
- The **Visual Studio C++ build tools**. The shipping build is a Native AOT publish,
  and ILC links with `link.exe`. The "Desktop development with C++" workload of the
  Build Tools is enough.
- `vpk`, the Velopack CLI. The script installs it if it is missing
  (`dotnet tool install --global vpk --version 1.2.0`).

**If the build fails with `'vswhere.exe' is not recognized`** — ILC shells out to
vswhere to locate the linker, and a Build Tools-only install does not put it on
`PATH`. The script prepends `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`
for exactly this reason; if you are publishing by hand rather than through the
script, do the same.

### The one line in the entry point

Velopack needs `VelopackApp.Build().Run()` as the **first statement in `Main`**,
before the single-instance guard and before Avalonia is touched. It is how the
installed application handles the hooks the installer invokes it with
(`--veloapp-install`, `--veloapp-updated`, `--veloapp-obsoleted`,
`--veloapp-uninstall`) and exits, rather than starting a tray icon in the middle of
an installation.

`vpk pack` refuses to package a binary that does not contain the marker that call
leaves behind, so this is enforced rather than remembered. (`--skipVeloAppCheck`
exists and should not be used.)

### Updates and deltas

`vpk pack` writes a full `.nupkg`, a `RELEASES` file and a `releases.win.json` feed
alongside the installer. A delta package is produced **only when the previous
release's `.nupkg` is already in the output directory** when `vpk pack` runs. The
release workflow therefore runs `vpk download github` into `dist/windows/win-x64`
first. On the very first release that download fails, which is expected and
ignored, and the release is full-only.

To reproduce a delta locally, build twice into the same output directory:

```powershell
./packaging/windows/build-windows.ps1 -Version 0.1.0
./packaging/windows/build-windows.ps1 -Version 0.1.1
# dist/windows/win-x64/Altim-0.1.1-delta.nupkg
```

Measured on the second of those two runs:

```
[INF] Starting: Building delta 0.1.0 -> 0.1.1
[INF] Delta processed 0040 files. 0003 patched, 0037 unchanged, 0000 new, 0000 removed
    Altim-0.1.0-full.nupkg      40.03 MB
    Altim-0.1.1-delta.nupkg      0.09 MB
    Altim-0.1.1-full.nupkg      40.03 MB
```

Three files changed between two builds that differ only in their version number —
`Altim.exe`, the manifest and `sq.version` — and the delta is 0.2% of the full
package.

The `.nupkg`, `RELEASES`, `releases.win.json` and `assets.win.json` files are
attached to the GitHub release deliberately: they are the update feed, and a
release without them is one that no installed copy can update from. Note that
`releases.win.json` is lower case, which matters when the collecting job runs on a
case-sensitive filesystem.

Packing a version the output directory already holds is refused, not overwritten:

```
[FTL] There is a release in channel win which is equal or greater to the current
      version 0.1.1. Please increase the current package version or remove that release.
```

Bump the version, or empty `dist/windows/win-x64` if you are re-cutting one that
was never published.

### Installing somewhere other than the default

```powershell
./Altim-win-Setup.exe --installto D:\Apps\Altim --silent --log setup.log
```

Without `--installto`, Velopack installs per user into `%LocalAppData%\Altim`, with
no administrator prompt.

### Uninstalling

Apps & features, or `%LocalAppData%\Altim\Update.exe --uninstall`.

Verified against a real install. It stops the running process, runs the
application's `--veloapp-uninstall` hook, then removes:

- the program directory (`current\`, `packages\`, `Altim.exe`, `Update.exe`)
- `%LocalAppData%\Temp\velopack_Altim`
- the Start menu shortcut
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Altim`

Three things survive, and all three are correct:

| Left behind | Why |
|---|---|
| `%APPDATA%\Altim` — database, WAL, log | Deliberate. Reinstalling keeps your history. Delete the folder by hand for a genuinely clean slate. |
| `%LocalAppData%\velopack\velopack_Altim.log` | Velopack's own updater log, ~9KB. It is the record of the uninstall that just happened, so it cannot delete itself. |
| One value under `HKCU\Control Panel\NotifyIconSettings` | Written by **Explorer**, not by Altim or the installer: the shell records one entry per executable path that has ever shown a tray icon, and prunes them on its own schedule. Every development build on a machine leaves one too. |

---

## The Windows App Runtime, and why the package does not carry it

ARCHITECTURE.md lists this as risk 1. This is the packaging decision it asks for,
and the measurements behind it.

**Decision: the package does not carry the Windows App Runtime.** Altim ships
framework-dependent. Notifications do not work today, they degrade exactly as
ARCHITECTURE.md requires — the app starts, runs and does not notify — and the
remaining work is a deployment concern rather than a payload one.

Four things were measured on Windows 11 26200, which has
`Microsoft.WindowsAppRuntime.2` 2.4.0 installed.

| # | Configuration | Result |
|---|---|---|
| 1 | As shipped today | `Register()` fails, `REGDB_E_CLASSNOTREG` |
| 2 | Windows App SDK self-contained | fails earlier, `0x8007007E`, missing resource DLL |
| 3 | Self-contained + one DLL scavenged from `C:\Program Files\WindowsApps` | register, show and unregister all succeed |
| 4 | Framework-dependent + manual bootstrap + reg-free WinRT | bootstrap succeeds, `Register()` **still** fails `REGDB_E_CLASSNOTREG` |

### 1. What is actually wrong today

Nothing initialises the Windows App SDK bootstrapper, so WinRT activation has no
idea the framework package exists:

```
[notifications] AppNotificationManager.Register failed: Class not registered
                (0x80040154 (REGDB_E_CLASSNOTREG))
```

That is in `%APPDATA%\Altim\altim.log` on every start, including the installed
build produced by this packaging, on a machine that has the runtime.

### 2. Self-contained is rejected, and it does not work anyway

`WindowsAppSDKSelfContained=true` adds **280 files and 68MB** — the whole WinUI
XAML stack, the Windows AI stack and 47 languages of `.mui` resources — for one
call to `AppNotificationManager`. And registration fails:

```
FAILED: System.Runtime.InteropServices.COMException: The specified module could not be found.
Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll
HRESULT: 0x8007007E
```

`Microsoft.WindowsAppRuntime.Insights.Resource.dll` **is not in the NuGet package
at all**. The only copy on the machine is inside the installed MSIX framework
package under `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__…`.
Copying that one file into the publish output makes registration, display and
unregistration all succeed (experiment 3), which confirms it is the only thing
missing — and confirms the fix is unavailable to a build. `C:\Program Files\WindowsApps`
is ACL'd, its contents are not redistributable from there, and nothing guarantees
the file exists on a build agent.

### 3. The SDK's auto-initialiser is rejected on behaviour

`WindowsAppSdkBootstrapInitialize=true` compiles in `MddBootstrapAutoInitializer`,
a module initialiser whose body is:

```csharp
if (!Bootstrap.TryInitialize(majorMinorVersion, versionTag, minVersion, options, out hr))
{
    global::System.Environment.Exit(hr);
}
```

…with default options of `OnNoMatch_ShowUI`. On a machine without the runtime that
is a Microsoft dialog followed by a process that exits before `Main` runs.
ARCHITECTURE.md requires the opposite: "notification failure must degrade to
'Altim runs and does not notify', never to a failed start." Never turn this on.

### 4. A manual bootstrap is necessary but not sufficient

The obvious fix — `Bootstrap.TryInitialize` with `InitializeOptions.None`, guarded,
plus `WindowsAppRuntime_EnsureIsLoaded()` for undocked reg-free WinRT — was written
and run. The bootstrap succeeds. `Register()` still fails with
`REGDB_E_CLASSNOTREG`.

The reason is the one ARCHITECTURE.md already records: **the runtime's main package
is absent.** The Windows App Runtime is three MSIX packages — Framework, Main and
Singleton — and the Main package is what carries the notification COM activator
registrations. This machine has:

```
Microsoft.WindowsAppRuntime.2                  2.4.0.0      <- framework, present
MicrosoftCorporationII.WinAppRuntime.Main.1.8  8000.946…    <- main, for 1.8 only
(no Main or Singleton for 2.x at all)
```

`DeploymentManager.Initialize()` is the API that provisions Main and Singleton out
of the framework package. It was not called here, because it installs MSIX packages
on the machine and that is the maintainer's decision, not a packaging experiment.
Worth knowing before anyone tries: its read-only companion
`DeploymentManager.GetStatus()` **terminates the process** (exit code 9, no
exception, no output) when the 2.x Main package is missing, so it cannot be used as
a safe probe from the very state it exists to detect.

### What this means for packaging

Adding files to the installer cannot fix this. The two candidate deployment fixes,
neither taken:

- **Velopack cannot install it.** The `--framework` bootstrapper knows .NET, .NET
  Framework and the VC++ redistributables, and nothing else:

  ```
  [FTL] The framework/runtime dependency 'windowsappruntime' is not valid.
  ```

- **Shipping `WindowsAppRuntimeInstall-x64.exe`** and running it as a custom install
  step. It is a ~100MB Microsoft redistributable per machine, it needs a Velopack
  hook Altim does not have, and gating installation on it contradicts the
  degrade-don't-fail rule.

The proportionate answer for a tray utility whose notifications are a convenience:
leave the payload as it is, fix the bootstrap in code so the app works on machines
that have a complete runtime, and have first-run onboarding say that notifications
need the Windows App Runtime and where to get it. The first of those is written up
under "Known gaps"; the other two are product decisions.

That decision survived the package narrowing below, but only because the narrowing
kept `Microsoft.WindowsAppSDK.Runtime`. Referencing `.Foundation` on its own turns
`WindowsAppSDKSelfContained` on, which puts the whole runtime in the payload — the
opposite of what this section decided, arrived at by leaving a package out rather than
by anybody choosing it. See "Package size".

---


## Package size

Measured on the `win-x64` publish output with the `.pdb` files excluded, since those are
not installed. `vpk pack` then drops them for you and adds `Update.exe`.

| | Payload | Files |
|---|---|---|
| With the `Microsoft.WindowsAppSDK` umbrella | 92.1MB | 37 |
| With the three feature packages, as now | **51.7MB** | **32** |

**40.4MB of the old payload was Windows App SDK machinery Altim never touches**, and it
arrived because `Directory.Packages.props` pinned the **metapackage**, which depends on
`.ML`, `.AI`, `.WinUI`, `.Widgets` and `.Search`:

| File | Size | Came from |
|---|---|---|
| `onnxruntime.dll` | 20.66MB | `Microsoft.Windows.AI.MachineLearning`, via `Microsoft.WindowsAppSDK.ML` |
| `DirectML.dll` | 17.83MB | as above |
| `Microsoft.Windows.AI.MachineLearning.dll` | 0.86MB | as above |
| `Microsoft.Web.WebView2.Core.dll`, `WebView2Loader.dll` | 0.91MB | `Microsoft.Web.WebView2`, via `Microsoft.WindowsAppSDK.WinUI` |

Altim uses one namespace from one of those components,
`Microsoft.Windows.AppNotifications`, which lives in `.Foundation`. The references are now:

```xml
<PackageReference Include="Microsoft.WindowsAppSDK.Foundation" />
<PackageReference Include="Microsoft.WindowsAppSDK.InteractiveExperiences" />
<PackageReference Include="Microsoft.WindowsAppSDK.Runtime" />
```

**All three are load-bearing, and finding out cost two publishes.** `.Foundation` alone
does remove the 40MB, but it also defaults to `WindowsAppSDKSelfContained`: the payload
comes back to 70.6MB in **78** files as the whole Windows App Runtime is binplaced into
the output, and a RID-less `dotnet build Altim.sln` — the documented build command —
fails outright with *"WindowsAppSDKSelfContained requires a supported Windows
architecture"*. `.Runtime` is what makes the build framework-dependent again, which is
the decision recorded under "The Windows App Runtime, and why the package does not carry
it" above. `.InteractiveExperiences` is pinned to 2.1.6 because `.Foundation` 2.3.9 asks
for 2.1.3 and `.Runtime` 2.4.0 refuses to build against anything older; the umbrella had
been resolving that conflict silently.

Packaging deliberately does **not** strip anything with an exclude rule: what
`dotnet publish` produces and what the installer contains should be the same set of
files, or a bug that only reproduces from the installer becomes possible. The fix belongs
one level up, in the package references, and that is where it is.

**Notification behaviour is unchanged, and this was checked rather than assumed.** The
verification machine has the 2.4.0 framework package and no 2.x Main package, so
registration fails there and always has. The umbrella build and the narrowed build were
each run on it and each logged the same line:

```
[notifications] AppNotificationManager.Register failed: The notification platform is
                unavailable, so usage alerts are off (COMException).
```

---

## Linux

### By hand

```bash
./packaging/linux/build-linux.sh --version 0.1.0                 # all three
./packaging/linux/build-linux.sh --version 0.1.0 --skip-packages # AppImage only
./packaging/linux/build-linux.sh --version 0.1.0 --rid linux-arm64
```

Output lands in `dist/linux/`. Tooling (`nfpm`, `appimagetool`) is downloaded on
demand into `dist/.tools/` at pinned versions; both are single files that need
nothing installed on the host.

The publish is **self-contained but not ahead-of-time compiled**. `PublishAot` is a
Windows-only setting in `Altim.App.csproj` and ARCHITECTURE.md keeps the other
platforms behind it until someone has verified them on real hardware.

Cross-publishing from a Windows host needs `-p:AltimPortableBuild=true`, which the
script always passes; it is a no-op on Linux. You still cannot build the AppImage
on Windows, because `appimagetool` is itself an AppImage.

### Verifying the portable face on Windows

`AltimPortableBuild=true` forces the non-Windows shape of `Altim.App` on a Windows
host, which is the only way to compile what the Linux CI job compiles without a
Linux machine. Nothing sets it by default:

```
dotnet build src/Altim.App -c Release -p:AltimPortableBuild=true --artifacts-path <scratch dir>
```

The output has to be redirected, because restore writes the chosen framework into
`obj/project.assets.json` and sharing that with the normal build makes each one
invalidate the other.

Use `--artifacts-path`, **not** `-p:BaseIntermediateOutputPath`. That one applies to
every project in the graph, so they all land in a single `obj` folder and the build
fails with `MSB4006: There is a circular dependency in the target dependency graph
involving target "ResolveProjectReferences"`. `--artifacts-path` keeps a subfolder
per project, and leaves the repository's own `obj` alone, so the next ordinary build
is not invalidated. Verified 2026-09-17 at 0 warnings.

### The `.deb` dependency list is hand-written, and has to be

`packaging/linux/nfpm.yaml` lists the dependencies literally. **Do not replace it
with anything that derives them.**

`dpkg-shlibdeps`, `dh_shlibdeps` and `fpm` all read ELF `DT_NEEDED` entries — what
the dynamic loader resolves at start-up. Avalonia does not link X11 that way: its
X11 backend `dlopen()`s every X library by soname from managed code.

This was measured rather than assumed. Every `DT_NEEDED` entry across all 19 ELF
files in a `linux-x64` publish is:

```
ld-linux-x86-64.so.2   libc.so.6        libdl.so.2          libfontconfig.so.1
libgcc_s.so.1          liblttng-ust.so.0  libm.so.6         libmscordaccore.so
libpthread.so.0        librt.so.1       libstdc++.so.6
```

Altim needs nine libraries to start. **One of them is in that list** —
`libfontconfig.so.1`, and only because `libSkiaSharp.so` links it directly. The
other eight exist only as string literals inside `Avalonia.X11.dll`:

```
libX11.so.6  libXext.so.6  libXi.so.6      libXrandr.so.2
libXcursor.so.1  libXfixes.so.3  libICE.so.6  libSM.so.6
```

(`libXfixes`, not `libXinerama` — Avalonia 12 does not use Xinerama. The list came
out of the binaries, which is the only way to get it right.) ICU, OpenSSL and
Kerberos are `dlopen`'d by the .NET native shims in the same way and are equally
invisible, which is why they are declared here too, with alternatives.

A generated list therefore contains one of nine. The package installs, `dpkg`
reports success, and Altim exits at start-up. nfpm is used here precisely because
it performs no dependency scanning at all: what is in the file is what is in the
package. Verified — this is the `Depends` field read back out of a `.deb` built
from this configuration:

```
Depends: libc6, libgcc-s1, libstdc++6, zlib1g,
 libicu76 | libicu75 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66,
 libssl3t64 | libssl3 | libssl1.1, libgssapi-krb5-2,
 libx11-6, libice6, libsm6, libxext6, libxi6, libxrandr2, libxcursor1, libxfixes3,
 libfontconfig1
Recommends: libgl1, dbus
```

Check a built package rather than the configuration:

```bash
dpkg-deb --field dist/linux/altim_0.1.0-1_amd64.deb Depends
rpm -qpR dist/linux/altim-0.1.0-1.x86_64.rpm
```

The release workflow prints both for every build.

**Anyone who adds a native dependency to Altim must add it to `nfpm.yaml` too.**
Nothing will notice for you.

The `.rpm` requires the X11 set by soname (`libX11.so.6()(64bit)` and friends)
because Fedora, RHEL and openSUSE disagree about package names; the .NET runtime
group has no stable soname to ask for and uses Fedora/RHEL package names.

`libnotify` is deliberately **not** a dependency. Altim's notifications are
`org.freedesktop.Notifications` calls made directly over the session bus through
`Tmds.DBus.Protocol`, and the status icon is a StatusNotifierItem on the same bus;
neither goes through libnotify or an appindicator library.

### Two traps in the nfpm configuration

Both were hit while building the packages, and both are guarded now.

**`${VAR}` does not expand in `contents[].src`.** nfpm expands environment
variables in some scalar fields — `arch` and `version` work — but hands
`contents[].src` to the filesystem literally, failing with
`GetFileAttributesEx ${NFPM_PAYLOAD}/: no such file`. `nfpm.yaml` is therefore a
template with `@ARCH@`, `@VERSION@` and `@PAYLOAD@` placeholders that
`build-linux.sh` substitutes with `sed` into `dist/.nfpm.yaml`. One mechanism, and
no field where it silently does not apply.

**`type: tree` copies source file modes verbatim.** A payload published from a
host with no POSIX executable bit — any Windows cross-publish — produces a `.deb`
whose `/usr/lib/altim/Altim` is `0664` and cannot run. `build-linux.sh` normalises
the payload with `chmod` before packaging, and every non-tree entry declares its
mode explicitly with `file_info`.

### Layout

```
/usr/lib/altim/           the whole self-contained publish
/usr/bin/altim            symlink to /usr/lib/altim/Altim
/usr/share/applications/altim.desktop
/usr/share/icons/hicolor/<size>x<size>/apps/altim.png
/usr/share/doc/altim/copyright
```

---

## macOS

### By hand

```bash
./packaging/macos/build-macos.sh --version 0.1.0 --arch arm64
./packaging/macos/build-macos.sh --version 0.1.0 --arch x64
```

One invocation, one architecture, one DMG. Output lands in `dist/macos/`: the DMG,
and the bundle again as a `ditto` archive. The staged bundle stays behind at
`dist/.macos-stage/<arch>/Altim.app`, which is what `verify-bundle.sh` is pointed
at. Without `--arch` the script builds for the machine it is running on.

### The bundle seal

**It seals.** The first run that reached this point reported
`Altim.app: valid on disk` and `satisfies its Designated Requirement` on both
architectures. Publishing as a single file was the fix: with no managed assemblies beside
the executable there is nothing in `Contents/MacOS` that cannot carry a signature.

The x64 job then failed at `hdiutil` with `No space left on device`. That was read as a
runner capacity problem, and it is not one: printing the free space first showed **91GB
available** on the run that failed, and the message names a path under `/Volumes`. It is the
image being sized too small, and only x64 trips it because its single-file host is the larger
of the two. The size is now stated rather than guessed by `hdiutil` — measured off the staged
tree with a fifth again and a 64MB floor, which compression makes free. The publish output is
still deleted first, because nothing reads it after the bundle is assembled.



This is why there were no macOS artefacts for eleven runs, and it is worth reading
before changing anything under `packaging/macos/`.

When `codesign` **seals** a bundle it applies a set of default resource rules, and
in those rules `Contents/MacOS/` is a *nested-code* location. Every file in that
directory other than the main executable has to already be an independently signed
code object. A normal `dotnet publish` lays every managed assembly down right
there, and a managed `.dll` is a PE file that can never carry a Mach-O signature,
so the seal fails:

```
dist/.macos-stage/Altim.app: replacing existing signature
dist/.macos-stage/Altim.app: code object is not signed at all
In subcomponent: .../Altim.app/Contents/MacOS/System.Diagnostics.Contracts.dll
```

The failure is at **sign** time, not verify time. Five things were tried against
it, and none of them can work. Do not spend a sixth attempt:

| Tried | Why it cannot work |
|---|---|
| dropping `--deep` | `--deep` was never the cause; sealing walks the resource rules either way |
| dropping `--strict` | same; `--strict` tightens verification, and this fails before verification |
| `--resource-rules` | removed from `codesign` in OS X 10.10 |
| signing the `.dll` files individually | a PE file cannot carry a Mach-O signature |
| moving only the managed assemblies to `Resources` and probing back | the apphost resolves `Altim.dll`, `Altim.runtimeconfig.json` and `Altim.deps.json` from its own directory, and those are exactly the files that break it |

**The fix is `PublishSingleFile=true`.** With the assemblies embedded in the host,
`Contents/MacOS/` holds nothing but Mach-O. Measured from a real `osx-arm64`
cross-publish on 2026-09-19, the whole directory is five files:

```
Altim                     88.8MB  Mach-O arm64 executable, ad-hoc signed by the SDK
libSkiaSharp.dylib        14.5MB  Mach-O universal (x86_64 + arm64)
libHarfBuzzSharp.dylib     2.8MB  Mach-O universal
libe_sqlite3.dylib         1.6MB  Mach-O arm64
libAvaloniaNative.dylib    1.5MB  Mach-O universal
```

Self-contained single-file uses the `singlefilehost`, which statically links the
runtime, so `libcoreclr`, `libhostfxr`, `libhostpolicy`, `createdump` and the
`System.*.Native` shims are gone as well as the managed assemblies.
`Altim.deps.json` and `Altim.runtimeconfig.json` are carried inside the bundle by
the bundler, which is what makes this different from every earlier attempt to move
files out of `Contents/MacOS/`.

`IncludeNativeLibrariesForSelfExtract` is deliberately left at its default of
`false`. Setting it would embed those four dylibs and make the host extract them to
a temporary directory on every start, and extracted copies are not covered by any
bundle seal.

**The icon assets are the same problem.** Content files are not bundled by a
single-file publish (`IncludeAllContentForSelfExtract` is `false` and stays that
way), so all 24 of them, the seven `altim-template-*.png` renderings among them,
land loose at `Contents/MacOS/assets/icons/`, which is nested code just as much as
a `.dll` is. `build-macos.sh` moves the whole `assets`
directory to `Contents/Resources/assets`. No source change is needed for that:
`MacOSTrayAssets.DiscoverAssetDirectory` asks
`MacOSAppBundle.FindResourcesDirectory` first, tries `Contents/Resources` itself,
then `Contents/Resources/assets/icons`, and only then walks up from
`AppContext.BaseDirectory`. The move lands on the second of those.

**Until a run proves the seal works, the ad-hoc seal is non-fatal.** If
`codesign --force --sign - Altim.app` fails, `build-macos.sh` reports it, re-signs
the main executable on its own so the binary can still be executed on Apple
silicon, and carries on to the DMG. `build.yml` passes `--allow-unsealed` to
`verify-bundle.sh`, which turns two checks into warnings: that the bundle carries a
seal, and that `Contents/MacOS` holds only Mach-O.

That state is deliberate, labelled and temporary. **It is not a pass.** A bundle
with no seal has:

- no `Contents/_CodeSignature/CodeResources`, so nothing can tell an intact bundle
  from a tampered one and `codesign --verify` reports the app as not signed at all
- **no start at login.** `SMAppService.registerAndReturnError:` answers
  `kSMErrorInvalidSignature` for an app that is not code signed as a bundle
- a hard Gatekeeper refusal of any downloaded copy, on top of the ordinary unsigned
  and un-notarised refusal

`release.yml` does **not** pass `--allow-unsealed`. A release that cannot seal its
bundle produces a draft with no macOS download and the other two platforms intact,
which is the intended outcome. Remove `--allow-unsealed` from `build.yml` as soon
as a run reports `sealed and valid, ad-hoc`.

### Why there is no universal build

.NET cannot emit a universal binary: a runtime identifier names exactly one
architecture. The previous script published `osx-arm64` and `osx-x64` separately
and merged every Mach-O present in both with `lipo`. That layer is gone, and the
release page carries `Altim-<version>-arm64.dmg` and `Altim-<version>-x64.dmg`
instead.

The reason is that `lipo -create` over two **single-file** hosts is a behaviour
nobody has observed. It is probably fine: .NET embeds the single-file payload
inside the Mach-O rather than appending past the end of it, specifically so that
`codesign` works. Read off the `osx-arm64` host built on 2026-09-19:

```
__LINKEDIT        fileoff 8372224   filesz 84697462   end 93069686
LC_CODE_SIGNATURE dataoff 92348000  datasize 721686   end 93069686
file size                                                 93069686
```

The 84MB `__LINKEDIT` is the embedded bundle, the segment ends exactly at the end
of the file, and the ad-hoc signature the SDK applied is the last thing in it. So
nothing is hanging off the end for `lipo` to drop. But "probably fine" was not
worth keeping in the middle of a job that had never once produced an artefact, and
removing it also deleted the already-universal special case and the both-slices
check that existed only to serve it. Two rows on a release page is the price.

If someone with a Mac wants universal back, the merge is the only part that has to
be re-proven, and the way to prove it is to `lipo -create` two single-file hosts
and run the result.

**Three of the native libraries are already universal**, which is unchanged and
still worth knowing. SkiaSharp, HarfBuzzSharp and Avalonia each ship one fat
`.dylib` under `runtimes/osx/native/`, which is the architecture-neutral `osx`
runtime identifier rather than `osx-arm64` or `osx-x64`, and runtime identifier
fallback resolves it for both. An arm64 bundle therefore carries three dylibs that
also hold `x86_64`. That is wasted bytes and not a fault.

**Reading the bundle back.** `packaging/macos/verify-bundle.sh` takes a built
`Altim.app` and checks the things that would otherwise only surface on somebody's
Mac: an unsubstituted `@SHORT_VERSION@`, a missing `CFBundleIdentifier` (which is
what silently turns off notifications and start at login), `LSUIElement` going
missing, the menu bar template assets not reaching anywhere
`MacOSTrayAssets.DiscoverAssetDirectory` looks, a file in `Contents/MacOS` that is
not Mach-O, and a Mach-O built for the wrong architecture. It also checks the code
signature, and reports which kind it is.

It has never been run against a bundle a Mac produced.

### `LSUIElement`, and why the plist alone is not enough

`packaging/macos/Info.plist` sets `LSUIElement`, which is what keeps the Dock tile
and the menu bar from appearing between launch and the first line of managed code.
It is **not sufficient**: Avalonia calls `NSApplication.setActivationPolicy` during
initialisation and that call wins over the plist. The other half is already in
`Program.cs`:

```csharp
.With(new MacOSPlatformOptions { ShowInDock = false })
```

Remove either one and Altim becomes a tray utility with a Dock icon and an
application menu it has no windows for.

`LSMinimumSystemVersion` is 13.0 because `SMAppService.mainAppService`, which is how
"start at login" is implemented, is macOS 13 and later.

### The icon

The repository ships PNGs up to 512. An `.iconset` wants a 1024 for
`icon_512x512@2x`, so that one slot is upscaled from the 512 with `sips`; every
other slot is an exact source size. Adding `assets/icons/altim-1024.png` would
remove the upscale.

---

## Signing

Nothing is signed with an identity today. Every identity-signing step is skipped
cleanly when its credential is absent, and the release notes the workflow writes say
so on the release page, so an unsigned release is obvious rather than silent.

### macOS is ad-hoc signed even with no credentials, and has to be

"Unsigned" is not one of the options on a Mac. Apple's macOS Big Sur universal apps
release notes are explicit: *"New in macOS 11 on Macs with Apple silicon ... the
operating system enforces that any executable must be signed before it's allowed to
run. There isn't a specific identity requirement for this signature: a simple ad-hoc
signature is sufficient."* The same page adds that a workflow using tools that modify
a binary after linking "might need to manually call `codesign(1)` as an additional
build phase". Nothing in this packaging modifies a binary after the SDK has signed it
any more, now that `lipo` is gone, but the bundle still has to be sealed and the
dylibs still have to be signed individually, which is what the loop below does.

`SMAppService` is the second reason. Its header states that apps using those APIs
must be code signed, and `registerAndReturnError:` answers `kSMErrorInvalidSignature`
otherwise, so an unsealed bundle has no start at login even on the machine that built
it. The third is simply that without a bundle seal there is no
`_CodeSignature/CodeResources` and nothing can tell an intact bundle from a tampered
one.

So `build-macos.sh` ad-hoc signs (`codesign --force --sign -`, inside out, bundle
last) whenever `MACOS_SIGNING_IDENTITY` is absent. **This is not a substitute for a
Developer ID.** An ad-hoc signature carries no identity, Gatekeeper still refuses a
downloaded copy, and notarisation is still impossible. It is the difference between a
bundle somebody can run on their own Mac and one they cannot, which matters because
CI uploads exactly such a bundle as an artefact.

Nothing in that ad-hoc path is fatal, and the identity path is fatal throughout.
See "The bundle seal" above for why, and for what is lost when the seal does not
happen.

The .NET SDK already ad-hoc signs the apphost itself for any `osx*` runtime
identifier, and from .NET 10 it does so when cross-building too, via a managed Mach-O
signer rather than by shelling out to `codesign`. That was read back off an
`osx-arm64` single-file host cross-published from Windows on 2026-09-19: it carries
an `LC_CODE_SIGNATURE` of 721686 bytes ending exactly at the end of the file. So the
signing step is about sealing the *bundle*, not about repairing the binary.

What the SDK does not do is sign the four `.dylib` files beside it or seal the
bundle, which is why the inside-out loop and the final `codesign` on `Altim.app`
both exist.

### Windows

**What you need:** an OV or EV code-signing certificate, or an Azure Trusted Signing
account. EV and Azure Trusted Signing clear SmartScreen immediately; a plain OV
certificate has to build reputation first and will still warn for a while.

**Add as a repository secret:**

| Secret | Value |
|---|---|
| `WINDOWS_SIGN_PARAMS` | The arguments after `signtool.exe sign`, e.g. `/a /tr http://timestamp.digicert.com /td sha256 /fd sha256` |

The build script forwards it to `vpk pack --signParams`, which signs all 14 payload
binaries and `Setup.exe`. A certificate held in a hardware token or a cloud KMS
needs `vpk pack --signTemplate` instead; add a parameter to
`packaging/windows/build-windows.ps1` when that day comes.

For Azure Trusted Signing, `vpk pack --azureTrustedSignFile <metadata.json>` is the
supported path.

### macOS

Two separate things, and one without the other is not enough: a **signed but
un-notarised** app is still refused on a machine that downloaded it.

**What you need:** an Apple Developer Program membership ($99/year), a **Developer
ID Application** certificate exported as a `.p12`, and an app-specific password for
notarisation.

**Add as repository secrets:**

| Secret | Value |
|---|---|
| `MACOS_CERTIFICATE_P12` | The Developer ID Application `.p12`, base64 encoded (`base64 -i cert.p12 \| pbcopy`) |
| `MACOS_CERTIFICATE_PASSWORD` | The password the `.p12` was exported with |
| `MACOS_SIGNING_IDENTITY` | The full identity string, e.g. `Developer ID Application: Your Name (ABCDE12345)` |
| `MACOS_NOTARY_APPLE_ID` | The Apple ID the membership is under |
| `MACOS_NOTARY_APP_PASSWORD` | An app-specific password from appleid.apple.com, **not** the account password |
| `MACOS_NOTARY_TEAM_ID` | The 10-character team identifier |

Set the first three and the bundle is signed. Set all six and it is notarised and
stapled as well. Set none and the workflow emits a `::warning::`, the release notes
tell users to right-click → Open, and everything still builds.

`packaging/macos/entitlements.plist` carries the three hardened-runtime
entitlements the .NET runtime needs and nothing else. There is no sandbox
entitlement on purpose: Altim reads provider transcripts from `~/.codex` and
`~/.claude`, outside any container, and a Developer ID app distributed outside the
App Store is not required to be sandboxed.

### Linux

Not signed, and there is nothing to configure. No GPG signature, no APT or DNF
repository. `SHA256SUMS.txt` is attached to each release and is the only integrity
check on offer.

`appimagetool --sign` can embed a GPG signature in an AppImage; it is not wired up
because a signature nobody can check against a published key is theatre. Publishing
a key first, then wiring it, is the order to do that in.

---

## Third-party notices

`THIRD-PARTY-NOTICES.md` at the repository root is the notice for everything Altim
redistributes. All three artefacts carry a copy:

| Artefact | Where it lands |
|---|---|
| Windows installer, portable zip | beside `Altim.exe` in the install directory. `build-windows.ps1` copies it into the publish directory before `vpk pack`, because `vpk` packs whatever is in `--packDir`. |
| AppImage | the AppDir root, and `usr/share/doc/altim/` inside the image |
| `.deb`, `.rpm` | `/usr/share/doc/altim/copyright`, which `build-linux.sh` assembles from `LICENSE` plus the notices |
| macOS `.app`, DMG | `Contents/Resources/`, where the bundle seal covers it |

All three scripts stop before building anything if the file is not there, because
finding out after a Native AOT compile helps nobody.

### Why

Altim is MIT and public and ships a great deal of other people's code. Until this
was wired there was no notices file anywhere in the repository and not one of the
three scripts copied a licence into an artefact. Unmet, and now met:

- **SQLitePCLRaw** is Apache-2.0. Section 4(a) wants the licence copy and 4(d) wants
  the project's `NOTICE`. Neither is inside any of the four packages, so both are
  committed under `packaging/notices/texts/`, taken from the `v2.1.12` tag the
  shipped build came from.
- **Skia**, and the **ANGLE** in `av_libglesv2.dll`, are BSD-3-Clause, whose clause 2
  wants reproduction in the materials accompanying a binary distribution.
- **FreeType** wants the FTL credit. Which artefacts owe it was measured rather than
  assumed: `libSkiaSharp.so` carries the `FT_` symbols and every FreeType module
  name, `libSkiaSharp.dll` carries none of them and resolves `DWriteCreateFactory`
  instead, and `libSkiaSharp.dylib` uses CoreText. **It is a Linux-only obligation**,
  and the notices file says so rather than claiming all three.
- **Inter** ships embedded in `Avalonia.Fonts.Inter.dll` under the SIL OFL 1.1. Its
  copyright line is in the font's own `name` table, so that half was already met; the
  terms were nowhere. The committed copy is the one from the `v3.19` tag, which is
  the `Version 3.019` the shipped font reports.
- Around twenty MIT notices, which the same file cures.

`/usr/share/doc/altim/copyright` was separately a Debian Policy 12.5 problem: it held
Altim's own 21-line MIT notice, `src: LICENSE` in `nfpm.yaml`, while the package
installs the whole .NET 10 runtime, Skia, HarfBuzz, SQLite and Inter under
`/usr/lib/altim`.

Windows is where it matters most. That publish is Native AOT, so the runtime,
Avalonia, the SQLite provider and the typeface are all inside one `Altim.exe`, and a
recipient of that file has no mechanism at all for finding out what is in it.

### Regenerating

Do not edit `THIRD-PARTY-NOTICES.md`; the next regeneration discards the edit.

```
dotnet publish src/Altim.App -c Release -r win-x64   --self-contained -o dist/.publish/win-x64
dotnet publish src/Altim.App -c Release -r linux-x64 --self-contained -p:AltimPortableBuild=true -p:DebugType=none --artifacts-path dist/.art/linux -o dist/.publish/linux-x64
dotnet publish src/Altim.App -c Release -r osx-arm64 --self-contained -p:AltimPortableBuild=true -p:DebugType=none --artifacts-path dist/.art/osx -o dist/.publish/osx-arm64

python packaging/notices/build-notices.py
```

Regenerate after any dependency change. The output is a pure function of the three
publishes, the NuGet cache and the script, so regenerating without changing anything
produces identical bytes; `--check` compares instead of writing and exits 1 when the
file is stale, which is enough to make it a CI step.

The Windows publish has to run on Windows. Native AOT does not cross-compile, and it
is the Windows target framework that decides which packages are in the graph at all:
that is why the Windows list is the long one and Linux and macOS resolve the same 29.

### Why the list comes from a publish

From `Altim.deps.json`, not from the project files, because the two sets differ.
`Directory.Packages.props` names test-only packages, `Avalonia.BuildServices` and
`Microsoft.NET.ILLink.Tasks` are build tooling, and `AvaloniaUI.DiagnosticsSupport` is
`IncludeAssets=none` outside Debug. None of them reach an artefact, so none of them
belong in a notice.

Each publish directory is then read back file by file and matched against that graph.
**A file nothing accounts for fails the run**, which is what stops a new native payload
turning up in an artefact with no notice. It has already earned its keep: it caught the
move from the `Microsoft.WindowsAppSDK` umbrella to `.Foundation` within minutes of that
change landing, because the publish it was reading no longer matched the graph.

Two kinds of file need help to pass it. A few are placed by MSBuild targets inside their
own packages and have no `deps.json` entry at all, so `UNDECLARED_PAYLOAD` names them
with the package that placed them. The Windows App Runtime is the other kind: running the
SDK self-contained copies about fifty DLLs, WinMDs and `.pri` files out of
`runtimes-framework/` trees, and listing those by name would be a list that rots the
first time Microsoft moves a file. `FRAMEWORK_PAYLOAD_PACKAGES` names the packages
instead, and a file is attributed when it is literally inside one of them at the version
the publish resolved.

### Why the `.nuspec` and not `nuget-license`

`nuget-license` 4.0.17 was installed and run, and it agrees with the generator on
every package that declares an SPDX expression. It is not what the generator uses,
for one specific reason. For `Avalonia.Angle.Windows.Natives` and
`Microsoft.Web.WebView2` it reports `License: BSD-3-Clause`; neither package declares
that. Both declare `<license type="file">`, and the tool inferred an identifier from a
deprecated `licenseUrl`. Both texts are BSD-3-Clause in substance, so the guess is a
good one, but a notices file that prints an SPDX identifier nobody asserted is doing
the exact thing it exists to avoid. The generator reads the `.nuspec`, reports a file
licence as a file licence, and reproduces the file.

### Why `copyright` is plain text and not DEP-5

The machine-readable format wants a `Files` paragraph with one `Copyright` and one
`License` short name per path. Several paths here are one binary carrying a dozen
licences: `/usr/lib/altim/libSkiaSharp.so` by itself is Skia, FreeType, libpng, zlib,
expat, libjpeg-turbo and libwebp. Reducing that to a single short name would assert
something untrue, and the format offers no honest way to write "and fifteen others,
named below". The stanzas are accurate; they are simply not parseable.

### It is 644KB, and most of that is three files

The Windows App SDK notice is 335KB, SkiaSharp's is 140KB and the .NET runtime's is
77KB, all reproduced whole. That is deliberate: they are upstream's statements about
the binaries Altim redistributes, and editing one down to the sections that look
relevant would mean guessing on somebody else's behalf. SkiaSharp's, for instance,
names SDL, Dear ImGui and libmicrohttpd, which a shipped Skia is unlikely to contain;
the file says as much under "What this file does not establish" rather than quietly
cutting them.

Sections appear only when the component ships. Moving from the `Microsoft.WindowsAppSDK`
umbrella to `.Foundation` took `Microsoft.Windows.AI.MachineLearning` and
`Microsoft.Web.WebView2` out of the Windows publish, and their licences and the 325KB
Windows ML notice left this file on the next regeneration without anybody editing it.
Reproducing the Windows ML terms after that payload had gone would have asserted that
Altim carries an inference runtime it does not.

---

## Known gaps

These are open, not hidden.

1. **Windows notifications do not work at all, on any machine**, and the fix has
   two halves, only one of which is code. Evidence and the full set of experiments
   are under "The Windows App Runtime" above; in short:

   ```
   [notifications] AppNotificationManager.Register failed: Class not registered
                   (0x80040154 (REGDB_E_CLASSNOTREG))
   ```

   **Half one, code.** Nothing initialises the Windows App SDK bootstrapper. In
   `src/Altim.Platform.Windows/WindowsNotificationService.cs`, in the constructor,
   ahead of the existing `Register()` call:

   ```csharp
   using Bootstrap = Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap;

   // WinRT activation has to be told the Windows App Runtime framework package
   // exists before AppNotificationManager can be activated at all.
   //
   // Deliberately the manual TryInitialize and NOT the SDK's auto-initialiser
   // (WindowsAppSdkBootstrapInitialize): the generated MddBootstrapAutoInitializer
   // is a module initialiser that calls Environment.Exit(hr) on failure, which
   // turns "no notifications" into "Altim does not start". InitializeOptions.None
   // is the other half of that — no OnNoMatch_ShowUI dialog, just false.
   if (!Bootstrap.TryInitialize(
           Microsoft.WindowsAppSDK.Release.MajorMinor,
           Microsoft.WindowsAppSDK.Release.VersionTag,
           new Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion(
               Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
           Bootstrap.InitializeOptions.None,
           out int hr))
   {
       _registrationError = $"The Windows App Runtime is not installed (0x{hr:X8}).";
       LastDelivery = WindowsNotificationDelivery.NotRegistered;
       return;
   }
   ```

   with a matching `Bootstrap.Shutdown()` in `Dispose`. Note the fully qualified
   `PackageVersion`: it collides with `Windows.ApplicationModel.PackageVersion`.

   **Half two, deployment, and this is the part that decides it.** That change was
   written and run, and on this machine `Register()` still fails, because the
   Windows App Runtime **Main and Singleton packages for 2.x are not installed** —
   only the framework package is. Nothing in the app or in the installer can
   conjure those; they come from Microsoft's `WindowsAppRuntimeInstall-x64.exe`.
   Do not reach for `DeploymentManager.GetStatus()` to detect the condition: it
   terminates the process (exit code 9, silently) in exactly that state.

   So the honest sequence is: make the code change, then test on a machine with a
   complete Windows App Runtime 2.4 installation. Until then, notifications are
   correctly reported as undelivered and everything else works — which is the
   behaviour ARCHITECTURE.md asks for.

2. **Linux and macOS artefacts are unverified end to end.** No `.deb`, `.rpm`,
   AppImage or `.app` built by these scripts has been installed or run.
   ARCHITECTURE.md's risk 1 already says the native integrations behind them are
   unverified; this adds the packaging layer to that list.

   macOS is **behind** Linux, not ahead of it, which an earlier version of this
   file had the wrong way round. The `macos-bundle` job failed every one of its
   first eleven runs at the `codesign` bundle seal, so unlike the `.deb` and the
   `.rpm` no macOS artefact has ever been produced at all. What is unproven is
   therefore everything: that the script completes, that the DMG mounts, that the
   bundle seals, and that anybody has copied it to a Mac, double-clicked it and
   seen a menu bar icon. "The bundle seal" above is the change that is meant to
   fix the first three; none of it has run yet.

3. **No update mechanism outside Windows.** Velopack supports macOS, but its
   updater replaces the `.app` in place, and an unsigned, un-notarised replacement
   is quarantined and killed. Wiring it up is blocked on the Developer ID secrets
   above, not on effort. Linux has no updater and is not expected to grow one:
   AppImage users can use AppImageUpdate if the release ever embeds update
   information, and `.deb`/`.rpm` users would need a repository.

4. **`win-arm64` is not built.** Native AOT cannot cross-compile, so it needs an
   arm64 Windows runner. The build script accepts `-Runtime win-arm64` and the
   Velopack channel layout has room for it; the release workflow does not have a
   job for it.

5. **The Velopack pin lives in the wrong file.** `src/Altim.App/Altim.App.csproj`
   references it with `VersionOverride="1.2.0"` because central package management
   is in force and `Directory.Packages.props` was not ours to edit. Move it to
   `<PackageVersion Include="Velopack" Version="1.2.0" />` and drop the attribute.

6. **`packaging/linux/build-linux.sh`, `packaging/macos/build-macos.sh` and
   `packaging/macos/verify-bundle.sh` need the executable bit in git** (`git update-index --chmod=+x`). The release workflow
   invokes them through `bash` so a missing bit cannot break a release, but a
   maintainer running `./packaging/...` locally will hit it.
