<#
.SYNOPSIS
    Builds the Windows artefacts: a Velopack installer, a portable zip and a release
    feed that later versions can be delta'd against.

.DESCRIPTION
    Two steps, in this order, and the order matters.

      1. `dotnet publish -c Release -r <rid>` produces the shipping build. Altim.App
         turns PublishAot on for exactly this combination (Release, a runtime
         identifier, a Windows target framework), so this is the ahead-of-time
         compiled, self-contained payload the README's performance numbers are
         measured against. A `dotnet build` is a different, slower program.

      2. `vpk pack` turns that folder into a release: Altim-win-Setup.exe,
         Altim-win-Portable.zip, a full .nupkg and a RELEASES file.

    Delta packages come out of step 2 for free, but only if the previous release's
    .nupkg is sitting in -OutputDir when vpk runs. That is what -OutputDir being a
    persistent "releases" directory means: the release workflow downloads the
    published feed into it first (`vpk download github`), so the pack that follows
    can diff against it. Point -OutputDir at an empty directory and you get a
    correct full release with no delta, which is exactly right for the first one.

.PARAMETER Version
    SemVer2, without a leading v. Stamped into the assembly and used as the release
    version. Defaults to the <Version> in Altim.App.csproj.

.PARAMETER Runtime
    A Windows runtime identifier: win-x64 (default) or win-arm64. Native AOT cannot
    cross-compile, so win-arm64 has to be built on an arm64 host.

.PARAMETER OutputDir
    Where the release lands, and where previous releases are read from for delta
    generation. Defaults to dist/windows at the repository root.

.PARAMETER PublishDir
    Intermediate publish folder. Defaults to a scratch directory under dist/.

.PARAMETER SignParams
    Passed to vpk as --signParams, which forwards them to signtool.exe. Absent
    means every binary in the package is unsigned, which is the current state of
    the world: see packaging/README.md.

.PARAMETER SkipPublish
    Package whatever is already in -PublishDir. For iterating on packaging without
    paying for an AOT compile each time.

.EXAMPLE
    ./packaging/windows/build-windows.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$PublishDir,
    [string]$SignParams,
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VpkVersion = '1.2.0'

# Join-Path with more than two arguments is PowerShell 7 only, and this script has
# to run under the Windows PowerShell 5.1 that ships with Windows.
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$project = Join-Path $repoRoot 'src/Altim.App/Altim.App.csproj'
$icon = Join-Path $repoRoot 'assets/icons/altim.ico'

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist/windows/$Runtime" }
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot "dist/.publish/$Runtime" }

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# Version
# ---------------------------------------------------------------------------
if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw 'No -Version given and no <Version> found in Altim.App.csproj.' }
    Write-Step "Version not given; using <Version> from the project: $Version"
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+([-+].*)?$') {
    throw "Version '$Version' is not SemVer2. Velopack will refuse it."
}

# ---------------------------------------------------------------------------
# Toolchain
# ---------------------------------------------------------------------------
# The ILC link step shells out to vswhere to find the MSVC linker, and a Build
# Tools-only install does not put vswhere on PATH. Prepending it is harmless when
# it is already there and is the difference between a build and MSB3073 otherwise.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

$env:PATH = "$env:PATH;$(Join-Path $env:USERPROFILE '.dotnet/tools')"
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Step "Installing the Velopack CLI (vpk $VpkVersion)"
    dotnet tool install --global vpk --version $VpkVersion
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install vpk failed with exit code $LASTEXITCODE." }
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step "Publishing $Runtime (Release, Native AOT) to $PublishDir"
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

    dotnet publish $project `
        --configuration Release `
        --runtime $Runtime `
        --self-contained `
        -p:Version=$Version `
        --output $PublishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $PublishDir 'Altim.exe'
if (-not (Test-Path $exe)) { throw "No Altim.exe in $PublishDir." }

# A Native AOT publish is a single native image; a framework-dependent one drops
# Altim.dll beside the host. If Altim.dll is here the AOT condition in
# Altim.App.csproj did not fire and this is not the build the README measures.
if (Test-Path (Join-Path $PublishDir 'Altim.dll')) {
    Write-Warning 'Altim.dll is present: this publish is NOT ahead-of-time compiled.'
}

# ---------------------------------------------------------------------------
# Pack
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$previous = @(Get-ChildItem -Path $OutputDir -Filter '*.nupkg' -ErrorAction SilentlyContinue)
if ($previous.Count -gt 0) {
    Write-Step "$($previous.Count) previous package(s) in $OutputDir; deltas will be generated against them"
} else {
    Write-Step "No previous packages in $OutputDir; this release will be full-only"
}

$packArgs = @(
    'pack'
    '--packId', 'Altim'
    '--packVersion', $Version
    '--packDir', $PublishDir
    '--packTitle', 'Altim'
    '--packAuthors', 'mpge'
    '--mainExe', 'Altim.exe'
    '--icon', $icon
    '--outputDir', $OutputDir
    '--runtime', $Runtime
    # Altim lives in the tray and starts at login. A Start menu entry is how you
    # find it the first time; a desktop icon for a process with no main window is
    # clutter, so the Velopack default of Desktop,StartMenuRoot is narrowed.
    '--shortcuts', 'StartMenuRoot'
    # BestSpeed is Velopack's default and is the right one here: the payload is a
    # single 28MB native image, so BestSize spends minutes to save little.
    '--delta', 'BestSpeed'
)

if ($SignParams) {
    Write-Step 'Signing with the supplied signtool parameters'
    $packArgs += @('--signParams', $SignParams)
} else {
    Write-Step 'No signing parameters: Setup.exe and Altim.exe will be UNSIGNED'
}

Write-Step "Packing Altim $Version"
& vpk @packArgs
if ($LASTEXITCODE -ne 0) {
    # The common one is "There is a release in channel win which is equal or
    # greater to the current version": the output directory is the release feed,
    # so packing a version it already contains is refused rather than overwritten.
    throw ("vpk pack failed with exit code $LASTEXITCODE. If it refused because a release " +
           "equal or greater than $Version is already in $OutputDir, bump the version or " +
           "empty that directory.")
}

# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------
# .nupkg, RELEASES, releases.win.json and assets.win.json are the update feed, not
# incidental build output: an installed copy reads them to discover that a newer
# version exists. A release that omits them is one nobody can update from.
$artefacts = Get-ChildItem -Path $OutputDir -File |
    Where-Object { $_.Extension -in '.exe', '.zip', '.nupkg', '.json' -or $_.Name -like 'RELEASES*' } |
    Sort-Object Name

$checksums = Join-Path $OutputDir 'SHA256SUMS.txt'
$artefacts |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -Path $checksums -Encoding ascii

Write-Host ''
Write-Step "Done: $OutputDir"
$artefacts | ForEach-Object { '    {0,-46} {1,9:N2} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host ''
if (-not $SignParams) {
    Write-Host '    UNSIGNED. SmartScreen will warn on first run. See packaging/README.md.' -ForegroundColor Yellow
}
