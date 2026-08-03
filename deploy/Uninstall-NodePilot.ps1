#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the NodePilot Windows Service and cleans up firewall rules and binaries.
.DESCRIPTION
    Stops and deletes the service, removes firewall rules, removes the machine-wide
    installation marker, and wipes the install directory. The data directory (logs, DPAPI-bound
    credentials on disk, DB artefacts, etc.) is preserved unless -PurgeData is specified. The
    SQL Server database is never touched.

    Two things are deliberately NOT undone, because both can be shared with something else on
    the host and revoking them blind would break it: the 'Log on as a service' right granted to
    a gMSA, and the read ACE on the TLS certificate's private key. The script names both, with
    the command to remove them, at the end of its run.
.PARAMETER ServiceName
    Windows Service name. Default: NodePilot.
.PARAMETER InstallPath
    Install directory to delete. Default: C:\Program Files\NodePilot.
.PARAMETER DataPath
    Writable data directory (logs, JWT key, admin-setup.token). Preserved unless -PurgeData.
.PARAMETER PurgeData
    Also delete DataPath (logs, JWT key, admin-setup.token, install-report). Irreversible.
.EXAMPLE
    .\deploy\Uninstall-NodePilot.ps1
.EXAMPLE
    .\deploy\Uninstall-NodePilot.ps1 -PurgeData
#>

[CmdletBinding()]
param(
    [string]$ServiceName = 'NodePilot',
    [string]$InstallPath = 'C:\Program Files\NodePilot',
    [string]$DataPath = 'C:\ProgramData\NodePilot',
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Write-Step { param([string]$Text) Write-Host "[uninstall] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[uninstall] $Text" -ForegroundColor Gray }
function Write-Ok   { param([string]$Text) Write-Host "[uninstall] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "[uninstall] $Text" -ForegroundColor Yellow }

# Read the runtime identity and the certificate thumbprint BEFORE anything is deleted, so the
# closing report can name what it is leaving behind.
$serviceStartName = $null
try {
    $escapedServiceName = $ServiceName.Replace("'", "''")
    $cimService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedServiceName'" -ErrorAction SilentlyContinue
    if ($cimService) { $serviceStartName = $cimService.StartName }
} catch { $serviceStartName = $null }

$installedThumbprint = $null
try {
    $settingsPath = Join-Path $InstallPath 'appsettings.Production.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $installedThumbprint = $settings.Kestrel.Https.CertificateThumbprint
    }
} catch { $installedThumbprint = $null }

Write-Step "Stopping and removing service '$ServiceName'"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (-not $s -or $s.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }
    }
    & sc.exe delete $ServiceName | Out-Null
    Write-Info "  sc.exe delete returned exit $LASTEXITCODE"

    # sc.exe delete normally takes the whole service key with it, INCLUDING the Environment
    # MULTI_SZ that holds ConnectionStrings__Postgres - i.e. the database password. But when
    # anything still holds an SCM handle, the service goes DELETE_PENDING and the key survives
    # until reboot, leaving that secret readable on disk. Clear the value explicitly rather
    # than hope the handle was closed.
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (Test-Path -LiteralPath $serviceKey) {
        Write-Info "  Service key still present (deletion pending); clearing its Environment value."
        Remove-ItemProperty -LiteralPath $serviceKey -Name 'Environment' -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Info "  Service not present."
}

Write-Step "Removing the installation marker"
# Without this, a fresh install after an uninstall is misdetected as an upgrade forever - the
# setup wizard reads this key to choose between the two.
$markerPath = 'HKLM:\SOFTWARE\NodePilot\Server'
if (Test-Path -LiteralPath $markerPath) {
    Remove-Item -LiteralPath $markerPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Info "  Removed: $markerPath"
} else {
    Write-Info "  No installation marker present."
}
# Drop the parent only when this uninstall emptied it; something else may live under it.
$markerParent = 'HKLM:\SOFTWARE\NodePilot'
if ((Test-Path -LiteralPath $markerParent) -and
    -not (Get-ChildItem -LiteralPath $markerParent -ErrorAction SilentlyContinue) -and
    -not (Get-Item -LiteralPath $markerParent -ErrorAction SilentlyContinue).GetValueNames()) {
    Remove-Item -LiteralPath $markerParent -Force -ErrorAction SilentlyContinue
}

Write-Step "Removing firewall rules"
foreach ($name in @("NodePilot $ServiceName HTTPS", "NodePilot $ServiceName HTTP-Redirect")) {
    $rules = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($rules) {
        $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        Write-Info "  Removed: $name"
    }
}

Write-Step "Removing install directory"
if (Test-Path $InstallPath) {
    Remove-Item -Path $InstallPath -Recurse -Force
    Write-Info "  Deleted: $InstallPath"
} else {
    Write-Info "  Install path not present."
}

if ($PurgeData) {
    Write-Step "Purging data directory"
    if (Test-Path $DataPath) {
        Remove-Item -Path $DataPath -Recurse -Force
        Write-Info "  Deleted: $DataPath"
    } else {
        Write-Info "  Data path not present."
    }
} else {
    Write-Info "Preserved data directory at $DataPath (pass -PurgeData to wipe)."
}

# Everything below is left in place on purpose. Each of these can be shared with something else
# on this host, so revoking them blind could break an unrelated service. Name them instead of
# silently leaving them, and give the exact command for each.
Write-Step "Left in place on purpose"
Write-Info "  The database is never touched by this script. Drop it via DBA tooling when you're sure."
$isManagedAccount = $serviceStartName -and $serviceStartName.TrimEnd().EndsWith('$') -and
    $serviceStartName.Trim().ToLowerInvariant() -ne 'localsystem'
if ($isManagedAccount) {
    Write-Warn "  '$serviceStartName' keeps its 'Log on as a service' right. Another service may rely on it."
    Write-Info "    Remove with secedit, or via secpol.msc > Local Policies > User Rights Assignment."
}
if ($installedThumbprint -and $isManagedAccount) {
    Write-Warn "  '$serviceStartName' keeps read access to the private key of certificate $installedThumbprint."
    Write-Info "    Inspect with: certlm.msc > Personal > Certificates > All Tasks > Manage Private Keys."
}

Write-Host ""
Write-Ok "Uninstall complete."
