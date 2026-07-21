#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Transactional in-place upgrade of NodePilot.
.DESCRIPTION
    Verifies and pre-extracts the signed artifact before stopping the service. The current
    binaries are backed up while the service is still running; appsettings.Production.json is
    never copied to a backup and remains only in memory. Any failure after mutation starts rolls
    binaries and configuration back before the old service is restarted.
.PARAMETER ArtifactPath
    Path to the new NodePilot-*.zip.
.PARAMETER TrustedArtifactSignerThumbprint
    Pinned Code Signing certificate thumbprint used to verify the detached CMS signature.
.PARAMETER ServiceName
    Windows Service name. Default: NodePilot.
.PARAMETER InstallPath
    Install directory. Default: C:\Program Files\NodePilot.
.PARAMETER DataPath
    Writable data directory. Retained for command-line compatibility.
.PARAMETER HttpsPort
    HTTPS port used for the health probe after restart. Default: 443.
.PARAMETER KeepBackupCount
    Number of binary-only backups to retain. Default: 3.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$TrustedArtifactSignerThumbprint,
    [string]$ServiceName = 'NodePilot',
    [string]$InstallPath = 'C:\Program Files\NodePilot',
    [string]$DataPath = 'C:\ProgramData\NodePilot',
    [int]$HttpsPort = 443,
    [int]$KeepBackupCount = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
if (-not (Test-Path -LiteralPath $ArtifactSecurityScript -PathType Leaf)) {
    throw "Artifact security helper not found: $ArtifactSecurityScript"
}
. $ArtifactSecurityScript

function Write-Step { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Gray }
function Write-Ok   { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Green }

function Resolve-ServiceAclIdentity {
    param([Parameter(Mandatory)][string]$Name)
    $account = $null
    try { $account = (Get-WmiObject Win32_Service -Filter "Name='$Name'" -ErrorAction Stop).StartName } catch {}
    if (-not $account) {
        try { $account = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop).StartName } catch {}
    }
    if (-not $account) {
        throw "Could not resolve the service account for '$Name'; refusing to read or rewrite production configuration."
    }
    if ($account.Trim().ToLowerInvariant() -in @('localsystem', '.\localsystem', 'system')) {
        return 'NT AUTHORITY\SYSTEM'
    }
    return $account
}

function Set-RestrictedSettingsAcl {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ServiceAccount)
    Set-NodePilotRestrictedFileAcl `
        -Path $Path `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:($ServiceAccount -eq 'NT AUTHORITY\SYSTEM')
}

