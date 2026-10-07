<#
.SYNOPSIS
  Runs the desktop-setup release scenarios on the Hyper-V lab client.

.DESCRIPTION
  From the clean checkpoint: T1 fresh install, a restart (T1b), then T2-T8 (over-install with the
  shell open, uninstall keeping data, reinstall, uninstall deleting everything, reinstall,
  /DISCARDDATA=1, final uninstall). Then, from the checkpoint again, U1: the previous release and
  this one over it. See ..\README.md.

.EXAMPLE
  .\Invoke-DesktopMatrix.ps1 -ConfigPath C:\lab-cred\release-lab.json -ArtifactDir ..\..\..\out -Version 1.4.1
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
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
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

function Invoke-Phase([string]$Phase, [string[]]$Files = @()) {
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
    Add-Content $log $output
    Write-Host $output
    return $output
}

$common = @($setup, (Join-Path $here 'DesktopLab.ps1'), (Join-Path $here 'Invoke-GuestDesktopScenarios.ps1'), (Join-Path (Split-Path $here) 'smoke-workflow.json'))
$outputs = @()

Restore-CleanVm
$outputs += Invoke-Phase 'first' $common
$before = Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { Restart-Computer -Force }
Wait-Guest $before
$outputs += Invoke-Phase 'second'

if ($config.previousRelease.PSObject.Properties['desktopSetup'] -and (Test-Path $config.previousRelease.desktopSetup)) {
    Restore-CleanVm
    $previous = Join-Path $ResultsDir 'previous-desktop-setup.exe'
    Copy-Item $config.previousRelease.desktopSetup $previous -Force
    $outputs += Invoke-Phase 'upgrade' ($common + $previous)
}
else { Add-Content $log 'U1 N/A: no previousRelease.desktopSetup configured' }

Stop-VM -Name $vm -TurnOff -Force
Restore-VMCheckpoint -VMSnapshot (Get-VMCheckpoint -VMName $vm -Name $config.desktop.checkpoint) -Confirm:$false

$summaries = @(($outputs -join "`n") -split "`n" | Where-Object { $_ -match 'SUMMARY desktop' })
$summaries | Set-Content (Join-Path $ResultsDir 'summary.md') -Encoding utf8
$summaries | Write-Host
if (-not $summaries -or @($summaries | Where-Object { $_ -notmatch ', 0 failed' }).Count) { exit 1 }
