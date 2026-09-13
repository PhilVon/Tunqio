<#
.SYNOPSIS
  Downloads the pinned ffmpeg build listed in tools/ffmpeg-dep.json into artifacts/ffmpeg/bin (T-136).

  ffmpeg is a test tool, not a dependency of the product: ffprobe is AC-105's independent tagger and ffmpeg
  encodes the fixture library. So it lives beside tools/fetch-native.ps1 rather than in native-deps.json, but
  follows the same discipline: a pinned archive, verified against a recorded SHA-256 before anything is
  extracted, and a hard failure on a mismatch. CI used to install it from the Chocolatey community feed, which
  went red on 2026-09-12 because the feed had a bad minute.

  Layout produced (under artifacts/, so gitignored):
    artifacts/ffmpeg/.cache/<archive>   the verified download
    artifacts/ffmpeg/bin/ffmpeg.exe
    artifacts/ffmpeg/bin/ffprobe.exe

  Under GitHub Actions the bin directory is appended to GITHUB_PATH, so later steps find both tools on PATH,
  which is where the tests look for them.
.PARAMETER PrintHash
  Maintenance: download without verifying and print the archive's SHA-256.
.PARAMETER Force
  Ignore the cache and download again.
#>
[CmdletBinding()]
param(
    [switch]$PrintHash,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $toolsDir
$pin = Get-Content -Raw -LiteralPath (Join-Path $toolsDir 'ffmpeg-dep.json') | ConvertFrom-Json

$root = Join-Path $repoRoot 'artifacts\ffmpeg'
$cache = Join-Path $root '.cache'
$bin = Join-Path $root 'bin'
foreach ($d in $cache, $bin) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

$archivePath = Join-Path $cache (Split-Path -Leaf $pin.url)
if (-not $Force -and -not $PrintHash -and (Test-Path -LiteralPath $archivePath) -and (Get-Sha256 $archivePath) -eq $pin.sha256) {
    Write-Host "  cached   $archivePath"
}
else {
    $tmp = "$archivePath.download"
    Write-Host "  download $($pin.url)"
    Invoke-WebRequest -Uri $pin.url -OutFile $tmp -UseBasicParsing
    $actual = Get-Sha256 $tmp
    if ($PrintHash) {
        Write-Host "  sha256   $actual"
        Move-Item -Force -LiteralPath $tmp -Destination $archivePath
        exit 0
    }
    if ($actual -ne $pin.sha256) {
        Remove-Item -Force -LiteralPath $tmp
        throw "SHA-256 mismatch for $($pin.url)`n  expected $($pin.sha256)`n  actual   $actual`nThe archive changed upstream; review it, then update tools/ffmpeg-dep.json (tools/fetch-ffmpeg.ps1 -PrintHash)."
    }
    Move-Item -Force -LiteralPath $archivePath -Destination "$archivePath.old" -ErrorAction SilentlyContinue
    Move-Item -Force -LiteralPath $tmp -Destination $archivePath
    Remove-Item -Force -LiteralPath "$archivePath.old" -ErrorAction SilentlyContinue
}

$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
        $entry = $archive.Entries | Where-Object { $_.FullName -like "*/bin/$tool" } | Select-Object -First 1
        if (-not $entry) { throw "no bin/$tool in $archivePath (layout changed upstream?)" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $bin $tool), $true)
    }
}
finally { $archive.Dispose() }

if ($env:GITHUB_PATH) { Add-Content -LiteralPath $env:GITHUB_PATH -Value $bin -Encoding utf8 }
Write-Host "Done: $($pin.description) in $bin"
exit 0
