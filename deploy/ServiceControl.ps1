<#
.SYNOPSIS
    Process-lifetime helpers shared by Install-NodePilot.ps1 and Update-NodePilot.ps1.

.DESCRIPTION
    A stopped Windows service does not mean a dead process: the SCM reports SERVICE_STOPPED while
    the hosting process is still unwinding, and its binaries stay mapped as image sections for a
    moment, so deleting a mapped DLL fails with "Access denied". These helpers wait for such
    processes to exit and only end what is still there after the grace period.

    Uninstall-NodePilot.ps1 keeps its own copy because it runs from inside the directory it scans
    and has to exclude its own process tree. Neither caller here runs from InstallPath.
#>

Set-StrictMode -Version 3.0

function Get-NodePilotProcessesUnderPath {
    <#
    .SYNOPSIS
        Processes whose executable lives under $Path. Never throws.
    .DESCRIPTION
        Process.Path throws for processes the caller cannot open, which is normal and no reason
        to abort an installation, so each process is guarded on its own instead of the pipeline.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $prefix = $Path.TrimEnd('\') + '\'
    $result = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = $process.Path } catch { $processPath = $null }
        if (-not $processPath) { continue }
        if ($processPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $result += $process
        }
    }
    return @($result)
}

function Wait-NodePilotProcessesUnderPath {
    <#
    .SYNOPSIS
        Waits for processes under $Path to exit and ends the stragglers if asked. Returns the
        processes still running, so the caller can decide to fail closed.
    .PARAMETER TimeoutSeconds
        How long a graceful exit is given before -Force applies.
    .PARAMETER Force
        Stop-Process the remainder after the timeout. Everything under an install directory is a
        NodePilot binary whose files are about to be replaced, so ending it is the caller's job.
        Without this switch the function only observes.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutSeconds = 30,
        [switch]$Force
    )

    $blocking = @(Get-NodePilotProcessesUnderPath -Path $Path)
    if ($blocking.Count -eq 0) { return @() }

    Write-Info "  Waiting up to $TimeoutSeconds s for $($blocking.Count) process(es) under '$Path' to exit."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $blocking = @(Get-NodePilotProcessesUnderPath -Path $Path)
        if ($blocking.Count -eq 0) { return @() }
        Start-Sleep -Milliseconds 500
    }

    if (-not $Force) { return @($blocking) }

    foreach ($process in $blocking) {
        Write-Warn "  Still running after $TimeoutSeconds s: PID $($process.Id) $($process.ProcessName) - ending it."
        # A failure here is not fatal: the re-check below decides. A process that cannot be ended
        # is reported to the operator instead of aborting with an access error.
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    # A killed process needs a moment to leave the process table and drop its image sections.
    # Returning immediately would report it as un-endable when it is merely dying.
    $graceDeadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $graceDeadline) {
        $blocking = @(Get-NodePilotProcessesUnderPath -Path $Path)
        if ($blocking.Count -eq 0) { return @() }
        Start-Sleep -Milliseconds 250
    }
    return @($blocking)
}
