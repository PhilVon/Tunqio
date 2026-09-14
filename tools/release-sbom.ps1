<#
.SYNOPSIS
  T-80 (E8-S1): writes a CycloneDX 1.5 JSON software bill of materials for a release: the NuGet graph Tunqio.App resolved,
  the first-party projects, mpcore, and the BASS packages from the hand-maintained tools/native-deps.json.

.DESCRIPTION
  The NuGet graph is read from Tunqio.App's project.assets.json (what restore resolved for win-x64, with each package's
  SHA-512 and its dependency edges) and each package's licence from its .nuspec in the NuGet package folder the assets file
  names. No CycloneDX tool is installed for this: a script in the repository gives the same document on a developer's
  machine and on the runner, with nothing downloaded at release time.

  Native components are the packages in tools/native-deps.json, with the SHA-256 of the pinned distribution archive.
  Vendored sources compiled into mpcore (pffft, nlohmann/json) are not in that list and so not in this document; they are in
  THIRD-PARTY-NOTICES.md.

.PARAMETER Tag
  The release tag, for example v1.0.0-rc.1.

.PARAMETER OutFile
  The .cdx.json to write.

.PARAMETER Assets
  project.assets.json to read. Defaults to Tunqio.App's under artifacts/obj.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$Assets
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$v = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $Tag -PassThru

# Windows PowerShell 5.1's ConvertFrom-Json refuses input over 2 MB, and a WinUI app's assets file is larger.
function Read-JsonDictionary([string]$path) {
    $text = [IO.File]::ReadAllText($path)
    if ($PSVersionTable.PSEdition -eq 'Core') { return (ConvertFrom-Json $text -AsHashtable) }
    Add-Type -AssemblyName System.Web.Extensions
    $serializer = New-Object Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = [int]::MaxValue
    return $serializer.DeserializeObject($text)
}

if (-not $Assets) {
    $candidates = @(Get-ChildItem (Join-Path $repo 'artifacts\obj\Tunqio.App') -Recurse -Filter project.assets.json -ErrorAction SilentlyContinue)
    if ($candidates.Count -ne 1) { throw "Expected one project.assets.json under artifacts/obj/Tunqio.App, found $($candidates.Count); restore Tunqio.App first." }
    $Assets = $candidates[0].FullName
}
$json = Read-JsonDictionary $Assets

$targetKey = @($json['targets'].Keys | Where-Object { $_ -like '*/win-x64' }) | Select-Object -First 1
if (-not $targetKey) { throw "$Assets has no win-x64 target." }
$target = $json['targets'][$targetKey]
$libraries = $json['libraries']
$packageFolders = @($json['packageFolders'].Keys)

# name (case-insensitive) -> "Name/Version" key in the target
$byName = @{}
foreach ($key in $target.Keys) { $byName[$key.Split('/')[0].ToLowerInvariant()] = $key }

function Get-Ref([string]$key) {
    $name, $version = $key.Split('/')
    if ($target[$key]['type'] -eq 'project') { return "project:$name" }
    return "pkg:nuget/$name@$version"
}

function Get-NuspecLicence([string]$relativePath) {
    foreach ($folder in $packageFolders) {
        $dir = Join-Path $folder $relativePath
        $nuspec = Get-ChildItem $dir -Filter *.nuspec -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $nuspec) { continue }
        $xml = [xml][IO.File]::ReadAllText($nuspec.FullName)
        $meta = $xml.SelectSingleNode("//*[local-name()='metadata']")
        $lic = $meta.SelectSingleNode("*[local-name()='license']")
        if ($lic -and $lic.GetAttribute('type') -eq 'expression') { return @{ expression = $lic.InnerText.Trim() } }
        if ($lic -and $lic.GetAttribute('type') -eq 'file') { return @{ license = @{ name = "See $($lic.InnerText.Trim()) in the package" } } }
        $url = $meta.SelectSingleNode("*[local-name()='licenseUrl']")
        if ($url -and $url.InnerText.Trim()) { return @{ license = @{ url = $url.InnerText.Trim() } } }
        return $null
    }
    return $null
}

$components = New-Object Collections.Generic.List[object]
$dependencies = New-Object Collections.Generic.List[object]
$appRef = "pkg:github/PhilVon/Tunqio@$($v.Tag)"
$directRefs = @()
$nugetCount = 0

