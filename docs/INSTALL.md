# Installing Altim

Downloads are on the [releases page](https://github.com/mpge/altim/releases). Every release
carries a `SHA256SUMS.txt`; checking against it is worthwhile because none of these artefacts is
signed with a paid certificate yet.

| Platform | Download | Notes |
|---|---|---|
| Windows 10 1809 or later, x64 | `Altim-win-Setup.exe` | Installs per user, no administrator prompt. SmartScreen will warn that the publisher is unknown: choose **More info**, then **Run anyway**. |
| Windows, no installer | `Altim-win-Portable.zip` | Unpack and run `Altim.exe`. No updates, no Start menu entry. |
| macOS 12 or later, Apple silicon | `Altim-<version>-arm64.dmg` | Ad-hoc signed, not notarised, so Gatekeeper refuses it on first open: **right-click the app, choose Open**, then Open again. |
| Debian, Ubuntu | `altim_<version>_amd64.deb` | `sudo apt install ./altim_<version>_amd64.deb` |
| Fedora, RHEL | `altim-<version>.x86_64.rpm` | `sudo dnf install ./altim-<version>.x86_64.rpm` |
| Any Linux desktop | `Altim-<version>-x86_64.AppImage` | `chmod +x` it and run it. |

**Altim lives in the tray**, not in a window. On Windows 11 a newly installed tray icon goes into
the overflow behind the chevron until you drag it out, so the first launch opens the dashboard once
to show you where things are. After that it starts quietly unless you turn **Start minimised** off.

Nothing here needs administrator rights, and Altim makes no network requests of its own — see
[PRIVACY.md](../PRIVACY.md).

**Not yet available:** an Intel macOS build (it builds in CI but is not published yet), `win-arm64`,
and automatic updates on any platform. Upgrading today means downloading the new release.

## Platform behaviour

| | Windows | macOS | Linux |
|---|---|---|---|
| Presence | system tray, own `Shell_NotifyIcon` host | menu bar, `NSStatusItem` | StatusNotifierItem |
| Panel anchor | exact icon rectangle, else cursor, else screen corner | status item frame | screen corner (the protocol carries no geometry) |
| Notifications | Windows App SDK | `UNUserNotificationCenter` | `org.freedesktop.Notifications` |
| Start at login | `Run` key, state read from `StartupApproved` | `SMAppService` | XDG autostart |

Where a desktop cannot do something, Altim degrades visibly rather than pretending.

The macOS column is written from Apple's headers and has been compiled and exercised as far as a
runner can reach, which is not far: CI constructs the whole macOS service stack on a real Mac and
proves the Objective-C runtime is reached, the dynamic callback class registers, and the guards
that keep an unbundled process away from `UNUserNotificationCenter` and `SMAppService` actually
fire. A runner has no menu bar to put a status item in, so everything in the first row remains
unverified. The same is true of Linux, for the same reason.

## Packaging

| Platform | Artefacts | State |
|---|---|---|
| Windows | `Setup.exe`, portable zip, delta updates | built, installed, run and uninstalled on Windows 11 |
| Linux | AppImage, `.deb`, `.rpm` | packages built and their metadata checked; **never installed or run** |
| macOS | `.app` and DMG, one per architecture | **no artefact has ever been produced**: the bundle job failed every one of its first eleven runs at the code signature step |

The macOS row is the one to read carefully, because an earlier version of it claimed more than was
true. Nothing macOS has ever been packaged successfully. `codesign` treats `Contents/MacOS` as a
nested-code location, a normal .NET publish fills it with managed assemblies, and sealing the
bundle therefore failed on every run. The fix is a single-file publish, which leaves that
directory holding the host and four dylibs and nothing else; it has been verified as far as a
Windows machine reaches and has not yet run on a macOS runner. Until it does, the bundle job
uploads an explicitly unsealed bundle rather than failing, so that something exists to try, and
says so on the run page. An unsealed bundle has no start at login and is refused outright by
Gatekeeper. `packaging/README.md`, "The bundle seal", is the full account.

Scripts live in [`packaging/`](../packaging/), with a README covering how to produce each artefact by
hand and what a maintainer needs in order to sign them. A version tag builds all three through
`.github/workflows/release.yml` and attaches them to a draft release.

Nothing is signed yet. Windows installers are unsigned until a certificate exists, and the macOS
signing and notarisation steps skip cleanly when the Apple secrets are absent, so the workflow runs
end to end without them and produces an unsigned bundle.

The `.deb` dependency list is **hand-maintained on purpose**: only one of the nine X11 and
fontconfig libraries Avalonia needs is visible to automatic dependency detection, because the rest
are loaded by name at runtime. A generated list under-declares, and the package then installs
cleanly and fails to start.
