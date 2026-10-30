# Execute the actual failover control flow with synthetic service/health boundaries.
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-Failover.ps1'))
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$transaction = $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] -and $_.Finally }
if (@($transaction).Count -ne 1) { throw 'Expected one service restoration transaction.' }
$fixture = @'
param([string]$Case, [string]$Log)
$ErrorActionPreference = 'Stop'
$leaderHost = 'fixture.invalid'; $leaderUrl = 'https://fixture.invalid'; $leaderName = 'A'; $followerUrl = 'https://follower.invalid'; $followerName = 'B'
$ServiceName = 'FixtureOnly'; $MaxWaitSeconds = 0; $expectedNewEpoch = $null; $VipUrl = $null
$SkipServiceStop = $Case -eq 'manual'
$restartRequired = $false; $failoverStart = Get-Date
function Write-Step($Message) { }
function Read-Host { return '' }
function Wait-ForLeaderTransition {
    param($baseUrl, $expectedReason, $timeoutSec)
    if ($Case -eq 'probe-error') { throw 'Synthetic health failure' }
    return $null
}
function Invoke-Command {
    param($ComputerName, $ArgumentList, $ErrorAction, $ScriptBlock)
    if ($ScriptBlock.ToString().Contains('Stop-Service')) {
        [IO.File]::AppendAllText($Log, "stop`n")
        if ($Case -eq 'stop-error') { throw 'Synthetic ambiguous stop failure' }
    } else {
        [IO.File]::AppendAllText($Log, "start`n")
        if ($Case -eq 'start-error') { throw 'Synthetic restore failure' }
    }
}
'@ + "`n" + $transaction.Extent.Text
$temp = Join-Path ([IO.Path]::GetTempPath()) ('NodePilotFailoverRecovery-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $fixturePath = Join-Path $temp 'fixture.ps1'
    [IO.File]::WriteAllText($fixturePath, $fixture)
    $shell = (Get-Process -Id $PID).Path
    foreach ($case in @('timeout', 'probe-error', 'stop-error', 'start-error', 'manual')) {
        $log = Join-Path $temp ($case + '.log')
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $fixturePath -Case $case -Log $log 2>&1
            $exitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = 'Stop' }
        if ($exitCode -eq 0) { throw "Failure was reported as success: $case ($output)" }
        if ($case -eq 'manual') {
            if (Test-Path -LiteralPath $log) { throw 'Manual mode touched a service.' }
        } elseif ([IO.File]::ReadAllText($log) -ne "stop`nstart`n") {
            throw "Original leader was not restored in $case."
        }
    }
} finally {
    if (([IO.Path]::GetFullPath($temp)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
Write-Host 'Failover recovery tests passed (5 synthetic failure scenarios).'
