<#
.SYNOPSIS
  T-80 (E8-S1): stages a release from the packaged build: Tunqio_<ver>_x64.msix, the Windows App Runtime packages it depends
  on, Tunqio.appinstaller, and the symbol archive. Signing is a separate step (tools/release-sign.ps1), done in place on the
  staged Tunqio_<ver>_x64.msix; nothing written here depends on the signature.

.DESCRIPTION
  The package is found the way ci.yml's check step finds it (T-190): exactly one artifacts/msix/Tunqio_*/Tunqio.msix. Its
  AppxManifest.xml must carry Name Tunqio, the manifest publisher, x64 and the version the tag maps to, so an unstamped
  build cannot be staged under a release name.

  Every PackageDependency the built manifest declares must be satisfied by a package the build placed under
  Dependencies/x64/ (same name, at least the declared MinVersion); those are staged beside Tunqio and listed in the
  .appinstaller, because a clean machine has no Windows App Runtime and App Installer installs dependencies only from the
  file.

  Tunqio.appinstaller (schema 2018): its Uri is the frozen address
  https://github.com/PhilVon/Tunqio/releases/latest/download/Tunqio.appinstaller (docs/identity.md); package Uris are this
  tag's release download addresses; updates are checked on launch when 8 hours have passed and by the background task every
  8 hours; ForceUpdateFromAnyVersion lets a release candidate move to its release, whose MSIX version is lower
  (tools/release-version.ps1).

  The symbol archive Tunqio_<ver>_x64_symbols.zip holds mpcore.pdb from the native Release output and every .pdb in the
  packaged app's output; Tunqio.pdb, Tunqio.Core.pdb, Tunqio.Interop.pdb and Tunqio.Library.pdb must be among them.

.PARAMETER Tag
  The release tag, for example v1.0.0-rc.1.

.PARAMETER OutDir
  The staging folder. Must be empty or absent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Frozen at 1.0 (docs/identity.md). Changing either strands every installed copy on its last update address.
$Repository = 'PhilVon/Tunqio'
$AppInstallerUri = "https://github.com/$Repository/releases/latest/download/Tunqio.appinstaller"

$v = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $Tag -PassThru
$downloadBase = "https://github.com/$Repository/releases/download/$Tag"

if (Test-Path $OutDir) {
    if (@(Get-ChildItem $OutDir -Force).Count -gt 0) { throw "$OutDir is not empty; stage into an empty folder." }
} else {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
}
$OutDir = (Resolve-Path $OutDir).Path

function Read-MsixManifest([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
        if (-not $entry) { throw "$path has no AppxManifest.xml." }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}

function Get-Identity([xml]$manifest) {
    $node = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    [pscustomobject]@{
        Name         = $node.GetAttribute('Name')
        Publisher    = $node.GetAttribute('Publisher')
        Version      = $node.GetAttribute('Version')
        Architecture = $node.GetAttribute('ProcessorArchitecture')
    }
}

function Escape-Xml([string]$s) { [Security.SecurityElement]::Escape($s) }

# ---- Tunqio's package ------------------------------------------------------------------------------------------------------
$found = @(Get-ChildItem (Join-Path $repo 'artifacts\msix') -Recurse -Filter Tunqio.msix -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -like 'Tunqio_*' })
if ($found.Count -ne 1) { throw "Expected exactly one artifacts/msix/Tunqio_*/Tunqio.msix; found $($found.Count): $(($found | ForEach-Object FullName) -join ', ')" }
$source = $found[0]

$manifestText = [IO.File]::ReadAllText((Join-Path $repo 'src\Tunqio.App\Package.appxmanifest'))
$publisher = [regex]::Match($manifestText, '<Identity\b[^>]*?\sPublisher="([^"]*)"').Groups[1].Value

$built = Read-MsixManifest $source.FullName
$id = Get-Identity $built
$wrong = @()
if ($id.Name -ne 'Tunqio') { $wrong += "Name '$($id.Name)'" }
if ($id.Publisher -ne $publisher) { $wrong += "Publisher '$($id.Publisher)' (manifest source says '$publisher')" }
if ($id.Version -ne $v.MsixVersion) { $wrong += "Version '$($id.Version)' (tag $Tag maps to $($v.MsixVersion); was tools/release-version.ps1 -Apply run before the build?)" }
if ($id.Architecture -ne 'x64') { $wrong += "ProcessorArchitecture '$($id.Architecture)'" }
if ($wrong.Count -gt 0) { throw "$($source.FullName) is not the package for $($Tag): $($wrong -join '; ')." }

$msixOut = Join-Path $OutDir $v.Package
Copy-Item $source.FullName $msixOut
Write-Output "package:      $($v.Package) ($($id.Name) $($id.Version) $($id.Architecture), $($id.Publisher))"

