#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Runs from the installer before it copies any file. On an existing installation it takes a
    database backup and then ends the shell, the clients and both services, so no program file is
    in use when setup replaces it. With -DiscardData it deletes the existing data directory
    instead, so the installation starts with an empty database.

.DESCRIPTION
    Setup extracts this script and its helpers to a temporary folder, because the copies inside an
    existing installation belong to the older version. A no-op on a clean computer.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstallPath,
    [string] $DataPath = (Join-Path $env:ProgramData 'NodePilot'),
    [string] $ApiServiceName = 'NodePilot',
    [string] $DbServiceName  = 'NodePilotDb',
    [switch] $DiscardData
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$TranscriptPath = Join-Path $env:TEMP 'nodepilot-setup-prepare.log'
try { Start-Transcript -Path $TranscriptPath -Force | Out-Null } catch { }

. (Join-Path $PSScriptRoot 'DesktopRuntime.ps1')

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

try {
    if (-not $DiscardData -and (Test-Path -LiteralPath (Join-Path $DataPath 'pgdata\PG_VERSION'))) {
        # Best effort: Update-Desktop.ps1 -BackupOnly never fails the install.
        Write-Step 'Backing up the existing database'
        & (Join-Path $PSScriptRoot 'Update-Desktop.ps1') -InstallPath $InstallPath -DataPath $DataPath `
            -ApiServiceName $ApiServiceName -BackupOnly
    }

    Write-Step 'Stopping the running NodePilot installation'
    Stop-DesktopRuntime -InstallPath $InstallPath -DataPath $DataPath `
        -ApiServiceName $ApiServiceName -DbServiceName $DbServiceName -RemoveServices:$DiscardData

    if ($DiscardData) {
        Write-Step "Deleting the existing data directory $DataPath"
        if (-not (Remove-DesktopDirectory -Path $DataPath)) { throw "$DataPath could not be removed completely." }
        Remove-DesktopSetupHandoffs
    }
} catch {
    Write-Warning $_.Exception.Message
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}

Write-Host 'Ready to install.' -ForegroundColor Green
try { Stop-Transcript | Out-Null } catch { }
exit 0
