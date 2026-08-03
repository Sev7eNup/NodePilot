<#
.SYNOPSIS
    Process-lifetime helpers shared by Install-NodePilot.ps1 and Update-NodePilot.ps1.

.DESCRIPTION
    A stopped Windows service does not mean a dead process. The SCM reports SERVICE_STOPPED as
    soon as the service says so, while the hosting process is still unwinding - disposing the
    generic host, flushing Serilog, running finalizers. For a second or two afterwards its
    binaries are still mapped as image sections, and deleting a mapped DLL fails with a plain
    "Access denied" in the middle of a wipe.

    Both scripts used to treat that window as a hard error and told the operator to stop the
    process by hand - the process the script itself had just asked to stop. This waits instead,
    and only ends what is still there after the grace period.

    Not used by Uninstall-NodePilot.ps1, which keeps its own copy on purpose: it is launched by
    unins000.exe from inside the very directory it is scanning, so it has to exclude its own
    process tree or it blocks itself. Neither of the two callers here runs from InstallPath, and
    an -ExcludeOwnProcessTree switch that is never true in production would be a switch nobody
    ever tests.
#>

Set-StrictMode -Version 3.0

function Get-NodePilotProcessesUnderPath {
    <#
    .SYNOPSIS
        Processes whose executable lives under $Path. Never throws.
    .DESCRIPTION
        Process.Path throws for processes the caller cannot open (protected, or another
        account's), which is normal and not a reason to abort an installation - hence the
        per-process try/catch rather than a single -ErrorAction on the pipeline.
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
        Waits for processes under $Path to exit; ends the stragglers if asked. Returns whatever
        is STILL running, so the caller can decide to fail closed.
    .PARAMETER TimeoutSeconds
        How long a graceful exit is given before -Force applies.
    .PARAMETER Force
        Stop-Process the remainder after the timeout. Everything under an install directory is a
        NodePilot binary whose files are about to be replaced anyway, so ending it is the caller's
        job rather than the operator's. Without this switch the function only observes.
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
        # Failing here is not fatal on its own: the re-check below decides. A process we cannot
        # end is reported by name to the operator instead of aborting with an access error.
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    # A killed process still needs a moment to leave the process table and drop its image
    # sections. Returning immediately would report it as un-endable when it is merely dying.
    $graceDeadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $graceDeadline) {
        $blocking = @(Get-NodePilotProcessesUnderPath -Path $Path)
        if ($blocking.Count -eq 0) { return @() }
        Start-Sleep -Milliseconds 250
    }
    return @($blocking)
}
