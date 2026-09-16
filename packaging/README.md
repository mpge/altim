# Packaging

Everything needed to turn a build of Altim into something a person can install, on
all three platforms, by hand or from CI.

| Platform | Artefacts | Tool | Updates |
|---|---|---|---|
| Windows | `Altim-win-Setup.exe`, `Altim-win-Portable.zip`, `*.nupkg` + `RELEASES` | [Velopack](https://velopack.io) 1.2.0 | in-app, delta |
| Linux | `Altim-<version>-x86_64.AppImage`, `altim_<version>_amd64.deb`, `altim-<version>.x86_64.rpm` | [appimagetool](https://github.com/AppImage/appimagetool) 1.9.1, [nfpm](https://nfpm.goreleaser.com) 2.47.0 | none |
| macOS | `Altim-<version>-universal.dmg`, `Altim.app` (zipped) | `lipo`, `iconutil`, `hdiutil` | none |

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
| macOS: anything at all | **never executed** |
| `release.yml`, `build.yml` | `actionlint` clean |
| the three shell scripts | `shellcheck --severity=style` clean |

Lint-clean is not the same as correct. Nothing on the macOS path, and nothing that
installs or launches on Linux, has been observed working.

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

---


## Package size

The Windows payload is **89.5MB** (`Altim.exe` alone is 28.25MB of Native AOT), and
the installer is **44.4MB** compressed.

**40.3MB of that payload is Windows App SDK machinery Altim never touches**:

| File | Size | Comes from |
|---|---|---|
| `onnxruntime.dll` | 20.66MB | `Microsoft.Windows.AI.MachineLearning`, via `Microsoft.WindowsAppSDK.ML` |
| `DirectML.dll` | 17.83MB | as above |
| `Microsoft.Windows.AI.MachineLearning.dll` | 0.86MB | as above |
| `Microsoft.Web.WebView2.Core.dll`, `WebView2Loader.dll` | 0.91MB | `Microsoft.Web.WebView2`, via `Microsoft.WindowsAppSDK.WinUI` |

They arrive because `Directory.Packages.props` pins the `Microsoft.WindowsAppSDK`
**metapackage**, which depends on `.ML`, `.AI`, `.WinUI`, `.Widgets` and `.Search`.
Altim uses one namespace from one of those components,
`Microsoft.Windows.AppNotifications`, which lives in `.Foundation`.

Packaging deliberately does **not** strip these with an exclude rule: what
`dotnet publish` produces and what the installer contains should be the same set of
files, or a bug that only reproduces from the installer becomes possible. The fix
belongs one level up, in the package references:

```diff
- <PackageVersion Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
+ <PackageVersion Include="Microsoft.WindowsAppSDK.Foundation" Version="2.3.9" />
+ <PackageVersion Include="Microsoft.WindowsAppSDK.Runtime" Version="2.4.0" />
```

with the matching change in `src/Altim.Platform.Windows/Altim.Platform.Windows.csproj`.
Neither `.Foundation` nor `.Runtime` depends on `.ML`, `.AI` or `.WinUI`, so the
40.3MB goes away. Verify with a publish and a file listing before believing it.

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
./packaging/macos/build-macos.sh --version 0.1.0                # universal
./packaging/macos/build-macos.sh --version 0.1.0 --arch arm64
```

Output lands in `dist/macos/`: a DMG, and the bundle again as a `ditto` archive.

**Universal needs two publishes.** .NET cannot emit a universal binary — a runtime
identifier names exactly one architecture — so `osx-arm64` and `osx-x64` are
published separately and every Mach-O file present in both is merged with `lipo`.
Managed assemblies are architecture-neutral and are taken from the arm64 tree
unchanged. A file that exists in only one of the two publishes is treated as an
error, not skipped: it would produce a bundle that works on one Mac and not the
other.

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

Nothing is signed today. Every signing step is skipped cleanly when its credential
is absent, and the release notes the workflow writes say so on the release page,
so an unsigned release is obvious rather than silent.

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

2. **"Start at login" from inside an AppImage writes a dead entry.**
   `LinuxAutoStartService` takes its executable path from
   `Environment.ProcessPath`, which inside an AppImage is
   `/tmp/.mount_Altim<random>/usr/bin/Altim` — a path that does not survive a
   reboot. The AppImage runtime exports `APPIMAGE` with the real path of the
   `.AppImage` file; the constructor already accepts an `executablePath` argument,
   so the composition root passing `Environment.GetEnvironmentVariable("APPIMAGE")`
   when it is set would close this.

3. **Linux and macOS artefacts are unverified end to end.** The scripts are
   lint-clean and the workflow is `actionlint`-clean, but no `.deb`, `.rpm`,
   AppImage or `.app` built by them has been installed or run. ARCHITECTURE.md's
   risk 2 already says the native integrations behind them are unverified; this
   adds the packaging layer to that list.

4. **No update mechanism outside Windows.** Velopack supports macOS, but its
   updater replaces the `.app` in place, and an unsigned, un-notarised replacement
   is quarantined and killed. Wiring it up is blocked on the Developer ID secrets
   above, not on effort. Linux has no updater and is not expected to grow one:
   AppImage users can use AppImageUpdate if the release ever embeds update
   information, and `.deb`/`.rpm` users would need a repository.

5. **`win-arm64` is not built.** Native AOT cannot cross-compile, so it needs an
   arm64 Windows runner. The build script accepts `-Runtime win-arm64` and the
   Velopack channel layout has room for it; the release workflow does not have a
   job for it.

6. **The Velopack pin lives in the wrong file.** `src/Altim.App/Altim.App.csproj`
   references it with `VersionOverride="1.2.0"` because central package management
   is in force and `Directory.Packages.props` was not ours to edit. Move it to
   `<PackageVersion Include="Velopack" Version="1.2.0" />` and drop the attribute.

7. **`packaging/linux/build-linux.sh` and `packaging/macos/build-macos.sh` need the
   executable bit in git** (`git update-index --chmod=+x`). The release workflow
   invokes them through `bash` so a missing bit cannot break a release, but a
   maintainer running `./packaging/...` locally will hit it.
