#!/usr/bin/env bash
#
# Builds the macOS artefacts for ONE architecture: Altim.app and a DMG holding it.
#
#   ./packaging/macos/build-macos.sh --version 0.1.0 --arch arm64
#   ./packaging/macos/build-macos.sh --version 0.1.0 --arch x64
#
# One invocation, one architecture, one DMG. There is deliberately no universal
# build any more: packaging/README.md, "Why there is no universal build", has the
# reasoning. In short, merging two single-file hosts with lipo is an unobserved
# behaviour sitting in the middle of a job that had never once produced an
# artefact, and two rows on a release page cost less than that risk.
#
# The publish is PublishSingleFile. That is a packaging decision, not a size one:
# codesign seals Contents/MacOS as a nested-code location, so every file in there
# apart from the main executable has to be an independently signed code object.
# A managed .dll is a PE file and can never carry a Mach-O signature, so a normal
# publish layout cannot be sealed at all. Embedding the assemblies in the host
# leaves Contents/MacOS holding nothing but Mach-O. assets/ is moved to
# Contents/Resources for the same reason.
#
# Signing and notarisation are skipped, loudly, when the credentials are absent.
# The ad-hoc bundle seal is additionally non-fatal: see "Signing" below.
#
# Neither this script nor the bundle it produces has ever been run on a Mac.
# Read it before you trust it.

set -euo pipefail

VERSION=""
ARCH=""
SKIP_PUBLISH=0
SKIP_DMG=0

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --arch) ARCH="$2"; shift 2 ;;
        --skip-publish) SKIP_PUBLISH=1; shift ;;
        --skip-dmg) SKIP_DMG=1; shift ;;
        -h|--help) sed -n '2,27p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

PROJECT="src/Altim.App/Altim.App.csproj"
NOTICES="THIRD-PARTY-NOTICES.md"

# Checked before the publish rather than after it. A bundle that assembles without
# the notices is one that has to be built again.
if [ ! -f "$NOTICES" ]; then
    echo "no $NOTICES at the repository root." >&2
    echo "Generate it with packaging/notices/build-notices.py; see packaging/README.md." >&2
    exit 1
fi

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

if [ -z "$ARCH" ]; then
    case "$(uname -m 2>/dev/null || echo unknown)" in
        arm64|aarch64) ARCH="arm64" ;;
        x86_64)        ARCH="x64" ;;
        *)             ARCH="arm64" ;;
    esac
fi

case "$ARCH" in
    arm64) RID="osx-arm64" ;;
    x64)   RID="osx-x64" ;;
    universal)
        echo "--arch universal was removed. Build arm64 and x64 separately;" >&2
        echo "see packaging/README.md, 'Why there is no universal build'." >&2
        exit 2 ;;
    *) echo "unsupported --arch: $ARCH (arm64 or x64)" >&2; exit 2 ;;
esac

DIST="$REPO_ROOT/dist/macos"
# One stage directory per architecture, so building both in sequence leaves both
# bundles on disk for verify-bundle.sh to read back.
STAGE="$REPO_ROOT/dist/.macos-stage/$ARCH"
APP="$STAGE/Altim.app"
MACOS_DIR="$APP/Contents/MacOS"
RESOURCES_DIR="$APP/Contents/Resources"
PUBLISH="$REPO_ROOT/dist/.publish/$RID"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

# Anything said through this reaches the run page as well as the log, because the
# state this script can leave the bundle in is not one to find out about later.
summary() {
    printf '%s\n' "$*"
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        printf '%s\n' "$*" >> "$GITHUB_STEP_SUMMARY"
    fi
}

mkdir -p "$DIST"

