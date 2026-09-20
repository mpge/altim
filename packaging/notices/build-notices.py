#!/usr/bin/env python3
"""Generates THIRD-PARTY-NOTICES.md from what Altim actually ships.

    python packaging/notices/build-notices.py \
        --publish windows=dist/.publish/win-x64 \
        --publish linux=dist/.publish/linux-x64 \
        --publish macos=dist/.publish/osx-arm64

Run `--help` for the full argument list and `packaging/README.md`, "Third-party
notices", for the whole procedure including the three publishes this needs.

Why it works from a publish rather than from the project files
--------------------------------------------------------------
A notices file has to describe what a recipient was handed, not what the
repository references. Those two sets differ here by more than a rounding error:
`Directory.Packages.props` lists analysers and test-only packages that are never
distributed, `Avalonia.BuildServices` and `Microsoft.NET.ILLink.Tasks` are build
tooling, and `AvaloniaUI.DiagnosticsSupport` is stripped from every configuration
but Debug. None of those are in an artefact and none of them belong in this file.

So the package list comes from `Altim.deps.json`, which the SDK writes from the
resolved graph for one runtime identifier and which names every assembly and
native library the publish actually contains. Each publish directory is then read
back and every file in it is matched against that list. A file nothing accounts
for is a hard error, not a warning: that is the check that stops a new native
payload from appearing in an artefact without a notice.

Where the licence text comes from
---------------------------------
Almost all of it is read out of the restored packages under the NuGet cache, so
regeneration is offline and the text is the text the package itself carries.
Three files are not in any package and are committed under
`packaging/notices/texts/` instead; `TEXTS` below records where each one was
fetched from and why the package cannot supply it.

Nothing here is written by hand, inferred from a licence URL, or copied from a
licence list. A package that declares its licence as a file rather than as an
SPDX expression is reported as declaring a file, and the file is reproduced.

Determinism
-----------
The output is a pure function of the publishes, the NuGet cache and this script.
Regenerating without changing a dependency produces identical bytes, so
`--check` can be wired to CI.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import textwrap
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
TEXTS_DIR = Path(__file__).resolve().parent / "texts"

PLATFORM_ORDER = ("windows", "linux", "macos")
PLATFORM_LABEL = {"windows": "Windows", "linux": "Linux", "macos": "macOS"}


# ---------------------------------------------------------------------------
# Payload that deps.json does not describe
# ---------------------------------------------------------------------------
# Some files reach a publish directory through MSBuild targets inside their own
# packages rather than through the dependency graph, so they have no entry in
# Altim.deps.json and nothing below would attribute them. They are named here with
# the package that placed them.
#
# The four Windows entries came from the Microsoft.WindowsAppSDK umbrella, which
# put an inference runtime and a browser control into every publish for the sake of
# the one type Altim uses. Referencing Microsoft.WindowsAppSDK.Foundation directly
# took them out, so on the graph as it stands none of these four resolves and none
# of them is reported. They are kept because the entry costs nothing and because a
# package reference is one edit away from bringing them back.
UNDECLARED_PAYLOAD = {
    "windows": {
        "DirectML.dll": "Microsoft.Windows.AI.MachineLearning",
        "onnxruntime.dll": "Microsoft.Windows.AI.MachineLearning",
        "Microsoft.Windows.AI.MachineLearning.dll": "Microsoft.Windows.AI.MachineLearning",
        "Microsoft.Web.WebView2.Core.dll": "Microsoft.Web.WebView2",
    },
    "linux": {},
    "macos": {},
}

# Packages whose whole tree is searched by file name, for payload that no
# dependency entry and no hand-written map can keep up with.
#
# `WindowsAppSDKSelfContained` copies the entire Windows App Runtime beside the
# application: about fifty DLLs, WinMDs, `.pri` files and an executable, from
# `runtimes-framework/` trees that no `deps.json` entry lists. Naming them one by
# one would be a list that rots the first time Microsoft moves a file, so instead
# the packages are named and a file is attributed when it is literally inside one
# of them. The version is whichever the publish resolved, so this cannot drift
# onto a package the build is not using.
#
# Only for packages that binplace a directory wholesale. Everything else is
# attributed from the dependency graph, which is more precise.
FRAMEWORK_PAYLOAD_PACKAGES = {
    "windows": (
        "Microsoft.WindowsAppSDK.Foundation",
        "Microsoft.WindowsAppSDK.InteractiveExperiences",
        "Microsoft.WindowsAppSDK.Runtime",
    ),
    "linux": (),
    "macos": (),
}

# ---------------------------------------------------------------------------
# What packaging does to a publish directory on its way into an artefact
# ---------------------------------------------------------------------------
# The payload tables below are built by reading a publish directory, which is not
# quite the artefact. Each packaging step changes it, and a table that claimed a
# file ships when it does not would be exactly the kind of inaccuracy this whole
# exercise is about.
#
# The Windows entry is measured, not assumed: `vpk pack` was run against this
# publish with Velopack 1.2.0 and the resulting Altim-win-Portable.zip and
# Altim-0.1.0-full.nupkg were read back. Every .pdb is dropped, and Velopack's own
# Update.exe is added.
PACK_ADJUSTMENTS = {
    "windows": {
        "drop": (".pdb",),
        "add": [("Update.exe", "Velopack, placed by vpk pack")],
        "note": "Read out of `dist/.publish/win-x64`, adjusted for what `vpk pack` then "
                "does to it: the debug symbols beside the binaries are dropped and "
                "Velopack's own `Update.exe` is added, along with its `sq.version` and "
                "`.portable` markers. Verified by reading the packed "
                "`Altim-win-Portable.zip` and `Altim-0.1.0-full.nupkg` back.",
    },
    "linux": {
        "drop": (),
        "add": [],
        "note": "Read out of `dist/.publish/linux-x64`. The AppImage adds `AppRun`, the "
                "desktop entry and the icon set around this; the `.deb` and `.rpm` install "
                "the same payload under `/usr/lib/altim` and add "
                "`/usr/share/doc/altim/copyright`.",
    },
    "macos": {
        "drop": (),
        "add": [],
        "note": "Read out of `dist/.publish/osx-arm64`. The bundle adds `Info.plist`, "
                "`PkgInfo` and `Altim.icns`, and moves `assets/` into "
                "`Contents/Resources` so that `Contents/MacOS` holds nothing but Mach-O.",
    },
}

# Altim's own output. Everything else in a publish directory has to come from a
# package or the run fails.
ALTIM_OWN = re.compile(
    r"""^(
        Altim(\.exe)?                 # the apphost, or the native image
      | Altim\.[A-Za-z.]+\.(dll|pdb|pri)
      | Altim\.(dll|pdb|pri|deps\.json|runtimeconfig\.json)
      | LICENSE
      | THIRD-PARTY-NOTICES\.md
      | assets/.*
    )$""",
    re.VERBOSE,
)


# ---------------------------------------------------------------------------
# Components inside a binary, which no package scan can see
# ---------------------------------------------------------------------------
# Each entry was confirmed against the built binary rather than taken from a
# dependency list, and `evidence` records how. `platforms` is the artefacts the
# component is actually in, which is not always all three.
EMBEDDED = [
    {
        "name": "Inter",
        "version": "3.019 (upstream tag v3.19)",
        "carrier": "Avalonia.Fonts.Inter",
        "requires": ("Avalonia.Fonts.Inter",),
        "where": "embedded as an Avalonia resource inside Avalonia.Fonts.Inter.dll, "
                 "which the publish links into the application",
        "platforms": ("windows", "linux", "macos"),
        "licence": "SIL Open Font License 1.1",
        "copyright": "Copyright (c) 2016-2020 The Inter Project Authors. "
                     '"Inter" is trademark of Rasmus Andersson.',
        "evidence": "read out of the shipped Avalonia.Fonts.Inter.dll: the name table of "
                    "each face carries "
                    '"Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter)", '
                    '"Version 3.019;git-0a5106e0b" and '
                    '"This Font Software is licensed under the SIL Open Font License, Version 1.1". '
                    "Six faces are present: Thin, Light, Regular, Medium, SemiBold and Bold.",
        "text": "OFL-1.1",
    },
    {
        "name": "Skia",
        "version": "as built by SkiaSharp 3.119.4",
        "carrier": "SkiaSharp.NativeAssets.*",
        "requires": ("SkiaSharp",),
        "where": "libSkiaSharp.dll, libSkiaSharp.so, libSkiaSharp.dylib",
        "platforms": ("windows", "linux", "macos"),
        "licence": "BSD-3-Clause",
        "copyright": "Copyright (c) 2011 Google Inc. All rights reserved.",
        "evidence": "the notice reproduced below is the THIRD-PARTY-NOTICES.txt file "
                    "shipped inside the SkiaSharp.NativeAssets packages themselves; the "
                    "three platforms' copies are byte-identical.",
        "text": "BSD-3-Skia",
    },
    {
        "name": "FreeType",
        "version": "not determinable from the distributed binary",
        "carrier": "SkiaSharp.NativeAssets.Linux",
        "requires": ("SkiaSharp.NativeAssets.Linux",),
        "where": "statically linked into libSkiaSharp.so",
        "platforms": ("linux",),
        "licence": "FreeType Project License (FTL) or GPLv2, at the recipient's choice; "
                   "the FTL is the one taken here",
        "copyright": "Portions of this software are copyright The FreeType Project "
                     "(www.freetype.org). All rights reserved.",
        "evidence": "measured on the built binaries, not assumed. libSkiaSharp.so carries "
                    "FT_* symbols, SkFontHost_FreeType source-file strings and every "
                    "FreeType module name (truetype, sfnt, psaux, psnames, pshinter, "
                    "autofitter, raster1, smooth). libSkiaSharp.dll carries none of them "
                    "and resolves DWriteCreateFactory instead; libSkiaSharp.dylib carries "
                    "none of them and uses CoreText. So this obligation is a Linux one.",
        "text": "FTL",
    },
    {
        "name": "ANGLE",
        "version": "2.1.27548.20260419, as built by the Avalonia project",
        "carrier": "Avalonia.Angle.Windows.Natives",
        "requires": ("Avalonia.Angle.Windows.Natives",),
        "where": "av_libglesv2.dll",
        "platforms": ("windows",),
        "licence": "BSD-3-Clause",
        "copyright": "Copyright 2018 The ANGLE Project Authors. All rights reserved.",
        "evidence": "the text below is the LICENSE file inside "
                    "Avalonia.Angle.Windows.Natives, which is the ANGLE build Altim ships. "
                    "SkiaSharp's notice also carries an ANGLE section, for a different "
                    "Microsoft build of ANGLE with a different copyright line; both are "
                    "reproduced rather than merged.",
        "text": "BSD-3-ANGLE",
    },
    {
        "name": "SQLite",
        "version": "as built by SQLitePCLRaw.lib.e_sqlite3 2.1.12",
        "carrier": "SQLitePCLRaw.lib.e_sqlite3",
        "requires": ("SQLitePCLRaw.lib.e_sqlite3",),
        "where": "e_sqlite3.dll, libe_sqlite3.so, libe_sqlite3.dylib",
        "platforms": ("windows", "linux", "macos"),
        "licence": "public domain; the author's dedication is reproduced below",
        "copyright": "none asserted by the author",
        "evidence": "the dedication below is quoted from SQLitePCL.raw's own NOTICE.TXT, "
                    "which is reproduced in full further down.",
        "text": "SQLite",
    },
]


# ---------------------------------------------------------------------------
# Licence and notice texts
# ---------------------------------------------------------------------------
# Every entry names exactly where its bytes come from. A `package` source is read
# out of the restored package in the NuGet cache. A `repo` source is a file
# committed under packaging/notices/texts/, which is only used where no package
# carries the text; `why` says why. A `section` source is cut out of another
# source file between two markers, so a quoted excerpt cannot drift from the full
# document reproduced below it.


def pkg_file(package: str, version: str, name: str) -> dict:
    return {"kind": "package", "package": package, "version": version, "name": name}


TEXTS = {
    # -- licence texts ------------------------------------------------------
    "MIT": pkg_file("skiasharp", "3.119.4", "LICENSE.txt"),
    "Apache-2.0": {
        "kind": "repo",
        "name": "SQLitePCL.raw-LICENSE.TXT",
        "origin": "https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT",
        "why": "the four SQLitePCLRaw packages declare Apache-2.0 as an SPDX expression "
               "and carry no licence file, so Apache-2.0 section 4(a) cannot be met from "
               "the package. This is the copy at the tag the shipped version was built from.",
    },
    "OFL-1.1": {
        "kind": "repo",
        "name": "Inter-LICENSE.txt",
        "origin": "https://raw.githubusercontent.com/rsms/inter/v3.19/LICENSE.txt",
        "why": "Avalonia.Fonts.Inter carries the font but not its licence. v3.19 is the "
               "tag matching the 'Version 3.019' string in the shipped font's name table.",
    },
    "BSD-3-ANGLE": pkg_file("avalonia.angle.windows.natives", "2.1.27548.20260419", "LICENSE"),
    "BSD-3-WebView2": pkg_file("microsoft.web.webview2", "1.0.3719.77", "LICENSE.txt"),
    "EULA-WindowsAppSDK": pkg_file("microsoft.windowsappsdk", "2.4.0", "license.txt"),
    "EULA-WindowsML": pkg_file("microsoft.windows.ai.machinelearning", "2.1.74", "license.txt"),
    # Cut out of the documents reproduced in full further down, so the prominent
    # copy and the verbatim copy are the same bytes by construction.
    "BSD-3-Skia": {
        "kind": "section",
        "of": "NOTICES-SkiaSharp",
        "start": "# skia\n",
        "end": "# END: skia\n",
    },
    "FTL": {
        "kind": "section",
        "of": "NOTICES-SkiaSharp",
        "start": "# freetype\n",
        "end": "# END: freetype\n",
    },
    "HarfBuzz": {
        "kind": "section",
        "of": "NOTICES-SkiaSharp",
        "start": "# HarfBuzz\n",
        "end": "# END: HarfBuzz\n",
    },
    "SQLite": {
        "kind": "section",
        "of": "NOTICES-SQLitePCLRaw",
        "start": "License for SQLite\n",
        "end": "License for MS Open Tech\n",
    },
    # -- upstream notice files, reproduced whole ----------------------------
    "NOTICES-dotnet": pkg_file(
        "microsoft.netcore.app.runtime.linux-x64", "10.0.12", "THIRD-PARTY-NOTICES.TXT"
    ),
    "NOTICES-SkiaSharp": pkg_file(
        "skiasharp.nativeassets.linux", "3.119.4", "THIRD-PARTY-NOTICES.txt"
    ),
    "NOTICES-Mvvm": pkg_file("communitytoolkit.mvvm", "8.4.2", "ThirdPartyNotices.txt"),
    "NOTICES-SQLitePCLRaw": {
        "kind": "repo",
        "name": "SQLitePCL.raw-NOTICE.TXT",
        "origin": "https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/NOTICE.TXT",
        "why": "Apache-2.0 section 4(d) requires the NOTICE file to travel with the "
               "distribution. None of the four SQLitePCLRaw packages contains it, so it "
               "is taken from the tag the shipped version was built from.",
    },
    "NOTICES-WindowsAppSDK": pkg_file("microsoft.windowsappsdk", "2.4.0", "NOTICE.txt"),
    "NOTICES-WindowsML": pkg_file(
        "microsoft.windows.ai.machinelearning", "2.1.74", "ThirdPartyNotices.txt"
    ),
    "NOTICES-WebView2": pkg_file("microsoft.web.webview2", "1.0.3719.77", "NOTICE.txt"),
}

# The .NET runtime packs for win-x64 and for linux-x64/osx-arm64 hold the same two
# documents with different line endings: 1139 against 1116 bytes for LICENSE.TXT
# and 78041 against 76623 for the notices, the difference being one byte per line.
# The LF copy is the one reproduced. `verify_dotnet_notice_equivalence` re-checks
# that on every run so this comment cannot become a claim nobody tests.
DOTNET_NOTICE_TWINS = [
    ("microsoft.netcore.app.runtime.linux-x64", "10.0.12"),
    ("microsoft.netcore.app.runtime.win-x64", "10.0.12"),
    ("microsoft.netcore.app.runtime.osx-arm64", "10.0.12"),
]


# ---------------------------------------------------------------------------
# NuGet cache
# ---------------------------------------------------------------------------
def nuget_root() -> Path:
    env = os.environ.get("NUGET_PACKAGES")
    if env:
        return Path(env)
    return Path.home() / ".nuget" / "packages"


def read_text(path: Path) -> str:
    """Reads a licence file without inventing characters.

    These files are a mix of UTF-8, UTF-8 with a BOM and the odd Latin-1 byte.
    `utf-8-sig` then `latin-1` reproduces all of them and never raises, which
    matters because a decode error here would silently drop a notice.
    """
    raw = path.read_bytes()
    for encoding in ("utf-8-sig", "latin-1"):
        try:
            text = raw.decode(encoding)
            break
        except UnicodeDecodeError:
            continue
    return text.replace("\r\n", "\n").replace("\r", "\n")


def trim_banners(text: str) -> str:
    """Drops the rule lines and the header continuation around a cut section.

    The two notice files sections are cut out of bracket their entries with rules of
    `#` or `-`, and SkiaSharp follows each section name with the project URL on a
    second comment line. Cutting between the name and the closing marker leaves
    those at the ends, so they come off here. Nothing inside the licence text is
    touched: a line is only dropped while it is a rule or a `# ` comment at the very
    start, or a rule at the very end.
    """
    lines = text.split("\n")

    def is_rule(line: str) -> bool:
        stripped = line.strip()
        return bool(stripped) and not stripped.strip("#-=_")

    while lines and (not lines[0].strip() or is_rule(lines[0]) or lines[0].startswith("# ")):
        lines.pop(0)
    while lines and (not lines[-1].strip() or is_rule(lines[-1])):
        lines.pop()
    return "\n".join(lines)


def package_dir(root: Path, package: str, version: str) -> Path:
    return root / package.lower() / version.lower()


def read_nuspec(root: Path, package: str, version: str) -> dict:
    """Pulls the declared licence, copyright and project URL out of a .nuspec.

    The nuspec is used rather than a licence-scanning tool's verdict because it is
    what the package's author declared. See packaging/README.md, "Why the list is
    read from the .nuspec", for the specific disagreement that decided this.
    """
    directory = package_dir(root, package, version)
    candidates = sorted(directory.glob("*.nuspec"))
    if not candidates:
        raise SystemExit(
            "no .nuspec for %s %s under %s. Restore the project first." % (package, version, directory)
        )
    text = read_text(candidates[0])

    def tag(name: str) -> str:
        match = re.search(r"<%s\b[^>]*>(.*?)</%s>" % (name, name), text, re.S)
        if not match:
            return ""
        return html.unescape(re.sub(r"\s+", " ", match.group(1)).strip())

    licence_element = re.search(r"<license\s+type=\"(\w+)\"[^>]*>(.*?)</license>", text, re.S)
    if licence_element:
        kind = licence_element.group(1)
        value = html.unescape(licence_element.group(2).strip())
    else:
        kind, value = "", ""

    return {
        "id": tag("id") or package,
        "version": tag("version") or version,
        "licence_kind": kind,
        "licence_value": value,
        "licence_url": tag("licenseUrl"),
        "copyright": tag("copyright"),
        "project_url": tag("projectUrl"),
        "authors": tag("authors"),
    }


# ---------------------------------------------------------------------------
# Publishes
# ---------------------------------------------------------------------------
def find_deps(platform: str, publish: Path, override: Path | None) -> Path:
    if override:
        if not override.is_file():
            raise SystemExit("no deps.json at %s" % override)
        return override

    inside = publish / "Altim.deps.json"
    if inside.is_file():
        return inside

    # A Native AOT publish is a single native image and carries no deps.json. The
    # build that produced it wrote one next to Altim.dll, describing the same
    # resolved graph, so that is the file to read.
    pattern = "src/Altim.App/bin/Release/net10.0-windows*/*/Altim.deps.json"
    found = sorted(REPO_ROOT.glob(pattern))
    if len(found) == 1:
        return found[0]
    if not found:
        raise SystemExit(
            "%s: no Altim.deps.json in %s and none under %s.\n"
            "A Native AOT publish carries no deps.json; pass --deps %s=<path> or publish first."
            % (platform, publish, pattern, platform)
        )
    raise SystemExit(
        "%s: %d candidate deps.json files under %s; pass --deps %s=<path> to choose.\n  %s"
        % (platform, len(found), pattern, platform, "\n  ".join(str(f) for f in found))
    )


def read_deps(path: Path) -> dict:
    """Returns the packages in the publish and the file each one contributes."""
    document = json.loads(read_text(path))

    # The last target is the runtime-identifier-specific one, which is the only
    # one that names native assets.
    target = list(document["targets"].values())[-1]

    packages: dict[str, str] = {}
    for key, entry in document.get("libraries", {}).items():
        if entry.get("type") != "package":
            continue
        name, _, version = key.partition("/")
        packages[name] = version

    owner: dict[str, str] = {}
    for key, entry in target.items():
        name, _, _version = key.partition("/")
        for section in ("runtime", "native", "resources"):
            for asset in entry.get(section, {}):
                owner.setdefault(asset.split("/")[-1], name)

    return {"packages": packages, "owner": owner, "path": path}


def framework_payload_index(
    root: Path, platform: str, packages: dict[str, str]
) -> dict[str, str]:
    """File name to package, for the packages that binplace a whole directory."""
    index: dict[str, str] = {}
    for package in FRAMEWORK_PAYLOAD_PACKAGES.get(platform, ()):
        version = packages.get(package)
        if not version:
            continue
        directory = package_dir(root, package, version)
        if not directory.is_dir():
            continue
        for sub_root, _dirs, files in os.walk(directory):
            for name in files:
                index.setdefault(name, package)
    return index


def audit_publish(
    platform: str, publish: Path, owner: dict[str, str], framework: dict[str, str]
) -> list[str]:
    """Every file in the publish must be attributable. Returns the unattributed."""
    undeclared = UNDECLARED_PAYLOAD.get(platform, {})
    unattributed = []
    for root, _dirs, files in os.walk(publish):
        for name in files:
            relative = os.path.relpath(os.path.join(root, name), publish).replace(os.sep, "/")
            if ALTIM_OWN.match(relative):
                continue
            base = relative.split("/")[-1]
            if base in owner or base in undeclared or base in framework:
                continue
            unattributed.append(relative)
    return sorted(unattributed)


# ---------------------------------------------------------------------------
# Text resolution
# ---------------------------------------------------------------------------
class Texts:
    def __init__(self, root: Path):
        self.root = root
        self._cache: dict[str, str] = {}
        self.provenance: dict[str, str] = {}

    def get(self, key: str) -> str:
        if key in self._cache:
            return self._cache[key]
        spec = TEXTS[key]
        if spec["kind"] == "package":
            path = package_dir(self.root, spec["package"], spec["version"]) / spec["name"]
            if not path.is_file():
                raise SystemExit(
                    "%s: expected %s inside %s %s and it is not there. The package may have "
                    "stopped shipping it; check before removing the notice."
                    % (key, spec["name"], spec["package"], spec["version"])
                )
            text = read_text(path)
            self.provenance[key] = "%s, from the restored package %s %s" % (
                spec["name"], spec["package"], spec["version"],
            )
        elif spec["kind"] == "repo":
            path = TEXTS_DIR / spec["name"]
            if not path.is_file():
                raise SystemExit("%s: missing %s" % (key, path))
            text = read_text(path)
            self.provenance[key] = "packaging/notices/texts/%s, fetched from %s. %s" % (
                spec["name"], spec["origin"], spec["why"],
            )
        elif spec["kind"] == "section":
            whole = self.get(spec["of"])
            start = whole.find(spec["start"])
            end = whole.find(spec["end"], start + 1)
            if start < 0 or end < 0:
                raise SystemExit(
                    "%s: could not find the %r .. %r section in %s. The upstream notice "
                    "file changed shape; re-read it before changing the markers."
                    % (key, spec["start"], spec["end"], spec["of"])
                )
            text = trim_banners(whole[start + len(spec["start"]):end])
            self.provenance[key] = "cut from %s, which is reproduced in full below" % spec["of"]
        else:
            raise SystemExit("%s: unknown source kind %r" % (key, spec["kind"]))
        self._cache[key] = text.strip("\n")
        return self._cache[key]


def verify_dotnet_notice_equivalence(root: Path) -> str:
    """Confirms the .NET notice files differ only by line ending, and says so."""
    seen = {}
    for package, version in DOTNET_NOTICE_TWINS:
        path = package_dir(root, package, version) / "THIRD-PARTY-NOTICES.TXT"
        if not path.is_file():
            return ("Only one runtime pack is restored, so the claim that the win-x64 copy "
                    "is the same document could not be re-checked on this run.")
        seen[package] = read_text(path)
    distinct = set(seen.values())
    if len(distinct) != 1:
        raise SystemExit(
            "the .NET runtime packs' THIRD-PARTY-NOTICES.TXT files differ by more than "
            "line endings. Reproduce each one separately rather than the LF copy alone."
        )
    return ("Re-checked on this run: the win-x64, linux-x64 and osx-arm64 runtime packs "
            "carry the same document, differing only in line endings.")


def measure_windows_notice_overlap(texts: "Texts") -> str:
    """Measures how far the two Microsoft aggregate notices duplicate each other.

    Both are Microsoft's notices for substantially the same set of ONNX Runtime
    dependencies, and together they are two thirds of this file. Both are still
    reproduced whole: deciding that one supersedes the other would mean editing
    somebody else's notice on a judgement. The overlap is measured rather than
    asserted so a reader can see why the duplication is there and re-check it.
    """
    sdk = set(line.strip() for line in texts.get("NOTICES-WindowsAppSDK").splitlines())
    ml_lines = [
        line.strip() for line in texts.get("NOTICES-WindowsML").splitlines()
        if line.strip() and line.strip().strip("_-") != ""
    ]
    outside = [line for line in ml_lines if line not in sdk]
    return (
        "Measured on this run: %d of its %d substantive lines appear verbatim in the "
        "Windows App SDK notice above, and the %d that do not are its preamble, which says "
        "the same thing in different line breaks. Both are reproduced whole anyway, because "
        "choosing which of two upstream notices to drop is not a judgement this file makes."
        % (len(ml_lines) - len(outside), len(ml_lines), len(outside))
    )


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------
def bullet(text: str) -> str:
    """One markdown list item, hard-wrapped so the file reads as plain text too."""
    return "\n".join(textwrap.wrap(text, width=88, initial_indent="- ", subsequent_indent="  "))


def block(text: str) -> str:
    """Fences a licence text so markdown cannot reflow it.

    A licence that contains a run of backticks would close the block early, so the
    fence is always one backtick longer than the longest run in the text.
    """
    longest = max((len(run) for run in re.findall(r"`+", text)), default=0)
    bar = "`" * max(3, longest + 1)
    return "%s\n%s\n%s" % (bar, text, bar)


def licence_phrase(nuspec: dict) -> str:
    kind = nuspec["licence_kind"]
    value = nuspec["licence_value"]
    if kind == "expression":
        return "%s (declared as an SPDX expression by the package)" % value
    if kind == "file":
        return "declared by the package as the file %s, reproduced below" % value
    if nuspec["licence_url"]:
        return "not declared as an SPDX expression; the package gives only the URL %s" % nuspec["licence_url"]
    return "NOT DECLARED BY THE PACKAGE"


def render(state: dict) -> str:
    texts: Texts = state["texts"]
    observed: dict[str, dict] = state["observed"]
    platforms = [p for p in PLATFORM_ORDER if p in observed]
    out: list[str] = []
    w = out.append

    w("# Third-party notices for Altim")
    w("")
    w("Altim is MIT licensed; `LICENSE` at the root of the repository is that grant and it")
    w("covers Altim's own code alone. Every artefact also carries a large amount of other")
    w("people's software, some of it compiled into the same file as Altim's, and this")
    w("document is the notice for all of it.")
    w("")
    w("It matters most on Windows, where the publish is Native AOT: the .NET runtime,")
    w("Avalonia, the SQLite wrapper, the font and Altim's own code are one `Altim.exe` and")
    w("there is nothing in that file a recipient can open to find out what is inside it.")
    w("")
    w("The vendor marks Altim displays are covered by `TRADEMARKS.md`, not by this file and")
    w("not by the MIT grant.")
    w("")
    w("---")
    w("")

    # -- provenance ---------------------------------------------------------
    w("## How this file was produced")
    w("")
    w("Generated. Do not edit it by hand; edit `packaging/notices/build-notices.py` and")
    w("regenerate, or the next regeneration will silently discard the edit.")
    w("")
    w("The component list is read from `Altim.deps.json`, which names what a publish")
    w("actually contains, rather than from the project files, which also name analysers,")
    w("build tooling and test-only packages that are never distributed. Each publish")
    w("directory is then read back file by file and anything the dependency graph does not")
    w("account for fails the run.")
    w("")
    w("Licence text is read out of the restored packages under the NuGet cache wherever the")
    w("package carries it. Three documents are not in any package and are committed under")
    w("`packaging/notices/texts/`; each says below where it came from and why.")
    w("")
    w("To regenerate, publish all three platforms and run the generator:")
    w("")
    w(block(
        "dotnet publish src/Altim.App -c Release -r win-x64   --self-contained "
        "-o dist/.publish/win-x64\n"
        "dotnet publish src/Altim.App -c Release -r linux-x64 --self-contained "
        "-p:AltimPortableBuild=true -p:DebugType=none --artifacts-path dist/.art/linux "
        "-o dist/.publish/linux-x64\n"
        "dotnet publish src/Altim.App -c Release -r osx-arm64 --self-contained "
        "-p:AltimPortableBuild=true -p:DebugType=none --artifacts-path dist/.art/osx "
        "-o dist/.publish/osx-arm64\n"
        "\n"
        "python packaging/notices/build-notices.py"
    ))
    w("")
    w("The Windows publish has to run on Windows: Native AOT does not cross-compile, and it")
    w("is the Windows target framework that decides which packages are in the graph. The")
    w("other two cross-publish from any host. `--check` regenerates and compares instead of")
    w("writing, for CI.")
    w("")
    w("This run read:")
    w("")
    for platform in platforms:
        entry = observed[platform]
        w(bullet("**%s** publish `%s`, dependency graph `%s`, %d packages, %d payload "
                 "files, all attributed."
                 % (PLATFORM_LABEL[platform], entry["publish_display"], entry["deps_display"],
                    len(entry["packages"]), entry["file_count"])))
    for platform in PLATFORM_ORDER:
        if platform not in observed:
            w(bullet("**%s** was NOT read on this run, so anything below marked %s is carried "
                     "from the previous generation and may be stale."
                     % (PLATFORM_LABEL[platform], PLATFORM_LABEL[platform])))
    w("")
    w("---")
    w("")

    # -- what is in each artefact ------------------------------------------
    w("## What each artefact contains")
    w("")
    for platform in platforms:
        entry = observed[platform]
        w("### %s" % PLATFORM_LABEL[platform])
        w("")
        for line in textwrap.wrap(PACK_ADJUSTMENTS[platform]["note"], width=88):
            w(line)
        w("")
        rows = entry["payload_rows"]
        width = max(len(name) for name, _ in rows)
        lines = ["%-*s  %s" % (width, name, note) for name, note in rows]
        w(block("\n".join(lines)))
        w("")

    w("---")
    w("")

    # -- components ---------------------------------------------------------
    w("## Components")
    w("")
    w("Every NuGet package in the resolved graph of a publish, with the licence and the")
    w("copyright line its own `.nuspec` declares. Packages referenced by the repository but")
    w("not distributed, which is to say analysers, build tooling and everything with")
    w("`PrivateAssets=all` or `IncludeAssets=none`, are deliberately absent: they are not in")
    w("any `Altim.deps.json` and so they are not here.")
    w("")
    w("The Windows list is the longer one, and it is the graph rather than the final image.")
    w("Native AOT links what it can reach and discards the rest, so a package listed as")
    w("shipping in Windows may contribute nothing to `Altim.exe`. Listing it anyway is the")
    w("safe direction for a notice; claiming it had been removed would not be.")
    w("")
    for package in sorted(state["inventory"], key=str.lower):
        record = state["inventory"][package]
        nuspec = record["nuspec"]
        ships = ", ".join(PLATFORM_LABEL[p] for p in PLATFORM_ORDER if p in record["platforms"])
        w("**%s** %s" % (nuspec["id"], nuspec["version"]))
        w("")
        w(bullet("Licence: %s" % licence_phrase(nuspec)))
        w(bullet("Copyright: %s" % (nuspec["copyright"] or
                                    "NOT DECLARED BY THE PACKAGE; see the project page")))
        if nuspec["project_url"]:
            w("- Project: %s" % nuspec["project_url"])
        w("- Ships in: %s" % ships)
        w("")

    w("### Components inside a binary")
    w("")
    w("These are compiled into a library or embedded as a resource. No dependency scan can")
    w("see them, so each one was confirmed against the built binary and the confirmation is")
    w("recorded with it.")
    w("")
    for item in state["embedded"]:
        ships = ", ".join(PLATFORM_LABEL[p] for p in PLATFORM_ORDER if p in item["platforms"])
        w("**%s** %s" % (item["name"], item["version"]))
        w("")
        w(bullet("Licence: %s" % item["licence"]))
        w(bullet("Copyright: %s" % item["copyright"]))
        w(bullet("Carried by: %s" % item["carrier"]))
        w(bullet("Where: %s" % item["where"]))
        w(bullet("Ships in: %s" % ships))
        w(bullet("How that was established: %s" % item["evidence"]))
        w("")

    w("### The .NET runtime")
    w("")
    w("Every artefact is self-contained, so the whole .NET 10 runtime travels with it:")
    w("statically linked into `Altim.exe` on Windows, and as `libcoreclr`, `libclrjit`, the")
    w("`System.*` assemblies and the native shims on Linux and macOS.")
    w("")
    w("- Licence: MIT")
    w("- Copyright: Copyright (c) .NET Foundation and Contributors. All rights reserved.")
    w("- Project: https://github.com/dotnet/runtime")
    w("- Ships in: Windows, Linux, macOS")
    w("")
    w("\n".join(textwrap.wrap(
        "The runtime carries third parties of its own. Its notice file is reproduced in "
        "full below. %s" % state["dotnet_equivalence"], width=88)))
    w("")
    w("---")
    w("")

    # -- what could not be resolved ----------------------------------------
    w("## What this file does not establish")
    w("")
    w("Stated rather than guessed at, because a notice that asserts something it has not")
    w("checked is worse than one that admits a gap.")
    w("")
    for line in state["unresolved"]:
        w(bullet(line))
    w("")
    w("---")
    w("")

    # -- licence texts ------------------------------------------------------
    w("## Licence texts")
    w("")
    w("### MIT")
    w("")
    w("Most of the packages above are MIT. The terms are the same in each; the copyright")
    w("line differs, and each package's own line is given with it in the list above. The")
    w("text below is the copy inside the restored SkiaSharp %s package, which carries its"
      % state["skia_version"])
    w("own copyright line as an example of the form.")
    w("")
    w(block(texts.get("MIT")))
    w("")

    for key, heading, lead in state["licence_sections"]:
        text = texts.get(key)
        w("### %s" % heading)
        w("")
        for line in lead:
            w("\n".join(textwrap.wrap(line, width=88)))
        w("")
        w("Source: %s." % texts.provenance[key])
        w("")
        w(block(text))
        w("")

    w("---")
    w("")

    # -- verbatim notice files ---------------------------------------------
    w("## Upstream notice files, reproduced verbatim")
    w("")
    w("Each of these is a document the upstream project ships with the binaries Altim")
    w("redistributes, copied here unchanged. They are upstream's statement about upstream's")
    w("build, not Altim's, and some of them name components that a particular build may not")
    w("contain. Reproducing them whole is deliberate: deciding which sections apply would")
    w("mean editing somebody else's notice on a guess.")
    w("")
    for key, heading, lead in state["notice_sections"]:
        text = texts.get(key)
        w("### %s" % heading)
        w("")
        for line in lead:
            w("\n".join(textwrap.wrap(line, width=88)))
        w("")
        w("Source: %s." % texts.provenance[key])
        w("")
        w(block(text))
        w("")

    return "\n".join(out).rstrip("\n") + "\n"


# ---------------------------------------------------------------------------
# Assembly
# ---------------------------------------------------------------------------
# The packaging scripts copy these two in, so they are added to every table by hand
# rather than read off disk. Otherwise a table would change depending on whether a
# packaging script had already run against the publish directory it is reading.
PACKAGED_DOCS = ("LICENSE", "THIRD-PARTY-NOTICES.md")


def payload_rows(
    platform: str, publish: Path, owner: dict[str, str], framework: dict[str, str]
) -> list[tuple[str, str]]:
    """One row per distinct file in the artefact, collapsing the noisy groups."""
    undeclared = UNDECLARED_PAYLOAD.get(platform, {})
    adjust = PACK_ADJUSTMENTS[platform]
    rows: dict[str, str] = {}
    runtime_assemblies = 0
    icons = 0
    framework_files: dict[str, int] = {}
    for root, _dirs, files in os.walk(publish):
        for name in files:
            relative = os.path.relpath(os.path.join(root, name), publish).replace(os.sep, "/")
            base = relative.split("/")[-1]
            if relative.startswith("assets/"):
                icons += 1
                continue  # collapsed into one row below
            if relative in PACKAGED_DOCS:
                continue  # added below, whether or not a packaging script has run
            if adjust["drop"] and relative.endswith(tuple(adjust["drop"])):
                continue
            if ALTIM_OWN.match(relative):
                rows[relative] = "Altim"
                continue
            source = owner.get(base) or undeclared.get(base)
            if source and source.startswith("runtimepack."):
                runtime_assemblies += 1
                continue
            if not source and base in framework:
                framework_files[framework[base]] = framework_files.get(framework[base], 0) + 1
                continue
            rows[relative] = source or "UNATTRIBUTED"
    for name, source in adjust["add"]:
        rows[name] = source
    for name in PACKAGED_DOCS:
        rows[name] = "Altim, copied in by the packaging script"
    ordered = sorted(rows.items())
    # The collapsed rows go last rather than wherever a bracket sorts.
    for package, count in sorted(framework_files.items()):
        ordered.append(
            ("%d more files" % count,
             "%s, the Windows App Runtime copied in whole" % package)
        )
    if icons:
        ordered.append(("assets/icons/, %d files" % icons, "Altim"))
    if runtime_assemblies:
        ordered.append(
            ("%d more files" % runtime_assemblies, "the .NET 10 runtime, self-contained")
        )
    return ordered


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Generates THIRD-PARTY-NOTICES.md from what Altim actually ships.",
    )
    parser.add_argument(
        "--publish", action="append", default=[], metavar="PLATFORM=DIR",
        help="a publish directory to read, as windows=, linux= or macos=. Repeatable. "
             "Defaults to dist/.publish/{win-x64,linux-x64,osx-arm64}.",
    )
    parser.add_argument(
        "--deps", action="append", default=[], metavar="PLATFORM=FILE",
        help="an explicit Altim.deps.json, for a Native AOT publish that has none.",
    )
    parser.add_argument("--out", default=str(REPO_ROOT / "THIRD-PARTY-NOTICES.md"))
    parser.add_argument(
        "--check", action="store_true",
        help="regenerate and compare instead of writing; exit 1 if the file is stale.",
    )
    args = parser.parse_args(argv)

    def split_pairs(values: list[str], what: str) -> dict[str, str]:
        pairs = {}
        for value in values:
            platform, _, path = value.partition("=")
            if platform not in PLATFORM_ORDER or not path:
                raise SystemExit("--%s wants PLATFORM=PATH with PLATFORM one of %s, got %r"
                                 % (what, ", ".join(PLATFORM_ORDER), value))
            pairs[platform] = path
        return pairs

    publishes = split_pairs(args.publish, "publish")
    if not publishes:
        defaults = {"windows": "win-x64", "linux": "linux-x64", "macos": "osx-arm64"}
        for platform, rid in defaults.items():
            candidate = REPO_ROOT / "dist" / ".publish" / rid
            if candidate.is_dir():
                publishes[platform] = str(candidate)
        if not publishes:
            raise SystemExit(
                "no publish directories. Publish all three platforms first, or pass "
                "--publish PLATFORM=DIR. See the module docstring."
            )
    deps_overrides = split_pairs(args.deps, "deps")

    root = nuget_root()
    if not root.is_dir():
        raise SystemExit("no NuGet cache at %s. Set NUGET_PACKAGES or restore first." % root)

    observed: dict[str, dict] = {}
    inventory: dict[str, dict] = {}
    failures: list[str] = []

    for platform in PLATFORM_ORDER:
        if platform not in publishes:
            continue
        publish = Path(publishes[platform]).resolve()
        if not publish.is_dir():
            raise SystemExit("%s: no publish directory at %s" % (platform, publish))
        override = deps_overrides.get(platform)
        deps_path = find_deps(platform, publish, Path(override).resolve() if override else None)
        deps = read_deps(deps_path)

        framework = framework_payload_index(root, platform, deps["packages"])
        unattributed = audit_publish(platform, publish, deps["owner"], framework)
        if unattributed:
            failures.append(
                "%s: %d file(s) in %s are attributed to nothing:\n    %s\n"
                "  Add them to UNDECLARED_PAYLOAD with the package that placed them, or to "
                "ALTIM_OWN if Altim produced them. Do not ship a file no notice covers."
                % (platform, len(unattributed), publish, "\n    ".join(unattributed))
            )
            continue

        # LICENSE and THIRD-PARTY-NOTICES.md are excluded, because the packaging
        # scripts copy them into the publish directory and a count that included
        # them would depend on whether one had already run. Everything else in the
        # directory is counted and has to be attributed.
        file_count = sum(
            1
            for walk_root, _d, files in os.walk(publish)
            for name in files
            if os.path.relpath(os.path.join(walk_root, name), publish).replace(os.sep, "/")
            not in PACKAGED_DOCS
        )
        for package, version in deps["packages"].items():
            record = inventory.setdefault(
                package, {"nuspec": read_nuspec(root, package, version), "platforms": set()}
            )
            record["platforms"].add(platform)
            if record["nuspec"]["version"] != version:
                failures.append(
                    "%s: %s is %s here but %s elsewhere. One notices file cannot describe two "
                    "versions; publish every platform from the same commit."
                    % (platform, package, version, record["nuspec"]["version"])
                )
        for base, package in UNDECLARED_PAYLOAD.get(platform, {}).items():
            if package in inventory:
                inventory[package]["platforms"].add(platform)
            elif base in [f for _r, _d, fs in os.walk(publish) for f in fs]:
                failures.append(
                    "%s: %s placed %s but is not in the dependency graph, so its version is "
                    "unknown." % (platform, package, base)
                )

        observed[platform] = {
            "publish_display": os.path.relpath(publish, REPO_ROOT).replace(os.sep, "/"),
            "deps_display": os.path.relpath(deps_path, REPO_ROOT).replace(os.sep, "/"),
            "packages": deps["packages"],
            "file_count": file_count,
            "payload_rows": payload_rows(platform, publish, deps["owner"], framework),
        }

    if failures:
        for failure in failures:
            sys.stderr.write("ERROR  %s\n" % failure)
        return 1

    texts = Texts(root)

    # Each section names the package whose presence in a publish makes it apply.
    # A component that stops shipping stops being described here, which is the
    # other half of the accuracy this file is for: reproducing the Windows ML
    # terms after that payload has gone would assert that Altim carries an
    # inference runtime it does not.
    def ships(*packages: str) -> bool:
        return any(
            any(name == package or name.startswith(package + ".") for name in inventory)
            for package in packages
        )

    licence_sections = [
        (("SQLitePCLRaw.core",), "Apache-2.0", "Apache License 2.0",
         ["Applies to the four SQLitePCLRaw packages. Section 4(a) requires this copy to "
          "travel with the distribution and section 4(d) requires the project's NOTICE "
          "file, which is reproduced further down."]),
        (("Avalonia.Fonts.Inter",), "OFL-1.1", "SIL Open Font License 1.1",
         ["Applies to the Inter typeface, which is embedded in the application on all three "
          "platforms. The font's own name table carries the copyright line and names this "
          "licence, so the notice was partially present already; the terms were not."]),
        (("SkiaSharp",), "BSD-3-Skia", "BSD 3-Clause, Skia",
         ["Clause 2 requires this to be reproduced in the materials that accompany a binary "
          "distribution, which is what this file is."]),
        (("Avalonia.Angle.Windows.Natives",), "BSD-3-ANGLE", "BSD 3-Clause, ANGLE as built by the Avalonia project",
         ["Windows only. This is the ANGLE in `av_libglesv2.dll`. SkiaSharp's notice carries "
          "a separate ANGLE section for a Microsoft build with a different copyright line; "
          "that one is reproduced there, unmerged."]),
        (("Microsoft.Web.WebView2",), "BSD-3-WebView2", "BSD 3-Clause, Microsoft Edge WebView2 SDK",
         ["Windows only, and see \"What this file does not establish\" above: Altim does not "
          "use WebView2, but the Windows App SDK meta-package places its two libraries in "
          "the publish, so they ship."]),
        (("SkiaSharp.NativeAssets.Linux",), "FTL", "The FreeType Project License",
         ["Linux only. FreeType is statically linked into `libSkiaSharp.so`; the Windows and "
          "macOS builds of the same library do not contain it. The credit the licence asks "
          "for: Portions of this software are copyright The FreeType Project "
          "(www.freetype.org). All rights reserved."]),
        (("HarfBuzzSharp",), "HarfBuzz", "HarfBuzz",
         ["HarfBuzz ships as its own library on all three platforms and is also inside "
          "Skia's build."]),
        (("SQLitePCLRaw.lib.e_sqlite3",), "SQLite", "SQLite",
         ["SQLite itself is in the public domain. The SQLitePCLRaw layer around it is "
          "Apache-2.0 and is covered above."]),
        (("Microsoft.WindowsAppSDK",), "EULA-WindowsAppSDK", "Microsoft Software License Terms, Windows App SDK",
         ["Windows only. Not an open-source licence. These are the terms under which the "
          "Windows App SDK libraries in the Windows publish are redistributable; section 3 "
          "is the grant that permits it."]),
        (("Microsoft.Windows.AI.MachineLearning",), "EULA-WindowsML", "Microsoft Software License Terms, Windows ML Runtime",
         ["Windows only. Not an open-source licence. Covers `DirectML.dll`, "
          "`onnxruntime.dll` and `Microsoft.Windows.AI.MachineLearning.dll`."]),
    ]

    notice_sections = [
        ((), "NOTICES-dotnet", "The .NET runtime",
         ["Every artefact carries the .NET 10 runtime."]),
        (("SkiaSharp",), "NOTICES-SkiaSharp", "SkiaSharp and HarfBuzzSharp",
         ["Covers `libSkiaSharp` and `libHarfBuzzSharp`. The same document ships in the "
          "win32, linux and macOS native-asset packages, byte for byte, and it names every "
          "third party Skia's build system can incorporate rather than only those in one "
          "platform's build."]),
        (("CommunityToolkit.Mvvm",), "NOTICES-Mvvm", "CommunityToolkit.Mvvm", []),
        (("SQLitePCLRaw.core",), "NOTICES-SQLitePCLRaw", "SQLitePCL.raw",
         ["Required by Apache-2.0 section 4(d). It covers builds Altim does not ship as well "
          "as the one it does: Altim uses `e_sqlite3`, so the SQLCipher and OpenSSL sections "
          "below describe the `e_sqlcipher` builds and not anything in an Altim artefact. "
          "The file is reproduced whole because that is what section 4(d) asks for."]),
        (("Microsoft.WindowsAppSDK",), "NOTICES-WindowsAppSDK", "Windows App SDK",
         ["Windows only."]),
        (("Microsoft.Windows.AI.MachineLearning",), "NOTICES-WindowsML", "Microsoft.Windows.AI.MachineLearning",
         ["Windows only. Covers `DirectML.dll`, `onnxruntime.dll` and "
          "`Microsoft.Windows.AI.MachineLearning.dll`. " + measure_windows_notice_overlap(texts)
          if ships("Microsoft.Windows.AI.MachineLearning") and ships("Microsoft.WindowsAppSDK")
          else "Windows only."]),
        (("Microsoft.Web.WebView2",), "NOTICES-WebView2", "Microsoft Edge WebView2", ["Windows only."]),
    ]

    licence_sections = [
        (key, heading, lead) for requires, key, heading, lead in licence_sections
        if not requires or ships(*requires)
    ]
    notice_sections = [
        (key, heading, lead) for requires, key, heading, lead in notice_sections
        if not requires or ships(*requires)
    ]
    embedded = [
        item for item in EMBEDDED
        if ships(*item["requires"]) and set(item["platforms"]) & set(observed)
    ]

    unresolved = [
        "The FreeType version inside `libSkiaSharp.so` could not be determined. The binary "
        "carries FreeType's module names and symbols but no version string, and SkiaSharp "
        "does not publish which revision it vendored into a given build. The FTL credit "
        "line is therefore given without a year.",
        "What is inside `libSkiaSharp` beyond Skia itself is taken from SkiaSharp's own "
        "notice file rather than measured. That document lists everything Skia's build can "
        "incorporate, including components such as SDL, Dear ImGui and libmicrohttpd that "
        "are developer tooling and are unlikely to be in a shipped build. Nothing here "
        "asserts that they are; the notice is reproduced as upstream wrote it.",
        "Whether Skia's build statically includes ICU is not established. The publish "
        "directories contain no ICU data file, and on all three platforms .NET uses the "
        "operating system's ICU or NLS, so no ICU is redistributed by the runtime. "
        "SkiaSharp's notice carries an ICU section and it is reproduced.",
        "`Avalonia.Angle.Windows.Natives` declares its licence as a file rather than as an "
        "SPDX expression. The file is BSD 3-Clause in substance and is reproduced in full; "
        "no SPDX identifier is claimed on the package's behalf.",
    ]
    if ships("Microsoft.Web.WebView2"):
        unresolved.append(
            "`Microsoft.Web.WebView2` also declares a file rather than an SPDX expression. "
            "Its text is BSD 3-Clause in substance and is reproduced; the package metadata "
            "claims no identifier and neither does this file."
        )
    if ships("Microsoft.Windows.AI.MachineLearning") or ships("Microsoft.Web.WebView2"):
        unresolved.append(
            "Altim uses neither Windows ML nor WebView2. They are in the Windows publish "
            "because a Windows App SDK package placed them there, and they are listed and "
            "licensed because they ship, not because Altim calls them. Referencing "
            "Microsoft.WindowsAppSDK.Foundation rather than the umbrella package is what "
            "keeps them out; see packaging/README.md, \"Package size\"."
        )
    if ships("Microsoft.WindowsAppSDK"):
        unresolved.append(
            "The Microsoft terms reproduced below are licences to the recipient from "
            "Microsoft, not grants Altim can make. They are included so that a recipient has "
            "them, which is what those terms require of anyone redistributing the files."
        )

    skia_version = inventory.get("SkiaSharp", {}).get("nuspec", {}).get("version", "3.119.4")

    state = {
        "texts": texts,
        "observed": observed,
        "inventory": inventory,
        "licence_sections": licence_sections,
        "notice_sections": notice_sections,
        "unresolved": unresolved,
        "embedded": embedded,
        "dotnet_equivalence": verify_dotnet_notice_equivalence(root),
        "skia_version": skia_version,
    }

    document = render(state)

    out_path = Path(args.out)
    if args.check:
        current = out_path.read_text(encoding="utf-8") if out_path.is_file() else ""
        if current.replace("\r\n", "\n") == document:
            sys.stdout.write("THIRD-PARTY-NOTICES.md is up to date.\n")
            return 0
        sys.stderr.write(
            "THIRD-PARTY-NOTICES.md is stale. Run packaging/notices/build-notices.py.\n"
        )
        return 1

    out_path.write_text(document, encoding="utf-8", newline="\n")
    sys.stdout.write(
        "wrote %s  (%d bytes, %d packages, %d platforms read)\n"
        % (out_path, len(document.encode("utf-8")), len(inventory), len(observed))
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
