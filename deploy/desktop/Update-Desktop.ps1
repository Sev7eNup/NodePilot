#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Coordinated update for the NodePilot desktop install. Two modes:
      -BackupOnly : just take an ACL-protected pg_dump (invoked by the Inno installer before it
                    overwrites binaries on an upgrade).
      full        : stage -> pg_dump -> stop shell + services -> swap binaries -> re-provision ->
                    health-check, with rollback of binaries + config + DB dump on failure.

.DESCRIPTION
    Postgres MAJOR-version upgrades are out of scope for v1 (the bundled PG stays on its major).
    The DB password / port are read from the ACL-restricted service-environment connection string,
    never from JSON. Not executed by Claude; requires on-VM validation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstallPath,
    [string] $DataPath = (Join-Path $env:ProgramData 'NodePilot'),
    [string] $ApiServiceName = 'NodePilot',
    [string] $DbServiceName  = 'NodePilotDb',
    # Directory containing the new app\ desktop\ pgsql\ deploy\ payload (full-update mode only).
    [string] $NewArtifactPath,
    [int]    $KeepBackupCount = 3,
    [switch] $BackupOnly
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$pgBin       = Join-Path $InstallPath 'pgsql\bin'
$pgDump      = Join-Path $pgBin 'pg_dump.exe'
$pgRestore   = Join-Path $pgBin 'pg_restore.exe'
$BackupsDir  = Join-Path $DataPath 'backups'
$DesktopJson = Join-Path $DataPath 'desktop.json'

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

function Get-PostgresConnection {
    # Authoritative source: the ACL-restricted service-environment connection string.
    $svcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ApiServiceName"
    $envArr = (Get-ItemProperty -Path $svcRegPath -Name 'Environment' -ErrorAction Stop).Environment
    $line = $envArr | Where-Object { $_ -like 'ConnectionStrings__Postgres=*' } | Select-Object -First 1
    if (-not $line) { throw "ConnectionStrings__Postgres not found on service '$ApiServiceName'." }
    $conn = $line -replace '^ConnectionStrings__Postgres=', ''
    $map = @{}
    foreach ($pair in ($conn -split ';')) {
        if ($pair -match '^\s*([^=]+)=(.*)$') { $map[$matches[1].Trim().ToLowerInvariant()] = $matches[2].Trim() }
    }
    return [pscustomobject]@{
        DbHost   = if ($map.ContainsKey('host')) { $map['host'] } else { '127.0.0.1' }
        Port     = if ($map.ContainsKey('port')) { $map['port'] } else { '5432' }
        Database = if ($map.ContainsKey('database')) { $map['database'] } else { 'nodepilot' }
        Username = if ($map.ContainsKey('username')) { $map['username'] } else { 'nodepilot' }
        Password = if ($map.ContainsKey('password')) { $map['password'] } else { '' }
    }
}

function New-DatabaseBackup {
    if (-not (Test-Path -LiteralPath $pgDump)) { throw "pg_dump not found at $pgDump." }
    New-Item -ItemType Directory -Force -Path $BackupsDir | Out-Null
    # Lock the backups directory to SYSTEM + Administrators.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $BackupsDir -AclObject $acl

    $c = Get-PostgresConnection
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $backupFile = Join-Path $BackupsDir "pre-update-$stamp.dump"
    Write-Step "Backing up database to $backupFile"
    $old = [Environment]::GetEnvironmentVariable('PGPASSWORD')
    [Environment]::SetEnvironmentVariable('PGPASSWORD', $c.Password)
    try {
        & $pgDump '-h' $c.DbHost '-p' $c.Port '-U' $c.Username '-d' $c.Database '-Fc' '-f' $backupFile
        if ($LASTEXITCODE -ne 0) { throw "pg_dump exited with code $LASTEXITCODE." }
    } finally {
        [Environment]::SetEnvironmentVariable('PGPASSWORD', $old)
    }

    # Prune to the most recent $KeepBackupCount.
    Get-ChildItem -LiteralPath $BackupsDir -Filter 'pre-update-*.dump' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $KeepBackupCount |
        Remove-Item -Force -ErrorAction SilentlyContinue

    return $backupFile
}

