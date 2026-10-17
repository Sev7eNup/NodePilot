#requires -Version 5.1
<# Synthetic adapter for Build-InstallerPreview.ps1. Never operates on an installation. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('InitSession', 'Cleanup', 'Certificates', 'Ports', 'Probe', 'Provision', 'Apply')][string]$Mode,
    [string]$SessionPath, [string]$HandoffPath, [string]$AnswerFile, [string]$ArtifactPath,
    [string]$TrustedArtifactSignerThumbprint, [string]$OutFile, [string]$LogPath,
    [string]$ProgressFile, [string]$ProbeServiceName, [string]$PayloadRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$utf8 = New-Object Text.UTF8Encoding($false)
if ($Mode -eq 'Apply') { throw 'PREVIEW ONLY. Installation is disabled.' }
if ($Mode -eq 'Cleanup') { exit 0 } # Keep synthetic session evidence for inspection.
if ($Mode -eq 'InitSession') {
    $session = Join-Path $env:TEMP ('nodepilot-ui-preview-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $session
    [IO.File]::WriteAllText($HandoffPath, $session, $utf8)
    exit 0
}
$result = "[summary]`r`nexitCode=0`r`n"
if ($Mode -eq 'Certificates') {
    $expiry = (Get-Date).AddYears(1).ToString('yyyy-MM-dd')
    $result += "[certificates]`r`ncount=1`r`n0=1111111111111111111111111111111111111111|CN=server.example.test (SIMULATED)|1|$expiry`r`n"
}
if ($Mode -eq 'Ports') { $result += "[ports]`r`nhttps=8443`r`nhttp=8080`r`n" }
if ($Mode -in @('Probe', 'Provision')) {
    $scenarioPath = Join-Path $env:TEMP 'nodepilot-installer-ui-scenario.txt'
    $scenario = ([IO.File]::ReadAllText($scenarioPath)).Trim()
    if ($scenario -notin @('pass', 'repair', 'crash', 'restart', 'timeout')) { throw 'Unknown preview scenario.' }
    $session = Split-Path -Parent $OutFile
    if ($ProgressFile) { [IO.File]::WriteAllText(($ProgressFile + '.pid'), [string]$PID, $utf8) }
    if ($Mode -eq 'Probe') {
        if ($scenario -eq 'timeout') { Start-Sleep -Seconds 15; exit 7 }
        if ($scenario -eq 'crash' -and -not (Test-Path -LiteralPath (Join-Path $session 'crashed'))) {
            [IO.File]::WriteAllText((Join-Path $session 'crashed'), 'fixture', $utf8)
            Start-Sleep -Seconds 2
            exit 7
        }
        $blocked = $scenario -in @('repair', 'restart') -and -not (Test-Path -LiteralPath (Join-Path $session 'fixed'))
        if ($blocked) { $result = "[summary]`r`nexitCode=2`r`n" }
        foreach ($id in @('dotnet', 'certificate', 'ports', 'gmsa', 'serviceIdentity', 'domainJoined', 'database', 'databaseVersion', 'databaseServiceLogin', 'signer')) {
            $failed = $blocked -and (($scenario -eq 'repair' -and $id -eq 'database') -or ($scenario -eq 'restart' -and $id -eq 'gmsa'))
            $status = if ($failed) { 'Fail' } else { 'Pass' }
            $fix = if ($failed) { '1' } else { '0' }
            $result += "[check.$id]`r`nstatus=$status`r`ntitle=TEST $id`r`ndetail=SIMULATED $scenario scenario. No system changes.\nLong detail line 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8\nLine 9\nEnd of test detail.`r`nrequired=1`r`ncanAutoFix=$fix`r`nautoFixDefault=0`r`nautoFixLabel=Simulate $scenario result`r`n"
        }
    } else {
        $attemptPath = Join-Path $session 'attempted'
        if ($scenario -eq 'repair' -and -not (Test-Path -LiteralPath $attemptPath)) {
            [IO.File]::WriteAllText($attemptPath, 'fixture', $utf8)
            $result = "[summary]`r`nexitCode=1`r`nerror=TEST: database administrator permission denied. Correct the account and retry.`r`n"
        } else {
            [IO.File]::WriteAllText((Join-Path $session 'fixed'), 'fixture', $utf8)
            $result += "actionsPerformed=1`r`n[provision.database]`r`nstatus=Pass`r`n"
            if ($scenario -eq 'restart') {
                $result += "[provision.gmsa]`r`nstatus=Pass`r`nrestartRequired=1`r`ndetail=TEST restart required; no restart was performed.`r`n"
            }
        }
    }
}
[IO.File]::WriteAllText($OutFile, $result, $utf8)
exit 0
