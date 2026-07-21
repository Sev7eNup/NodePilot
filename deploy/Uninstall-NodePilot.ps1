#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the NodePilot Windows Service and cleans up firewall rules and binaries.
.DESCRIPTION
    Stops and deletes the service, removes firewall rules, and wipes the install
    directory. The data directory (logs, DPAPI-bound credentials on disk, DB artefacts,
    etc.) is preserved unless -PurgeData is specified. The SQL Server database is never
    touched.
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
} else {
    Write-Info "  Service not present."
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
    Write-Info "  Note: the SQL Server database is never touched by this script. Drop it manually via DBA tooling when you're sure."
}

Write-Host ""
Write-Ok "Uninstall complete."