function Write-RestrictedSettings {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][byte[]]$Content, [Parameter(Mandatory)][string]$ServiceAccount)
    Write-NodePilotRestrictedFile `
        -Path $Path `
        -Content $Content `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:($ServiceAccount -eq 'NT AUTHORITY\SYSTEM')
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [string]$ExcludedFileName
    )
    Get-ChildItem -LiteralPath $Source -Force |
        Where-Object { -not $ExcludedFileName -or $_.Name -ne $ExcludedFileName } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
        }
}

function Stop-ServiceAndVerify {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSeconds = 30)
    if ((Get-Service -Name $Name -ErrorAction Stop).Status -eq 'Stopped') { return }
    Stop-Service -Name $Name -Force -ErrorAction Stop
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Service -Name $Name -ErrorAction Stop).Status -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Service '$Name' did not stop within ${TimeoutSeconds}s; installed files were not changed."
}

$artifactLock = $null
$artifactStage = $null
$settingsBytes = $null
$backupDir = $null
$installTouched = $false
$previousCertificatePolicy = $null
$previousSecurityProtocol = $null
$certificatePolicyChanged = $false

try {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
        throw "Artifact not found: $ArtifactPath"
    }
    $ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
    $artifactLock = [IO.File]::Open(
        $ArtifactPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $verifiedArtifact = Assert-NodePilotSignedArtifact `
        -ArtifactPath $ArtifactPath `
        -TrustedSignerThumbprint $TrustedArtifactSignerThumbprint `
        -ArtifactStream $artifactLock
    Write-Ok "Verified signed artifact version $($verifiedArtifact.Version), signer $($verifiedArtifact.SignerThumbprint)."

    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
        throw "Install path not found: $InstallPath - run Install-NodePilot.ps1 for a fresh install."
    }
    $settingsPath = Join-Path $InstallPath 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "No appsettings.Production.json at $settingsPath. Refusing to upgrade a non-installer layout."
    }

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$ServiceName' not found. Nothing to update." }
    $serviceWasRunning = $svc.Status -ne 'Stopped'
    $svcAccount = Resolve-ServiceAclIdentity -Name $ServiceName
    Set-RestrictedSettingsAcl -Path $settingsPath -ServiceAccount $svcAccount
    $settingsBytes = [IO.File]::ReadAllBytes($settingsPath)

    # Reject a malformed signed ZIP before stopping the service or touching the installation.
    $artifactStage = Expand-NodePilotArtifactToStaging -ArtifactPath $ArtifactPath
    Write-Info "Verified restricted staging: $artifactStage"

    # Installed binaries are immutable. Backing them up while the service still runs ensures a
    # disk/ACL failure here cannot leave an otherwise healthy service stopped.
    Write-Step 'Backing up current install'
    $backupDir = "$InstallPath.backup.$(Get-Date -Format 'yyyyMMdd-HHmmssfff')-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $backupDir -ErrorAction Stop | Out-Null
    Copy-DirectoryContents `
        -Source $InstallPath `
        -Destination $backupDir `
        -ExcludedFileName 'appsettings.Production.json'
    Write-Info "Binary-only backup: $backupDir"

    try {
        Write-Step "Stopping service '$ServiceName'"
        Stop-ServiceAndVerify -Name $ServiceName

        Write-Step 'Installing verified artifact'
        $installTouched = $true
        Get-ChildItem -LiteralPath $InstallPath -Force |
            Remove-Item -Recurse -Force -ErrorAction Stop
        Copy-DirectoryContents -Source $artifactStage -Destination $InstallPath
        Assert-NodePilotExtractedFiles -RootPath $InstallPath
        Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount

        Write-Step "Starting service '$ServiceName'"
        Start-Service -Name $ServiceName -ErrorAction Stop

        # The localhost probe is the only operation that bypasses certificate validation. Restore
        # the process-global Windows PowerShell 5.1 policy when the script exits.
        if ($PSVersionTable.PSVersion.Major -lt 6) {
            if (-not ('TrustAllCertsUpdate' -as [type])) {
                Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsUpdate : ICertificatePolicy {
  public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; }
}
"@
            }
            $previousCertificatePolicy = [System.Net.ServicePointManager]::CertificatePolicy
            $previousSecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol
            [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsUpdate
            [System.Net.ServicePointManager]::SecurityProtocol = 'Tls12, Tls13'
            $certificatePolicyChanged = $true
        }

        $probeUrl = "https://localhost:$HttpsPort/healthz/ready"
        Write-Info "Probing $probeUrl (up to 60s)..."
        $deadline = (Get-Date).AddSeconds(60)
        $healthy = $false
        while ((Get-Date) -lt $deadline) {
            try {
                $response = if ($PSVersionTable.PSVersion.Major -ge 6) {
                    Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -SkipCertificateCheck -TimeoutSec 5
                } else {
                    Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 5
                }
                if ($response.StatusCode -eq 200) { $healthy = $true; break }
            }
            catch { Start-Sleep -Seconds 2 }
        }
        if (-not $healthy) { throw 'Service did not become ready after upgrade.' }
        Write-Ok '/healthz/ready returned 200 OK'

        if (-not $serviceWasRunning) {
            Stop-ServiceAndVerify -Name $ServiceName
            Write-Info 'Restored the service to its pre-update stopped state.'
        }

        try {
            $backups = @(Get-ChildItem -Directory "$InstallPath.backup.*" -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending)
            if ($backups.Count -gt $KeepBackupCount) {
                $backups | Select-Object -Skip $KeepBackupCount | ForEach-Object {
                    Write-Info "Pruning old backup: $($_.FullName)"
                    Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
                }
            }
        }
        catch {
            Write-Host "[update] Update succeeded, but old backup pruning failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
        Write-Ok 'Update complete.'
    }
    catch {
        $updateError = $_
        Write-Host "[update] FAILED: $($updateError.Exception.Message)" -ForegroundColor Red
        try {
            if ($installTouched) {
                Write-Host "[update] Rolling back from $backupDir" -ForegroundColor Yellow
                Stop-ServiceAndVerify -Name $ServiceName
                if (Test-Path -LiteralPath $InstallPath) {
                    Get-ChildItem -LiteralPath $InstallPath -Force |
                        Remove-Item -Recurse -Force -ErrorAction Stop
                }
                Copy-DirectoryContents -Source $backupDir -Destination $InstallPath
                Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount
            }

            if ($serviceWasRunning -and
                (Get-Service -Name $ServiceName -ErrorAction Stop).Status -eq 'Stopped') {
                Start-Service -Name $ServiceName -ErrorAction Stop
            }
            if ($installTouched) {
                Write-Host "[update] Rollback complete. Restored $backupDir" -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "[update] Rollback ALSO failed: $($_.Exception.Message). Manual intervention required; backup: $backupDir" -ForegroundColor Red
        }
        throw $updateError
    }
}
finally {
    if ($certificatePolicyChanged) {
        [System.Net.ServicePointManager]::CertificatePolicy = $previousCertificatePolicy
        [System.Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
    }
    if ($settingsBytes) { [Array]::Clear($settingsBytes, 0, $settingsBytes.Length) }
    if ($artifactStage -and (Test-Path -LiteralPath $artifactStage)) {
        Remove-Item -LiteralPath $artifactStage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($artifactLock) { $artifactLock.Dispose() }
}
