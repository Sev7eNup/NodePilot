#requires -Version 5.1
<#
.SYNOPSIS
    Unit tests for the switcher-config helpers shared by install and update.
.DESCRIPTION
    The helpers decide whether the installed Switcher can reach the server at all. They rewrite
    switcher.json in place instead of round-tripping it through ConvertTo-Json, so the pattern
    they match on and the shipped template have to stay in step - a reformatted template turns
    into a warning during setup and a Switcher that never connects.

    The tests run against the shipped template in a temporary copy, so they touch nothing in the
    real environment and need no dependencies.
#>

[CmdletBinding()]
param([string]$SwitcherConfigScriptPath, [string]$SwitcherTemplatePath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SwitcherConfigScriptPath)) {
    $SwitcherConfigScriptPath = Join-Path $scriptDirectory 'SwitcherConfig.ps1'
}
if (-not (Test-Path -LiteralPath $SwitcherConfigScriptPath -PathType Leaf)) {
    throw "SwitcherConfig helper not found: $SwitcherConfigScriptPath"
}
if ([string]::IsNullOrWhiteSpace($SwitcherTemplatePath)) {
    $SwitcherTemplatePath = Join-Path $scriptDirectory '..\src\NodePilot.Switcher\switcher.json'
}
if (-not (Test-Path -LiteralPath $SwitcherTemplatePath -PathType Leaf)) {
    throw "Switcher configuration template not found: $SwitcherTemplatePath"
}
. $SwitcherConfigScriptPath

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

$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("nodepilot-switcher-test-" + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $workingDirectory)

function New-SwitcherConfigCopy {
    # A copy of the shipped template, not an inline fixture: the point of these checks is that
    # the helper still matches the file that actually ships.
    param([string]$Name = ([Guid]::NewGuid().ToString('N') + '.json'))
    $path = Join-Path $workingDirectory $Name
    Copy-Item -LiteralPath $SwitcherTemplatePath -Destination $path -Force
    return $path
}

function New-JsonFile {
    param([Parameter(Mandatory)][string]$Content, [Parameter(Mandatory)][string]$Name)
    $path = Join-Path $workingDirectory $Name
    [System.IO.File]::WriteAllText($path, $Content, (New-Object System.Text.UTF8Encoding $false))
    return $path
}

