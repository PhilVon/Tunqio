<#
.SYNOPSIS
  T-80 (E8-S1): signs the release MSIX with the self-signed CN=Tunqio certificate held as repository secrets, and refuses,
  loudly, when the secrets are missing or wrong. A release is never published unsigned.

.DESCRIPTION
  The certificate reaches this script only through two environment variables, which release.yml fills from repository
  secrets of the same names:

    TUNQIO_SIGNING_PFX_BASE64    the PFX (certificate and private key), base64-encoded
    TUNQIO_SIGNING_PFX_PASSWORD  the PFX password

  -CheckOnly opens the PFX and checks it (subject equal to the manifest publisher, a private key, the code-signing EKU, not
  expired) without signing; release.yml runs it first, so missing or wrong secrets fail the run in seconds rather than
  after the build. Without -CheckOnly the PFX is written to a temporary file for signtool, deleted in a finally block, the
  package is signed with an RFC 3161 timestamp (so the signature stays valid after the certificate expires), and the
  signature inside the package is read back and must name the same certificate. -CerOut exports the public certificate
  (never the key) for users to trust.

  -ReadSignature prints the signer of any signed .msix, reading AppxSignature.p7x directly; it needs no secrets and no
  trust, which is how tools/release-dry-run.ps1 proves the reader on a Microsoft-signed dependency package.

.PARAMETER Msix
  The package to sign.

.PARAMETER CerOut
  Where to write the public certificate (DER .cer).

.PARAMETER CheckOnly
  Validate the secrets and stop.

.PARAMETER ReadSignature
  A signed .msix whose signer to print.

.PARAMETER TimestampUrl
  RFC 3161 timestamp server.
#>
[CmdletBinding()]
param(
    [string]$Msix,
    [string]$CerOut,
    [switch]$CheckOnly,
    [string]$ReadSignature,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security

function Write-GitHubOutput([string]$name, [string]$value) {
    if ($env:GITHUB_OUTPUT) { [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "$name=$value`n", (New-Object Text.UTF8Encoding($false))) }
}

function Stop-Release([string]$message) {
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::error title=Release signing::$message" }
    throw $message
}

# The signer certificate of a signed MSIX: AppxSignature.p7x is the 4-byte 'PKCX' magic followed by a PKCS #7 SignedData.
function Get-MsixSigner([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'AppxSignature.p7x' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $ms = New-Object IO.MemoryStream
        $s = $entry.Open()
        try { $s.CopyTo($ms) } finally { $s.Dispose() }
        $bytes = $ms.ToArray()
    } finally { $zip.Dispose() }
    if ($bytes.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'PKCX') { throw "$path has an AppxSignature.p7x without the PKCX header." }
    $cms = New-Object Security.Cryptography.Pkcs.SignedCms
    $body = New-Object byte[] ($bytes.Length - 4)
    [Array]::Copy($bytes, 4, $body, 0, $body.Length)
    $cms.Decode($body)
    return $cms.SignerInfos[0].Certificate
}

if ($ReadSignature) {
    $signer = Get-MsixSigner (Resolve-Path $ReadSignature).Path
    if (-not $signer) { throw "$ReadSignature is not signed (no AppxSignature.p7x)." }
    Write-Output "signer:     $($signer.Subject)"
    Write-Output "thumbprint: $($signer.Thumbprint)"
    return
}

# ---- the secrets -----------------------------------------------------------------------------------------------------------
$names = @('TUNQIO_SIGNING_PFX_BASE64', 'TUNQIO_SIGNING_PFX_PASSWORD')
$missing = @($names | Where-Object { -not [Environment]::GetEnvironmentVariable($_) })
if ($missing.Count -gt 0) {
    Stop-Release ("Signing secret(s) missing or empty: $($missing -join ', '). A Tunqio release is never published unsigned. " +
        "Add both as repository secrets (GitHub: Settings > Secrets and variables > Actions > New repository secret), " +
        "as README.md 'Releasing Tunqio (maintainer)' describes, then re-run this workflow run (a re-run reads the secrets as they are now).")
}

$manifestText = [IO.File]::ReadAllText((Join-Path $repo 'src\Tunqio.App\Package.appxmanifest'))
$publisher = [regex]::Match($manifestText, '<Identity\b[^>]*?\sPublisher="([^"]*)"').Groups[1].Value
if (-not $publisher) { Stop-Release 'Could not read Identity Publisher from src/Tunqio.App/Package.appxmanifest.' }

try {
    $pfxBytes = [Convert]::FromBase64String(($env:TUNQIO_SIGNING_PFX_BASE64 -replace '\s', ''))
} catch {
    Stop-Release 'TUNQIO_SIGNING_PFX_BASE64 is not valid base64. Encode the .pfx file itself: [Convert]::ToBase64String([IO.File]::ReadAllBytes(''Tunqio-release.pfx'')).'
}
$password = $env:TUNQIO_SIGNING_PFX_PASSWORD
try {
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxBytes, $password, $flags)
} catch {
    Stop-Release "TUNQIO_SIGNING_PFX_BASE64 does not open as a PFX with TUNQIO_SIGNING_PFX_PASSWORD ($($_.Exception.GetType().Name)). Check that the password secret matches the export."
}

