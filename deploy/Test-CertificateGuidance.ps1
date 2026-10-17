#requires -Version 5.1
<# Exercises certificate guidance without installing certificates or changing machine settings. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'Preflight.ps1')
. (Join-Path $PSScriptRoot 'SetupContract.ps1')
$script:checks = 0
function Assert-Guidance([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Certificate guidance: $Message" }
    $script:checks++
}
$certificate = [pscustomobject]@{
    Subject = 'CN=np.example.test'; Issuer = 'CN=np.example.test'; Thumbprint = ('A' * 40)
    DnsNameList = @('np.example.test', 'GW1', 'localhost') | ForEach-Object { [pscustomobject]@{ Unicode = $_ } }
    NotBefore = [datetime]'2026-01-01'; NotAfter = [datetime]'2027-01-01'; RawData = [byte[]](1, 2, 3, 4)
}
$verdict = New-NodePilotCertificateVerdict -Certificate $certificate -Thumbprint $certificate.Thumbprint `
    -PublicHostname 'np.example.test' -Now ([datetime]'2026-10-17')
Assert-Guidance ($verdict.Status -eq 'Pass' -and $verdict.Detail -match 'np.example.test, GW1, localhost') 'Passing readiness must list every certificate name.'
Assert-Guidance ($verdict.Detail -match '2027-01-01') 'Passing readiness must always show expiry.'
$expired = New-NodePilotCertificateVerdict -Certificate $certificate -Thumbprint $certificate.Thumbprint `
    -PublicHostname 'np.example.test' -Now ([datetime]'2028-01-01')
Assert-Guidance ($expired.Status -eq 'Warn' -and $expired.Detail -match 'GW1') 'Expiry remains a warning with certificate names.'
$future = New-NodePilotCertificateVerdict -Certificate $certificate -Thumbprint $certificate.Thumbprint `
    -PublicHostname 'np.example.test' -Now ([datetime]'2025-01-01')
Assert-Guidance ($future.Status -eq 'Warn') 'Not-yet-valid certificates remain warnings.'
$names = @(Get-NodePilotServerNames -PublicHostname 'np.example.test' -ComputerName 'GW1')
$allowed = Resolve-NodePilotAllowedHosts -PublicHostname 'np.example.test' -ComputerName 'GW1'
Assert-Guidance ($allowed -eq ($names -join ';') -and $names.Count -eq 3) 'Generated SAN names and default AllowedHosts must match.'
Assert-Guidance ((Resolve-NodePilotAllowedHosts -PublicHostname 'GW1' -ComputerName 'gw1') -eq 'GW1;localhost') 'Default names are deduplicated case-insensitively.'
Assert-Guidance ((Resolve-NodePilotAllowedHosts -PublicHostname 'np.example.test' -ComputerName 'GW1' -AllowedHosts 'proxy.example.test;LOCALHOST') -eq 'proxy.example.test;LOCALHOST') 'Explicit host restrictions must not be widened.'
Assert-Guidance ((Resolve-NodePilotAllowedHosts -PublicHostname 'np.example.test' -ComputerName 'GW1' -AllowedHosts 'proxy.example.test') -eq 'proxy.example.test;localhost') 'A single explicit hostname appends only the health-probe name.'
$summary = Format-NodePilotCertificateSummary -Certificate $certificate -ServerUrl 'https://np.example.test:8443/'
Assert-Guidance ($summary -match 'SHA-1' -and $summary -match ('A' * 40)) 'Windows store ID must be labelled SHA-1.'
Assert-Guidance ($summary -match 'np.example.test, GW1, localhost' -and $summary -match '2027-01-01') 'Finish metadata includes names and expiry.'
Assert-Guidance ($summary -match 'self-signed' -and $summary -match 'each client') 'Self-signed trust instructions are client-specific.'
Assert-Guidance ($summary.Contains('np auth login --server "https://np.example.test:8443/" --tls-thumbprint 9F64A747E1B97F131FABB6B447296C9B6F0201E79FB3C5356E6C77E89B6A806A')) 'CLI pin is SHA-256 over RawData and retains the port.'
$certificate.Issuer = 'CN=Fixture issuing CA'
$issued = Format-NodePilotCertificateSummary -Certificate $certificate -ServerUrl 'https://np.example.test/'
Assert-Guidance ($issued -notmatch 'self-signed' -and $issued -match 'issuing CA') 'CA-issued certificates must not be described as self-signed or client-trusted.'
Assert-Guidance ($issued -match 'certificate pinning \(optional\)') 'CA-issued pinning is optional.'
$noAddress = Format-NodePilotCertificateSummary -Certificate $certificate -ServerUrl ''
Assert-Guidance ($noAddress -notmatch 'np auth login' -and $noAddress -match 'SHA-256') 'Missing address retains certificate metadata without fabricating a command.'
$ini = ConvertTo-NodePilotIniValue $summary
Assert-Guidance ($ini -notmatch "`r|`n" -and $ini -match '\\n') 'Full guidance survives the INI transport.'
Assert-Guidance ((Get-NodePilotTlsLoginCommand -PublicHostname 'np.example.test' -HttpsPort 443 -Sha256 ('B' * 64)).Contains('"https://np.example.test/"')) 'Default HTTPS port uses the same URL builder.'
Assert-Guidance ((Get-NodePilotTlsLoginCommand -PublicHostname 'np.example.test' -HttpsPort 9443 -Sha256 ('B' * 64)).Contains('"https://np.example.test:9443/"')) 'Certificate helper command preserves nonstandard ports.'
$defaultHost = Get-NodePilotDefaultPublicHostname
Assert-Guidance (-not [string]::IsNullOrWhiteSpace($defaultHost)) 'Hostname detection always returns a usable suggestion.'
$defaultsOutput = Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-defaults-' + [guid]::NewGuid().ToString('N') + '.ini')
try {
    & (Join-Path $PSScriptRoot 'Invoke-NodePilotSetup.ps1') -Mode Defaults -OutFile $defaultsOutput
    Assert-Guidance ((Get-Content -LiteralPath $defaultsOutput -Raw).Contains("publicHostname=$defaultHost")) 'Wizard adapter returns the same default as the script installer.'
} finally { if (Test-Path -LiteralPath $defaultsOutput) { Remove-Item -LiteralPath $defaultsOutput } }
& {
    $creation = @{ Names = @() }
    function New-SelfSignedCertificate {
        param($DnsName, $CertStoreLocation, $KeyAlgorithm, $KeyLength, $HashAlgorithm, $KeyExportPolicy,
            $NotAfter, $FriendlyName, $Type, $TextExtension, $Provider, $KeyUsage)
        $creation.Names = $DnsName
        return $certificate
    }
    $output = & (Join-Path $PSScriptRoot 'New-NodePilotSelfSignedCertificate.ps1') -PublicHostname 'np.example.test' -HttpsPort 9443 6>&1
    Assert-Guidance (($creation.Names -join ';') -eq (Resolve-NodePilotAllowedHosts -PublicHostname 'np.example.test')) 'Actual certificate creation uses the shared host defaults.'
    Assert-Guidance (($output | Out-String).Contains('np auth login --server "https://np.example.test:9443/" --tls-thumbprint')) 'Certificate script output retains its selected HTTPS port.'
}
Write-Host "Certificate guidance checks passed ($script:checks assertions; no machine changes)."
