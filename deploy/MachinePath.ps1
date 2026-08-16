#requires -Version 5.1
<#
.SYNOPSIS
    Pure PATH-string helpers shared by install, update and uninstall.
.DESCRIPTION
    Install-NodePilot.ps1 puts <install>\tools\np on the machine PATH so operators can type
    `np` instead of knowing where setup landed. Update-NodePilot.ps1 has to add it too, because
    an installation predating the shipped clients never had a directory to register, and
    Uninstall-NodePilot.ps1 has to take it away again before deleting the directory it points at.

    Three copies of the same string surgery is how one of them ends up subtly different, so the
    decision lives here as two pure functions: they take the current PATH and return the new one,
    touching no environment. That also makes the awkward parts testable without a machine-wide
    side effect - idempotence (a re-install must not grow PATH, which has a real length limit),
    trailing-backslash and case differences (C:\NP\tools\np\ and c:\np\TOOLS\np are the same
    directory to Windows), and empty segments from a PATH that ends in ';'.
#>

Set-StrictMode -Version 3.0

function Split-NodePilotPathEntries {
    [OutputType([string[]])]
    param([string]$PathValue)

    if ([string]::IsNullOrEmpty($PathValue)) { return @() }
    # Empty segments are dropped rather than preserved: they are no-ops for the loader, and
    # keeping them would make the "did anything change" comparison below meaningless.
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

    # A typed list, not "$array + $item": PowerShell unwraps a single-element array on return,
    # so a PATH with exactly one entry comes back as a bare string and '+' then concatenates
    # STRINGS instead of appending to a collection - producing "C:\WindowsC:\...\tools\np", one
    # unusable entry with no separator. Caught by Test-MachinePath.ps1's empty-segment case.
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
