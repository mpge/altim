#!/usr/bin/env bash
#
# Reads a built Altim.app back and checks the things that decide whether it works.
#
#   ./packaging/macos/verify-bundle.sh dist/.macos-stage/Altim.app 0.1.0 universal
#
# Arguments: the bundle, the CFBundleShortVersionString it should carry, and the
# architecture it was built for (universal, arm64 or x64). The third is optional
# and defaults to universal.
#
# Why this exists. build-macos.sh finishing without an error says the commands ran,
# not that the result is an application. Every check below is something that would
# leave a bundle that assembles cleanly and then misbehaves on a Mac in a way that
# is invisible until someone has one:
#
#   - a sed substitution that silently did not match leaves @SHORT_VERSION@ in the
#     plist, and macOS refuses to launch a bundle whose version is not a number
#   - a bundle with no CFBundleIdentifier has no bundle identity, which is exactly
#     what MacOSNotificationService and MacOSAutoStartService check for before they
#     touch UNUserNotificationCenter or SMAppService; without it Altim silently has
#     no notifications and no start-at-login
#   - LSUIElement going missing turns a menu bar utility into an app with a Dock
#     tile and an application menu it has no windows for
#   - the menu bar template assets not reaching the bundle gives a status item with
#     no image, which looks like the app failed to start
#   - a Mach-O with one architecture slice produces a .app that runs on one kind of
#     Mac and dies on the other
#
# It is a separate script rather than steps in the workflow so that someone with a
# real Mac can run exactly the same checks against a downloaded artefact.

set -euo pipefail

APP="${1:-}"
EXPECTED_VERSION="${2:-}"
ARCH="${3:-universal}"

[ -n "$APP" ] || { echo "usage: $0 <path to Altim.app> [expected version] [universal|arm64|x64]" >&2; exit 2; }

failures=0

ok()   { printf '  \033[32mok\033[0m   %s\n' "$*"; }
bad()  { printf '  \033[31mBAD\033[0m  %s\n' "$*"; failures=$((failures + 1)); }
note() { printf '  --   %s\n' "$*"; }
step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

plist_value() {
    # plutil exits non-zero for a key that is not there, which is a failure to
    # report rather than one to abort on.
    plutil -extract "$1" raw -o - "$APP/Contents/Info.plist" 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# Shape
# ---------------------------------------------------------------------------
step "Bundle layout"

[ -d "$APP" ] || { echo "no bundle at $APP" >&2; exit 1; }

EXECUTABLE="$APP/Contents/MacOS/Altim"
if [ -f "$EXECUTABLE" ]; then ok "Contents/MacOS/Altim is present"; else bad "Contents/MacOS/Altim is missing"; fi
if [ -x "$EXECUTABLE" ]; then ok "the executable bit is set"; else bad "the executable bit is not set"; fi
if [ -f "$APP/Contents/Info.plist" ]; then ok "Contents/Info.plist is present"; else bad "Contents/Info.plist is missing"; fi
if [ -f "$APP/Contents/PkgInfo" ]; then ok "Contents/PkgInfo is present"; else bad "Contents/PkgInfo is missing"; fi

# ---------------------------------------------------------------------------
# Info.plist
# ---------------------------------------------------------------------------
step "Info.plist"

if plutil -lint "$APP/Contents/Info.plist" >/dev/null 2>&1; then
    ok "the plist parses"
else
    bad "the plist does not parse"
fi

if grep -q '@[A-Z_]*@' "$APP/Contents/Info.plist"; then
    bad "an unsubstituted @PLACEHOLDER@ is still in the plist"
else
    ok "no template placeholder survived the substitution"
fi

short_version="$(plist_value CFBundleShortVersionString)"
if [ -n "$EXPECTED_VERSION" ]; then
    if [ "$short_version" = "$EXPECTED_VERSION" ]; then
        ok "CFBundleShortVersionString is $short_version"
    else
        bad "CFBundleShortVersionString is '$short_version', expected '$EXPECTED_VERSION'"
    fi
else
    note "CFBundleShortVersionString is '$short_version' (nothing to compare it with)"
fi

# Three integers at most. Anything else and LaunchServices rejects the bundle,
# which is why build-macos.sh strips a prerelease suffix before it substitutes.
if printf '%s' "$short_version" | grep -Eq '^[0-9]+(\.[0-9]+){0,2}$'; then
    ok "CFBundleShortVersionString is a version LaunchServices accepts"
else
    bad "CFBundleShortVersionString '$short_version' is not one to three integers"
fi

identifier="$(plist_value CFBundleIdentifier)"
if [ -n "$identifier" ]; then
    ok "CFBundleIdentifier is $identifier"
else
    bad "CFBundleIdentifier is empty: the bundle has no identity, so notifications and start at login will both report themselves unavailable"
fi

executable_key="$(plist_value CFBundleExecutable)"
if [ "$executable_key" = "Altim" ]; then
    ok "CFBundleExecutable names the binary that is actually there"
else
    bad "CFBundleExecutable is '$executable_key' but the binary is Contents/MacOS/Altim"
fi

if [ "$(plist_value LSUIElement)" = "true" ]; then
    ok "LSUIElement is set: no Dock tile"
else
    bad "LSUIElement is not true: Altim would launch with a Dock tile and an application menu"
fi

minimum="$(plist_value LSMinimumSystemVersion)"
if [ -n "$minimum" ]; then
    ok "LSMinimumSystemVersion is $minimum"
else
    bad "LSMinimumSystemVersion is not set"
fi

if [ "$(plist_value CFBundlePackageType)" = "APPL" ]; then
    ok "CFBundlePackageType is APPL"
else
    bad "CFBundlePackageType is not APPL"
fi

# ---------------------------------------------------------------------------
# Resources
# ---------------------------------------------------------------------------
step "Resources"

if [ -f "$APP/Contents/Resources/Altim.icns" ]; then
    case "$(file -b "$APP/Contents/Resources/Altim.icns")" in
        *icon*) ok "Altim.icns is an icon file" ;;
        *)      bad "Altim.icns is present but is not an icon file" ;;
    esac
