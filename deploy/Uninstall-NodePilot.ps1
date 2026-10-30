#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the NodePilot Windows Service and cleans up firewall rules and binaries.
.DESCRIPTION
    Stops and deletes the service, removes firewall rules, removes the machine-wide
    installation marker, and wipes the install directory together with the binary backups that
    updates and re-installs leave beside it. The data directory (logs, DPAPI-bound
    credentials on disk, DB artefacts, etc.) is preserved unless -PurgeData is specified. The
    SQL Server database is never touched.

    Two things are not undone, because both can be shared with something else on the host and
    revoking them blind would break it: the 'Log on as a service' right granted to a gMSA, and
    the read ACE on the TLS certificate's private key. The script names both, with the command
    to remove them, at the end of its run.
.PARAMETER ServiceName
    Windows Service name. Default: NodePilot.
.PARAMETER InstallPath
    Install directory to delete, together with its <InstallPath>.backup.* and
    <InstallPath>.rollback.* siblings. Default: C:\Program Files\NodePilot.
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

# Read the runtime identity and the certificate thumbprint before anything is deleted, so the
# closing report can name what it is leaving behind.
$serviceStartName = $null
try {
    $escapedServiceName = $ServiceName.Replace("'", "''")
    $cimService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedServiceName'" -ErrorAction SilentlyContinue
    if ($cimService) { $serviceStartName = $cimService.StartName }
} catch { $serviceStartName = $null }

# The installed configuration is read once here, before anything is removed - it lives inside the
# install directory this script is about to delete. The closing report works from this snapshot.
#
# It is read for reporting only. The database it points at is not removed: this installer did not
# create it, and it may be shared, replicated or backed up elsewhere.
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

      The GUI setup registers its uninstaller as <InstallPath>\unins000.exe and calls this script
      from there, so that uninstaller runs out of the directory the guard below watches and is an
      ancestor of this script. Excluding the own process tree keeps the guard from waiting out its
      timeout and then refusing to uninstall anything.
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
      Processes whose image lives under $Path, excluding this script's own process tree.
      Update-NodePilot.ps1 asks the same question before it wipes an install directory.
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

