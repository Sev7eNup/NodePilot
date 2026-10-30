<#
.SYNOPSIS
  Runs the desktop-setup release scenarios on the Hyper-V lab client.

.DESCRIPTION
  From the clean checkpoint: T1 fresh install, a restart (T1b), then T2-T8 (over-install with the
  shell open, uninstall keeping data, reinstall, uninstall deleting everything, reinstall,
  /DISCARDDATA=1, final uninstall). Then U1 from each of the five previous releases directly to
  this one, restoring the clean checkpoint before each upgrade. See ..\README.md.

.EXAMPLE
  .\Invoke-DesktopMatrix.ps1 -ConfigPath C:\lab-cred\release-lab.json -ArtifactDir ..\..\..\out -Version 1.4.6
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$ResultsDir = (Join-Path $env:TEMP "nodepilot-release-lab\$Version\desktop")
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path (Split-Path $here -Parent) 'UpgradeReleases.ps1')
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$upgradeReleases = @(Get-UpgradeReleases -Config $config -Version $Version -SetupKind desktop)
$vm = $config.desktop.vm
$cred = Import-Clixml $config.credentialPath
$setup = Join-Path $ArtifactDir "NodePilot-Desktop-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Desktop setup not found: $setup" }
New-Item -ItemType Directory -Force $ResultsDir | Out-Null
$log = Join-Path $ResultsDir 'desktop.log'
if (Test-Path $log) { Remove-Item $log -Force }

function Wait-Guest([datetime]$After) {
    $deadline = (Get-Date).AddMinutes(8)
    do {
        Start-Sleep 10
        $boot = try { Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime } -ErrorAction Stop } catch { $null }
    } until (($boot -and $boot -gt $After) -or (Get-Date) -gt $deadline)
    if (-not ($boot -and $boot -gt $After)) { throw "$vm did not come back." }
    Start-Sleep 30
}

function Restore-CleanVm {
    Stop-VM -Name $vm -TurnOff -Force
    Restore-VMCheckpoint -VMSnapshot (Get-VMCheckpoint -VMName $vm -Name $config.desktop.checkpoint) -Confirm:$false
    Start-VM -Name $vm
    Wait-Guest ([datetime]::MinValue)
}

function Invoke-Phase([string]$Phase, [string[]]$Files = @(), [string]$CaseId = $Phase) {
    $session = New-PSSession -VMName $vm -Credential $cred
    try {
        Invoke-Command -Session $session -ScriptBlock { New-Item -ItemType Directory -Force C:\np-lab | Out-Null }
        foreach ($f in $Files) { Copy-Item -ToSession $session -LiteralPath $f -Destination 'C:\np-lab\' -Force }
        $output = Invoke-Command -Session $session -ArgumentList $Phase -ScriptBlock {
            param($p)
            Set-ExecutionPolicy -Scope Process Bypass -Force
            & C:\np-lab\Invoke-GuestDesktopScenarios.ps1 -Phase $p *>&1 | Out-String -Width 400
        }
    }
    finally { Remove-PSSession $session }
    Add-Content $log "=== $CaseId -> $Version"
    Add-Content $log $output
    $output | Set-Content (Join-Path $ResultsDir "$CaseId.log") -Encoding utf8
    Write-Host $output
    $summaries = @($output -split "`n" | Where-Object { $_ -match "SUMMARY desktop ${Phase}:" })
    $passed = $summaries.Count -eq 1 -and $summaries[0] -match ': [1-9][0-9]* passed, 0 failed\s*$' -and $output -notmatch '\bFAIL\b'
    return [pscustomobject]@{ id = $CaseId; verdict = if ($passed) { 'PASS' } else { 'FAIL' }; summary = $summaries -join '; ' }
}

$common = @($setup, (Join-Path $here 'DesktopLab.ps1'), (Join-Path $here 'Invoke-GuestDesktopScenarios.ps1'), (Join-Path (Split-Path $here) 'smoke-workflow.json'))
$outputs = @()

Restore-CleanVm
$outputs += Invoke-Phase 'first' $common
$before = Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { Restart-Computer -Force }
Wait-Guest $before
$outputs += Invoke-Phase 'second'

foreach ($release in $upgradeReleases) {
    Restore-CleanVm
    $caseId = "U1-from-$($release.version)"
    $stage = Join-Path $ResultsDir $caseId
    New-Item -ItemType Directory -Force $stage | Out-Null
    $previous = Join-Path $stage 'previous-desktop-setup.exe'
    Copy-Item -LiteralPath $release.desktopSetup -Destination $previous -Force
    $result = Invoke-Phase 'upgrade' ($common + $previous) $caseId
    $result | Add-Member -NotePropertyName sourceVersion -NotePropertyValue $release.version
    $outputs += $result
}

Stop-VM -Name $vm -TurnOff -Force
Restore-VMCheckpoint -VMSnapshot (Get-VMCheckpoint -VMName $vm -Name $config.desktop.checkpoint) -Confirm:$false

$summary = @("# Desktop setup $Version - release lab", '', '| Scenario | Source | Target | Verdict | Details |', '|---|---|---|---|---|')
foreach ($result in $outputs) {
    $summary += "| $($result.id) | $($result.sourceVersion) | $Version | $($result.verdict) | $($result.summary) |"
}
$summary | Set-Content (Join-Path $ResultsDir 'summary.md') -Encoding utf8
$summary | Write-Host
if ($outputs.Count -ne 7 -or @($outputs | Where-Object { $_.verdict -ne 'PASS' }).Count) { exit 1 }
