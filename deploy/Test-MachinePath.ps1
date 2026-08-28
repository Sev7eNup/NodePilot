#requires -Version 5.1
<#
.SYNOPSIS
    Unit tests for the machine-PATH helpers shared by install, update and uninstall.
.DESCRIPTION
    The helpers decide whether <install>\tools\np is added to or removed from the machine PATH.
    They must be idempotent, because PATH has a length limit and install/upgrade cycles repeat,
    and they must compare entries the way Windows does: "C:\NP\tools\np\" and "c:\np\TOOLS\np"
    name the same directory, so an uninstall that compares strings naively leaves a dead entry
    behind.

    The helpers are pure string transforms, so these checks need no dependencies and touch
    nothing in the real environment.
#>

[CmdletBinding()]
param([string]$MachinePathScriptPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($MachinePathScriptPath)) {
    $MachinePathScriptPath = Join-Path $scriptDirectory 'MachinePath.ps1'
}
if (-not (Test-Path -LiteralPath $MachinePathScriptPath -PathType Leaf)) {
    throw "MachinePath helper not found: $MachinePathScriptPath"
}
. $MachinePathScriptPath

$script:failures = 0
function Assert-Equal {
    param([string]$Name, $Expected, $Actual)
    if ([string]$Expected -ceq [string]$Actual) {
        Write-Host "  PASS  $Name" -ForegroundColor DarkGray
    } else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        expected: '$Expected'" -ForegroundColor Red
        Write-Host "        actual  : '$Actual'" -ForegroundColor Red
        $script:failures++
    }
}
function Assert-True {
    param([string]$Name, [bool]$Condition)
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor DarkGray }
    else { Write-Host "  FAIL  $Name" -ForegroundColor Red; $script:failures++ }
}

$tools = 'C:\Program Files\NodePilot\tools\np'

Write-Host 'MachinePath: adding' -ForegroundColor Cyan
Assert-Equal 'appends to a populated PATH' `
    "C:\Windows;C:\Windows\System32;$tools" `
    (Add-NodePilotPathEntry -PathValue 'C:\Windows;C:\Windows\System32' -Directory $tools)

Assert-Equal 'appends to an empty PATH' `
    $tools `
    (Add-NodePilotPathEntry -PathValue '' -Directory $tools)

Assert-Equal 'a null PATH is treated as empty' `
    $tools `
    (Add-NodePilotPathEntry -PathValue $null -Directory $tools)

# The re-install case: without this the variable grows on every upgrade until PATH is truncated.
Assert-Equal 'adding twice is a no-op' `
    "C:\Windows;$tools" `
    (Add-NodePilotPathEntry -PathValue "C:\Windows;$tools" -Directory $tools)

Assert-Equal 'an existing entry with a trailing backslash counts as present' `
    "C:\Windows;$tools\" `
    (Add-NodePilotPathEntry -PathValue "C:\Windows;$tools\" -Directory $tools)

Assert-Equal 'an existing entry in different case counts as present' `
    "C:\Windows;$($tools.ToUpperInvariant())" `
    (Add-NodePilotPathEntry -PathValue "C:\Windows;$($tools.ToUpperInvariant())" -Directory $tools)

Assert-Equal 'empty segments are dropped rather than preserved' `
    "C:\Windows;$tools" `
    (Add-NodePilotPathEntry -PathValue 'C:\Windows;;' -Directory $tools)

Assert-Equal 'a sibling directory sharing the prefix is not mistaken for the entry' `
    "C:\Program Files\NodePilot\tools\npx;$tools" `
    (Add-NodePilotPathEntry -PathValue 'C:\Program Files\NodePilot\tools\npx' -Directory $tools)

Write-Host 'MachinePath: removing' -ForegroundColor Cyan
Assert-Equal 'removes the entry' `
    'C:\Windows;C:\Windows\System32' `
    (Remove-NodePilotPathEntry -PathValue "C:\Windows;$tools;C:\Windows\System32" -Directory $tools)

Assert-Equal 'removes a trailing-backslash spelling' `
    'C:\Windows' `
    (Remove-NodePilotPathEntry -PathValue "C:\Windows;$tools\" -Directory $tools)

Assert-Equal 'removes a different-case spelling' `
    'C:\Windows' `
    (Remove-NodePilotPathEntry -PathValue "C:\Windows;$($tools.ToLowerInvariant())" -Directory $tools)

# Two installs, then two uninstalls: the second uninstall must not leave the duplicate behind.
Assert-Equal 'removes every occurrence' `
    'C:\Windows' `
    (Remove-NodePilotPathEntry -PathValue "$tools;C:\Windows;$tools" -Directory $tools)

Assert-Equal 'removing what is not there changes nothing' `
    'C:\Windows;C:\Windows\System32' `
    (Remove-NodePilotPathEntry -PathValue 'C:\Windows;C:\Windows\System32' -Directory $tools)

Assert-Equal 'a sibling directory sharing the prefix survives removal' `
    'C:\Program Files\NodePilot\tools\npx' `
    (Remove-NodePilotPathEntry -PathValue "C:\Program Files\NodePilot\tools\npx;$tools" -Directory $tools)

Write-Host 'MachinePath: membership probe' -ForegroundColor Cyan
Assert-True 'detects the entry'                    (Test-NodePilotPathContains -PathValue "C:\Windows;$tools" -Directory $tools)
Assert-True 'detects it case-insensitively'        (Test-NodePilotPathContains -PathValue $tools.ToUpperInvariant() -Directory $tools)
Assert-True 'detects it with a trailing backslash' (Test-NodePilotPathContains -PathValue "$tools\" -Directory $tools)
Assert-True 'reports absence'              (-not (Test-NodePilotPathContains -PathValue 'C:\Windows' -Directory $tools))
Assert-True 'does not match a sibling'     (-not (Test-NodePilotPathContains -PathValue 'C:\Program Files\NodePilot\tools\npx' -Directory $tools))
Assert-True 'an empty PATH contains nothing' (-not (Test-NodePilotPathContains -PathValue '' -Directory $tools))

if ($script:failures -gt 0) {
    throw "Machine-PATH helper checks failed: $($script:failures) assertion(s)."
}
Write-Host 'Machine-PATH helper checks passed.' -ForegroundColor Green
