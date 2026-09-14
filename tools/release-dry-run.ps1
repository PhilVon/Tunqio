<#
.SYNOPSIS
  T-80 (E8-S1): runs everything release.yml does except signing and publishing, on this machine, for a tag that is never
  created. The evidence that the release pipeline works before a real tag spends runner minutes.

.DESCRIPTION
  Steps, each PASS or FAIL, stopping at the first failure; the summary is printed on every outcome:
    1. tools/release-version.ps1 -Apply stamps the tag's versions into Directory.Build.props and the manifest.
    2. (-FullBuild) the solution build at the stamped version, -warnaserror, and IdentityTests over the stamped manifest.
    3. The packaged build, unsigned, exactly as ci.yml's package step but at -m:<MaxCpu>.
    4. tools/check-package.ps1 and tools/check-presets.ps1 over the built MSIX, as ci.yml selects it.
    5. tools/release-sign.ps1 -CheckOnly with no signing secrets in the environment must fail and name both secrets.
    6. tools/release-assets.ps1: Tunqio_<ver>_x64.msix, its dependencies, Tunqio.appinstaller and the symbol archive.
    7. tools/release-sign.ps1 -ReadSignature reads the signer of a staged, Microsoft-signed dependency package.
    8. check-package.ps1 and check-presets.ps1 again over the staged Tunqio_<ver>_x64.msix.
    9. tools/release-sbom.ps1, then the document is parsed back and counted.
   10. tools/release-notes.ps1.
   11. tools/check-release-workflow.py over .github/workflows/release.yml, when Python with PyYAML is present.

  Directory.Build.props and Package.appxmanifest are restored byte for byte in a finally block; the script refuses to start
  when either has uncommitted changes. Output goes under artifacts/release-dry-run/<stamp>. Nothing is signed, tagged,
  pushed or published, and no certificate is created or read.

.PARAMETER Tag
  The pretend tag. Default v1.0.0-rc.1, the first tag Phil will push.

.PARAMETER FullBuild
  Also rebuild the solution at the stamped version and run IdentityTests over it (a few minutes more).

.PARAMETER MaxCpu
  MSBuild -m. Default 2: other agents may be building on this machine.
#>
[CmdletBinding()]
param(
    [string]$Tag = 'v1.0.0-rc.1',
    [switch]$FullBuild,
    [int]$MaxCpu = 2,
    [string]$MSBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) { $MSBuild = & $vswhere -latest -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1 }
    if (-not $MSBuild) { $MSBuild = 'd:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' }
}
if (-not (Test-Path $MSBuild)) { throw "MSBuild.exe not found ($MSBuild); pass -MSBuild." }
$shell = (Get-Process -Id $PID).Path

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $repo "artifacts\release-dry-run\$stamp"
$stage = Join-Path $work 'release'
New-Item -ItemType Directory -Force $work | Out-Null

$props = Join-Path $repo 'Directory.Build.props'
$manifest = Join-Path $repo 'src\Tunqio.App\Package.appxmanifest'
& git -C $repo diff --quiet -- Directory.Build.props src/Tunqio.App/Package.appxmanifest
if ($LASTEXITCODE -ne 0) { throw 'Directory.Build.props or Package.appxmanifest has uncommitted changes; the dry run stamps and restores them, so commit or set those changes aside first.' }
$originalProps = [IO.File]::ReadAllBytes($props)
$originalManifest = [IO.File]::ReadAllBytes($manifest)

$results = New-Object Collections.Generic.List[object]
$script:failed = $false

function Invoke-Step([string]$name, [scriptblock]$body) {
    if ($script:failed) {
        $results.Add([pscustomobject]@{ Step = $name; Result = 'SKIPPED'; Seconds = 0; Detail = 'an earlier step failed' })
        return
    }
    Write-Host ''
    Write-Host "==== $name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $script:last = ''
    try {
        & $body | ForEach-Object { Write-Host $_; if ("$_".Trim()) { $script:last = "$_".Trim() } }
        $result = 'PASS'
        $detail = $script:last
    } catch {
        $result = 'FAIL'
        $detail = $_.Exception.Message
        $script:failed = $true
        Write-Host "FAIL: $detail"
    }
    $results.Add([pscustomobject]@{ Step = $name; Result = $result; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $detail })
}

