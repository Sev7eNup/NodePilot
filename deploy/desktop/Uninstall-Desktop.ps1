#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the NodePilot desktop runtime: stops + deletes both Windows services and removes the
    self-signed loopback certificate. ProgramData (including the Postgres data directory) is
    preserved unless -PurgeData is passed.
.NOTES
    Invoked by the Inno Setup uninstaller [UninstallRun]. Not executed by Claude.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstallPath,
    [string] $DataPath = (Join-Path $env:ProgramData 'NodePilot'),
    [string] $ApiServiceName = 'NodePilot',
    [string] $DbServiceName  = 'NodePilotDb',
    [switch] $PurgeData
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Continue'

function Remove-NodePilotService([string] $name) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    Write-Host "Stopping and removing service '$name'..."
    & sc.exe stop $name | Out-Null
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') { break }
    }
    & sc.exe delete $name | Out-Null
}

# Stop the API first (it depends on the DB), then Postgres.
Remove-NodePilotService $ApiServiceName
Remove-NodePilotService $DbServiceName

# Remove the loopback certificate (identified by friendly name).
try {
    Get-ChildItem -Path Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq 'NodePilot Desktop Local' } |
        ForEach-Object {
            Write-Host "Removing certificate $($_.Thumbprint)..."
            Remove-Item -Path ("Cert:\LocalMachine\My\{0}" -f $_.Thumbprint) -Force -ErrorAction SilentlyContinue
        }
} catch { Write-Warning "Certificate cleanup skipped: $($_.Exception.Message)" }

# Best-effort: remove the per-user first-run handoff if it still exists.
$handoff = Join-Path $env:LOCALAPPDATA 'NodePilot\admin-setup.handoff'
if (Test-Path -LiteralPath $handoff) { Remove-Item -LiteralPath $handoff -Force -ErrorAction SilentlyContinue }

if ($PurgeData) {
    Write-Host "Purging data directory $DataPath..."
    if (Test-Path -LiteralPath $DataPath) { Remove-Item -LiteralPath $DataPath -Recurse -Force -ErrorAction SilentlyContinue }
} else {
    Write-Host "Preserving data directory $DataPath (pass -PurgeData to remove)."
}

Write-Host 'NodePilot desktop runtime removed.'
exit 0
