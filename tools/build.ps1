<#
.SYNOPSIS
  Builds Tunqio: native projects with the Visual Studio MSBuild, managed projects with the .NET SDK.

  The canonical single command is `msbuild Tunqio.sln -restore -p:Configuration=Release -p:Platform=x64`
  (what CI runs). It needs the ".NET desktop development" and "WinUI application development" workloads in
  the same Visual Studio as the C++ tools, because MSBuild.exe resolves the .NET SDK through them. This script
  works without those workloads: it drives the C++ projects through MSBuild.exe and everything else through
  `dotnet` via the Tunqio.Managed.slnf filter.
.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER Test
  Also run the native suite (Release and ASan), the ASan proof and the managed test projects.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    Write-Host "== $Description" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit $LASTEXITCODE)" }
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ toolset not found.' }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'

foreach ($proj in 'native\mpcore\mpcore.vcxproj', 'native\mpcore.tests\mpcore.tests.vcxproj', 'native\mpcore.tests\abi_stub\abi_stub.vcxproj', 'native\spikes\bass_hello\bass_hello.vcxproj') {
    Invoke-Checked "native: $proj ($Configuration)" {
        & $msbuild $proj -p:Configuration=$Configuration -p:Platform=x64 -m -nologo -v:minimal
    }
}
Invoke-Checked 'managed: Tunqio.Managed.slnf' {
    dotnet build Tunqio.Managed.slnf -c $Configuration -p:Platform=x64 -nologo -v:minimal
}

if ($Test) {
    Invoke-Checked "native tests ($Configuration)" {
        & "artifacts\native\$Configuration\x64\mpcore.tests.exe" --reporter compact
    }
    Invoke-Checked 'native: mpcore.tests (ASan)' {
        & $msbuild native\mpcore.tests\mpcore.tests.vcxproj -p:Configuration=ASan -p:Platform=x64 -m -nologo -v:minimal
    }
    Invoke-Checked 'native tests (ASan)' {
        & 'artifacts\native\ASan\x64\mpcore.tests.exe' --reporter compact
    }
    Invoke-Checked 'ASan detects use-after-free' {
        & (Join-Path $PSScriptRoot 'check-asan.ps1')
    }
    Invoke-Checked 'managed tests' {
        dotnet test Tunqio.Managed.slnf -c $Configuration -p:Platform=x64 --no-build -nologo -v:minimal
    }
}

Write-Host 'Done.' -ForegroundColor Green
