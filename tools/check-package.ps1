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
    [string]$Deps
)

$ErrorActionPreference = 'Stop'
# T-158: defaults are resolved here in the body, never in param(). Windows PowerShell 5.1 leaves $PSScriptRoot empty
# while param() defaults are evaluated under 'powershell -File', so a default built from it pointed at
# '\native-deps.json' and the script threw "manifest is missing" before checking anything.
$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Root -and -not $Msix) { $Root = @("$repo\artifacts\bin\Tunqio.App\release_win-x64") }
if (-not $Deps) { $Deps = Join-Path $repo 'tools\native-deps.json' }

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
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName })
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

    # E7-S4 (T-77): toast presses. Packaged, Windows App SDK's Register writes nothing; the activator is this declaration, and
    # COM starts Tunqio.exe with the argument the SDK reads as an AppNotification activation.
    $toast = $manifest.SelectSingleNode("//*[local-name()='ToastNotificationActivation']")
    $toastClsid = if ($toast) { $toast.GetAttribute('ToastActivatorCLSID') } else { '' }
    Check-Registration 'toast activation CLSID declared' ($toastClsid -match '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') "'$toastClsid'"
    $servers = @($manifest.SelectNodes("//*[local-name()='ComServer']/*[local-name()='ExeServer']"))
    $server = $servers | Where-Object { @($_.SelectNodes("*[local-name()='Class']") | Where-Object { $_.GetAttribute('Id') -eq $toastClsid }).Count -gt 0 } | Select-Object -First 1
    Check-Registration 'a COM server declares the toast activator class' ($null -ne $server) "$($servers.Count) exe server(s)"
    if ($server) {
        $serverExe = $server.GetAttribute('Executable')
        Check-Registration 'it starts Tunqio.exe with ----AppNotificationActivated:' ($serverExe -eq 'Tunqio.exe' -and $server.GetAttribute('Arguments') -eq '----AppNotificationActivated:') "Executable '$serverExe', Arguments '$($server.GetAttribute('Arguments'))'"
        Check-Registration 'that executable is in the package where the server names it' ($entryNames -contains $serverExe) "$(if ($entryNames -contains $serverExe) { 'present' } else { 'missing' })"
    }

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
        $stem = [regex]::Escape(($ref -replace '\.png$', ''))
        $variants = @($declared | Where-Object { $_ -eq $ref -or $_ -match "^$stem\.(scale|targetsize)-[^.]+\.png$" })
        Check-Registration "manifest $name is a generated image" ($ref -and $variants.Count -gt 0) "'$($wanted[$name])', $($variants.Count) file(s)"
    }
    Check-Registration 'the file association has its own logo' ($wanted['tunqio-audio association Logo'] -eq 'Assets\FileAssociation.png') "'$($wanted['tunqio-audio association Logo'])'"
}

