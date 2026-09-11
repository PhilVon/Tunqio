<#
.SYNOPSIS
  T-128: the audio engine, its BASS runtime and the licence texts reached the artifact. Fails when a built app
  or an MSIX is missing mpcore.dll, any BASS DLL the app loads, or any licence text the project commits to
  shipping.

.DESCRIPTION
  Every MSIX built before T-128 carried none of these. The two AfterTargets="Build" <Copy> targets that placed
  them wrote into $(OutDir), and the appx layout is computed from item groups, so the package got the managed
  assemblies, the assets and nothing else. Nobody noticed for the whole of development, because the unpackaged
  output -- the one every developer runs and every test loads -- was correct the entire time. Only a check that
  opens the package can tell those two apart, which is why this script exists and why CI runs it over both.

  What is asserted is the shape of the artifact on disk, not what the build said it did:

    mpcore.dll             the engine. Without it Tunqio is a library browser: AudioStartup reports "audio
                           unavailable" and nothing plays.
    <name>.dll             one per package in tools/native-deps.json - bass, bassmix, basswasapi and the format
                           add-ons. mpcore.dll loads these by name from its own directory; a missing add-on is
                           a format that silently will not open.
    licenses/<name>.txt    one per package, plus licenses/THIRD-PARTY-NOTICES.md. BASS is proprietary and free
                           for non-commercial use only on condition that its notices ship with the product
                           (ADR-003, docs/build-test-release.md "Third-party licences"). A package without them
                           is not merely incomplete, it is not licensed to be distributed.

  The expectations come from tools/native-deps.json rather than a list written down here, and from that file
  rather than from native/bass/: the fetched directory is gitignored, so on a machine where fetch-native.ps1
  has not run a check derived from it would expect nothing and pass over an empty package. The pinned manifest
  is the thing that says what the app ships.

  This is a sibling of tools/check-presets.ps1 rather than more of it. They assert different invariants owned by
  different stories -- that one, the preset root the renderer scans (T-125); this one, the engine and licence
  payload (T-128) -- and a red build should name one thing. They share only the six lines that know a .msix is
  a zip.

.PARAMETER Root
  A built app directory to check: the folder holding Tunqio.exe and mpcore.dll. Defaults to the unpackaged
  Release output. Repeatable.

.PARAMETER Msix
  A .msix to check. It is a zip and is read as one; nothing is extracted.

.PARAMETER Deps
  The pinned native dependency manifest. Defaults to tools/native-deps.json.
#>
[CmdletBinding()]
param(
    [string[]]$Root,
    [string]$Msix,
    [string]$Deps = "$PSScriptRoot\native-deps.json"
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Root -and -not $Msix) { $Root = @("$repo\artifacts\bin\Tunqio.App\release_win-x64") }

$Deps = (Resolve-Path $Deps -ErrorAction SilentlyContinue).Path
if (-not $Deps) { throw "The pinned native dependency manifest is missing; expected $repo\tools\native-deps.json." }

# ---- what the app ships --------------------------------------------------------------------------------------
$packages = @((Get-Content $Deps -Raw | ConvertFrom-Json).packages)
if ($packages.Count -eq 0) { throw "$Deps lists no packages; there is nothing to check against." }

$expected = @('mpcore.dll')
$expected += @($packages | ForEach-Object { "$($_.name).dll" })
$expected += 'licenses/THIRD-PARTY-NOTICES.md'
$expected += @($packages | ForEach-Object { "licenses/$($_.name).txt" })

Write-Output "manifest: $Deps"
Write-Output "engine:   mpcore.dll"
Write-Output "bass:     $(($packages | ForEach-Object { $_.name }) -join ', ')"
Write-Output ''

# ---- the artifacts -------------------------------------------------------------------------------------------
$failures = @()

function Test-Payload([string]$what, [string[]]$present) {
    foreach ($want in $expected) {
        if ($present -contains $want.ToLowerInvariant()) {
            Write-Output "  ok    $want"
        }
        else {
            $script:failures += "$what - $want is not in the artifact"
            Write-Output "  FAIL  $want"
        }
    }
    Write-Output ''
}

foreach ($dir in $Root) {
    $resolved = (Resolve-Path $dir -ErrorAction SilentlyContinue).Path
    if (-not $resolved) { $resolved = $dir }
    Write-Output 'unpackaged app'
    Write-Output "  $resolved"
    if (-not (Test-Path $resolved)) {
        $failures += "unpackaged app - $resolved does not exist; build it first"
        Write-Output "  FAIL  nothing to check: the artifact is not there`n"
        continue
    }
    $present = @(Get-ChildItem $resolved -Recurse -File |
        ForEach-Object { $_.FullName.Substring($resolved.Length).TrimStart('\', '/').Replace('\', '/').ToLowerInvariant() })
    Test-Payload 'unpackaged app' $present
}

if ($Msix) {
    Write-Output 'packaged app (MSIX payload)'
    Write-Output "  $Msix"
    $package = (Resolve-Path $Msix -ErrorAction SilentlyContinue).Path
    if (-not $package) {
        $failures += "MSIX - $Msix does not exist; build it first"
        Write-Output "  FAIL  the package is not there`n"
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
        try { $present = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/').ToLowerInvariant() }) }
        finally { $zip.Dispose() }
        Test-Payload 'packaged app (MSIX payload)' $present
    }
}

if ($failures.Count -eq 0) {
    Write-Output 'PASS: every artifact carries mpcore.dll, the BASS runtime it loads and the licence texts.'
    exit 0
}
foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
exit 1
