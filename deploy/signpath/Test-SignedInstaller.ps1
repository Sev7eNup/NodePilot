#requires -Version 5.1
<#
.SYNOPSIS
    Verifies a SignPath test-signed installer and writes checksums for the signed bytes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Server', 'Desktop')][string]$Target,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$SignedDirectory,
    [Parameter(Mandatory)][string]$UnsignedDirectory,
    [Parameter(Mandatory)][string]$RequestUrl,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CmsThumbprint
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Test certificate trust is restricted to a disposable GitHub-hosted runner.'
}

$expectedThumbprint = '1E3876C87EF8D2B73F940F80674D5B81C2AA3649'
$name = "NodePilot-$Target-Setup-$Version.exe"
$signedPath = Join-Path $SignedDirectory $name
$unsignedPath = Join-Path $UnsignedDirectory $name
$expectedPath = (Resolve-Path -LiteralPath $signedPath).Path
$returned = @(Get-ChildItem -LiteralPath $SignedDirectory -Recurse -File)
if ($returned.Count -ne 1 -or $returned[0].FullName -ne $expectedPath) {
    throw 'SignPath must return exactly the expected installer at the artifact root.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $signedPath
if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
    throw 'The installer is not signed by the pinned SignPath test certificate.'
}

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
$store.Open('ReadWrite')
$alreadyTrusted = @($store.Certificates | Where-Object { $_.Thumbprint -eq $expectedThumbprint }).Count -gt 0
try {
    if (-not $alreadyTrusted) { $store.Add($signature.SignerCertificate) }
    $verified = Get-AuthenticodeSignature -LiteralPath $signedPath
    if ($verified.Status -ne 'Valid') {
        throw "Authenticode verification failed: $($verified.Status) - $($verified.StatusMessage)"
    }
} finally {
    if (-not $alreadyTrusted) { $store.Remove($signature.SignerCertificate) }
    $store.Close()
}

$signedHash = (Get-FileHash -LiteralPath $signedPath -Algorithm SHA256).Hash
$unsignedHash = (Get-FileHash -LiteralPath $unsignedPath -Algorithm SHA256).Hash
if ($signedHash -eq $unsignedHash) { throw 'Signing returned unchanged installer bytes.' }
"$signedHash  $name" | Set-Content -LiteralPath (Join-Path $SignedDirectory 'SHA256SUMS.txt') -Encoding ascii
@(
    'TEST ONLY - not a production release. Do not install on production systems.'
    "Source commit: $env:GITHUB_SHA"
    "Build: $env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
    "SignPath request: $RequestUrl"
    "Installer: $name"
    "Authenticode signer: $expectedThumbprint"
    'Authenticode verification: Valid with the pinned self-signed test certificate.'
    "Runner-local CMS signer: $CmsThumbprint (seven-day test certificate; separate from SignPath)."
    "Unsigned installer SHA-256: $unsignedHash"
    "Signed installer SHA-256: $signedHash"
    'Only the outer setup executable was signed through SignPath; embedded binaries and scripts were not re-signed.'
) | Set-Content -LiteralPath (Join-Path $SignedDirectory 'signing-evidence.txt') -Encoding ascii
Get-Content -LiteralPath (Join-Path $SignedDirectory 'signing-evidence.txt') |
    Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding ascii