# --- BackupOnly mode -------------------------------------------------------------------------
if ($BackupOnly) {
    try {
        $f = New-DatabaseBackup
        Write-Host "Pre-update backup complete: $f" -ForegroundColor Green
        exit 0
    } catch {
        # Best-effort: never block an in-place installer upgrade on a backup failure.
        Write-Warning "Pre-update DB backup failed (continuing): $($_.Exception.Message)"
        exit 0
    }
}

# --- Full staged update ----------------------------------------------------------------------
if (-not $NewArtifactPath) { throw 'Full update requires -NewArtifactPath (or use -BackupOnly).' }
if (-not (Test-Path -LiteralPath $NewArtifactPath)) { throw "New artifact path not found: $NewArtifactPath." }

$backupFile = New-DatabaseBackup
$rollbackRoot = Join-Path $DataPath ("rollback\{0}" -f (Get-Date).ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
$components = @('app', 'desktop', 'pgsql')

function Stop-Everything {
    Write-Step 'Stopping shell and services'
    Get-Process -Name 'NodePilot' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($svc in @($ApiServiceName, $DbServiceName)) {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { & sc.exe stop $svc | Out-Null }
    }
    Start-Sleep -Seconds 3
}

function Start-Services {
    foreach ($svc in @($DbServiceName, $ApiServiceName)) {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { & sc.exe start $svc | Out-Null }
        Start-Sleep -Seconds 2
    }
}

function Test-Ready {
    if (-not (Test-Path -LiteralPath $DesktopJson)) { return $false }
    $origin = (Get-Content -LiteralPath $DesktopJson -Raw | ConvertFrom-Json).origin
    $thumb = (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'NodePilot Desktop Local' } | Select-Object -First 1).Thumbprint
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { param($s,$c,$ch,$e) $c.Thumbprint -eq $thumb }
    try {
        for ($i = 0; $i -lt 60; $i++) {
            try { if ((Invoke-WebRequest "$origin/healthz/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return $true } }
            catch { Start-Sleep -Seconds 2 }
        }
        return $false
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
    }
}

try {
    Stop-Everything

    Write-Step 'Swapping binaries'
    foreach ($c in $components) {
        $cur = Join-Path $InstallPath $c
        $new = Join-Path $NewArtifactPath $c
        if (-not (Test-Path -LiteralPath $new)) { continue }
        if (Test-Path -LiteralPath $cur) { Move-Item -LiteralPath $cur -Destination (Join-Path $rollbackRoot $c) -Force }
        Copy-Item -LiteralPath $new -Destination $cur -Recurse -Force
    }

    Write-Step 'Re-provisioning'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $InstallPath 'deploy\Provision-LocalDb.ps1') -InstallPath $InstallPath
    if ($LASTEXITCODE -ne 0) { throw "Provisioner exited with code $LASTEXITCODE." }

    if (-not (Test-Ready)) { throw 'Health check failed after update.' }

    Write-Host "Update complete. Backup retained at $backupFile" -ForegroundColor Green
    exit 0
}
catch {
    Write-Warning "Update failed: $($_.Exception.Message). Rolling back."
    Stop-Everything
    foreach ($c in $components) {
        $cur = Join-Path $InstallPath $c
        $saved = Join-Path $rollbackRoot $c
        if (Test-Path -LiteralPath $saved) {
            if (Test-Path -LiteralPath $cur) { Remove-Item -LiteralPath $cur -Recurse -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $saved -Destination $cur -Force
        }
    }
    # Restore the DB snapshot taken before the swap.
    try {
        $c = Get-PostgresConnection
        Start-Services
        Start-Sleep -Seconds 3
        [Environment]::SetEnvironmentVariable('PGPASSWORD', $c.Password)
        & $pgRestore '-h' $c.DbHost '-p' $c.Port '-U' $c.Username '-d' $c.Database '--clean' '--if-exists' $backupFile
    } catch {
        Write-Warning "DB restore during rollback failed: $($_.Exception.Message). Manual restore from $backupFile may be required."
    } finally {
        [Environment]::SetEnvironmentVariable('PGPASSWORD', $null)
    }
    Start-Services
    Write-Error 'Update rolled back to the previous version.'
    exit 1
}