try {
    Write-Host 'SwitcherConfig: URL construction' -ForegroundColor Cyan
    Assert-Equal 'the default HTTPS port is left out of the URL' `
        'https://nodepilot.contoso.local' `
        (Get-NodePilotSwitcherServerUrlFor -Hostname 'nodepilot.contoso.local' -HttpsPort 443)

    Assert-Equal 'a non-default port is appended' `
        'https://nodepilot.contoso.local:8443' `
        (Get-NodePilotSwitcherServerUrlFor -Hostname 'nodepilot.contoso.local' -HttpsPort 8443)

    # Guards the ${Hostname} braces: without them the port becomes part of the variable name.
    Assert-Equal 'the hostname is not glued to the port' `
        'https://np:5001' `
        (Get-NodePilotSwitcherServerUrlFor -Hostname 'np' -HttpsPort 5001)

    Write-Host 'SwitcherConfig: reading' -ForegroundColor Cyan
    $fresh = New-SwitcherConfigCopy
    Assert-True 'the shipped template reads as unset' `
        ($null -eq (Get-NodePilotSwitcherServerUrl -ConfigPath $fresh))

    Assert-True 'a missing file reads as unset' `
        ($null -eq (Get-NodePilotSwitcherServerUrl -ConfigPath (Join-Path $workingDirectory 'absent.json')))

    Assert-True 'a file that is not JSON reads as unset' `
        ($null -eq (Get-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content 'not json at all' -Name 'garbage.json')))

    Assert-True 'a configuration without the nodePilot node reads as unset' `
        ($null -eq (Get-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content '{ "other": 1 }' -Name 'no-node.json')))

    Assert-True 'a blank server URL reads as unset' `
        ($null -eq (Get-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content '{ "nodePilot": { "serverUrl": "   " } }' -Name 'blank.json')))

    Assert-Equal 'a configured server URL reads back' `
        'https://configured.contoso.local' `
        (Get-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content '{ "nodePilot": { "serverUrl": "https://configured.contoso.local" } }' -Name 'configured.json'))

    Write-Host 'SwitcherConfig: writing' -ForegroundColor Cyan
    $config = New-SwitcherConfigCopy
    Assert-True 'writing into the shipped template reports a change' `
        (Set-NodePilotSwitcherServerUrl -ConfigPath $config -ServerUrl 'https://nodepilot.contoso.local:8443')

    Assert-Equal 'the written server URL reads back' `
        'https://nodepilot.contoso.local:8443' `
        (Get-NodePilotSwitcherServerUrl -ConfigPath $config)

    # The update case: the previous value is carried across and must replace, not duplicate.
    Assert-True 'an existing value is overwritten' `
        (Set-NodePilotSwitcherServerUrl -ConfigPath $config -ServerUrl 'https://other.contoso.local')

    Assert-Equal 'the overwritten server URL reads back' `
        'https://other.contoso.local' `
        (Get-NodePilotSwitcherServerUrl -ConfigPath $config)

    # In-place rewrite, not a ConvertTo-Json round-trip: the OData query keeps its $ and & unescaped.
    $pristineJobsPath = ((Get-Content -LiteralPath $SwitcherTemplatePath -Raw -Encoding UTF8) | ConvertFrom-Json).systemCenterOrchestrator.activeJobsPath
    $rewrittenJobsPath = ((Get-Content -LiteralPath $config -Raw -Encoding UTF8) | ConvertFrom-Json).systemCenterOrchestrator.activeJobsPath
    Assert-Equal 'the OData query survives the rewrite unescaped' $pristineJobsPath $rewrittenJobsPath

    # Pins the in-place edit: every other line of the template comes back byte-identical.
    Assert-Equal 'the rewrite changes exactly one line' `
        '1' `
        (@(Compare-Object -ReferenceObject (Get-Content -LiteralPath $SwitcherTemplatePath) -DifferenceObject (Get-Content -LiteralPath $config) | Where-Object { $_.SideIndicator -eq '=>' }).Count)

    # The switcher's JSON reader rejects a BOM.
    Assert-True 'the file is written without a BOM' `
        (([System.IO.File]::ReadAllBytes($config))[0] -eq 0x7B)

    Assert-True 'a missing file is reported as not written' `
        (-not (Set-NodePilotSwitcherServerUrl -ConfigPath (Join-Path $workingDirectory 'absent.json') -ServerUrl 'https://x.contoso.local'))

    Assert-True 'a configuration without a serverUrl key is reported as not written' `
        (-not (Set-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content '{ "nodePilot": { "profile": "switcher" } }' -Name 'no-key.json') -ServerUrl 'https://x.contoso.local'))

    # Ambiguity is refused rather than guessed: two matches means the pattern no longer
    # identifies the key.
    $ambiguous = New-JsonFile -Content '{ "a": { "serverUrl": null }, "b": { "serverUrl": "x" } }' -Name 'ambiguous.json'
    $ambiguousBefore = Get-Content -LiteralPath $ambiguous -Raw -Encoding UTF8
    Assert-True 'a second serverUrl is reported as not written' `
        (-not (Set-NodePilotSwitcherServerUrl -ConfigPath $ambiguous -ServerUrl 'https://x.contoso.local'))

    Assert-Equal 'a refused write leaves the file untouched' `
        $ambiguousBefore `
        (Get-Content -LiteralPath $ambiguous -Raw -Encoding UTF8)

    # The parse-before-write valve: a value that would produce invalid JSON throws instead of
    # writing, so the switcher never gets a configuration it refuses to load.
    $broken = New-SwitcherConfigCopy
    $brokenBefore = Get-Content -LiteralPath $broken -Raw -Encoding UTF8
    $brokenThrew = $false
    try { $null = Set-NodePilotSwitcherServerUrl -ConfigPath $broken -ServerUrl 'https://x"evil' } catch { $brokenThrew = $true }
    Assert-True 'a value that would break the JSON is refused' $brokenThrew

    Assert-Equal 'the broken write leaves the file untouched' `
        $brokenBefore `
        (Get-Content -LiteralPath $broken -Raw -Encoding UTF8)

    # Hand-edited spacing is documented as supported.
    Assert-True 'a hand-spaced serverUrl spelling is still matched' `
        (Set-NodePilotSwitcherServerUrl -ConfigPath (New-JsonFile -Content "{`r`n  `"nodePilot`": {`r`n    `"serverUrl`" : null`r`n  }`r`n}" -Name 'spaced.json') -ServerUrl 'https://spaced.contoso.local')

    # The installer's own two-step, kept in one place so the composed URL and the writer stay tied.
    $installerConfig = New-SwitcherConfigCopy
    $installerUrl = Get-NodePilotSwitcherServerUrlFor -Hostname 'nodepilot.contoso.local' -HttpsPort 443
    Assert-True 'the installer composition is accepted by the writer' `
        (Set-NodePilotSwitcherServerUrl -ConfigPath $installerConfig -ServerUrl $installerUrl)

    Assert-Equal 'the installer composition reads back' `
        'https://nodepilot.contoso.local' `
        (Get-NodePilotSwitcherServerUrl -ConfigPath $installerConfig)
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($script:failures -gt 0) {
    throw "Switcher-config helper checks failed: $($script:failures) assertion(s)."
}
Write-Host 'Switcher-config helper checks passed.' -ForegroundColor Green