foreach ($key in ($target.Keys | Sort-Object)) {
    $entry = $target[$key]
    $name, $version = $key.Split('/')
    $ref = Get-Ref $key
    if ($entry['type'] -eq 'project') {
        $c = [ordered]@{ type = 'library'; 'bom-ref' = $ref; name = $name; version = $v.SemVer; description = 'Tunqio first-party project' }
    } else {
        $c = [ordered]@{ type = 'library'; 'bom-ref' = $ref; name = $name; version = $version; purl = $ref }
        $lib = $libraries[$key]
        if ($lib -and $lib['sha512']) {
            $hex = [BitConverter]::ToString([Convert]::FromBase64String($lib['sha512'])).Replace('-', '').ToLowerInvariant()
            $c['hashes'] = @(@{ alg = 'SHA-512'; content = $hex })
        }
        if ($lib -and $lib['path']) {
            $licence = Get-NuspecLicence $lib['path']
            if ($licence) { $c['licenses'] = @($licence) }
        }
        $nugetCount++
    }
    $components.Add($c)
    $edges = @()
    if ($entry.Keys -contains 'dependencies') {
        foreach ($depName in $entry['dependencies'].Keys) {
            $depKey = $byName[$depName.ToLowerInvariant()]
            if ($depKey) { $edges += (Get-Ref $depKey) }
        }
    }
    $dependencies.Add([ordered]@{ ref = $ref; dependsOn = @($edges | Sort-Object -Unique) })
}

# Direct references of Tunqio.App: the project's own framework entry in projectFileDependencyGroups.
foreach ($group in $json['projectFileDependencyGroups'].Keys) {
    foreach ($spec in $json['projectFileDependencyGroups'][$group]) {
        $depKey = $byName[($spec -split '\s')[0].ToLowerInvariant()]
        if ($depKey) { $directRefs += (Get-Ref $depKey) }
    }
}

# ---- first-party native core and the pinned native packages ------------------------------------------------------------------
$mpcoreRef = 'tunqio:native/mpcore'
$components.Add([ordered]@{ type = 'library'; 'bom-ref' = $mpcoreRef; name = 'mpcore'; version = $v.SemVer; description = 'Tunqio native audio and visualization core (mpcore.dll)' })
$deps = Get-Content (Join-Path $repo 'tools\native-deps.json') -Raw | ConvertFrom-Json
$nativeRefs = @()
foreach ($p in $deps.packages) {
    $ver = [regex]::Match($p.description, '\d+(\.\d+)+').Value
    $ref = "tunqio:native/$($p.name)"
    $nativeRefs += $ref
    $components.Add([ordered]@{
            type               = 'library'
            'bom-ref'          = $ref
            name               = $p.name
            version            = $ver
            description        = $p.description
            licenses           = @(@{ license = @{ name = "Proprietary (un4seen BASS licence), free for non-commercial use; shipped as licenses/$($p.name).txt" } })
            externalReferences = @(@{ type = 'distribution'; url = $p.url; hashes = @(@{ alg = 'SHA-256'; content = $p.sha256 }) })
        })
}
$dependencies.Add([ordered]@{ ref = $mpcoreRef; dependsOn = $nativeRefs })
$dependencies.Add([ordered]@{ ref = $appRef; dependsOn = @(@($directRefs | Sort-Object -Unique) + $mpcoreRef) })
if ($nugetCount -eq 0) { throw "No NuGet packages in $Assets target $targetKey." }
if ($nativeRefs.Count -ne @($deps.packages).Count) { throw 'Native component count does not match tools/native-deps.json.' }

$bom = [ordered]@{
    bomFormat    = 'CycloneDX'
    specVersion  = '1.5'
    serialNumber = "urn:uuid:$([guid]::NewGuid())"
    version      = 1
    metadata     = [ordered]@{
        timestamp  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        tools      = [ordered]@{ components = @([ordered]@{ type = 'application'; name = 'tools/release-sbom.ps1'; group = 'Tunqio' }) }
        component  = [ordered]@{ type = 'application'; 'bom-ref' = $appRef; name = 'Tunqio'; version = $v.SemVer; purl = $appRef }
        properties = @(
            [ordered]@{ name = 'tunqio:msixVersion'; value = $v.MsixVersion },
            [ordered]@{ name = 'tunqio:nugetTarget'; value = $targetKey })
    }
    components   = $components.ToArray()
    dependencies = $dependencies.ToArray()
}
$text = $bom | ConvertTo-Json -Depth 32
[IO.File]::WriteAllText($OutFile, $text, (New-Object Text.UTF8Encoding($false)))
$projects = @($components | Where-Object { $_['bom-ref'] -like 'project:*' }).Count
Write-Output "sbom: $(Split-Path $OutFile -Leaf), CycloneDX 1.5: $nugetCount NuGet packages, $projects first-party projects, mpcore, $($nativeRefs.Count) native packages ($targetKey)"
