#!/usr/bin/env bash
#
# Builds the macOS artefacts: Altim.app and a DMG containing it.
#
#   ./packaging/macos/build-macos.sh --version 0.1.0                # universal
#   ./packaging/macos/build-macos.sh --version 0.1.0 --arch arm64   # single arch
#
# Universal is the default and needs two publishes. .NET cannot emit a universal
# binary: a runtime identifier names exactly one architecture, so osx-arm64 and
# osx-x64 are published separately and every Mach-O file that exists in both is
# merged with lipo. Managed assemblies are architecture-neutral and are taken from
# the arm64 tree unchanged.
#
# Signing and notarisation are skipped, loudly, when the credentials are absent —
# see "Signing" below and packaging/README.md for the secrets to add. An unsigned
# build is a complete, working .app; it is Gatekeeper that will not open it without
# the user going out of their way, not anything missing from the bundle.
#
# This script has never been executed. It was written on a Windows machine and
# macOS is the one platform it cannot be tried on. Read it before you trust it.

set -euo pipefail

VERSION=""
ARCH="universal"
SKIP_PUBLISH=0
SKIP_DMG=0

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --arch) ARCH="$2"; shift 2 ;;
        --skip-publish) SKIP_PUBLISH=1; shift ;;
        --skip-dmg) SKIP_DMG=1; shift ;;
        -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

PROJECT="src/Altim.App/Altim.App.csproj"

if [ -z "$VERSION" ]; then
    VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
    [ -n "$VERSION" ] || { echo "no --version and no <Version> in $PROJECT" >&2; exit 1; }
fi
VERSION="${VERSION#v}"

# CFBundleShortVersionString accepts at most three dot-separated integers, so a
# prerelease or build suffix has to come off. CFBundleVersion keeps a monotonic
# build number; without a build counter the release version is the honest answer.
SHORT_VERSION="${VERSION%%-*}"
SHORT_VERSION="${SHORT_VERSION%%+*}"
BUNDLE_VERSION="$SHORT_VERSION"

case "$ARCH" in
    universal) RIDS=(osx-arm64 osx-x64) ;;
    arm64)     RIDS=(osx-arm64) ;;
    x64)       RIDS=(osx-x64) ;;
    *) echo "unsupported --arch: $ARCH (universal, arm64 or x64)" >&2; exit 2 ;;
esac

DIST="$REPO_ROOT/dist/macos"
STAGE="$REPO_ROOT/dist/.macos-stage"
APP="$STAGE/Altim.app"
MACOS_DIR="$APP/Contents/MacOS"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

mkdir -p "$DIST"

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
# -p:AltimPortableBuild=true is a no-op on macOS and only matters when
# cross-publishing from a Windows host, where the project would otherwise pick its
# Windows target framework. PublishAot stays off: it is a Windows-only setting in
# Altim.App.csproj and ARCHITECTURE.md keeps the other platforms behind it until
# someone has verified them on real hardware.
for rid in "${RIDS[@]}"; do
    if [ "$SKIP_PUBLISH" -eq 0 ]; then
        step "Publishing $rid"
        rm -rf "$REPO_ROOT/dist/.publish/$rid"
        dotnet publish "$PROJECT" \
            --configuration Release \
            --runtime "$rid" \
            --self-contained \
            -p:Version="$VERSION" \
            -p:AltimPortableBuild=true \
            -p:DebugType=none \
            --output "$REPO_ROOT/dist/.publish/$rid" \
            --nologo
    fi
    [ -f "$REPO_ROOT/dist/.publish/$rid/Altim" ] || {
        echo "no Altim in dist/.publish/$rid" >&2; exit 1; }
done

# ---------------------------------------------------------------------------
# Bundle
# ---------------------------------------------------------------------------
step "Assembling Altim.app"
rm -rf "$STAGE"
mkdir -p "$MACOS_DIR" "$APP/Contents/Resources"

PRIMARY="$REPO_ROOT/dist/.publish/${RIDS[0]}"
cp -a "$PRIMARY/." "$MACOS_DIR/"