function Assert-Exit([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what exited with $LASTEXITCODE." }
}

function Invoke-Checks([string]$msixPath) {
    $root = Join-Path $repo 'artifacts\bin\Tunqio.App\release_win-x64_msix'
    $global:LASTEXITCODE = 0
    & (Join-Path $repo 'tools\check-package.ps1') -Root $root -Msix $msixPath
    Assert-Exit 'check-package.ps1'
    $global:LASTEXITCODE = 0
    & (Join-Path $repo 'tools\check-presets.ps1') -Root $root -Msix $msixPath
    Assert-Exit 'check-presets.ps1'
    "check-package.ps1 and check-presets.ps1 PASS over $(Split-Path $msixPath -Leaf)"
}

$v = $null
try {
    Invoke-Step "1. Version stamped from $Tag" {
        & (Join-Path $repo 'tools\release-version.ps1') -Tag $Tag -Apply
        $script:v = & (Join-Path $repo 'tools\release-version.ps1') -Tag $Tag -PassThru
        $p = [IO.File]::ReadAllText($props)
        $m = [IO.File]::ReadAllText($manifest)
        if ($p -notmatch "<TunqioVersion>$([regex]::Escape($script:v.Version))</TunqioVersion>") { throw 'TunqioVersion was not stamped.' }
        if ($p -notmatch "<TunqioVersionRevision>$($script:v.Revision)</TunqioVersionRevision>") { throw 'TunqioVersionRevision was not stamped.' }
        if ($m -notmatch "Version=`"$([regex]::Escape($script:v.MsixVersion))`"") { throw 'The manifest Identity Version was not stamped.' }
        "stamped $($script:v.SemVer) as MSIX $($script:v.MsixVersion)"
    }

    if ($FullBuild) {
        Invoke-Step "2a. Solution build at the stamped version (-warnaserror, -m:$MaxCpu)" {
            & $MSBuild (Join-Path $repo 'Tunqio.sln') -restore "-m:$MaxCpu" -p:Configuration=Release -p:Platform=x64 -warnaserror -nologo -v:minimal '-clp:ErrorsOnly;Summary'
            Assert-Exit 'MSBuild Tunqio.sln'
            'solution build: 0 warnings, 0 errors'
        }
        Invoke-Step '2b. IdentityTests over the stamped manifest and props' {
            & dotnet test (Join-Path $repo 'tests\Tunqio.Core.Tests\Tunqio.Core.Tests.csproj') -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName~IdentityTests' --nologo
            Assert-Exit 'dotnet test IdentityTests'
            'IdentityTests passed'
        }
    }

    Invoke-Step "3. Packaged build, unsigned (-m:$MaxCpu)" {
        & $MSBuild (Join-Path $repo 'src\Tunqio.App\Tunqio.App.csproj') -p:Configuration=Release -p:Platform=x64 -p:TunqioPackaged=true -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false "-m:$MaxCpu" -nologo -v:minimal '-clp:ErrorsOnly;Summary'
        Assert-Exit 'MSBuild Tunqio.App.csproj (packaged)'
        'packaged build: 0 warnings, 0 errors'
    }

    Invoke-Step '4. check-package and check-presets over the built MSIX (ci.yml selection)' {
        $msix = @(Get-ChildItem (Join-Path $repo 'artifacts\msix') -Recurse -Filter Tunqio.msix | Where-Object { $_.Directory.Name -like 'Tunqio_*' })
        if ($msix.Count -ne 1) { throw "expected exactly one artifacts/msix/Tunqio_*/Tunqio.msix, found $($msix.Count)" }
        "package: $($msix[0].FullName)"
        Invoke-Checks $msix[0].FullName
    }

    Invoke-Step '5. Signing refuses without the secrets' {
        $saved = @{}
        foreach ($n in 'TUNQIO_SIGNING_PFX_BASE64', 'TUNQIO_SIGNING_PFX_PASSWORD') { $saved[$n] = [Environment]::GetEnvironmentVariable($n); [Environment]::SetEnvironmentVariable($n, $null) }
        try {
            $out = & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\release-sign.ps1') -CheckOnly 2>&1 | Out-String
            $code = $LASTEXITCODE
        } finally {
            foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
        }
        $message = ($out -split "`r?`n" | Where-Object { $_ -match 'Signing secret' } | Select-Object -First 1)
        if ($code -eq 0) { throw 'release-sign.ps1 -CheckOnly succeeded with no secrets.' }
        if ($out -notmatch 'TUNQIO_SIGNING_PFX_BASE64' -or $out -notmatch 'TUNQIO_SIGNING_PFX_PASSWORD' -or $out -notmatch 'never published unsigned') { throw "the refusal does not name both secrets: $out" }
        "exit $code; message: $("$message".Trim())"
    }

    Invoke-Step '6. Stage assets: package, dependencies, Tunqio.appinstaller, symbols' {
        & (Join-Path $repo 'tools\release-assets.ps1') -Tag $Tag -OutDir $stage
        Write-Output '---- Tunqio.appinstaller'
        Get-Content (Join-Path $stage 'Tunqio.appinstaller')
        Write-Output '---- symbol archive entries'
        $zipPath = Join-Path $stage "Tunqio_$($script:v.MsixVersion)_x64_symbols.zip"
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try { $zip.Entries | ForEach-Object { '{0,-40} {1,12:N0} bytes' -f $_.FullName, $_.Length } } finally { $zip.Dispose() }
        Write-Output '---- staged files'
        Get-ChildItem $stage -File | ForEach-Object { '{0,-60} {1,14:N0} bytes' -f $_.Name, $_.Length }
    }

    Invoke-Step '7. The signature reader reads a signed MSIX (a staged Microsoft dependency)' {
        $dep = Get-ChildItem $stage -Filter *.msix | Where-Object { $_.Name -notlike 'Tunqio_*' } | Select-Object -First 1
        if (-not $dep) { throw 'no dependency package was staged to read' }
        $global:LASTEXITCODE = 0
        & (Join-Path $repo 'tools\release-sign.ps1') -ReadSignature $dep.FullName
        "read $($dep.Name)"
    }

    Invoke-Step '8. check-package and check-presets over the staged release package' {
        Invoke-Checks (Join-Path $stage $script:v.Package)
    }

    Invoke-Step '9. SBOM (CycloneDX 1.5)' {
        $sbom = Join-Path $stage "Tunqio_$($script:v.MsixVersion)_x64.cdx.json"
        & (Join-Path $repo 'tools\release-sbom.ps1') -Tag $Tag -OutFile $sbom
        $text = [IO.File]::ReadAllText($sbom)
        if ($PSVersionTable.PSEdition -eq 'Core') { $doc = ConvertFrom-Json $text } else {
            Add-Type -AssemblyName System.Web.Extensions
            $ser = New-Object Web.Script.Serialization.JavaScriptSerializer
            $ser.MaxJsonLength = [int]::MaxValue
            $doc = $ser.DeserializeObject($text)
        }
        $components = @($doc['components'])
        if ($doc['bomFormat'] -ne 'CycloneDX' -or $doc['specVersion'] -ne '1.5') { throw 'not a CycloneDX 1.5 document' }
        $nuget = @($components | Where-Object { "$($_['purl'])" -like 'pkg:nuget/*' })
        $unhashed = @($nuget | Where-Object { -not $_['hashes'] })
        $unlicensed = @($nuget | Where-Object { -not $_['licenses'] })
        $native = @($components | Where-Object { "$($_['bom-ref'])" -like 'tunqio:native/*' })
        'components: {0} ({1} NuGet, {2} without a hash, {3} without a licence; {4} native incl. mpcore)' -f $components.Count, $nuget.Count, $unhashed.Count, $unlicensed.Count, $native.Count
        if ($unlicensed.Count -gt 0) { 'NuGet packages whose .nuspec declares no licence: ' + (($unlicensed | ForEach-Object { $_['name'] }) -join ', ') }
        "dependency entries: $(@($doc['dependencies']).Count)"
    }

    Invoke-Step '10. Release notes' {
        $notes = Join-Path $work 'release-notes.md'
        & (Join-Path $repo 'tools\release-notes.ps1') -Tag $Tag -OutFile $notes -Thumbprint '(dry run: no certificate)'
        Write-Output '---- first 30 lines'
        Get-Content $notes -TotalCount 30
    }

    Invoke-Step '11. Workflow file validation (Python + PyYAML)' {
        $py = Get-Command python -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $py) { throw 'python is not on PATH; run tools/check-release-workflow.py where it is' }
        $global:LASTEXITCODE = 0
        & $py.Source (Join-Path $repo 'tools\check-release-workflow.py')
        Assert-Exit 'check-release-workflow.py'
    }
} finally {
    [IO.File]::WriteAllBytes($props, $originalProps)
    [IO.File]::WriteAllBytes($manifest, $originalManifest)
    Write-Host ''
    Write-Host 'restored: Directory.Build.props, src/Tunqio.App/Package.appxmanifest'
}

Write-Host ''
Write-Host "==== Release dry run for $Tag ($stamp), output $work"
foreach ($r in $results) { Write-Host ('{0,-7} {1,7}s  {2}  ->  {3}' -f $r.Result, $r.Seconds, $r.Step, $r.Detail) }
$bad = @($results | Where-Object { $_.Result -ne 'PASS' }).Count
Write-Host ("DRY RUN: {0} of {1} step(s) PASS" -f ($results.Count - $bad), $results.Count)
if ($bad -gt 0) { exit 1 }
exit 0