step "Building Altim $VERSION for $ARCH ($RID)"

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
# -p:AltimPortableBuild=true is a no-op on macOS and only matters when
# cross-publishing from a Windows host, where the project would otherwise pick its
# Windows target framework. PublishAot stays off: it is a Windows-only setting in
# Altim.App.csproj and ARCHITECTURE.md keeps the other platforms behind it until
# someone has verified them on real hardware.
#
# PublishSingleFile is what makes the bundle sealable. With it, the managed
# assemblies, Altim.deps.json and Altim.runtimeconfig.json are all carried inside
# the host, and what is left beside it in Contents/MacOS is the host plus the
# native .dylib files, every one of them Mach-O and individually signable.
#
# It adds no new analyser surface: Directory.Build.props already sets
# IsAotCompatible=true, which the SDK turns into EnableSingleFileAnalyzer=true
# (Microsoft.NET.Sdk.Analyzers.targets), so the IL3000 series is already an error
# on every ordinary build of this repository.
#
# IncludeNativeLibrariesForSelfExtract is deliberately left at its default of
# false. Embedding the dylibs would make the host extract them to a temporary
# directory on every start, and those extracted copies are not covered by any
# bundle seal.
if [ "$SKIP_PUBLISH" -eq 0 ]; then
    step "Publishing $RID"
    rm -rf "$PUBLISH"
    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --self-contained \
        -p:Version="$VERSION" \
        -p:AltimPortableBuild=true \
        -p:DebugType=none \
        -p:PublishSingleFile=true \
        --output "$PUBLISH" \
        --nologo
fi
[ -f "$PUBLISH/Altim" ] || { echo "no Altim in dist/.publish/$RID" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Bundle
# ---------------------------------------------------------------------------
step "Assembling Altim.app"
rm -rf "$STAGE"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp -a "$PUBLISH/." "$MACOS_DIR/"

# The menu bar template renderings are content files, so the single-file bundler
# leaves them loose, and loose in Contents/MacOS is exactly where codesign calls
# them unsigned nested code. Contents/Resources is where they belong and is also
# the first place MacOSTrayAssets.DiscoverAssetDirectory looks: it asks
# MacOSAppBundle.FindResourcesDirectory first, tries Contents/Resources itself,
# then Contents/Resources/assets/icons, and only then walks up from
# AppContext.BaseDirectory. Moving the directory wholesale lands on the second of
# those, so no source change is needed.
if [ -d "$MACOS_DIR/assets" ]; then
    step "Moving assets out of Contents/MacOS into Contents/Resources"
    rm -rf "$RESOURCES_DIR/assets"
    mv "$MACOS_DIR/assets" "$RESOURCES_DIR/assets"
fi

# Contents/Resources, for the same reason the assets are there: anything in
# Contents/MacOS is nested code under the default resource rules and a text file
# cannot be sealed. Resources is where a bundle's documentation belongs anyway, and
# it is covered by _CodeSignature/CodeResources once the bundle is sealed, so the
# notices are tamper-evident along with everything else.
step "Copying LICENSE and $NOTICES into Contents/Resources"
install -m 0644 LICENSE "$RESOURCES_DIR/LICENSE"
install -m 0644 "$NOTICES" "$RESOURCES_DIR/$NOTICES"

# Whether the bundle can be sealed at all is decided here, before codesign is
# reached: every file in Contents/MacOS other than the main executable is nested
# code under the default resource rules, and a file that is not Mach-O can never
# satisfy that. This is reported rather than fatal, because an unsealed bundle
# somebody can look at is worth more than a red job, and the seal step below says
# plainly what it ended up with.
step "Checking Contents/MacOS holds nothing but Mach-O"
stray=0
while IFS= read -r -d '' file; do
    [ "$file" = "$MACOS_DIR/Altim" ] && continue
    case "$(file -b "$file")" in
        *Mach-O*) ;;
        *)
            echo "    not Mach-O, so it cannot be sealed: ${file#"$MACOS_DIR"/}" >&2
            stray=$((stray + 1))
            ;;
    esac
done < <(find "$MACOS_DIR" -type f -print0)
if [ "$stray" -eq 0 ]; then
    step "Contents/MacOS is Mach-O only"
else
    summary "WARNING: $stray file(s) in Contents/MacOS are not Mach-O. The bundle seal will fail."
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
iconutil --convert icns --output "$RESOURCES_DIR/Altim.icns" "$ICONSET"
rm -rf "$ICONSET"

