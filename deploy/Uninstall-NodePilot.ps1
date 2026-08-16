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
.PARAMETER ProcessExitTimeoutSeconds
    How long to wait for processes running out of InstallPath to exit after the service is
    stopped, before the service is deleted. Default: 90.
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
    [int]$ProcessExitTimeoutSeconds = 90,
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

# The installed configuration is read ONCE, here, before anything is removed - it lives inside the
# install directory this script is about to delete. The closing report works from this snapshot
# rather than re-reading a file that no longer exists.
#
# It is read to REPORT, never to act on. The database this configuration points at is not removed
# and cannot be: this installer did not create it. It was provisioned separately, it may be
# replicated, backed up or shared with something else, and an installer that deletes what it never
# installed is an installer nobody can trust with a production system.
$installedThumbprint = $null
$installedSettings = $null
$settingsReadError = $null
try {
    $settingsPath = Join-Path $InstallPath 'appsettings.Production.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $installedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $installedThumbprint = $installedSettings.Kestrel.Https.CertificateThumbprint
    }
    else {
        $settingsReadError = "No appsettings.Production.json under $InstallPath."
    }
} catch {
    $installedSettings = $null
    $installedThumbprint = $null
    $settingsReadError = $_.Exception.Message
}

function Get-SelfAndAncestorProcessIds {
    <#
      This script's own process and everything that launched it.

      Needed because the GUI setup registers its uninstaller as <InstallPath>\unins000.exe and
      calls this script from there. That uninstaller is therefore a process running out of the very
      directory the guard below is watching - and it is this script's grandparent. Without this
      exclusion the guard waits out its full timeout and then refuses to uninstall anything,
      blaming a process that is only there because it is doing the uninstalling. Observed on the
      lab host as an uninstall that removed nothing and took exactly the timeout to do it.
    #>
    $ids = @()
    $current = $PID
    for ($hop = 0; $hop -lt 12 -and $current -gt 0; $hop++) {
        if ($ids -contains $current) { break }
        $ids += $current
        $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$current" -ErrorAction SilentlyContinue
        if (-not $process) { break }
        $current = [int]$process.ParentProcessId
    }
    return $ids
}

function Get-ProcessesUnderPath {
    <#
      Processes whose image lives under $Path, excluding this script's own process tree. Same
      question Update-NodePilot.ps1 asks before it wipes an install directory; the uninstaller has
      to ask it too, and earlier.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $prefix = $Path.TrimEnd('\') + '\'
    $ownTree = Get-SelfAndAncestorProcessIds
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        if ($ownTree -contains $_.Id) { return $false }
        $imagePath = $null
        try { $imagePath = $_.Path } catch { $imagePath = $null }
        $imagePath -and $imagePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    }
}

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

    # The SCM reports 'Stopped' as soon as the service acknowledges the control code, but the
    # process keeps running while ASP.NET Core drains. Measured on a real installation: 31
    # seconds after Stop-Service returned.
    #
    # This wait has to happen BEFORE sc.exe delete, not after. Deleting the service first
    # ORPHANS a still-running process: nothing can stop it through the SCM any more, and the
    # file deletion below then rips DLLs out from under a live process. That is exactly what
    # happened on the lab host - a half-deleted install directory and a process nobody could
    # address.
    $processDeadline = (Get-Date).AddSeconds($ProcessExitTimeoutSeconds)
    $blocking = @(Get-ProcessesUnderPath -Path $InstallPath)
    if ($blocking.Count -gt 0) {
        Write-Info "  Waiting up to $ProcessExitTimeoutSeconds s for the service process to exit."
        while ((Get-Date) -lt $processDeadline) {
            $blocking = @(Get-ProcessesUnderPath -Path $InstallPath)
            if ($blocking.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        }
    }
    if ($blocking.Count -gt 0) {
        foreach ($process in $blocking) {
            Write-Warn "  Still running: PID $($process.Id) $($process.Name) ($($process.Path))"
        }
        # Fail closed, with the service still registered so the operator keeps a supported way
        # to stop it.
        throw ("Processes are still running out of '$InstallPath' after $ProcessExitTimeoutSeconds s. " +
               "The service has NOT been deleted, so you can still stop it. End the processes above " +
               '(or reboot), then re-run this script.')
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

Write-Step "Removing the CLI from the machine PATH"
# Install-NodePilot.ps1 appends <install>\tools\np so operators can just type `np`. Leaving it
# behind would point PATH at a directory this uninstall is about to delete, and every new shell
# would carry a dead entry - PATH has a real length limit, so repeated install/uninstall cycles
# accumulate.
try {
    . (Join-Path $PSScriptRoot 'MachinePath.ps1')
    $toolsPath = Join-Path $InstallPath 'tools
p'
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (Test-NodePilotPathContains -PathValue $machinePath -Directory $toolsPath) {
        [Environment]::SetEnvironmentVariable('Path',
            (Remove-NodePilotPathEntry -PathValue $machinePath -Directory $toolsPath), 'Machine')
        Write-Info "  Removed: $toolsPath"
    } else {
        Write-Info "  No NodePilot CLI entry on the machine PATH."
    }
} catch {
    Write-Warn "  Could not update the machine PATH: $($_.Exception.Message)"
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
$installPathRemaining = $false
if (Test-Path $InstallPath) {
    # Contents first, then the directory - and never the GUI setup's own uninstaller.
    #
    # When this runs from Inno Setup's [UninstallRun], unins000.exe is executing out of this very
    # directory and cannot delete itself. A blanket Remove-Item -Recurse hits it, throws partway,
    # and leaves an arbitrary remainder behind: on the lab host, VERSION.txt and web.config plus
    # the directory. Skipping Inno's files lets it finish the job itself; run standalone there are
    # no such files and everything goes.
    $uninstallerPattern = 'unins*'
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $failed = $false
        foreach ($entry in Get-ChildItem -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue) {
            if ($entry.Name -like $uninstallerPattern) { continue }
            try { Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction Stop }
            catch {
                $failed = $true
                if ($attempt -eq 3) { Write-Warn "  Could not delete $($entry.Name): $($_.Exception.Message)" }
            }
        }
        if (-not $failed) { break }
        # Retry briefly: an antivirus scanner or the search indexer can hold a freshly closed file
        # for a moment. This is NOT the guard against a running service - that one is above, before
        # the service was deleted, because by this point there is no supported way to stop anything.
        if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
    }

    # Only when nothing is left. Inno removes its own uninstaller and the empty directory after
    # this script returns.
    $remaining = @(Get-ChildItem -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue)
    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue
    }
    elseif (@($remaining | Where-Object { $_.Name -notlike $uninstallerPattern }).Count -eq 0) {
        Write-Info '  Leaving the setup uninstaller to remove itself and the directory.'
    }

    # Anything still here that is NOT the setup's own uninstaller is a genuine leftover.
    $leftovers = @(Get-ChildItem -LiteralPath $InstallPath -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike $uninstallerPattern })
    if ($leftovers.Count -gt 0) {
        # Do NOT abort here. The service is already gone and the marker with it; stopping now
        # would skip the data notice and the residue report and leave the operator with less
        # information, not more. Report precisely, then carry on and exit non-zero at the end.
        $installPathRemaining = $true
        Write-Warn "  $($leftovers.Count) file(s) could not be deleted under $InstallPath."
        $holders = @(Get-ProcessesUnderPath -Path $InstallPath)
        foreach ($process in $holders) {
            Write-Warn "    held by PID $($process.Id) $($process.Name)"
        }
        if ($holders.Count -eq 0) {
            Write-Info '    No process is running out of that path; a file handle from antivirus or'
            Write-Info '    the search indexer is the usual cause. Delete the folder manually or reboot.'
        }
    }
    elseif (-not (Test-Path $InstallPath)) {
        Write-Info "  Deleted: $InstallPath"
    }
} else {
    Write-Info "  Install path not present."
}

