#!/usr/bin/env bash
#
# Reads a built Altim.app back and checks the things that decide whether it works.
#
#   ./packaging/macos/verify-bundle.sh dist/.macos-stage/arm64/Altim.app 0.1.0 arm64
#   ./packaging/macos/verify-bundle.sh dist/.macos-stage/x64/Altim.app 0.1.0 x64 --allow-unsealed
#
# Arguments: the bundle, the CFBundleShortVersionString it should carry, and the
# architecture it was built for (arm64 or x64). The third is optional; without it
# the architecture is reported and not judged. There is no universal option any
# more: see packaging/README.md, "Why there is no universal build".
#
# --allow-unsealed downgrades two checks from failures to warnings: that the
# bundle carries a code signature seal, and that Contents/MacOS holds nothing but
# Mach-O. It exists for one reason and it is named so that nobody can pass it by
# accident. Read "The bundle seal" in packaging/README.md before using it.
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
#   - a file in Contents/MacOS that is not Mach-O cannot be sealed, because the
#     default resource rules make that directory a nested-code location
#   - a Mach-O built for the wrong architecture produces a .app that will not run
#
# It is a separate script rather than steps in the workflow so that someone with a
# real Mac can run exactly the same checks against a downloaded artefact.

set -euo pipefail

# Parsed without an array: macOS ships bash 3.2, where an empty array under
# set -u is a trap that only fires when someone runs the script with no options.
ALLOW_UNSEALED=0
APP=""
EXPECTED_VERSION=""
ARCH=""
positional=0

while [ $# -gt 0 ]; do
    case "$1" in
        --allow-unsealed) ALLOW_UNSEALED=1 ;;
        -h|--help) sed -n '2,38p' "$0"; exit 0 ;;
        --*) echo "unknown option: $1" >&2; exit 2 ;;
        *)
            case "$positional" in
                0) APP="$1" ;;
                1) EXPECTED_VERSION="$1" ;;
                2) ARCH="$1" ;;
                *) echo "unexpected argument: $1" >&2; exit 2 ;;
            esac
            positional=$((positional + 1))
            ;;
    esac
    shift
done

[ -n "$APP" ] || { echo "usage: $0 <path to Altim.app> [expected version] [arm64|x64] [--allow-unsealed]" >&2; exit 2; }

if [ "$ARCH" = "universal" ]; then
    echo "there is no universal bundle any more; pass arm64 or x64" >&2
    exit 2
fi

failures=0

ok()   { printf '  \033[32mok\033[0m   %s\n' "$*"; }
bad()  { printf '  \033[31mBAD\033[0m  %s\n' "$*"; failures=$((failures + 1)); }
warn() { printf '  \033[33mWARN\033[0m %s\n' "$*"; }
note() { printf '  --   %s\n' "$*"; }
step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

# A check that --allow-unsealed turns into a warning. Nothing else may use this.
seal_fault() {
    if [ "$ALLOW_UNSEALED" -eq 1 ]; then
        warn "$*"
    else
        bad "$*"
    fi
}

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

# The bundle is the only copy a recipient gets: a DMG carries no package metadata
# and there is no /usr/share/doc on this platform. If these two are not in it, the
# notices for the .NET runtime, Skia, HarfBuzz, SQLite and the Inter typeface did
# not reach anybody.
for doc in LICENSE THIRD-PARTY-NOTICES.md; do
    if [ -s "$APP/Contents/Resources/$doc" ]; then
        ok "Contents/Resources/$doc is present and not empty"
    else
        bad "Contents/Resources/$doc is missing or empty"
    fi
done

if [ -f "$APP/Contents/Resources/Altim.icns" ]; then
    case "$(file -b "$APP/Contents/Resources/Altim.icns")" in
        *icon*) ok "Altim.icns is an icon file" ;;
        *)      bad "Altim.icns is present but is not an icon file" ;;
    esac
else
    bad "Contents/Resources/Altim.icns is missing"
fi

# The three places MacOSTrayAssets.DiscoverAssetDirectory looks, in its order.
# Finding none of them is a status item with no image. The third is a fallback the
# source keeps for an unbundled run out of a build output; packaging puts the
# assets in the second, because anything under Contents/MacOS is nested code that
# the bundle seal will then reject.
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
    if [ "$found_template" = "$APP/Contents/MacOS/assets/icons" ]; then
        note "they are under Contents/MacOS, which is a nested-code location:"
        note "the bundle seal cannot succeed while they are there."
    fi