# ---------------------------------------------------------------------------
# Signing
# ---------------------------------------------------------------------------
# Every identity step is conditional on credentials that nobody has yet.
#
# Neither path uses --deep or --strict. --deep is deprecated and applies one set
# of entitlements to nested code that may not want them; --strict adds nothing the
# inside-out loop below does not already cover. Both were ruled out once already
# by CI runs that failed on the contents of Contents/MacOS, which was never a
# verification problem and is now fixed at its cause by PublishSingleFile.
SIGNED=0
SEALED=0
if [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
    step "Signing with $MACOS_SIGNING_IDENTITY"

    # Inside out: nested Mach-O first, the bundle last, because signing the bundle
    # seals the main executable and writes _CodeSignature/CodeResources.
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*)
                [ "$file" = "$MACOS_DIR/Altim" ] && continue
                codesign --force --timestamp --options runtime \
                    --sign "$MACOS_SIGNING_IDENTITY" "$file"
                ;;
        esac
    done < <(find "$MACOS_DIR" -type f -print0)

    # Fatal on purpose, unlike the ad-hoc path below. An app signed with a real
    # identity and then not sealed is not something to ship quietly: Gatekeeper
    # would refuse it and notarisation would reject it anyway, so a release that
    # stops here is better than one that goes out.
    codesign --force --timestamp --options runtime \
        --entitlements packaging/macos/entitlements.plist \
        --sign "$MACOS_SIGNING_IDENTITY" "$APP"
    codesign --verify --verbose=2 "$APP"
    SIGNED=1
    SEALED=1
else
    # Ad-hoc, because "unsigned" is not actually an option. Three separate reasons:
    #
    #   1. macOS 11 and later enforce that any executable must be signed before it
    #      is allowed to run on an Apple silicon Mac. Apple's Big Sur universal
    #      apps release notes say a simple ad-hoc signature is sufficient.
    #   2. SMAppService, which is how start at login is implemented, requires it:
    #      its header states that apps using those APIs must be code signed, and
    #      registerAndReturnError: answers kSMErrorInvalidSignature otherwise.
    #   3. Without a bundle seal there is no _CodeSignature/CodeResources, so
    #      nothing can tell an intact bundle from a tampered one.
    #
    # This is NOT a substitute for a Developer ID. An ad-hoc signature carries no
    # identity, Gatekeeper still refuses a downloaded copy, and notarisation is
    # still impossible.
    #
    # Nothing in this branch is fatal. The macOS bundle job failed all eleven times
    # it ran, every one of them at the bundle seal, so no .app, no DMG and no
    # artefact had ever existed and there was nothing to diagnose from. A bundle
    # that assembles and uploads with its state written down is worth more than a
    # red job with no output. What must not happen is a bundle that quietly
    # pretends to be sealed, so every outcome below is reported.
    step "No MACOS_SIGNING_IDENTITY: ad-hoc signing"

    nested_total=0
    nested_failed=0
    while IFS= read -r -d '' file; do
        case "$(file -b "$file")" in
            *Mach-O*)
                [ "$file" = "$MACOS_DIR/Altim" ] && continue
                nested_total=$((nested_total + 1))
                if ! codesign --force --sign - "$file"; then
                    echo "    could not sign: ${file#"$MACOS_DIR"/}" >&2
                    nested_failed=$((nested_failed + 1))
                fi
                ;;
        esac
    done < <(find "$MACOS_DIR" -type f -print0)
    step "Ad-hoc signed $((nested_total - nested_failed)) of $nested_total nested Mach-O files"
    if [ "$nested_failed" -ne 0 ]; then
        summary "WARNING: $nested_failed nested Mach-O file(s) could not be ad-hoc signed."
    fi

    if codesign --force --sign - "$APP"; then
        SEALED=1
        codesign --verify --verbose=2 "$APP" || SEALED=0
    fi

    if [ "$SEALED" -eq 0 ]; then
        # A --force seal that gets part way through can leave the main executable
        # without the signature the SDK's bundler gave it, and on Apple silicon an
        # unsigned Mach-O is one the kernel refuses to execute. Re-signing the
        # binary on its own is not a bundle seal and buys none of what a seal buys,
        # but it is the difference between an app that starts and one that is
        # killed at exec.
        step "Bundle seal failed: re-signing the executable alone so it can still run"
        codesign --force --sign - "$MACOS_DIR/Altim" || true
        codesign --verify --verbose=2 "$MACOS_DIR/Altim" || true
    fi
