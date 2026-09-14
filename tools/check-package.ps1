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

# ---- E7-S1 (AC-472, AC-172): what Windows registers from the package's own AppxManifest.xml -------------------------------
# The file type association, the tunqio URI scheme, the tunqio.exe execution alias and unvirtualised writes. Read from the
# manifest inside the .msix, which is what an install registers from, rather than from src/Tunqio.App/Package.appxmanifest,
# which the packaging step rewrites. The names come from src/Tunqio.Core/Identity.cs and the extensions from
# src/Tunqio.Core/Library/AudioFormats.cs, the list the library scanner accepts, so neither is a second copy kept here.
# Elements are found by local name: the namespace prefixes are the packaging tool's choice.
function Read-CoreConstant([string]$file, [string]$name) {
    $m = [regex]::Match((Get-Content $file -Raw), "const string $name = `"([^`"]+)`"")
    if (-not $m.Success) { throw "$file declares no const string $name" }
    return $m.Groups[1].Value
}

function Test-Registrations([string]$package) {
    $identityFile = "$repo\src\Tunqio.Core\Identity.cs"
    $formatsFile = "$repo\src\Tunqio.Core\Library\AudioFormats.cs"
    $scheme = Read-CoreConstant $identityFile 'UriScheme'
    $alias = Read-CoreConstant $identityFile 'ExecutionAlias'
    $group = Read-CoreConstant $identityFile 'FileTypeAssociationGroup'
    $extensions = @([regex]::Matches((Get-Content $formatsFile -Raw), '\["(\.[a-z0-9]+)"\]') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
    if ($extensions.Count -eq 0) { throw "$formatsFile lists no extensions; there is nothing to check the association against." }

    Write-Output 'packaged app (registrations in AppxManifest.xml)'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
        if (-not $entry) {
            $script:failures += 'MSIX - AppxManifest.xml is not in the package'
            Write-Output "  FAIL  AppxManifest.xml is not in the package`n"
            return
        }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }

    function Check-Registration([string]$what, [bool]$ok, [string]$detail) {
        if ($ok) { Write-Output "  ok    $what ($detail)" }
        else { $script:failures += "MSIX - $what ($detail)"; Write-Output "  FAIL  $what ($detail)" }
    }

    $associations = @($manifest.SelectNodes("//*[local-name()='FileTypeAssociation']"))
    $association = $associations | Where-Object { $_.GetAttribute('Name') -eq $group } | Select-Object -First 1
    Check-Registration "file type association '$group'" ($null -ne $association) "$($associations.Count) association(s) declared"
    if ($association) {
        $display = $association.SelectSingleNode("*[local-name()='DisplayName']")
        Check-Registration 'association display name' ($display -and $display.InnerText -eq 'Tunqio audio file') "'$(if ($display) { $display.InnerText })'"
        $declared = @($association.SelectNodes(".//*[local-name()='FileType']") | ForEach-Object { $_.InnerText.Trim().ToLowerInvariant() } | Sort-Object -Unique)
        $missing = @($extensions | Where-Object { $declared -notcontains $_ })
        $extra = @($declared | Where-Object { $extensions -notcontains $_ })
        Check-Registration 'every extension the scanner accepts is associated' ($missing.Count -eq 0) "$($declared.Count) declared; missing: $(if ($missing.Count) { $missing -join ' ' } else { 'none' })"
        Check-Registration 'nothing the scanner refuses is associated' ($extra.Count -eq 0) "extra: $(if ($extra.Count) { $extra -join ' ' } else { 'none' })"
    }

    $protocols = @($manifest.SelectNodes("//*[local-name()='Protocol']") | ForEach-Object { $_.GetAttribute('Name') })
    Check-Registration "URI scheme '$scheme'" ($protocols -contains $scheme) "declared: $(if ($protocols.Count) { $protocols -join ', ' } else { 'none' })"

    $aliases = @($manifest.SelectNodes("//*[local-name()='AppExecutionAlias']/*[local-name()='ExecutionAlias']") | ForEach-Object { $_.GetAttribute('Alias') })
    Check-Registration "execution alias '$alias'" ($aliases -contains $alias) "declared: $(if ($aliases.Count) { $aliases -join ', ' } else { 'none' })"

    $virtualization = $manifest.SelectSingleNode("//*[local-name()='Properties']/*[local-name()='FileSystemWriteVirtualization']")
    Check-Registration 'file system write virtualisation is disabled' ($virtualization -and $virtualization.InnerText.Trim() -eq 'disabled') "'$(if ($virtualization) { $virtualization.InnerText.Trim() } else { 'not declared' })'"
    $capabilities = @($manifest.SelectNodes("//*[local-name()='Capability']") | ForEach-Object { $_.GetAttribute('Name') })
    Check-Registration 'the unvirtualizedResources capability it requires' ($capabilities -contains 'unvirtualizedResources') "capabilities: $($capabilities -join ', ')"
    Test-ManifestImages $manifest
    Write-Output ''
}

# ---- T-191 (AC-499): every icon asset at its size ------------------------------------------------------------------------
# The list is assets/brand/icon-assets.json, the one tools/IconGen renders from assets/brand/tunqio-icon.svg, so it is not a
# second copy kept here. Each file is parsed, not trusted by name: a PNG's IHDR gives its pixel size, and an .ico's directory
# must list exactly the declared sizes with a PNG of that size behind each entry. Paths in the list are relative to the app
# root, which is where they sit beside Tunqio.exe and inside the package alike.
$iconSpec = Get-Content "$repo\assets\brand\icon-assets.json" -Raw | ConvertFrom-Json

function Read-BigEndian32([byte[]]$b, [int]$at) { return ([int]$b[$at] -shl 24) -bor ([int]$b[$at + 1] -shl 16) -bor ([int]$b[$at + 2] -shl 8) -bor [int]$b[$at + 3] }
function Read-Little([byte[]]$b, [int]$at, [int]$count) { $v = 0; for ($i = $count - 1; $i -ge 0; $i--) { $v = ($v -shl 8) -bor [int]$b[$at + $i] }; return $v }
function Get-PngSize([byte[]]$b, [int]$at = 0) {
    if ($b.Length -lt $at + 24 -or $b[$at] -ne 137 -or $b[$at + 1] -ne 80 -or $b[$at + 12] -ne 73 -or $b[$at + 15] -ne 82) { return $null }
    return "$(Read-BigEndian32 $b ($at + 16))x$(Read-BigEndian32 $b ($at + 20))"
}
function Get-IcoSizes([byte[]]$b) {
    if ($b.Length -lt 6 -or (Read-Little $b 0 2) -ne 0 -or (Read-Little $b 2 2) -ne 1) { return $null }
    $sizes = @()
    for ($i = 0; $i -lt (Read-Little $b 4 2); $i++) {
        $e = 6 + 16 * $i
        $w = if ($b[$e] -eq 0) { 256 } else { [int]$b[$e] }
        $png = Get-PngSize $b (Read-Little $b ($e + 12) 4)
        if ($png -ne "${w}x${w}") { return "entry $i says $w px but holds $png" }
        $sizes += $w
    }
    return ($sizes -join ',')
}

# $reader: a scriptblock from an app-root-relative path to its bytes, or $null when the artifact does not hold it.
function Test-IconAssets([string]$what, [scriptblock]$reader) {
    $count = 0
    foreach ($icon in @($iconSpec.icons)) {
        $bytes = & $reader $icon.path
        $want = (@($icon.sizes) -join ',')
        $got = if ($bytes) { Get-IcoSizes $bytes } else { 'missing' }
        if ($got -eq $want) { $count++ }
        else { $script:failures += "$what - $($icon.path) should be an icon with $want px entries, is $got"; Write-Output "  FAIL  $($icon.path) ($want px wanted, $got)" }
    }
    foreach ($image in @($iconSpec.images)) {
        $bytes = & $reader $image.path
        $want = "$($image.width)x$($image.height)"
        $got = if ($bytes) { Get-PngSize $bytes } else { 'missing' }
        if ($got -eq $want) { $count++ }
        else { $script:failures += "$what - $($image.path) should be a $want PNG, is $got"; Write-Output "  FAIL  $($image.path) ($want wanted, $got)" }
    }
    $total = @($iconSpec.icons).Count + @($iconSpec.images).Count
    Write-Output "  $(if ($count -eq $total) { 'ok  ' } else { 'FAIL' })  icon assets at their sizes: $count of $total ($(@($iconSpec.icons).Count) .ico, $(@($iconSpec.images).Count) .png, from assets/brand/icon-assets.json)"
    Write-Output ''
}

function Test-ManifestImages([xml]$manifest) {
    $visual = $manifest.SelectSingleNode("//*[local-name()='VisualElements']")
    $wanted = [ordered]@{
        'Properties Logo' = $manifest.SelectSingleNode("//*[local-name()='Properties']/*[local-name()='Logo']").InnerText
        'Square150x150Logo' = $visual.GetAttribute('Square150x150Logo')
        'Square44x44Logo' = $visual.GetAttribute('Square44x44Logo')
        'Wide310x150Logo' = $manifest.SelectSingleNode("//*[local-name()='DefaultTile']").GetAttribute('Wide310x150Logo')
        'SplashScreen' = $manifest.SelectSingleNode("//*[local-name()='SplashScreen']").GetAttribute('Image')
        'tunqio-audio association Logo' = "$($manifest.SelectSingleNode("//*[local-name()='FileTypeAssociation']/*[local-name()='Logo']").InnerText)"
    }
    $declared = @($iconSpec.images | ForEach-Object { $_.path })
    foreach ($name in $wanted.Keys) {
        $ref = "$($wanted[$name])".Replace('\', '/')
        $stem = [regex]::Escape($ref -replace '\.png$', '')
        $variants = @($declared | Where-Object { $_ -eq $ref -or $_ -match "^$stem\.(scale|targetsize)-[^.]+\.png$" })
        Check-Registration "manifest $name is a generated image" ($ref -and $variants.Count -gt 0) "'$($wanted[$name])', $($variants.Count) file(s)"
    }
    Check-Registration 'the file association has its own logo' ($wanted['tunqio-audio association Logo'] -eq 'Assets\FileAssociation.png') "'$($wanted['tunqio-audio association Logo'])'"
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
    Test-IconAssets 'unpackaged app' {
        param($path)
        $file = Join-Path $resolved $path
        if (Test-Path $file) { , [System.IO.File]::ReadAllBytes($file) }
    }
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
        try {
            $present = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/').ToLowerInvariant() })
            Test-Payload 'packaged app (MSIX payload)' $present
            Test-IconAssets 'MSIX' {
                param($path)
                $entry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq $path } | Select-Object -First 1
                if ($entry) {
                    $stream = $entry.Open()
                    try { $copy = New-Object System.IO.MemoryStream; $stream.CopyTo($copy); , $copy.ToArray() }
                    finally { $stream.Dispose() }
                }
            }
        }
        finally { $zip.Dispose() }
        Test-Registrations $package
    }
}

if ($failures.Count -eq 0) {
    Write-Output 'PASS: every artifact carries mpcore.dll, the BASS runtime it loads, the licence texts and every icon asset at its size.'
    exit 0
}
foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
exit 1
