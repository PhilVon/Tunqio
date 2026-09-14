<#
.SYNOPSIS
  T-80 (E8-S1): release notes from the commit subjects since the previous release tag.

.DESCRIPTION
  Reads main's first-parent history from the previous v* tag (none for the first release) to the tag, so a merged task
  appears once, by its merge subject. Conventional commit subjects (feat, fix, perf, and the rest, with ! or BREAKING
  CHANGE for breaking) are grouped under their type; this project's task subjects ("T-80: ...") are listed under their task.
  At most -Limit subjects are listed; the rest are counted.

  When the tag does not exist yet (tools/release-dry-run.ps1 uses a tag that is never created), the range ends at HEAD.

.PARAMETER Tag
  The release tag.

.PARAMETER OutFile
  The Markdown file to write.

.PARAMETER Thumbprint
  The signing certificate's SHA-1 thumbprint, printed so users can compare it before trusting Tunqio.cer.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$Thumbprint,
    [int]$Limit = 250
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
$Repository = 'PhilVon/Tunqio'
$v = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $Tag -PassThru

function Invoke-Git([string[]]$arguments) {
    $previous = [Console]::OutputEncoding
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    try {
        $out = & git -C $repo @arguments
        return @{ Code = $LASTEXITCODE; Lines = @($out) }
    } finally { [Console]::OutputEncoding = $previous }
}

$exists = (Invoke-Git @('rev-parse', '-q', '--verify', "refs/tags/$Tag")).Code -eq 0
$end = if ($exists) { $Tag } else { 'HEAD' }
$describe = Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v*', "$end^")
$previousTag = if ($describe.Code -eq 0 -and $describe.Lines.Count -gt 0) { "$($describe.Lines[0])".Trim() } else { $null }
$range = if ($previousTag) { "$previousTag..$end" } else { $end }
$log = Invoke-Git @('log', '--first-parent', '--format=%s', $range)
if ($log.Code -ne 0) { throw "git log $range failed." }
$subjects = @($log.Lines | Where-Object { $_ })

$sections = [ordered]@{
    'Breaking changes' = New-Object Collections.Generic.List[string]
    'Features'         = New-Object Collections.Generic.List[string]
    'Fixes'            = New-Object Collections.Generic.List[string]
    'Performance'      = New-Object Collections.Generic.List[string]
    'Changes by task'  = New-Object Collections.Generic.List[string]
    'Maintenance'      = New-Object Collections.Generic.List[string]
    'Other commits'    = New-Object Collections.Generic.List[string]
}
$listed = 0
foreach ($s in $subjects) {
    if ($listed -ge $Limit) { break }
    $listed++
    $cc = [regex]::Match($s, '^(?<type>[a-z]+)(?:\((?<scope>[^)]*)\))?(?<bang>!)?:\s+(?<text>.+)$')
    $task = [regex]::Match($s, '^(?<tasks>T-\d+(?:,\s*T-\d+)*):\s+(?<text>.+)$')
    if ($cc.Success) {
        $scope = if ($cc.Groups['scope'].Success -and $cc.Groups['scope'].Value) { "**$($cc.Groups['scope'].Value):** " } else { '' }
        $line = "- $scope$($cc.Groups['text'].Value)"
        if ($cc.Groups['bang'].Success -or $s -match 'BREAKING CHANGE') { $sections['Breaking changes'].Add($line) }
        switch ($cc.Groups['type'].Value) {
            'feat' { $sections['Features'].Add($line) }
            'fix' { $sections['Fixes'].Add($line) }
            'perf' { $sections['Performance'].Add($line) }
            default { $sections['Maintenance'].Add($line) }
        }
    } elseif ($task.Success) {
        $sections['Changes by task'].Add("- **$($task.Groups['tasks'].Value)** $($task.Groups['text'].Value)")
    } else {
        $sections['Other commits'].Add("- $s")
    }
}

$kind = if ($v.Prerelease) { "Release candidate $($v.Rc) of Tunqio $($v.Version)." } else { "Tunqio $($v.Version)." }
$md = New-Object Text.StringBuilder
[void]$md.AppendLine("# Tunqio $($v.SemVer)")
[void]$md.AppendLine()
[void]$md.AppendLine("$kind MSIX version $($v.MsixVersion), x64, Windows 10 2004 (build 19041) or later.")
[void]$md.AppendLine()
[void]$md.AppendLine('## Installing')
[void]$md.AppendLine()
[void]$md.AppendLine('Tunqio is signed with its own self-signed certificate (publisher `CN=Tunqio`), so Windows asks you to trust it once per machine.')
[void]$md.AppendLine('Download `Tunqio.cer` and `Tunqio.appinstaller` from the assets below and follow "Installing a release" in the')
[void]$md.AppendLine("[README](https://github.com/$Repository#installing-a-release). Installed through ``Tunqio.appinstaller``, Tunqio checks for updates every 8 hours.")
if ($Thumbprint) {
    [void]$md.AppendLine()
    [void]$md.AppendLine("Signing certificate SHA-1 thumbprint: ``$Thumbprint``. Compare it with the certificate's Details tab before you trust it.")
}
[void]$md.AppendLine()
if ($previousTag) {
    [void]$md.AppendLine("## Changes since $previousTag")
    [void]$md.AppendLine()
    [void]$md.AppendLine("$($subjects.Count) change(s); full diff: https://github.com/$Repository/compare/$previousTag...$Tag")
} else {
    [void]$md.AppendLine('## Changes')
    [void]$md.AppendLine()
    [void]$md.AppendLine("The first release: $($subjects.Count) change(s) on main.")
}
foreach ($name in $sections.Keys) {
    if ($sections[$name].Count -eq 0) { continue }
    [void]$md.AppendLine()
    [void]$md.AppendLine("### $name")
    [void]$md.AppendLine()
    foreach ($line in $sections[$name]) { [void]$md.AppendLine($line) }
}
if ($subjects.Count -gt $listed) {
    [void]$md.AppendLine()
    [void]$md.AppendLine("...and $($subjects.Count - $listed) earlier change(s), not listed.")
}
[IO.File]::WriteAllText($OutFile, $md.ToString(), (New-Object Text.UTF8Encoding($false)))
$from = if ($previousTag) { $previousTag } else { 'the first commit' }
Write-Output "notes: $(Split-Path $OutFile -Leaf), $($subjects.Count) change(s) from $from to $end, $listed listed"