# ---- dependencies ------------------------------------------------------------------------------------------------------------
$depDir = Join-Path $source.Directory.FullName 'Dependencies\x64'
$available = @()
if (Test-Path $depDir) {
    foreach ($f in Get-ChildItem $depDir -Filter *.msix) {
        $available += [pscustomobject]@{ File = $f; Identity = (Get-Identity (Read-MsixManifest $f.FullName)) }
    }
}
$declared = @($built.SelectNodes("//*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
$dependencies = @()
foreach ($d in $declared) {
    $name = $d.GetAttribute('Name')
    $min = [version]$d.GetAttribute('MinVersion')
    $match = @($available | Where-Object { $_.Identity.Name -eq $name -and [version]$_.Identity.Version -ge $min })
    if ($match.Count -ne 1) { throw "The package depends on $name >= $min, and $depDir holds $($match.Count) matching package(s). A clean machine could not install Tunqio from the .appinstaller." }
    $dep = $match[0]
    Copy-Item $dep.File.FullName (Join-Path $OutDir $dep.File.Name)
    $dependencies += $dep
    Write-Output "dependency:   $($dep.File.Name) ($($dep.Identity.Name) $($dep.Identity.Version), needs >= $min)"
}
foreach ($extra in @($available | Where-Object { $dependencies.File.FullName -notcontains $_.File.FullName })) {
    Write-Output "not staged:   $($extra.File.Name) (in Dependencies\x64 but not a PackageDependency of Tunqio)"
}

# ---- Tunqio.appinstaller ---------------------------------------------------------------------------------------------------
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine("<!-- Tunqio $($v.SemVer), generated by tools/release-assets.ps1 (T-80). The Uri below is frozen at 1.0 (docs/identity.md). -->")
[void]$sb.AppendLine("<AppInstaller xmlns=`"http://schemas.microsoft.com/appx/appinstaller/2018`" Version=`"$($v.AppInstallerVersion)`" Uri=`"$(Escape-Xml $AppInstallerUri)`">")
[void]$sb.AppendLine("  <MainPackage Name=`"$(Escape-Xml $id.Name)`" Publisher=`"$(Escape-Xml $id.Publisher)`" Version=`"$($id.Version)`" ProcessorArchitecture=`"x64`" Uri=`"$(Escape-Xml "$downloadBase/$($v.Package)")`" />")
if ($dependencies.Count -gt 0) {
    [void]$sb.AppendLine('  <Dependencies>')
    foreach ($dep in $dependencies) {
        [void]$sb.AppendLine("    <Package Name=`"$(Escape-Xml $dep.Identity.Name)`" Publisher=`"$(Escape-Xml $dep.Identity.Publisher)`" Version=`"$($dep.Identity.Version)`" ProcessorArchitecture=`"$($dep.Identity.Architecture)`" Uri=`"$(Escape-Xml "$downloadBase/$($dep.File.Name)")`" />")
    }
    [void]$sb.AppendLine('  </Dependencies>')
}
[void]$sb.AppendLine('  <UpdateSettings>')
[void]$sb.AppendLine('    <OnLaunch HoursBetweenUpdateChecks="8" />')
[void]$sb.AppendLine('    <AutomaticBackgroundTask />')
[void]$sb.AppendLine('    <ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>')
[void]$sb.AppendLine('  </UpdateSettings>')
[void]$sb.AppendLine('</AppInstaller>')
$appInstallerPath = Join-Path $OutDir 'Tunqio.appinstaller'
[IO.File]::WriteAllText($appInstallerPath, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))

# Read it back: well-formed, and every package Uri names a file staged beside it.
$ai = [xml][IO.File]::ReadAllText($appInstallerPath)
$staged = @(Get-ChildItem $OutDir -File | ForEach-Object Name)
foreach ($p in @($ai.SelectNodes("//*[@Uri]"))) {
    $uri = $p.GetAttribute('Uri')
    if ($p.LocalName -eq 'AppInstaller') {
        if ($uri -ne $AppInstallerUri) { throw "Tunqio.appinstaller Uri is $uri, not the frozen $AppInstallerUri." }
        continue
    }
    if (-not $uri.StartsWith("$downloadBase/")) { throw "$($p.LocalName) Uri $uri is not under $downloadBase/." }
    $file = $uri.Substring($downloadBase.Length + 1)
    if ($staged -notcontains $file) { throw "$($p.LocalName) Uri names $file, which is not staged." }
}
Write-Output "appinstaller: Tunqio.appinstaller (file version $($v.AppInstallerVersion); checks every 8 hours; update address $AppInstallerUri)"

# ---- symbols -------------------------------------------------------------------------------------------------------------------
$nativePdb = Join-Path $repo 'artifacts\native\Release\x64\mpcore.pdb'
$appOut = Join-Path $repo 'artifacts\bin\Tunqio.App\release_win-x64_msix'
if (-not (Test-Path $nativePdb)) { throw "mpcore.pdb not found at $nativePdb; build the solution in Release first." }
$pdbs = @(Get-ChildItem $appOut -Filter *.pdb -File -ErrorAction SilentlyContinue)
$names = @($pdbs | ForEach-Object Name)
$required = @('Tunqio.pdb', 'Tunqio.Core.pdb', 'Tunqio.Interop.pdb', 'Tunqio.Library.pdb')
$absent = @($required | Where-Object { $names -notcontains $_ })
if ($absent.Count -gt 0) { throw "The packaged output $appOut lacks $($absent -join ', ')." }
$symbolsPath = Join-Path $OutDir ("Tunqio_$($v.MsixVersion)_x64_symbols.zip")
$zip = [IO.Compression.ZipFile]::Open($symbolsPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $nativePdb, 'mpcore.pdb', [IO.Compression.CompressionLevel]::Optimal)
    foreach ($p in $pdbs) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $p.FullName, $p.Name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
Write-Output "symbols:      $(Split-Path $symbolsPath -Leaf) (mpcore.pdb, $($names -join ', '))"