# ---- T-198 (AC-542): every third-party file in the package has a row in THIRD-PARTY-NOTICES.md ---------------------------
# The About page lists what the notices list, so a file the package ships with no row is a component shipped without its
# licence on the page. A unit test cannot see the package; this script already opens it, so the assertion lives here. The
# rows are read from the notices file itself, as T-86 checked them by hand: a BASS table row (first column 'Package')
# accounts for <name>.dll, and every backticked pattern in a 'Files in the package' cell accounts for the entries it
# matches. Tunqio's own files are the only ones allowed without a row; mpcore.dll is Tunqio's, and the vendored sources
# compiled into it (pffft, nlohmann/json) must still have their rows.
$noticesPath = Join-Path $repo 'THIRD-PARTY-NOTICES.md'
$ownPatterns = @('Tunqio*', 'Assets/*', 'presets/*', 'licenses/*', 'AppxMetadata/*')
$ownFiles = @('mpcore.dll', 'AppxManifest.xml', 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml')

function Read-NoticesPatterns {
    $lines = @(Get-Content $noticesPath)
    $found = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not $lines[$i].TrimStart().StartsWith('|')) { continue }
        $header = @($lines[$i].Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
        $filesColumn = [array]::IndexOf($header, 'Files in the package')
        $isBass = $header[0] -eq 'Package'
        $i++ # the separator row
        while ($i + 1 -lt $lines.Count -and $lines[$i + 1].TrimStart().StartsWith('|')) {
            $i++
            $cells = @($lines[$i].Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
            $row = $cells[0].Replace('`', '')
            if (-not $row) { continue }
            if ($isBass) { $found += [pscustomobject]@{ Row = $row; Pattern = "$row.dll" } }
            elseif ($filesColumn -ge 0 -and $filesColumn -lt $cells.Count) {
                foreach ($m in [regex]::Matches($cells[$filesColumn], '`([^`]+)`')) { $found += [pscustomobject]@{ Row = $row; Pattern = $m.Groups[1].Value } }
            }
        }
    }
    return $found
}

function Test-NoticesCoverage([string[]]$entries) {
    Write-Output 'packaged app (every third-party file has a THIRD-PARTY-NOTICES.md row)'
    if (-not (Test-Path $noticesPath)) { $script:failures += "MSIX - $noticesPath is missing"; Write-Output "  FAIL  $noticesPath is missing`n"; return }
    $patterns = @(Read-NoticesPatterns)
    if ($patterns.Count -eq 0) { throw "$noticesPath yields no BASS rows and no 'Files in the package' patterns; there is nothing to map the package to." }
    $noticesText = Get-Content $noticesPath -Raw
    foreach ($vendored in 'pffft', 'nlohmann/json') {
        $ok = $noticesText -match "(?m)^\| $([regex]::Escape($vendored)) \|[^\r\n]*\| Yes, compiled into ``mpcore\.dll`` \|"
        if ($ok) { Write-Output "  ok    $vendored (compiled into mpcore.dll) has a shipped row" }
        else { $script:failures += "MSIX - mpcore.dll carries $vendored but THIRD-PARTY-NOTICES.md has no shipped row for it"; Write-Output "  FAIL  $vendored has no shipped row" }
    }
    $own = 0; $mapped = 0; $unmapped = @(); $usedRows = @{}
    foreach ($entry in $entries) {
        if ($entry.EndsWith('/')) { continue }
        if ($ownFiles -contains $entry -or @($ownPatterns | Where-Object { $entry -like $_ }).Count -gt 0) { $own++; continue }
        $hit = $patterns | Where-Object { $entry -like $_.Pattern.Replace('\', '/') } | Select-Object -First 1
        if ($hit) { $mapped++; $usedRows[$hit.Row] = $true }
        else { $unmapped += $entry }
    }
    foreach ($entry in $unmapped) {
        $script:failures += "MSIX - $entry ships in the package but no row of THIRD-PARTY-NOTICES.md accounts for it"
        Write-Output "  FAIL  $entry has no notices row"
    }
    $rows = @($patterns | ForEach-Object { $_.Row } | Sort-Object -Unique)
    $idle = @($rows | Where-Object { -not $usedRows.ContainsKey($_) })
    Write-Output "  $(if ($unmapped.Count -eq 0) { 'ok  ' } else { 'FAIL' })  $mapped third-party file(s) map to $($usedRows.Count) of $($rows.Count) notices row(s); $own of Tunqio's own; $($unmapped.Count) unaccounted for"
    if ($idle.Count) { Write-Output "  note  rows with no file in this package: $($idle -join '; ')" }
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
            Test-NoticesCoverage @($zip.Entries | ForEach-Object { [uri]::UnescapeDataString($_.FullName.Replace('\', '/')) })
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
    Write-Output "PASS: every artifact carries mpcore.dll, the BASS runtime it loads, the licence texts and every icon asset at its size$(if ($Msix) { ', and every third-party file in the package has a THIRD-PARTY-NOTICES.md row' })."
    exit 0
}
foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
exit 1