$dataPathRemaining = $false
if ($PurgeData) {
    Write-Step "Purging data directory"
    if (Test-Path $DataPath) {
        # The installer deliberately writes some of these owner-only to the SERVICE account -
        # jwt-secret.key and admin-setup.token are not meant to be readable by an administrator
        # while the service runs. That protection also stops an administrator DELETING them, so a
        # plain Remove-Item gets partway through the directory and then throws: measured on the
        # lab host as 12 of 17 entries gone, an aborted script and a half-purged data directory.
        #
        # Ownership is taken first, using the well-known Administrators SID rather than the group
        # name - "BUILTIN\Administrators" does not resolve on a non-English Windows, which is the
        # same reason the ACL helpers in ArtifactSecurity.ps1 use SIDs.
        #
        # The grant carries NO inheritance flags, and that is not a detail. (OI)(CI) are container
        # flags: applied to a leaf file icacls silently drops them, reports "Successfully processed
        # 1 files", and adds no ACE at all. Measured on the lab host - with (OI)(CI) the file kept
        # exactly one ACE for the service account and stayed undeletable; without them the
        # Administrators ACE appeared and the delete succeeded. /T visits every item, so a flat
        # grant is both correct and sufficient for a tree that is about to be deleted anyway.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & takeown.exe /F $DataPath /R /D Y 2>&1 | Out-Null
            & icacls.exe $DataPath /grant '*S-1-5-32-544:(F)' /T /C 2>&1 | Out-Null
        }
        finally { $ErrorActionPreference = $previousPreference }

        try {
            Remove-Item -LiteralPath $DataPath -Recurse -Force -ErrorAction Stop
            Write-Info "  Deleted: $DataPath"
        }
        catch {
            # Report and continue. Aborting here would skip the closing report, and the operator
            # would be left with a half-purged directory and no idea which parts survived.
            $dataPathRemaining = $true
            Write-Warn "  Could not fully delete $DataPath : $($_.Exception.Message)"
            $survivors = @(Get-ChildItem -LiteralPath $DataPath -Recurse -Force -File -ErrorAction SilentlyContinue)
            Write-Warn "  $($survivors.Count) file(s) remain. Delete the folder by hand, or reboot and retry."
        }
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

# Named, not silently skipped. An operator finishing an uninstall should not have to guess whether
# the database is still there.
$databaseLabel = 'the NodePilot database'
if ($installedSettings) {
    try {
        $provider = if ($installedSettings.PSObject.Properties.Name -contains 'Database') {
            [string]$installedSettings.Database.Provider
        } else { 'sqlserver' }
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = [string]$installedSettings.ConnectionStrings.DefaultConnection
        foreach ($serverKey in 'Server', 'Data Source') {
            foreach ($databaseKey in 'Database', 'Initial Catalog') {
                if ($builder.ContainsKey($serverKey) -and $builder.ContainsKey($databaseKey)) {
                    $databaseLabel = "[$($builder[$databaseKey])] on $($builder[$serverKey]) ($provider)"
                }
            }
        }
    } catch { }
}
elseif ($settingsReadError) {
    Write-Info "  Could not read the installed configuration ($settingsReadError), so the items"
    Write-Info "  below are named generically."
}
Write-Info "  $databaseLabel is untouched. This installer never created it, so it does not remove it."
Write-Info "  Drop it with your usual DBA tooling once you are certain nothing else uses it."
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
if ($installPathRemaining -or $dataPathRemaining) {
    Write-Warn 'Uninstall finished with leftovers (see above).'
    exit 1
}
Write-Ok "Uninstall complete."
