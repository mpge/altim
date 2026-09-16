#!/usr/bin/env bash
#
# Builds the Linux artefacts: an AppImage (the primary one) plus a .deb and an
# .rpm from the same published payload.
#
#   ./packaging/linux/build-linux.sh --version 0.1.0
#   ./packaging/linux/build-linux.sh --version 0.1.0 --rid linux-arm64
#   ./packaging/linux/build-linux.sh --version 0.1.0 --skip-appimage
#
# The AppImage is primary because it is the only one of the three that runs on a
# distribution nobody tested: it carries the whole .NET runtime and asks the host
# only for X11, fontconfig and libc. The .deb and .rpm exist so that people who
# expect their package manager to know about an application are not told to go and
# chmod +x something.
#
# Tooling is downloaded on demand and pinned. nfpm is a single Go binary and
# appimagetool is an AppImage, so neither needs anything installed on the host.
#
# This script has never been executed: it is written on and for Linux, and the
# machine it was authored on runs Windows. Read it before you trust it.

set -euo pipefail

VERSION=""
RID="linux-x64"
SKIP_PUBLISH=0
SKIP_APPIMAGE=0
SKIP_PACKAGES=0

NFPM_TOOL_VERSION="2.47.0"
APPIMAGETOOL_VERSION="1.9.1"

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --rid) RID="$2"; shift 2 ;;
        --skip-publish) SKIP_PUBLISH=1; shift ;;
        --skip-appimage) SKIP_APPIMAGE=1; shift ;;
        --skip-packages) SKIP_PACKAGES=1; shift ;;
        -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

PROJECT="src/Altim.App/Altim.App.csproj"

if [ -z "$VERSION" ]; then
    VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
    [ -n "$VERSION" ] || { echo "no --version and no <Version> in $PROJECT" >&2; exit 1; }
    echo "==> Version not given; using <Version> from the project: $VERSION"
fi
VERSION="${VERSION#v}"

case "$RID" in
    linux-x64)   APPIMAGE_ARCH="x86_64";  NFPM_ARCH="amd64" ;;
    linux-arm64) APPIMAGE_ARCH="aarch64"; NFPM_ARCH="arm64" ;;
    *) echo "unsupported --rid: $RID (linux-x64 or linux-arm64)" >&2; exit 2 ;;
esac

DIST="$REPO_ROOT/dist/linux"
PUBLISH_DIR="$REPO_ROOT/dist/.publish/$RID"
TOOLS="$REPO_ROOT/dist/.tools"
APPDIR="$REPO_ROOT/dist/.appdir/Altim.AppDir"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

mkdir -p "$DIST" "$TOOLS"

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
# No PublishAot here. Altim.App only turns it on for a Windows target framework,
# and ARCHITECTURE.md is explicit that AOT is a Windows-first setting with the
# other platforms following once verified. This is a self-contained JIT publish:
# the whole runtime travels with the app, nothing is expected of the host.
#
# -p:AltimPortableBuild=true is only needed when cross-publishing from a Windows
# host, where the project would otherwise select its Windows target framework. It
# is a no-op on Linux, so it is passed unconditionally rather than guessed at.
if [ "$SKIP_PUBLISH" -eq 0 ]; then
    step "Publishing $RID (Release, self-contained) to $PUBLISH_DIR"
    rm -rf "$PUBLISH_DIR"
    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --self-contained \
        -p:Version="$VERSION" \
        -p:AltimPortableBuild=true \
        -p:DebugType=none \
        --output "$PUBLISH_DIR" \
        --nologo
fi

[ -f "$PUBLISH_DIR/Altim" ] || { echo "no Altim in $PUBLISH_DIR" >&2; exit 1; }

# Normalise the payload's permissions before anything packages it. nfpm's `tree`
# content type copies each source file's mode verbatim, so a payload published
# from a host without a POSIX executable bit — a Windows cross-publish — produces
# a .deb whose /usr/lib/altim/Altim is 0664 and will not run. Observed, not
# hypothesised. Doing it here means the AppImage and the packages agree.
chmod 0755 "$PUBLISH_DIR/Altim"
find "$PUBLISH_DIR" -type d -exec chmod 0755 {} +
find "$PUBLISH_DIR" -type f ! -name 'Altim' -exec chmod 0644 {} +
find "$PUBLISH_DIR" -type f -name '*.so*' -exec chmod 0755 {} +

