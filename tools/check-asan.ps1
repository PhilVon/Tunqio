<#
.SYNOPSIS
  Proves the ASan build catches a use-after-free (E0-S1 criterion). Runs the tagged scratch test on the
  ASan mpcore.tests binary and requires a non-zero exit with "heap-use-after-free" in the output.
#>
[CmdletBinding()]
param([string]$Exe)

$ErrorActionPreference = 'Continue'
if (-not $Exe) {
    $root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    $Exe = Join-Path $root 'artifacts\native\ASan\x64\mpcore.tests.exe'
}
if (-not (Test-Path $Exe)) { throw "ASan test binary not found: $Exe" }

$output = & $Exe '[asan-scratch]' 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $output

if ($code -eq 0) { Write-Error 'ASan scratch test exited 0: the use-after-free was not detected.'; exit 1 }
if ($output -notmatch 'heap-use-after-free') { Write-Error "ASan scratch test failed (exit $code) but did not report heap-use-after-free."; exit 1 }
Write-Host "ASan detected the deliberate heap-use-after-free (exit $code)."
# Explicit success so callers checking $LASTEXITCODE do not see the test binary's non-zero exit.
exit 0