else
    bad "no altim-template-*.png anywhere DiscoverAssetDirectory looks: the menu bar item would have no image"
fi

# ---------------------------------------------------------------------------
# Nested code
# ---------------------------------------------------------------------------
# codesign's default resource rules mark Contents/MacOS as a nested-code location,
# so every file in it apart from the main executable has to be an independently
# signed code object. A managed .dll is a PE file and can never carry a Mach-O
# signature; neither can a .png or a .json. This is the check that says in advance
# whether the seal below can possibly work, and which file is in the way.
step "Nested code in Contents/MacOS"

strays=0
nested=0
while IFS= read -r -d '' file; do
    [ "$file" = "$EXECUTABLE" ] && continue
    case "$(file -b "$file")" in
        *Mach-O*) nested=$((nested + 1)) ;;
        *)
            seal_fault "not Mach-O, so the bundle cannot be sealed: ${file#"$APP/Contents/MacOS"/}"
            strays=$((strays + 1))
            ;;
    esac
done < <(find "$APP/Contents/MacOS" -type f -print0)

if [ "$strays" -eq 0 ]; then
    ok "Contents/MacOS holds only Mach-O: the main executable and $nested nested binaries"
else
    note "$strays file(s) in Contents/MacOS are not Mach-O, beside $nested that are."
    note "A single-file publish is what keeps managed assemblies out of this"
    note "directory; assets belong in Contents/Resources. See build-macos.sh."
fi

# ---------------------------------------------------------------------------
# Architecture
# ---------------------------------------------------------------------------
step "Architecture"

if [ -f "$EXECUTABLE" ]; then
    archs="$(lipo -archs "$EXECUTABLE" 2>/dev/null || echo '')"
    note "Contents/MacOS/Altim carries: ${archs:-nothing lipo could read}"

    case "$ARCH" in
        arm64) wanted="arm64" ;;
        x64)   wanted="x86_64" ;;
        "")    wanted="" ; note "no architecture given, so the slices are reported and not judged" ;;
        *)     wanted="" ; note "unknown architecture '$ARCH', not checking slices" ;;
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
# Two separate things, and the difference matters:
#
#   - the main executable carrying a valid signature of its own is what lets the
#     kernel execute it at all on Apple silicon. It is never optional.
#   - the BUNDLE being sealed is what writes _CodeSignature/CodeResources, which
#     is what tells an intact bundle from a tampered one and what SMAppService
#     requires before it will register start at login.
#
# The kind of signature, ad-hoc or Developer ID, is reported and not judged: that
# depends on whether a secret was available.
step "Code signature"

# Not --strict and not --deep. See the note beside the signing step in
# build-macos.sh; the nested-code check above is what covers the same ground.
if codesign --verify "$APP" 2>/dev/null; then
    authority="$(codesign -dvv "$APP" 2>&1 | sed -n 's/^Authority=//p' | head -n 1)"
    if [ -n "$authority" ]; then
        ok "sealed and valid, authority: $authority"
    else
        ok "sealed and valid, ad-hoc (no certificate authority)"
        note "Ad-hoc is enough to run locally and is not enough to distribute:"
        note "Gatekeeper refuses a downloaded copy and notarisation is impossible."
        note "packaging/README.md lists the secrets that change this."
    fi
else
    seal_fault "the bundle is NOT sealed: there is no _CodeSignature/CodeResources, so nothing can tell an intact bundle from a tampered one, SMAppService will answer kSMErrorInvalidSignature and start at login cannot work, and Gatekeeper will refuse a downloaded copy"

    # An unsealed bundle may still launch, and that is the whole value of shipping
    # one, so the weaker claim is checked rather than assumed. This one is a
    # failure either way: without it there is nothing worth uploading.
    if [ -f "$EXECUTABLE" ] && codesign --verify "$EXECUTABLE" 2>/dev/null; then
        ok "the main executable does carry a valid signature of its own, so it can be executed"
    else
        bad "the main executable carries no valid signature either: on Apple silicon the kernel will refuse to execute it"
    fi
fi
codesign -dvv "$APP" 2>&1 | sed 's/^/       /' || true

# ---------------------------------------------------------------------------
step "Result"
if [ "$failures" -eq 0 ]; then
    if [ "$ALLOW_UNSEALED" -eq 1 ]; then
        echo "  the bundle passed every check that --allow-unsealed still enforces"
        echo "  This is NOT the same as passing. Read the WARN lines above."
    else
        echo "  the bundle passed every check"
    fi
else
    echo "  $failures check(s) failed" >&2
    exit 1
fi
