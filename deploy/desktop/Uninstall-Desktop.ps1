#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the NodePilot desktop runtime: ends the shell and clients, stops and deletes both
    Windows services, and removes the loopback certificate with its private key. With -PurgeData
    it also deletes the data directory (database cluster, keys, settings, logs, backups) and the
    per-user NodePilot folders, so a following installation starts empty.

.DESCRIPTION
    Invoked by the Inno Setup uninstaller before it deletes the program files. Exits 1 when
    something could not be removed, so the uninstaller can say so.
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
$ErrorActionPreference = 'Stop'

$TranscriptPath = Join-Path $env:TEMP 'nodepilot-uninstall.log'
try { Start-Transcript -Path $TranscriptPath -Force | Out-Null } catch { }

. (Join-Path $PSScriptRoot 'DesktopRuntime.ps1')

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

$problems = New-Object System.Collections.Generic.List[string]
function Invoke-UninstallStep([string] $title, [scriptblock] $action) {
    # Every step runs even when an earlier one failed: a service that cannot be deleted is no
    # reason to keep the data the user asked to remove.
    Write-Step $title
    try { & $action } catch {
        Write-Warning "$title failed: $($_.Exception.Message)"
        $problems.Add("${title}: $($_.Exception.Message)")
    }
}

Invoke-UninstallStep 'Stopping NodePilot and removing its services' {
    Stop-DesktopRuntime -InstallPath $InstallPath -DataPath $DataPath `
        -ApiServiceName $ApiServiceName -DbServiceName $DbServiceName -RemoveServices
}

Invoke-UninstallStep 'Removing the loopback certificate' {
    foreach ($cert in @(Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'NodePilot Desktop Local' })) {
        Write-Host "    $($cert.Thumbprint)"
        # -DeleteKey also removes the private key file, which a plain Remove-Item leaves behind.
        Remove-Item -LiteralPath ("Cert:\LocalMachine\My\{0}" -f $cert.Thumbprint) -DeleteKey -Force
    }
}

Invoke-UninstallStep 'Removing first-run setup handoffs' { Remove-DesktopSetupHandoffs }

# Written by the provisioner next to the application, so Inno does not track it.
Invoke-UninstallStep 'Removing the rendered configuration' {
    $rendered = Join-Path $InstallPath 'app\appsettings.Production.json'
    if (Test-Path -LiteralPath $rendered) { Remove-Item -LiteralPath $rendered -Force }
}

if ($PurgeData) {
    Invoke-UninstallStep "Deleting the data directory $DataPath" {
        if (-not (Remove-DesktopDirectory -Path $DataPath)) { throw "$DataPath could not be removed completely." }
    }
    # %APPDATA%\NodePilot holds the shell's browser profile and the np / nodepilot-mcp client
    # configuration, %LOCALAPPDATA%\NodePilot the setup handoff.
    Invoke-UninstallStep 'Deleting per-user NodePilot folders' {
        foreach ($profilePath in @(Get-DesktopUserProfilePaths)) {
            foreach ($relative in @('AppData\Roaming\NodePilot', 'AppData\Local\NodePilot')) {
                $folder = Join-Path $profilePath $relative
                if (-not (Test-Path -LiteralPath $folder)) { continue }
                Write-Host "    $folder"
                if (-not (Remove-DesktopDirectory -Path $folder)) { throw "$folder could not be removed completely." }
            }
        }
    }
    # Versions up to 1.3.1 wrote their first log lines relative to the service's working directory.
    Invoke-UninstallStep 'Deleting early start-up logs' {
        $legacyLogDir = Join-Path $env:SystemRoot 'System32\logs'
        if (Test-Path -LiteralPath $legacyLogDir) {
            Get-ChildItem -LiteralPath $legacyLogDir -Filter 'nodepilot-*.log' -File | Remove-Item -Force
            if (@(Get-ChildItem -LiteralPath $legacyLogDir -Force).Count -eq 0) { Remove-Item -LiteralPath $legacyLogDir -Force }
        }
    }
    $provisionLog = Join-Path $env:TEMP 'nodepilot-provision.log'
    if (Test-Path -LiteralPath $provisionLog) { Remove-Item -LiteralPath $provisionLog -Force -ErrorAction SilentlyContinue }
} else {
    Write-Step "Keeping the data directory $DataPath"
}

if ($problems.Count -gt 0) {
    Write-Warning ("Uninstall finished with problems:`n  " + ($problems -join "`n  "))
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}

Write-Host 'NodePilot desktop runtime removed.' -ForegroundColor Green
try { Stop-Transcript | Out-Null } catch { }
# Nothing went wrong, so the log is not needed; a purge leaves no file behind.
Remove-Item -LiteralPath $TranscriptPath -Force -ErrorAction SilentlyContinue
exit 0
