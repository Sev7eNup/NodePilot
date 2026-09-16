#requires -Version 5.1
<#
.SYNOPSIS
    Stop and removal helpers shared by the desktop provisioner, the setup-time preparation, the
    updater and the uninstaller. Dot-source this file.

.DESCRIPTION
    A service that reports Stopped can still have a live process that holds its binaries and the
    PostgreSQL data directory. "sc.exe delete" on such a service only marks it for deletion, and
    registering the same name again then fails. The helpers here wait for the process, not for
    the reported status.
#>

Set-StrictMode -Version 3.0

function Get-DesktopProcessesUnderPath {
    # Win32_Process also reports the image path of service processes (SYSTEM, NetworkService),
    # which Get-Process cannot always read. The Inno uninstaller itself is excluded.
    param([Parameter(Mandatory)] [string] $Path)
    $prefix = $Path.TrimEnd('\') + '\'
    return @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -notmatch '^unins\d+\.exe$'
    })
}

function Test-DesktopServiceExists {
    # sc.exe runs in its own process, so no service handle stays open in this session. An open
    # handle is exactly what keeps a deleted service registered.
    param([Parameter(Mandatory)] [string] $Name)
    $ErrorActionPreference = 'Continue'
    & sc.exe query $Name 2>&1 | Out-Null
    return $LASTEXITCODE -ne 1060
}