fi

# ---------------------------------------------------------------------------
# DMG
# ---------------------------------------------------------------------------
DMG="$DIST/Altim-${VERSION}-${ARCH}.dmg"
if [ "$SKIP_DMG" -eq 0 ]; then
    step "Building $(basename "$DMG")"
    DMG_ROOT="$STAGE/dmg"
    # The publish output was copied into the bundle long ago and nothing reads it again.
    # Keeping it costs another copy of a ninety megabyte single-file host at the exact
    # moment hdiutil wants room for an uncompressed image plus the compressed result: the
    # x64 runner ran out here on the first run that got this far, while arm64 finished.
    rm -rf "$PUBLISH"

    rm -rf "$DMG_ROOT"
    mkdir -p "$DMG_ROOT"

    cp -a "$APP" "$DMG_ROOT/"
    ln -s /Applications "$DMG_ROOT/Applications"

    # "No space left on device" out of hdiutil is about the image being built, not the
    # disk underneath it. The x64 job failed that way with 91GB free, and the message
    # names a path under /Volumes: left to size the image itself, hdiutil lands too small
    # for x64, whose single-file host is the larger of the two. So the size is stated,
    # measured off the tree with a fifth again and a floor on top. Compression makes the
    # slack free.
    dmg_kb="$(du -sk "$DMG_ROOT" | awk '{print $1}')"
    dmg_mb=$(( (dmg_kb / 1024) * 6 / 5 + 64 ))

    step "Image source is $(( dmg_kb / 1024 ))MB, asking hdiutil for ${dmg_mb}MB"

    rm -f "$DMG"
    hdiutil create \
        -size "${dmg_mb}m" \
        -volname "Altim ($ARCH)" \
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
if [ "$SIGNED" -eq 1 ] && [ "$SKIP_DMG" -eq 0 ] && [ -n "${MACOS_NOTARY_APPLE_ID:-}" ] \
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
    # The trailing "|| true" is for --skip-dmg, where *.dmg matches nothing, shasum
    # exits non-zero over the unexpanded glob, and pipefail would otherwise end the
    # run one step from the finish line with everything already built.
    # shellcheck disable=SC2035
    shasum -a 256 *.dmg *.zip 2>/dev/null | tee SHA256SUMS.txt || true
)

step "Done: dist/macos"
ls -lh "$DIST"
echo

# ---------------------------------------------------------------------------
# What this bundle actually is
# ---------------------------------------------------------------------------
summary ""
summary "### macOS bundle ($ARCH), Altim $VERSION"
summary ""
if [ "$SIGNED" -eq 1 ] && [ "$NOTARISED" -eq 1 ]; then
    summary "Signed with a Developer ID and notarised."
elif [ "$SIGNED" -eq 1 ]; then
    summary "Signed with a Developer ID and sealed, and NOT notarised."
    summary "Gatekeeper will still refuse a downloaded copy."
elif [ "$SEALED" -eq 1 ]; then
    summary "Ad-hoc signed and sealed. NOT signed with an identity, NOT notarised."
    summary "It will run on a Mac it was copied to by hand; Gatekeeper refuses a"
    summary "downloaded copy. packaging/README.md, 'Signing', lists the secrets."
else
    summary "**NOT SEALED.** This bundle is being shipped in a deliberately degraded"
    summary "state so that an artefact exists at all. It is not a bundle that passed."
    summary ""
    summary "The main executable and the nested dylibs carry ad-hoc signatures, so the"
    summary "application should still launch. What a bundle with no seal does not have:"
    summary ""
    summary "- no Contents/_CodeSignature/CodeResources, so nothing can tell an intact"
    summary "  bundle from a tampered one, and codesign --verify reports the app as"
    summary "  not signed at all"
    summary "- **start at login cannot work.** SMAppService.registerAndReturnError:"
    summary "  answers kSMErrorInvalidSignature for an app not code signed as a"
    summary "  bundle, so the setting reports itself unavailable"
    summary "- Gatekeeper refuses a downloaded copy outright, beyond the ordinary"
    summary "  unsigned and un-notarised refusal"
    summary ""
    summary "packaging/README.md, 'The bundle seal', has the cause and the fix."
fi