else
    bad "Contents/Resources/Altim.icns is missing"
fi

# The three places MacOSTrayAssets.DiscoverAssetDirectory looks, in its order.
# Finding none of them is a status item with no image.
found_template=""
for dir in "$APP/Contents/Resources" \
           "$APP/Contents/Resources/assets/icons" \
           "$APP/Contents/MacOS/assets/icons"; do
    for candidate in "$dir"/altim-template-*.png; do
        [ -f "$candidate" ] || continue
        found_template="$dir"
        break
    done
    [ -z "$found_template" ] || break
done
if [ -n "$found_template" ]; then
    ok "menu bar template assets are in ${found_template#"$APP"/}"
else
    bad "no altim-template-*.png anywhere DiscoverAssetDirectory looks: the menu bar item would have no image"
fi

# ---------------------------------------------------------------------------
# Architecture
# ---------------------------------------------------------------------------
step "Architecture"

if [ -f "$EXECUTABLE" ]; then
    archs="$(lipo -archs "$EXECUTABLE" 2>/dev/null || echo '')"
    note "Contents/MacOS/Altim carries: ${archs:-nothing lipo could read}"

    case "$ARCH" in
        universal) wanted="arm64 x86_64" ;;
        arm64)     wanted="arm64" ;;
        x64)       wanted="x86_64" ;;
        *)         wanted="" ; note "unknown --arch '$ARCH', not checking slices" ;;
    esac

    for want in $wanted; do
        case " $archs " in
            *" $want "*) ok "the $want slice is present" ;;
            *)           bad "the $want slice is missing" ;;
        esac
    done
fi

# ---------------------------------------------------------------------------
# Code signature
# ---------------------------------------------------------------------------
# A signature is required; the KIND of signature is only reported. build-macos.sh
# signs with a Developer ID when one is configured and ad-hoc when one is not, and
# neither case may produce a bundle with no seal at all: macOS 11 and later refuse
# to execute an unsigned Mach-O on Apple silicon, and SMAppService refuses to
# register an app that is not code signed. Which of the two it is depends on
# whether a secret was available, so that is printed rather than judged.
step "Code signature"

if codesign --verify --strict "$APP" 2>/dev/null; then
    authority="$(codesign -dvv "$APP" 2>&1 | sed -n 's/^Authority=//p' | head -n 1)"
    if [ -n "$authority" ]; then
        ok "signed and valid, authority: $authority"
    else
        ok "signed and valid, ad-hoc (no certificate authority)"
        note "Ad-hoc is enough to run locally and is not enough to distribute:"
        note "Gatekeeper refuses a downloaded copy and notarisation is impossible."
        note "packaging/README.md lists the secrets that change this."
    fi
else
    bad "no valid code signature: on Apple silicon this bundle cannot be executed at all, and SMAppService would refuse to register it"
fi
codesign -dvv "$APP" 2>&1 | sed 's/^/       /' || true

# ---------------------------------------------------------------------------
step "Result"
if [ "$failures" -eq 0 ]; then
    echo "  the bundle passed every check"
else
    echo "  $failures check(s) failed" >&2
    exit 1
fi