$problems = @()
if ($cert.Subject -cne $publisher) { $problems += "its subject is '$($cert.Subject)', but the manifest publisher is '$publisher' (they must be equal, and the publisher is frozen)" }
if (-not $cert.HasPrivateKey) { $problems += 'it holds no private key (export the PFX with its key)' }
$eku = @($cert.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
if ($eku -notcontains '1.3.6.1.5.5.7.3.3') { $problems += 'it lacks the code-signing enhanced key usage (1.3.6.1.5.5.7.3.3)' }
if ($cert.NotAfter -lt (Get-Date)) { $problems += "it expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))" }
if ($problems.Count -gt 0) { Stop-Release ("The signing certificate cannot sign Tunqio: " + ($problems -join '; ') + '.') }

Write-Output "certificate: $($cert.Subject), thumbprint $($cert.Thumbprint), valid to $($cert.NotAfter.ToString('yyyy-MM-dd'))"
if ($cert.NotAfter -lt (Get-Date).AddDays(90)) {
    $warn = "The signing certificate expires on $($cert.NotAfter.ToString('yyyy-MM-dd')). A new certificate has to be trusted again on every machine."
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Output "::warning title=Release signing::$warn" } else { Write-Warning $warn }
}
Write-GitHubOutput 'thumbprint' $cert.Thumbprint
if ($CheckOnly) {
    Write-Output 'signing secrets: present, open, and fit to sign'
    return
}

# ---- sign --------------------------------------------------------------------------------------------------------------------
if (-not $Msix) { throw '-Msix is required unless -CheckOnly or -ReadSignature.' }
$Msix = (Resolve-Path $Msix).Path
$propsText = [IO.File]::ReadAllText((Join-Path $repo 'Directory.Build.props'))
$sdk = [regex]::Match($propsText, '<TunqioWindowsSdkVersion>([^<]+)</TunqioWindowsSdkVersion>').Groups[1].Value
$signtool = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$sdk\x64\signtool.exe"
if (-not (Test-Path $signtool)) { throw "signtool.exe not found at $signtool (Windows SDK $sdk, pinned in Directory.Build.props)." }

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfxPath = Join-Path $tempRoot ('tunqio-sign-' + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
    $signed = $false
    for ($attempt = 1; $attempt -le 3 -and -not $signed; $attempt++) {
        # The timestamp server is the one network dependency here; retry it a bounded number of times.
        & $signtool sign /fd SHA256 /f $pfxPath /p $password /tr $TimestampUrl /td SHA256 $Msix
        if ($LASTEXITCODE -eq 0) { $signed = $true } elseif ($attempt -lt 3) { Start-Sleep -Seconds 15 }
    }
    if (-not $signed) { Stop-Release "signtool could not sign $Msix after 3 attempts (see its output above; the timestamp server is $TimestampUrl)." }
} finally {
    if (Test-Path $pfxPath) { Remove-Item -Force $pfxPath }
}

$signer = Get-MsixSigner $Msix
if (-not $signer) { Stop-Release "$Msix has no AppxSignature.p7x after signing." }
if ($signer.Thumbprint -ne $cert.Thumbprint) { Stop-Release "$Msix is signed by $($signer.Subject) ($($signer.Thumbprint)), not the release certificate ($($cert.Thumbprint))." }
Write-Output "signed: $Msix by $($signer.Subject) ($($signer.Thumbprint))"

if ($CerOut) {
    [IO.File]::WriteAllBytes($CerOut, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Output "public certificate: $CerOut"
}
$cert.Dispose()
