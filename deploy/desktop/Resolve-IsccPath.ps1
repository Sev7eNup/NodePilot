#requires -Version 5.1
<#
.SYNOPSIS
    Locates the Inno Setup 6 compiler (ISCC.exe).
.DESCRIPTION
    Dot-sourced by Build-DesktopInstaller.ps1 and by Build-Artifact.ps1's pre-flight, so both
    agree on where the compiler may live. Inno Setup installs machine-wide under Program Files
    (x86) when run elevated, but per-user under %LOCALAPPDATA%\Programs otherwise - and the
    per-user location is what you get from a normal double-click install. Probing only the
    machine-wide path made a perfectly good installation look missing.
#>

function Get-NodePilotIsccCandidates {
    @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
}

<#
.SYNOPSIS
    Returns the path to ISCC.exe, or $null when it cannot be found.
.PARAMETER Explicit
    A caller-supplied path. When given it is used verbatim if it exists, and no probing happens -
    an explicit override that is wrong should surface as an error, not be silently replaced.
#>
function Resolve-NodePilotIsccPath {
    param([string]$Explicit)

    if ($Explicit) {
        return $(if (Test-Path -LiteralPath $Explicit) { $Explicit } else { $null })
    }

    foreach ($candidate in Get-NodePilotIsccCandidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}