function Wait-DesktopProcessExit {
    param([Parameter(Mandatory)] [int[]] $ProcessId, [int] $TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $alive = @($ProcessId | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($alive.Count -eq 0) { return $true }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 500
    }
}

function Stop-DesktopService {
    # Stops the service and waits for its process to exit. Ends the process after the timeout.
    param([Parameter(Mandatory)] [string] $Name, [int] $TimeoutSeconds = 90)
    $ErrorActionPreference = 'Continue'
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    $processId = [int] $svc.ProcessId
    if ($svc.State -ne 'Stopped') {
        Write-Host "    Stopping service '$Name'"
        & sc.exe stop $Name | Out-Null
    }
    if ($processId -le 0) { return }
    if (-not (Wait-DesktopProcessExit -ProcessId $processId -TimeoutSeconds $TimeoutSeconds)) {
        Write-Warning "Service '$Name' did not stop within $TimeoutSeconds s. Ending process $processId."
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        if (-not (Wait-DesktopProcessExit -ProcessId $processId -TimeoutSeconds 15)) {
            throw "Process $processId of service '$Name' could not be ended."
        }
    }
}

function Remove-DesktopService {
    # Stops and deletes the service, then waits until the SCM has really removed it.
    param([Parameter(Mandatory)] [string] $Name, [int] $TimeoutSeconds = 90)
    $ErrorActionPreference = 'Continue'
    if (-not (Test-DesktopServiceExists -Name $Name)) { return }
    Stop-DesktopService -Name $Name -TimeoutSeconds $TimeoutSeconds
    Write-Host "    Removing service '$Name'"
    & sc.exe delete $Name | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while (Test-DesktopServiceExists -Name $Name) {
        if ((Get-Date) -ge $deadline) {
            throw ("Service '$Name' is marked for deletion but still registered. Close the Services " +
                   "console and any other tool that has it open, or restart Windows, then try again.")
        }
        Start-Sleep -Milliseconds 500
    }
}

function Stop-DesktopRuntime {
    # Ends everything that holds a file under InstallPath or the database cluster: the Electron
    # shell and the CLI/MCP clients, both services, and any PostgreSQL process that outlived its
    # service wrapper. Throws when a process under InstallPath cannot be ended.
    param(
        [Parameter(Mandatory)] [string] $InstallPath,
        [Parameter(Mandatory)] [string] $DataPath,
        [string] $ApiServiceName = 'NodePilot',
        [string] $DbServiceName  = 'NodePilotDb',
        # Delete the services as well. Setup keeps them, the provisioner re-creates them.
        [switch] $RemoveServices
    )
    # Native tools report on stderr; under 'Stop' Windows PowerShell turns that into an exception.
    $ErrorActionPreference = 'Continue'

    foreach ($client in @((Join-Path $InstallPath 'desktop'), (Join-Path $InstallPath 'tools'))) {
        foreach ($p in @(Get-DesktopProcessesUnderPath -Path $client)) {
            Write-Host "    Ending $($p.Name) (PID $($p.ProcessId))"
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    # API first: it depends on the database.
    foreach ($name in @($ApiServiceName, $DbServiceName)) {
        if ($RemoveServices) { Remove-DesktopService -Name $name } else { Stop-DesktopService -Name $name }
    }

    # Ending the pg_ctl service wrapper leaves the postmaster running. Shut it down cleanly first,
    # so the cluster does not need crash recovery on its next start.
    $pgCtl  = Join-Path $InstallPath 'pgsql\bin\pg_ctl.exe'
    $pgData = Join-Path $DataPath 'pgdata'
    if ((Test-Path -LiteralPath $pgCtl) -and
        (Test-Path -LiteralPath (Join-Path $pgData 'postmaster.pid')) -and
        @(Get-DesktopProcessesUnderPath -Path (Join-Path $InstallPath 'pgsql')).Count -gt 0) {
        Write-Host '    Stopping the PostgreSQL server'
        & $pgCtl stop -D $pgData -m fast -w -t 60 2>&1 | Out-Null
    }

    $remaining = @(Get-DesktopProcessesUnderPath -Path $InstallPath)
    if ($remaining.Count -gt 0) {
        [void](Wait-DesktopProcessExit -ProcessId ([int[]] @($remaining | ForEach-Object { $_.ProcessId })) -TimeoutSeconds 30)
        foreach ($p in @(Get-DesktopProcessesUnderPath -Path $InstallPath)) {
            Write-Warning "Still running: $($p.Name) (PID $($p.ProcessId)). Ending it."
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
        $remaining = @(Get-DesktopProcessesUnderPath -Path $InstallPath)
        if ($remaining.Count -gt 0) {
            [void](Wait-DesktopProcessExit -ProcessId ([int[]] @($remaining | ForEach-Object { $_.ProcessId })) -TimeoutSeconds 15)
            $remaining = @(Get-DesktopProcessesUnderPath -Path $InstallPath)
        }
    }
    if ($remaining.Count -gt 0) {
        throw ("Processes are still running from ${InstallPath}: " +
               (($remaining | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }) -join ', '))
    }
}

function Get-DesktopUserProfilePaths {
    # Profile directories of every local account, from the registry rather than C:\Users, so
    # redirected profiles are found as well.
    $root = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    return @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
        $raw = (Get-ItemProperty -LiteralPath $_.PSPath -Name 'ProfileImagePath' -ErrorAction SilentlyContinue)
        if ($raw) {
            $path = [Environment]::ExpandEnvironmentVariables($raw.ProfileImagePath)
            if ($path -and (Test-Path -LiteralPath $path -PathType Container)) { $path }
        }
    } | Sort-Object -Unique)
}

function Remove-DesktopSetupHandoffs {
    # The first-run token copy lives in the profile of whichever user launched the shell. Stale
    # copies make the shell show the setup page for a token the server no longer accepts.
    foreach ($profilePath in @(Get-DesktopUserProfilePaths)) {
        $handoff = Join-Path $profilePath 'AppData\Local\NodePilot\admin-setup.handoff'
        if (Test-Path -LiteralPath $handoff) {
            Remove-Item -LiteralPath $handoff -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $handoff) { Write-Warning "Could not remove $handoff" }
            else { Write-Host "    Removed $handoff" }
        }
    }
}

function Remove-DesktopDirectory {
    # Deletes a directory tree and returns $true when it is gone. The data directory carries
    # protected ACLs written by the API and PostgreSQL, so a plain delete that fails is retried
    # after taking ownership for Administrators.
    param([Parameter(Mandatory)] [string] $Path)
    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to delete through a reparse point: $Path"
    }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $Path)) { return $true }
        & cmd.exe /d /c rd /s /q "$Path" 2>&1 | Out-Null
        if (-not (Test-Path -LiteralPath $Path)) { return $true }

        # Item by item and without takeown /r, which prompts for a localized answer on folders it
        # cannot list. A folder unlocked in this pass exposes its children to the next one.
        Write-Host "    Taking ownership of what is left under $Path (pass $attempt)"
        $left = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue) + @(Get-Item -LiteralPath $Path -Force)
        foreach ($entry in @($left | Sort-Object { $_.FullName.Length } -Descending)) {
            & takeown.exe /f $entry.FullName /a 2>&1 | Out-Null
            $grant = if ($entry.PSIsContainer) { '*S-1-5-32-544:(OI)(CI)F' } else { '*S-1-5-32-544:F' }
            & icacls.exe $entry.FullName /grant $grant /c /q 2>&1 | Out-Null
        }
    }
    return -not (Test-Path -LiteralPath $Path)
}