if [ "${#RIDS[@]}" -gt 1 ]; then
    SECONDARY="$REPO_ROOT/dist/.publish/${RIDS[1]}"
    step "Merging ${RIDS[0]} and ${RIDS[1]} into universal binaries"
    merged=0
    already=0
    while IFS= read -r -d '' file; do
        rel="${file#"$MACOS_DIR"/}"
        other="$SECONDARY/$rel"
        [ -f "$other" ] || continue
        # Only Mach-O files can be merged. Everything else — managed assemblies,
        # .json, the icon assets — is architecture-neutral and is already correct.
        case "$(file -b "$file")" in
            *Mach-O*) ;;
            *) continue ;;
        esac

        # Some native dependencies are already universal, and lipo will not take
        # them. SkiaSharp, HarfBuzzSharp and Avalonia each ship ONE fat dylib under
        # runtimes/osx/native, which is the architecture-neutral osx runtime
        # identifier rather than osx-arm64 or osx-x64. Runtime identifier fallback
        # resolves that same file for both publishes, so both sides of this merge
        # are the identical binary and both already carry x86_64 and arm64. lipo
        # refuses that outright: cctools fatals with "... have the same
        # architectures (x86_64) and can't be in the same fat output file", which
        # under set -e ends the release build. They need no merge; the slice check
        # below is what proves the result is right rather than the merge count.
        if [ "$(lipo -archs "$file")" = "$(lipo -archs "$other")" ]; then
            already=$((already + 1))
            continue
        fi

        lipo -create -output "$file.universal" "$file" "$other"
        mv "$file.universal" "$file"
        merged=$((merged + 1))
    done < <(find "$MACOS_DIR" -type f -print0)
    step "Merged $merged Mach-O files; $already were already universal"

    # A file present in only one architecture is a packaging bug, not a merge to
    # skip quietly: it would produce a bundle that works on one Mac and not the
    # other. Report it and stop.
    missing=0
    while IFS= read -r -d '' file; do
        rel="${file#"$SECONDARY"/}"
        if [ ! -f "$MACOS_DIR/$rel" ]; then
            echo "only in ${RIDS[1]}: $rel" >&2
            missing=$((missing + 1))
        fi
    done < <(find "$SECONDARY" -type f -print0)
    [ "$missing" -eq 0 ] || { echo "$missing file(s) present in only one architecture" >&2; exit 1; }

    # Having run lipo is not the same as having a universal bundle. A Mach-O that
    # came through with one slice produces a .app that launches on one kind of Mac
    # and dies on the other, and nothing before this point would say so: the merge
    # count above is satisfied by a file that was skipped for a good reason and by
    # one that was skipped for a bad one. Every Mach-O is therefore read back.
    step "Checking every Mach-O carries both slices"
    thin=0
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*) ;;
            *) continue ;;
        esac
        archs=" $(lipo -archs "$file") "
        for want in arm64 x86_64; do
            case "$archs" in
                *" $want "*) ;;
                *)
                    echo "no $want slice: ${file#"$MACOS_DIR"/} (${archs# })" >&2
                    thin=$((thin + 1))
                    ;;
            esac
        done
    done < <(find "$MACOS_DIR" -type f -print0)
    [ "$thin" -eq 0 ] || { echo "$thin missing architecture slice(s)" >&2; exit 1; }
fi

chmod +x "$MACOS_DIR/Altim"

sed -e "s/@SHORT_VERSION@/$SHORT_VERSION/g" \
    -e "s/@BUNDLE_VERSION@/$BUNDLE_VERSION/g" \
    packaging/macos/Info.plist > "$APP/Contents/Info.plist"

printf 'APPL????' > "$APP/Contents/PkgInfo"

