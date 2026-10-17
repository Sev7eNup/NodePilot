#requires -Version 5.1

Set-StrictMode -Version 3.0

function Get-NodePilotPfxCertificate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][SecureString]$Password)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'The PFX file was not found.' }
    try {
        $data = Get-PfxData -FilePath $Path -Password $Password -ErrorAction Stop
        if (@($data.EndEntityCertificates).Count -ne 1) {
            throw 'Select a PFX containing exactly one server certificate.'
        }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $Path, $Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            if (-not $certificate.HasPrivateKey) { throw 'The PFX has no private key.' }
            return [pscustomobject]@{ Thumbprint = $certificate.Thumbprint; Subject = $certificate.Subject }
        } finally { $certificate.Dispose() }
    } catch {
        throw 'The PFX could not be opened. Check the password and select a PFX with one server certificate and its private key.'
    }
}

function Import-NodePilotPfxCertificate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][SecureString]$Password)
    $expected = Get-NodePilotPfxCertificate -Path $Path -Password $Password
    $existing = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($expected.Thumbprint)" -ErrorAction SilentlyContinue
    if (-not $existing -or -not $existing.HasPrivateKey) {
        $null = Import-PfxCertificate -FilePath $Path -Password $Password -CertStoreLocation 'Cert:\LocalMachine\My' -ErrorAction Stop
    }
    $installed = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($expected.Thumbprint)" -ErrorAction Stop
    if (-not $installed.HasPrivateKey) { throw 'The imported certificate has no machine private key.' }
    return $installed.Thumbprint
}

function Install-NodePilotLocalGmsa {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Account, [switch]$InstallAdTools)
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        if (-not $InstallAdTools) { throw 'AD PowerShell tools are missing. Select their installation or install RSAT-AD-PowerShell first.' }
        if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) {
            throw 'Automatic AD tools installation is available on Windows Server. Install the ActiveDirectory PowerShell module on this computer first.'
        }
        $feature = Install-WindowsFeature -Name RSAT-AD-PowerShell -ErrorAction Stop
        if (-not $feature.Success) { throw 'AD PowerShell tools could not be installed. Check the Windows feature source and policy.' }
        if ([string]$feature.RestartNeeded -eq 'Yes') {
            return [pscustomobject]@{ Status = 'Fail'; RestartRequired = $true; Detail = 'AD tools require a restart. Restart this computer, then run setup again to prepare the gMSA.' }
        }
    }
    Import-Module ActiveDirectory -ErrorAction Stop
    $sam = ($Account.Split('\')[-1]).TrimEnd('$')
    if (-not (Test-ADServiceAccount -Identity $sam -ErrorAction Stop)) {
        Install-ADServiceAccount -Identity $sam -ErrorAction Stop
    }
    if (-not (Test-ADServiceAccount -Identity $sam -ErrorAction Stop)) {
        throw 'The gMSA is not usable on this host. Have an AD administrator check password retrieval permissions and computer group membership; a restart may be needed.'
    }
    return [pscustomobject]@{ Status = 'Pass'; RestartRequired = $false; Detail = "The existing gMSA '$Account' is ready on this computer." }
}