function Remove-NodePilotBinaryBackups {
    <#
      Deletes the binary backups Update-NodePilot.ps1 and Install-NodePilot.ps1 leave beside the
      install directory. Only directories named the way those scripts name them are touched, and
      never a junction or symlink. Returns the paths that could not be deleted.
    #>
    param([Parameter(Mandatory)][string]$InstallPath)

    $installRoot = $InstallPath.TrimEnd('\')
    $parent = Split-Path -Parent $installRoot
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { return @() }
    $leafPattern = [regex]::Escape((Split-Path -Leaf $installRoot))
    $namePattern = "^$leafPattern\.(backup|rollback)\.\d{8}-\d{9}-[0-9a-f]{8,32}$"
    $failed = @()
    foreach ($backup in @(Get-ChildItem -LiteralPath $parent -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($backup.Name -notmatch $namePattern) { continue }
        if ($backup.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        try {
            Remove-Item -LiteralPath $backup.FullName -Recurse -Force -ErrorAction Stop
            Write-Info "  Deleted: $($backup.FullName)"
        }
        catch {
            Write-Warn "  Could not delete $($backup.FullName): $($_.Exception.Message)"
            $failed += $backup.FullName
        }
    }
    return $failed
}

function Grant-NodePilotPurgeAccess {
    <#
      Takes ownership of $Path and everything below it for Administrators (takeown /A), then grants
      that group full control, item by item. takeown /R prompts with a localized answer on folders
      it cannot list, so the recursion happens here: a folder unlocked in one pass exposes its
      children to the next. A failed takeown skips the grant for that item. Returns the failures.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [scriptblock]$TakeOwnership = {
            param($ItemPath)
            & takeown.exe /F $ItemPath /A 2>&1 | Out-Null
            $LASTEXITCODE
        },
        # No (OI)(CI): those are container flags, and on a leaf file icacls drops them and adds no
        # ACE at all while still reporting success.
        [scriptblock]$GrantAdministrators = {
            param($ItemPath)
            & icacls.exe $ItemPath /grant '*S-1-5-32-544:(F)' /C /Q 2>&1 | Out-Null
            $LASTEXITCODE
        },
        [int]$MaxPasses = 16
    )
    # Native tools report on stderr; under 'Stop' Windows PowerShell turns that into an exception.
    $ErrorActionPreference = 'Continue'
    $done = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $failures = @{}
    for ($pass = 1; $pass -le $MaxPasses; $pass++) {
        $items = @(Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue) +
            @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)
        $pending = @($items | Where-Object { -not $done.Contains($_.FullName) })
        if ($pending.Count -eq 0) { break }
        $progress = $false
        # Parents first, so their children become listable for the next pass.
        foreach ($item in @($pending | Sort-Object { $_.FullName.Length })) {
            $ownerExit = & $TakeOwnership $item.FullName
            if ($ownerExit -ne 0) {
                $failures[$item.FullName] = "takeown exited with $ownerExit for $($item.FullName)"
                continue
            }
            $grantExit = & $GrantAdministrators $item.FullName
            if ($grantExit -ne 0) {
                $failures[$item.FullName] = "icacls exited with $grantExit for $($item.FullName)"
                continue
            }
            $failures.Remove($item.FullName)
            [void]$done.Add($item.FullName)
            $progress = $true
        }
        if (-not $progress) { break }
    }
    return @($failures.Values | Sort-Object)
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
    # process keeps running while ASP.NET Core drains.
    #
    # This wait has to happen before sc.exe delete. Deleting the service first orphans a
    # still-running process: it can no longer be stopped through the SCM, and the file deletion
    # below then pulls DLLs out from under a live process.
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
    $deleteExit = $LASTEXITCODE
    Write-Info "  sc.exe delete returned exit $deleteExit"
    # 1072: already marked for deletion. Anything else leaves the service registered, so stop
    # before removing the files it runs from.
    if ($deleteExit -ne 0 -and $deleteExit -ne 1072) {
        throw ("sc.exe delete '$ServiceName' failed with exit $deleteExit. Nothing else was removed; " +
               'fix the cause and run the uninstall again.')
    }

    # sc.exe delete normally removes the whole service key, including the Environment MULTI_SZ
    # that holds ConnectionStrings__Postgres - the database password. While anything still holds
    # an SCM handle the service goes DELETE_PENDING and the key survives until reboot, leaving
    # that secret readable on disk. Clear the value explicitly instead of assuming the handle
    # was closed.
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (Test-Path -LiteralPath $serviceKey) {
        Write-Info "  Service key still present (deletion pending); clearing its Environment value."
        Remove-ItemProperty -LiteralPath $serviceKey -Name 'Environment' -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Info "  Service not present."
}

Write-Step "Removing the CLI from the machine PATH"
# Install-NodePilot.ps1 appends <install>\tools\np so operators can just type `np`. Leaving it
# behind would point PATH at a directory this uninstall is about to delete, and repeated
# install/uninstall cycles would accumulate dead entries against the PATH length limit.
try {
    . (Join-Path $PSScriptRoot 'MachinePath.ps1')
    $toolsPath = Join-Path $InstallPath 'tools\np'
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
    # When this runs from Inno Setup's [UninstallRun], unins000.exe executes out of this directory
    # and cannot delete itself. A blanket Remove-Item -Recurse would throw partway and leave an
    # arbitrary remainder behind. Skipping Inno's files lets it finish the job itself; run
    # standalone there are no such files and everything goes.
    $uninstallerPattern = 'unins*'
    # deploy\ holds this script. It goes last, once everything else is gone, so a failed run can
    # be repeated from the same place.
    $recoveryHelperName = 'deploy'
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $failed = $false
        foreach ($entry in Get-ChildItem -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue) {
            if ($entry.Name -like $uninstallerPattern -or $entry.Name -eq $recoveryHelperName) { continue }
            try { Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction Stop }
            catch {
                $failed = $true
                if ($attempt -eq 3) { Write-Warn "  Could not delete $($entry.Name): $($_.Exception.Message)" }
            }
        }
        if (-not $failed) { break }
        # Retry briefly: an antivirus scanner or the search indexer can hold a freshly closed file
        # for a moment. The guard against a running service is above, before the service was
        # deleted, because by this point there is no supported way to stop anything.
        if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
    }

    # Anything still here that is not the setup's own uninstaller or deploy\ is a genuine leftover.
    $recoveryHelperPrefix = (Join-Path $InstallPath $recoveryHelperName).TrimEnd('\') + '\'
    $leftovers = @(Get-ChildItem -LiteralPath $InstallPath -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -notlike $uninstallerPattern -and
            -not $_.FullName.StartsWith($recoveryHelperPrefix, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($leftovers.Count -gt 0) {
        # Do not abort here. The service is already gone; stopping now would skip the data notice
        # and the residue report. Report the leftovers, carry on, and exit non-zero at the end,
        # with the marker and deploy\ kept for the retry.
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

Write-Step "Removing binary backups"
# Copies of earlier service binaries; nothing uses them once the service is gone.
$backupsRemaining = @(Remove-NodePilotBinaryBackups -InstallPath $InstallPath).Count -gt 0

$dataPathRemaining = $false
if ($PurgeData) {
    Write-Step "Purging data directory"
    if (Test-Path $DataPath) {
        # The installer writes jwt-secret.key and admin-setup.token owner-only to the service
        # account, so an administrator can neither read nor delete them and a plain Remove-Item
        # gets partway through the directory and then throws.
        #
        # Ownership is taken first, for the Administrators group via takeown /A and the
        # well-known SID: neither the group name nor a takeown answer is the same on every
        # Windows language.
        $ownershipFailures = @(Grant-NodePilotPurgeAccess -Path $DataPath)
        if ($ownershipFailures.Count -gt 0) {
            # Nothing is deleted from a tree that is only partly accessible, so a retry starts from
            # the same state.
            $dataPathRemaining = $true
            foreach ($failure in $ownershipFailures) { Write-Warn "  $failure" }
            Write-Warn "  $DataPath was not deleted. Resolve the failures above and run the uninstall again."
        }
        else {
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
Write-Info "  $databaseLabel is untouched. Uninstall never removes databases, including those created by setup."
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
if ($installPathRemaining -or $backupsRemaining -or $dataPathRemaining) {
    # The marker and deploy\ stay, so the setup uninstaller and this script can be run again.
    Write-Warn 'Uninstall finished with leftovers (see above). The installation marker and the deploy scripts were kept; run the uninstall again once the cause is fixed.'
    exit 1
}

Write-Step "Removing the deploy scripts and the installation marker"
$recoveryHelperPath = Join-Path $InstallPath 'deploy'
if (Test-Path -LiteralPath $recoveryHelperPath) {
    try { Remove-Item -LiteralPath $recoveryHelperPath -Recurse -Force -ErrorAction Stop }
    catch {
        Write-Warn "  Could not delete $recoveryHelperPath : $($_.Exception.Message)"
        Write-Warn 'Uninstall finished with leftovers. The installation marker was kept; run the uninstall again.'
        exit 1
    }
    Write-Info "  Deleted: $recoveryHelperPath"
}
# Only when nothing is left. Inno removes its own uninstaller and the empty directory after this
# script returns.
if (Test-Path -LiteralPath $InstallPath) {
    $remaining = @(Get-ChildItem -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue)
    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $InstallPath -Force -ErrorAction SilentlyContinue
        Write-Info "  Deleted: $InstallPath"
    }
    else {
        Write-Info '  Leaving the setup uninstaller to remove itself and the directory.'
    }
}
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
Write-Ok "Uninstall complete."
