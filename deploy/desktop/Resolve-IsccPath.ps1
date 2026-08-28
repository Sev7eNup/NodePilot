#requires -Version 5.1
<#
.SYNOPSIS
    Locates the Inno Setup 6 compiler (ISCC.exe).
.DESCRIPTION
    Dot-sourced by Build-DesktopInstaller.ps1 and by Build-Artifact.ps1's pre-flight, so both
    agree on where the compiler may live. Inno Setup installs machine-wide under Program Files
    (x86) when run elevated, and per-user under %LOCALAPPDATA%\Programs otherwise, which is what
    a normal double-click install produces.
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
    A caller-supplied path. When given it is used verbatim if it exists and no probing happens,
    so a wrong override surfaces as an error instead of being silently replaced.
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