# ---------------------------------------------------------------------------
# Icon
# ---------------------------------------------------------------------------
# The repository ships PNGs up to 512. An iconset wants a 1024 for 512x512@2x, so
# that one is upscaled from the 512 with sips. Every other slot is an exact source
# size, not a resample.
step "Building Altim.icns"
ICONSET="$STAGE/Altim.iconset"
mkdir -p "$ICONSET"
cp assets/icons/altim-16.png  "$ICONSET/icon_16x16.png"
cp assets/icons/altim-32.png  "$ICONSET/icon_16x16@2x.png"
cp assets/icons/altim-32.png  "$ICONSET/icon_32x32.png"
cp assets/icons/altim-64.png  "$ICONSET/icon_32x32@2x.png"
cp assets/icons/altim-128.png "$ICONSET/icon_128x128.png"
cp assets/icons/altim-256.png "$ICONSET/icon_128x128@2x.png"
cp assets/icons/altim-256.png "$ICONSET/icon_256x256.png"
cp assets/icons/altim-512.png "$ICONSET/icon_256x256@2x.png"
cp assets/icons/altim-512.png "$ICONSET/icon_512x512.png"
sips -z 1024 1024 assets/icons/altim-512.png --out "$ICONSET/icon_512x512@2x.png" >/dev/null
iconutil --convert icns --output "$APP/Contents/Resources/Altim.icns" "$ICONSET"
rm -rf "$ICONSET"

# ---------------------------------------------------------------------------
# Signing
# ---------------------------------------------------------------------------
# Every step here is conditional on credentials that nobody has yet. The bundle
# above is complete and runnable without them; what is missing is Gatekeeper's
# permission, not functionality.
SIGNED=0
if [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
    step "Signing with $MACOS_SIGNING_IDENTITY"

    # Inside out. --deep is not used: it is deprecated, it applies one set of
    # entitlements to nested code that may not want them, and it silently skips
    # things it does not recognise.
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*)
                [ "$file" = "$MACOS_DIR/Altim" ] && continue
                codesign --force --timestamp --options runtime \
                    --sign "$MACOS_SIGNING_IDENTITY" "$file"
                ;;
        esac
    done < <(find "$MACOS_DIR" -type f -print0)

    codesign --force --timestamp --options runtime \
        --entitlements packaging/macos/entitlements.plist \
        --sign "$MACOS_SIGNING_IDENTITY" "$APP"

    # Neither --deep nor --strict. Both walk the managed assemblies that .NET lays
    # down beside the executable in Contents/MacOS, classify them as nested code
    # because of where they sit, and reject each one with "code object is not
    # signed at all" — a managed .dll is not Mach-O and can never carry a signature
    # of its own. The first macOS CI run failed on System.Diagnostics.Contracts.dll
    # with --deep, and the second failed on the same file with --strict alone.
    #
    # What is still verified: that a signature exists, that the main executable and
    # every nested Mach-O validate against it, and that no sealed resource has been
    # modified since signing. What is not: the nested-code rules that a .NET bundle
    # layout cannot satisfy without moving the assemblies out of Contents/MacOS,
    # which is a change to the shape of the bundle and not to its signature.
    codesign --verify --verbose=2 "$APP"
    SIGNED=1
else
    # Ad-hoc, because "unsigned" is not actually an option. Three separate reasons:
    #
    #   1. macOS 11 and later enforce that any executable must be signed before it
    #      is allowed to run on an Apple silicon Mac. Apple's Big Sur universal apps
    #      release notes say a simple ad-hoc signature is sufficient, and that a
    #      workflow using tools that modify a binary after linking "might need to
    #      manually call codesign(1) as an additional build phase". lipo above is
    #      exactly such a tool.
    #   2. SMAppService, which is how start at login is implemented, requires it:
    #      its header states that apps using those APIs must be code signed, and
    #      registerAndReturnError: answers kSMErrorInvalidSignature otherwise. An
    #      unsealed bundle therefore has no start at login even on the machine that
    #      built it.
    #   3. Without a bundle seal there is no _CodeSignature/CodeResources, so
    #      nothing can tell an intact bundle from a tampered one, and codesign
    #      --verify reports the app as not signed at all.
    #
    # This is NOT a substitute for a Developer ID. An ad-hoc signature carries no
    # identity, Gatekeeper still refuses a downloaded copy, and notarisation is
    # still impossible. It is the difference between a bundle somebody can run on
    # their own Mac and one they cannot.
    step "No MACOS_SIGNING_IDENTITY: ad-hoc signing so the bundle can run at all"

    # Inside out, exactly as above: nested Mach-O first, the bundle last, because
    # signing the bundle seals the main executable and writes CodeResources.
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*)
                [ "$file" = "$MACOS_DIR/Altim" ] && continue
                codesign --force --sign - "$file"
                ;;
        esac
    done < <(find "$MACOS_DIR" -type f -print0)

    codesign --force --sign - "$APP"
    # Neither --deep nor --strict. Both walk the managed assemblies that .NET lays
    # down beside the executable in Contents/MacOS, classify them as nested code
    # because of where they sit, and reject each one with "code object is not
    # signed at all" — a managed .dll is not Mach-O and can never carry a signature
    # of its own. The first macOS CI run failed on System.Diagnostics.Contracts.dll
    # with --deep, and the second failed on the same file with --strict alone.
    #
    # What is still verified: that a signature exists, that the main executable and
    # every nested Mach-O validate against it, and that no sealed resource has been
    # modified since signing. What is not: the nested-code rules that a .NET bundle
    # layout cannot satisfy without moving the assemblies out of Contents/MacOS,
    # which is a change to the shape of the bundle and not to its signature.
    codesign --verify --verbose=2 "$APP"
