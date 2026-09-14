<#
.SYNOPSIS
  T-80 (E8-S1): the versions a release tag stands for, and with -Apply stamps them into the build.

.DESCRIPTION
  A release tag is vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N. Every number is 0-65535 without leading zeros, and N
  starts at 1. Anything else (another prerelease label, build metadata, a missing v) is refused: MSIX versions are four
  numbers, so a label with no numeric mapping cannot be built. docs/build-test-release.md, "Release versions", has the rule:

    vM.m.p        ->  TunqioVersion M.m.p, revision 0, MSIX M.m.p.0   (the frozen scheme in docs/identity.md)
    vM.m.p-rc.N   ->  TunqioVersion M.m.p, revision N, MSIX M.m.p.N

  A release therefore has a lower MSIX version than its own release candidates (1.0.0.0 < 1.0.0.2). The .appinstaller
  carries ForceUpdateFromAnyVersion so a copy installed from rc.N still moves to the release, and its own file version is
  M.m.p.N for rc.N and M.m.p.65535 for the release, which does increase along rc.1, rc.2, release.

  GitHub's releases/latest/download address never serves a release marked prerelease, and it is the frozen update address.
  So, from the v* tags already in the repository:
    - a release candidate while no release (non-rc) tag exists is published as an ordinary release and becomes latest,
      so installed candidates update to the next candidate (T-80 AC-160);
    - a release candidate once a release tag exists is published as a GitHub prerelease and is never latest, so installed
      releases do not move to it;
    - a release is latest unless a higher release tag already exists (a patch tagged for an older line).

  -Apply rewrites TunqioVersion and TunqioVersionRevision in Directory.Build.props and Identity Version in
  src/Tunqio.App/Package.appxmanifest, each of which must match exactly once. On GitHub Actions every value is also written
  to $GITHUB_OUTPUT for later steps.

.PARAMETER Tag
  The tag, for example v1.0.0-rc.1.

.PARAMETER Apply
  Stamp the versions into Directory.Build.props and the manifest.

.PARAMETER PassThru
  Return the values as an object instead of printing them.

.PARAMETER ExistingTags
  The v* tags to decide the channel from, instead of the repository's (for checking the rule without creating tags).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [switch]$Apply,
    [switch]$PassThru,
    [string[]]$ExistingTags
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path

function ConvertTo-TagVersion([string]$text) {
    $m = [regex]::Match($text, '^v(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})(?:-rc\.([1-9]\d{0,4}))?$')
    if (-not $m.Success) { return $null }
    $major = [int]$m.Groups[1].Value
    $minor = [int]$m.Groups[2].Value
    $patch = [int]$m.Groups[3].Value
    $rc = if ($m.Groups[4].Success) { [int]$m.Groups[4].Value } else { 0 }
    foreach ($n in @($major, $minor, $patch, $rc)) { if ($n -gt 65535) { return $null } }
    $version = "$major.$minor.$patch"
    $pre = $rc -gt 0
    [pscustomobject]@{
        Tag                 = $text
        Major               = $major
        Minor               = $minor
        Patch               = $patch
        Rc                  = $rc
        Prerelease          = $pre
        SemVer              = $(if ($pre) { "$version-rc.$rc" } else { $version })
        Version             = $version
        Revision            = $rc
        MsixVersion         = "$version.$rc"
        AppInstallerVersion = $(if ($pre) { "$version.$rc" } else { "$version.65535" })
        Package             = "Tunqio_$version.$($rc)_x64.msix"
    }
}

# Release precedence: M, m, p, then any rc before the release of the same numbers.
function Get-Rank($v) {
    '{0:D5}.{1:D5}.{2:D5}.{3}' -f $v.Major, $v.Minor, $v.Patch, $(if ($v.Prerelease) { '{0:D5}' -f $v.Rc } else { 'final' })
}

$v = ConvertTo-TagVersion $Tag
if (-not $v) {
    throw "'$Tag' is not a release tag. Release tags are vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N, every number 0-65535 without leading zeros and N from 1 (docs/build-test-release.md, 'Release versions')."
}

# ---- which GitHub release channel ------------------------------------------------------------------------------------------
if ($PSBoundParameters.ContainsKey('ExistingTags')) {
    $tags = @($ExistingTags | Where-Object { $_ })
} else {
    $tags = @(& git -C $repo tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw "git tag --list failed in $repo" }
}
$others = @($tags | Where-Object { $_ -ne $Tag } | ForEach-Object { ConvertTo-TagVersion $_ } | Where-Object { $_ })
$releases = @($others | Where-Object { -not $_.Prerelease })
if ($v.Prerelease -and $releases.Count -gt 0) {
    $githubPrerelease = $true
    $latest = $false
} else {
    $githubPrerelease = $false
    $pool = if ($releases.Count -gt 0) { $releases } else { $others }
    $latest = $true
    foreach ($o in $pool) {
        if ([string]::CompareOrdinal((Get-Rank $o), (Get-Rank $v)) -gt 0) { $latest = $false }
    }
}
$v | Add-Member -NotePropertyName GitHubPrerelease -NotePropertyValue $githubPrerelease
$v | Add-Member -NotePropertyName Latest -NotePropertyValue $latest

# ---- stamping --------------------------------------------------------------------------------------------------------------
function Set-OneMatch([string]$path, [string]$pattern, [string]$replacement) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path)
    $count = [regex]::Matches($text, $pattern).Count
    if ($count -ne 1) { throw "Expected exactly one match of '$pattern' in $path, found $count." }
    $new = [regex]::Replace($text, $pattern, $replacement)
    [IO.File]::WriteAllText($path, $new, (New-Object Text.UTF8Encoding($bom)))
}

if ($Apply) {
    $props = Join-Path $repo 'Directory.Build.props'
    $manifest = Join-Path $repo 'src\Tunqio.App\Package.appxmanifest'
    Set-OneMatch $props '(<TunqioVersion>)[^<]*(</TunqioVersion>)' ('${1}' + $v.Version + '${2}')
    Set-OneMatch $props '(<TunqioVersionRevision>)[^<]*(</TunqioVersionRevision>)' ('${1}' + $v.Revision + '${2}')
    Set-OneMatch $manifest '(<Identity\b[^>]*?\sVersion=")[^"]*(")' ('${1}' + $v.MsixVersion + '${2}')
}

# ---- report ----------------------------------------------------------------------------------------------------------------
$outputs = [ordered]@{
    tag                  = $v.Tag
    semver               = $v.SemVer
    version              = $v.Version
    revision             = $v.Revision
    msix_version         = $v.MsixVersion
    appinstaller_version = $v.AppInstallerVersion
    package              = $v.Package
    prerelease           = $v.Prerelease.ToString().ToLowerInvariant()
    github_prerelease    = $githubPrerelease.ToString().ToLowerInvariant()
    latest               = $latest.ToString().ToLowerInvariant()
}
if ($env:GITHUB_OUTPUT) {
    $lines = ($outputs.Keys | ForEach-Object { "$_=$($outputs[$_])" }) -join "`n"
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, $lines + "`n", (New-Object Text.UTF8Encoding($false)))
}
if ($PassThru) { return $v }
foreach ($k in $outputs.Keys) { Write-Output ('{0,-21} {1}' -f $k, $outputs[$k]) }
if ($Apply) { Write-Output "stamped: Directory.Build.props (TunqioVersion $($v.Version), TunqioVersionRevision $($v.Revision)), Package.appxmanifest (Identity Version $($v.MsixVersion))" }