# ---------------------------------------------------------------------------
# AppImage
# ---------------------------------------------------------------------------
if [ "$SKIP_APPIMAGE" -eq 0 ]; then
    step "Assembling the AppDir"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications"

    cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
    ln -sf Altim "$APPDIR/usr/bin/altim"

    install -m 0755 packaging/linux/AppRun "$APPDIR/AppRun"
    install -m 0644 packaging/linux/altim.desktop "$APPDIR/altim.desktop"
    install -m 0644 packaging/linux/altim.desktop "$APPDIR/usr/share/applications/altim.desktop"

    for size in 16 24 32 48 64 128 256 512; do
        mkdir -p "$APPDIR/usr/share/icons/hicolor/${size}x${size}/apps"
        install -m 0644 "assets/icons/altim-${size}.png" \
            "$APPDIR/usr/share/icons/hicolor/${size}x${size}/apps/altim.png"
    done

    # appimagetool wants the icon named after the desktop file's Icon= key at the
    # AppDir root, and .DirIcon is what file managers show for the mounted image.
    install -m 0644 assets/icons/altim-256.png "$APPDIR/altim.png"
    cp "$APPDIR/altim.png" "$APPDIR/.DirIcon"

    APPIMAGETOOL="$TOOLS/appimagetool-${APPIMAGE_ARCH}-${APPIMAGETOOL_VERSION}.AppImage"
    if [ ! -x "$APPIMAGETOOL" ]; then
        step "Fetching appimagetool $APPIMAGETOOL_VERSION"
        curl -fsSL -o "$APPIMAGETOOL" \
            "https://github.com/AppImage/appimagetool/releases/download/${APPIMAGETOOL_VERSION}/appimagetool-${APPIMAGE_ARCH}.AppImage"
        chmod +x "$APPIMAGETOOL"
    fi

    OUT_APPIMAGE="$DIST/Altim-${VERSION}-${APPIMAGE_ARCH}.AppImage"
    step "Building $(basename "$OUT_APPIMAGE")"

    # --appimage-extract-and-run because CI containers have no FUSE, and
    # appimagetool is itself an AppImage. ARCH is read from the environment by
    # appimagetool and is not inferred from the payload.
    ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" \
        --appimage-extract-and-run \
        --no-appstream \
        "$APPDIR" "$OUT_APPIMAGE"

    chmod +x "$OUT_APPIMAGE"
fi

# ---------------------------------------------------------------------------
# .deb and .rpm
# ---------------------------------------------------------------------------
if [ "$SKIP_PACKAGES" -eq 0 ]; then
    NFPM="$TOOLS/nfpm-${NFPM_TOOL_VERSION}/nfpm"
    if [ ! -x "$NFPM" ]; then
        step "Fetching nfpm $NFPM_TOOL_VERSION"
        mkdir -p "$TOOLS/nfpm-${NFPM_TOOL_VERSION}"
        case "$NFPM_ARCH" in
            amd64) NFPM_ASSET_ARCH="x86_64" ;;
            arm64) NFPM_ASSET_ARCH="arm64" ;;
        esac
        curl -fsSL \
            "https://github.com/goreleaser/nfpm/releases/download/v${NFPM_TOOL_VERSION}/nfpm_${NFPM_TOOL_VERSION}_Linux_${NFPM_ASSET_ARCH}.tar.gz" \
            | tar -xz -C "$TOOLS/nfpm-${NFPM_TOOL_VERSION}" nfpm
        chmod +x "$NFPM"
    fi

    # nfpm.yaml is a template. nfpm expands ${VAR} in some scalar fields but not
    # in contents[].src — it hands the literal string to the filesystem and fails
    # with "GetFileAttributesEx ${NFPM_PAYLOAD}/: no such file" (measured, nfpm
    # 2.47.0). So all three placeholders are substituted here instead, which also
    # means there is one mechanism to understand rather than two.
    GENERATED="$REPO_ROOT/dist/.nfpm.yaml"
    sed -e "s|@ARCH@|$NFPM_ARCH|g" \
        -e "s|@VERSION@|$VERSION|g" \
        -e "s|@PAYLOAD@|$PUBLISH_DIR|g" \
        packaging/linux/nfpm.yaml > "$GENERATED"

    # nfpm resolves the remaining relative `src` paths against its working
    # directory, which is why everything here runs from the repository root.
    for packager in deb rpm; do
        step "Building the .$packager"
        "$NFPM" package \
            --config "$GENERATED" \
            --packager "$packager" \
            --target "$DIST"
    done
fi

# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------
step "Checksums"
(
    cd "$DIST"
    rm -f SHA256SUMS.txt
    # shellcheck disable=SC2035
    sha256sum *.AppImage *.deb *.rpm 2>/dev/null | tee SHA256SUMS.txt
)

step "Done: $DIST"
ls -lh "$DIST"
echo
echo "    UNSIGNED. No GPG signature, no repository. See packaging/README.md."
