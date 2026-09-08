<#
.SYNOPSIS
  clang-format check for native/ (excluding third_party). Exits 1 on any file that would change.
.PARAMETER Fix
  Rewrite files in place instead of checking.
#>
[CmdletBinding()]
param([switch]$Fix)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

# Prefer the clang-format that ships with the pinned Visual Studio toolset so local and CI agree on the version.
$clangFormat = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -property installationPath
    $candidate = Join-Path $vs 'VC\Tools\Llvm\x64\bin\clang-format.exe'
    if (Test-Path $candidate) { $clangFormat = Get-Command $candidate }
}
if (-not $clangFormat) { $clangFormat = Get-Command clang-format -ErrorAction SilentlyContinue }
if (-not $clangFormat) { throw 'clang-format not found in Visual Studio or on PATH.' }
Write-Host "Using $($clangFormat.Source) ($((& $clangFormat.Source --version).Trim()))"

$files = Get-ChildItem -Path (Join-Path $root 'native\mpcore'), (Join-Path $root 'native\mpcore.tests') -Recurse -Include *.cpp, *.h, *.hpp, *.c |
    Where-Object { $_.FullName -notmatch '[\\/]third_party[\\/]' }

$failed = 0
foreach ($file in $files) {
    if ($Fix) {
        & $clangFormat.Source -i $file.FullName
    }
    else {
        # clang-format reports violations on stderr; let them through untouched (no 2>&1: Windows PowerShell wraps them as errors).
        & $clangFormat.Source --dry-run --Werror $file.FullName
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
}

if ($Fix) { Write-Host "Formatted $($files.Count) files." }
elseif ($failed -gt 0) { Write-Error "$failed file(s) need formatting. Run tools/check-format.ps1 -Fix."; exit 1 }
else { Write-Host "clang-format: $($files.Count) files clean." }
