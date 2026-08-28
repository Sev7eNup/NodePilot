#requires -Version 5.1

<#
.SYNOPSIS
    Creates a self-signed TLS certificate for Kestrel. Lab and pilot use only.
.DESCRIPTION
    Opt-in helper behind the setup wizard's readiness page, for a machine that has no PKI
    certificate yet.

    Two deliberate limits:

      * The default lifetime is two years, so a lab certificate expires while its purpose is
        still known.
      * The certificate is not imported into LocalMachine\Root; granting machine-wide trust is
        left to the operator. The commands are printed instead, and they have to be run on every
        client anyway.
.PARAMETER PublicHostname
    The name clients will use. Becomes the subject and the first SAN entry.
.PARAMETER ValidityYears
    Lifetime. Default 2.
.OUTPUTS
    The thumbprint, so the caller can fill it into the installer's -CertThumbprint.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicHostname,
    [int]$ValidityYears = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

if ([string]::IsNullOrWhiteSpace($PublicHostname)) {
    throw 'A public hostname is required; the certificate subject and SAN are built from it.'
}

# localhost is included so the installer's own health probe against https://localhost:<port>
# validates against the same certificate the service presents.
$dnsNames = @($PublicHostname, $env:COMPUTERNAME, 'localhost') |
    Where-Object { $_ } |
    Select-Object -Unique

Write-Host "[setup] Creating a self-signed certificate for $($dnsNames -join ', ')" -ForegroundColor Cyan
Write-Host '[setup] LAB USE ONLY. Browsers and API clients will reject it until its public part' -ForegroundColor Yellow
Write-Host '[setup] is imported into the trusted root store on every machine that talks to this server.' -ForegroundColor Yellow

$certificate = New-SelfSignedCertificate `
    -Subject "CN=$PublicHostname" `
    -DnsName $dnsNames `
    -CertStoreLocation 'Cert:\LocalMachine\My' `
    -FriendlyName "NodePilot Server $PublicHostname (self-signed)" `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -KeyExportPolicy NonExportable `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
    -NotAfter (Get-Date).AddYears($ValidityYears)

Write-Host "[setup] Created $($certificate.Thumbprint), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
Write-Host ''
Write-Host '  To make clients trust it, export the public part and import it on each of them:' -ForegroundColor Yellow
Write-Host "    Export-Certificate -Cert Cert:\LocalMachine\My\$($certificate.Thumbprint) -FilePath nodepilot-tls.cer" -ForegroundColor Gray
Write-Host '    Import-Certificate -FilePath nodepilot-tls.cer -CertStoreLocation Cert:\LocalMachine\Root' -ForegroundColor Gray
Write-Host ''

return $certificate.Thumbprint
