<#
.SYNOPSIS
  T-125: the preset root reached the built app. Fails when a build output has no presets/ directory beside
  mpcore.dll, or when it is missing a preset the repo ships.

.DESCRIPTION
  The core scans one preset root at renderer creation: MPCORE_PRESET_ROOT when it is set, otherwise presets/
  next to the module (native/mpcore/src/render/preset.cpp, default_preset_root). When that directory is not in
  the build output the app still runs and still draws - it falls back to the preset compiled into the core - so
  the symptom is a short preset list and nothing in the log that says why. That is the failure this script
  exists to make loud.

  IT NEVER READS MPCORE_PRESET_ROOT. A developer with that variable set sees every preset on their machine
  whatever the build did, which is exactly how this defect stayed invisible; a check that consulted it would
  pass for the same reason. What is asserted here is the shape of the artifact on disk: mpcore.dll, presets/
  beside it, and inside it every preset directory the repo's presets/ declares, each with its preset.json and
  the shader that manifest names. If the variable is set in this environment the script says so and ignores it.

  The expectations come from the repo's presets/ rather than a list written down here, so E4-S4 and E4-S5 are
  covered by this check the moment they add their directories, with no edit to it.

.PARAMETER Root
  A built app directory to check: the folder holding Tunqio.exe and mpcore.dll. Defaults to the unpackaged
  Release output. Repeatable.

.PARAMETER Msix
  A .msix to check. It is a zip; the payload is expanded to a scratch folder and checked the same way.
  Usable in CI since T-128 put mpcore.dll in the package: until then this failed on the MSIX truthfully but for
  a reason that was not about presets, so CI ran it over -Root only. The engine and licence payload it was
  tripping over is now asserted in its own right by tools/check-package.ps1.

.PARAMETER Source
  The repo's preset root. Defaults to presets/.
#>
[CmdletBinding()]
param(
    [string[]]$Root,
    [string]$Msix,
    [string]$Source = "$PSScriptRoot\..\presets"
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Root -and -not $Msix) { $Root = @("$repo\artifacts\bin\Tunqio.App\release_win-x64") }

$Source = (Resolve-Path $Source -ErrorAction SilentlyContinue).Path
if (-not $Source) { throw "The repo has no preset root; expected $repo\presets." }

# ---- what the repo ships -------------------------------------------------------------------------------------
# A preset is a directory holding preset.json and the HLSL it names. Anything else in presets/ (the README) is
# not a preset and the core's scan skips it, so it is not expected in the output either.
$expected = @()
foreach ($dir in Get-ChildItem $Source -Directory) {
    $manifest = Join-Path $dir.FullName 'preset.json'
    if (-not (Test-Path $manifest)) { continue }
    $shader = $null
    try { $shader = (Get-Content $manifest -Raw | ConvertFrom-Json).shader }
    catch { throw "$manifest does not parse as JSON: $($_.Exception.Message)" }
    $expected += [pscustomobject]@{ Name = $dir.Name; Shader = $shader }
}

Write-Output "source:   $Source"
if ($expected.Count -eq 0) {
    Write-Output 'presets:  none yet - E4-S4 and E4-S5 write them. What is checked is that the root itself reaches the output.'
}
else {
    Write-Output "presets:  $(($expected | ForEach-Object { $_.Name }) -join ', ')"
}
if ($env:MPCORE_PRESET_ROOT) {
    Write-Output "note:     MPCORE_PRESET_ROOT is set in this environment ($env:MPCORE_PRESET_ROOT) and is ignored here."
}
Write-Output ''

# ---- the artifacts -------------------------------------------------------------------------------------------
$failures = @()
$scratch = $null

function Test-Artifact([string]$what, [string]$path) {
    Write-Output "$what"
    Write-Output "  $path"

    if (-not (Test-Path $path)) {
        $script:failures += "$what - $path does not exist; build it first"
        Write-Output '  FAIL  nothing to check: the artifact is not there'
        return
    }

    # The core looks beside its own module, so mpcore.dll is what says where the preset root has to be. An
    # artifact without it has nowhere for presets to be beside, and saying that is more use than checking a
    # directory the core will never open.
    $core = Join-Path $path 'mpcore.dll'
    if (Test-Path $core) {
        Write-Output '  ok    mpcore.dll'
    }
    else {
        # Not a return: the rest still says whether the preset root reached this artifact, which is what the
        # reader wants to know even when the artifact is missing the module the root belongs beside.
        $script:failures += "$what - no mpcore.dll, so there is nowhere for a preset root to be beside"
        Write-Output '  FAIL  mpcore.dll is not in this artifact'
    }

    $root = Join-Path $path 'presets'
    if (-not (Test-Path $root)) {
        $script:failures += "$what - no presets/ directory beside mpcore.dll; the build did not place the preset root"
        Write-Output '  FAIL  presets/ is not beside mpcore.dll - the app will find only the built-in preset'
        return
    }
    Write-Output '  ok    presets/ is beside mpcore.dll'

    foreach ($preset in $expected) {
        $dir = Join-Path $root $preset.Name
        $manifest = Join-Path $dir 'preset.json'
        if (-not (Test-Path $manifest)) {
            $script:failures += "$what - presets/$($preset.Name)/preset.json is missing"
            Write-Output "  FAIL  presets/$($preset.Name)/preset.json"
            continue
        }
        if ($preset.Shader -and -not (Test-Path (Join-Path $dir $preset.Shader))) {
            $script:failures += "$what - presets/$($preset.Name)/$($preset.Shader) is missing, so the preset is skipped with a warning"
            Write-Output "  FAIL  presets/$($preset.Name)/$($preset.Shader)"
            continue
        }
        Write-Output "  ok    presets/$($preset.Name)"
    }

    # An incremental build leaves a preset behind after its source directory is deleted, which is untidy rather
    # than broken; it is said out loud so a package is never quietly wider than the repo.
    $names = @($expected | ForEach-Object { $_.Name })
    $extra = @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin $names -and (Test-Path (Join-Path $_.FullName 'preset.json')) })
    foreach ($dir in $extra) {
        Write-Output "  note  presets/$($dir.Name) is in the output but not in the repo (a stale incremental copy?)"
    }
    Write-Output ''
}

try {
    foreach ($dir in $Root) {
        $resolved = (Resolve-Path $dir -ErrorAction SilentlyContinue).Path
        if (-not $resolved) { $resolved = $dir }
        Test-Artifact 'unpackaged app' $resolved
    }

    if ($Msix) {
        $package = (Resolve-Path $Msix -ErrorAction SilentlyContinue).Path
        if (-not $package) {
            $failures += "MSIX - $Msix does not exist; build it first"
            Write-Output "packaged app`n  $Msix`n  FAIL  the package is not there`n"
        }
        else {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('tunqio-check-presets-' + [guid]::NewGuid().ToString('n'))
            [System.IO.Compression.ZipFile]::ExtractToDirectory($package, $scratch)
            Test-Artifact 'packaged app (MSIX payload)' $scratch
        }
    }
}
finally {
    if ($scratch) { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -eq 0) {
    Write-Output 'PASS: every artifact carries a preset root beside mpcore.dll with the presets the repo ships.'
    exit 0
}
foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
exit 1
