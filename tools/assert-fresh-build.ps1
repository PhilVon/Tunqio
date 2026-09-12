<#
.SYNOPSIS
  T-161: refuses to drive a build of the shell that is older than the source it was built from.

.DESCRIPTION
  Dot-source this and call Assert-FreshBuild before launching Tunqio.exe.

  WHY IT EXISTS. Building the SOLUTION builds native/mpcore; building a managed PROJECT does not, and says
  nothing. Directory.Build.targets now refuses that at build time, but a harness can be pointed at an output
  directory that no build touched at all this session - which is the commoner case and the more expensive one,
  because the app does not fail, it degrades into something that reads as a product bug. Measured 2026-09-12:
  a shell whose mpcore.dll predated the story implementing mp_renderer_enum_presets by thirty-five minutes
  opened Settings > Visualization with an empty preset list and no Refresh button, and twelve of
  check-visualization-settings.ps1's seventeen cases failed naming a missing CONTROL. Nothing said the core was
  old. That cost two false review rejections in one day: once for a page that existed, once for a slider bug
  that had been fixed ninety minutes earlier.

  So a harness now says "your binaries are older than your source" up front, and names which one and by how
  much, instead of producing a list of missing controls for somebody to read as a regression.

  BOTH binaries are checked, because both false rejections happened: the first was a stale mpcore.dll (native
  source), the second a stale Tunqio.dll (managed source).
#>

Set-StrictMode -Version Latest

function Get-NewestSource {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Include)
    $newest = $null
    foreach ($pattern in $Include) {
        Get-ChildItem -LiteralPath $Root -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts)\\' } |
            ForEach-Object {
                if ($null -eq $newest -or $_.LastWriteTimeUtc -gt $newest.LastWriteTimeUtc) { $newest = $_ }
            }
    }
    return $newest
}

function Assert-FreshBuild {
    [CmdletBinding()]
    param(
        # The directory holding Tunqio.exe and mpcore.dll.
        [Parameter(Mandatory)][string]$AppDir,
        # Report and carry on rather than throwing. For a harness deliberately run against an older build.
        [switch]$WarnOnly
    )

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $checks = @(
        @{ Binary = 'mpcore.dll'; Source = (Join-Path $repoRoot 'native\mpcore'); Include = @('*.cpp', '*.h')
           Build = 'msbuild Tunqio.sln -restore -p:Configuration=Debug -p:Platform=x64  (a project-scoped build does NOT build the native core)' }
        @{ Binary = 'Tunqio.dll'; Source = (Join-Path $repoRoot 'src'); Include = @('*.cs', '*.xaml')
           Build = 'msbuild Tunqio.sln -restore -p:Configuration=Debug -p:Platform=x64' }
    )

    $stale = @()
    foreach ($check in $checks) {
        $binary = Join-Path $AppDir $check.Binary
        if (-not (Test-Path -LiteralPath $binary)) {
            $stale += "  $($check.Binary) is not in $AppDir at all. Build: $($check.Build)"
            continue
        }
        if (-not (Test-Path -LiteralPath $check.Source)) { continue }

        $newest = Get-NewestSource -Root $check.Source -Include $check.Include
        if ($null -eq $newest) { continue }

        $built = (Get-Item -LiteralPath $binary).LastWriteTimeUtc
        if ($newest.LastWriteTimeUtc -gt $built) {
            $behind = $newest.LastWriteTimeUtc - $built
            $stale += ("  {0} was built {1:yyyy-MM-dd HH:mm:ss} and {2} changed {3:yyyy-MM-dd HH:mm:ss} -- the binary is {4} behind its source.`n    Build: {5}" -f `
                    $check.Binary, (Get-Item -LiteralPath $binary).LastWriteTime, `
                    $newest.FullName.Substring($repoRoot.Length + 1), $newest.LastWriteTime, `
                ('{0:%d}d {0:%h}h {0:%m}m' -f $behind), $check.Build)
        }
    }

    if ($stale.Count -eq 0) {
        Write-Output "  freshness  ok (binaries in $AppDir are newer than their source)"
        return
    }

    $message = "This build of the shell is OLDER than the source it was built from, so anything this harness reports is about the old binary and not about your change (T-161):`n" +
    ($stale -join "`n") +
    "`nRe-run the harness after building. Pass -SkipFreshnessCheck to drive an old build deliberately."
    if ($WarnOnly) {
        Write-Warning $message
        return
    }
    throw $message
}
