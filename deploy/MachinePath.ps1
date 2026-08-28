#requires -Version 5.1
<#
.SYNOPSIS
    Pure PATH-string helpers shared by install, update and uninstall.
.DESCRIPTION
    Install, update and uninstall all add or remove <install>\tools\np on the machine PATH so
    operators can call `np` from anywhere. The string surgery lives here as pure functions that
    take the current PATH and return the new one without touching the environment, which keeps
    the awkward cases testable: idempotent re-adds, trailing backslashes, case-insensitive
    directory names, and empty segments from a PATH that ends in ';'.
#>

Set-StrictMode -Version 3.0

function Split-NodePilotPathEntries {
    [OutputType([string[]])]
    param([string]$PathValue)

    if ([string]::IsNullOrEmpty($PathValue)) { return @() }
    # Empty segments are dropped: the loader ignores them, and keeping them would blur the
    # comparison that decides whether PATH changed.
    return @(($PathValue -split ';') | Where-Object { $_ -and $_.Trim() })
}

function Test-NodePilotPathContains {
    [OutputType([bool])]
    param(
        [string]$PathValue,
        [Parameter(Mandatory)][string]$Directory)

    $needle = $Directory.TrimEnd('\')
    foreach ($entry in (Split-NodePilotPathEntries -PathValue $PathValue)) {
        if ($entry.Trim().TrimEnd('\').Equals($needle, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    Returns PATH with $Directory appended, or unchanged when it is already there.
#>
function Add-NodePilotPathEntry {
    [OutputType([string])]
    param(
        [string]$PathValue,
        [Parameter(Mandatory)][string]$Directory)

    # A typed list, not "$array + $item": PowerShell unwraps a single-element array, so '+' would
    # concatenate strings and yield one unusable entry without a separator.
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in (Split-NodePilotPathEntries -PathValue $PathValue)) { $entries.Add($entry) }
    if (-not (Test-NodePilotPathContains -PathValue $PathValue -Directory $Directory)) {
        $entries.Add($Directory.TrimEnd('\'))
    }
    return ($entries -join ';')
}

<#
.SYNOPSIS
    Returns PATH with every entry naming $Directory removed.
#>
function Remove-NodePilotPathEntry {
    [OutputType([string])]
    param(
        [string]$PathValue,
        [Parameter(Mandatory)][string]$Directory)

    $needle = $Directory.TrimEnd('\')
    $remaining = @(Split-NodePilotPathEntries -PathValue $PathValue | Where-Object {
        -not $_.Trim().TrimEnd('\').Equals($needle, [StringComparison]::OrdinalIgnoreCase)
    })
    return ($remaining -join ';')
}