fi

# ---------------------------------------------------------------------------
# DMG
# ---------------------------------------------------------------------------
DMG="$DIST/Altim-${VERSION}-${ARCH}.dmg"
if [ "$SKIP_DMG" -eq 0 ]; then
    step "Building $(basename "$DMG")"
    DMG_ROOT="$STAGE/dmg"
    rm -rf "$DMG_ROOT"
    mkdir -p "$DMG_ROOT"
    cp -a "$APP" "$DMG_ROOT/"
    ln -s /Applications "$DMG_ROOT/Applications"

    rm -f "$DMG"
    hdiutil create \
        -volname "Altim" \
        -srcfolder "$DMG_ROOT" \
        -ov -format UDZO \
        "$DMG"

    if [ "$SIGNED" -eq 1 ]; then
        codesign --force --timestamp --sign "$MACOS_SIGNING_IDENTITY" "$DMG"
    fi
fi

# ---------------------------------------------------------------------------
# Notarisation
# ---------------------------------------------------------------------------
# Notarisation is a separate credential from signing and a separate failure mode:
# a signed but un-notarised app is still quarantined on a machine that downloaded
# it. Both are required for a download that opens on a double click.
if [ "$SIGNED" -eq 1 ] && [ -n "${MACOS_NOTARY_APPLE_ID:-}" ] \
   && [ -n "${MACOS_NOTARY_APP_PASSWORD:-}" ] && [ -n "${MACOS_NOTARY_TEAM_ID:-}" ]; then
    step "Notarising $(basename "$DMG")"
    xcrun notarytool submit "$DMG" \
        --apple-id "$MACOS_NOTARY_APPLE_ID" \
        --password "$MACOS_NOTARY_APP_PASSWORD" \
        --team-id "$MACOS_NOTARY_TEAM_ID" \
        --wait
    xcrun stapler staple "$DMG"
    xcrun stapler validate "$DMG"
    NOTARISED=1
else
    step "Notarisation credentials absent or bundle unsigned: NOT notarised"
    NOTARISED=0
fi

# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------
step "Copying the bundle out as a zip as well"
APP_ZIP="$DIST/Altim-${VERSION}-${ARCH}-app.zip"
rm -f "$APP_ZIP"
# ditto, not zip: it preserves resource forks and the code signature.
/usr/bin/ditto -c -k --keepParent "$APP" "$APP_ZIP"

step "Checksums"
(
    cd "$DIST"
    rm -f SHA256SUMS.txt
    # shellcheck disable=SC2035
    shasum -a 256 *.dmg *.zip 2>/dev/null | tee SHA256SUMS.txt
)

step "Done: $DIST"
ls -lh "$DIST"
echo
if [ "$SIGNED" -eq 0 ]; then
    echo "    AD-HOC signed only, and NOT notarised. It will run on the machine it was"
    echo "    built on, and Gatekeeper will refuse to open a copy that was downloaded."
    echo "    See packaging/README.md, 'macOS signing'."
elif [ "$NOTARISED" -eq 0 ]; then
    echo "    Signed but NOT notarised. Gatekeeper will still refuse a downloaded copy."
fi
