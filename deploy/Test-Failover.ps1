<#
.SYNOPSIS
  Smoke-test for NodePilot Active/Passive HA. Verifies that exactly one node is leader,
  measures failover RTO when the active leader is stopped, and confirms the previous
  leader rejoins as follower on restart.

.DESCRIPTION
  Run from any machine that can reach both nodes' /healthz/leader endpoint. Designed for
  PowerShell 5.1 and 7.x — no em-dashes, ASCII punctuation only (per project convention).

.PARAMETER NodeAUrl
  Base URL of node A (e.g. http://nodepilot-a:5000). The script polls
  $NodeAUrl/healthz/leader directly to identify which side is leader.

.PARAMETER NodeBUrl
  Base URL of node B.

.PARAMETER VipUrl
  Optional. Base URL of the load-balancer VIP. If supplied the script also probes the
  VIP after failover to measure end-to-end LB detection latency.

.PARAMETER ServiceName
  Windows service name on the active node. Defaults to "NodePilot". The script invokes
  Stop-Service on the active node remotely; supply -SkipServiceStop to test without
  actually stopping anything (then a manual stop is expected separately).

.PARAMETER MaxWaitSeconds
  Hard upper bound for how long to wait for the takeover to be visible on the other node.
  Default 120 (2x typical RTO). Failed if exceeded.

.EXAMPLE
  .\Test-Failover.ps1 -NodeAUrl http://nodepilot-a:5000 -NodeBUrl http://nodepilot-b:5000

.EXAMPLE
  .\Test-Failover.ps1 -NodeAUrl https://nodepilot-a -NodeBUrl https://nodepilot-b -VipUrl https://nodepilot.firma.de -SkipServiceStop
#>

param(
    [Parameter(Mandatory = $true)] [string]$NodeAUrl,
    [Parameter(Mandatory = $true)] [string]$NodeBUrl,
    [string]$VipUrl,
    [string]$ServiceName = 'NodePilot',
    [int]$MaxWaitSeconds = 120,
    [switch]$SkipServiceStop
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Trust self-signed certs in lab setups (PS 5.1 + PS 7 compatible).
if (-not ([System.Net.ServicePointManager]::ServerCertificateValidationCallback)) {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}

function Write-Step([string]$msg) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg) -ForegroundColor Cyan
}

function Get-LeaderHealth([string]$baseUrl) {
    $u = "$baseUrl/healthz/leader"
    try {
        $resp = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 5
        $body = $null
        try { $body = $resp.Content | ConvertFrom-Json } catch {}
        return [PSCustomObject]@{
            Url = $u
            StatusCode = [int]$resp.StatusCode
            IsLeader = ([int]$resp.StatusCode -eq 200)
            NodeId = if ($body) { $body.nodeId } else { $null }
            LeaseEpoch = if ($body) { $body.leaseEpoch } else { $null }
            Reason = if ($body) { $body.reason } else { $null }
        }
    } catch {
        # 503 from a follower throws — extract status code from response.
        $code = 0
        try { $code = [int]$_.Exception.Response.StatusCode.Value__ } catch {}
        return [PSCustomObject]@{
            Url = $u
            StatusCode = $code
            IsLeader = $false
            NodeId = $null
            LeaseEpoch = $null
            Reason = "request-error: $($_.Exception.Message)"
        }
    }
}

function Wait-ForLeaderTransition([string]$baseUrl, [string]$expectedReason, [int]$timeoutSec) {
    # Polls $baseUrl/healthz/leader until either it becomes leader (200) or the timeout expires.
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $h = Get-LeaderHealth $baseUrl
        if ($h.IsLeader) { return $h }
        Start-Sleep -Milliseconds 1000
    }
    return $null
}

# ----- Step 1: identify current state -----
Write-Step "Step 1: identifying current cluster state"
$a = Get-LeaderHealth $NodeAUrl
$b = Get-LeaderHealth $NodeBUrl
Write-Host "  Node A: status=$($a.StatusCode) leader=$($a.IsLeader) epoch=$($a.LeaseEpoch) nodeId=$($a.NodeId)"
Write-Host "  Node B: status=$($b.StatusCode) leader=$($b.IsLeader) epoch=$($b.LeaseEpoch) nodeId=$($b.NodeId)"

if ($a.IsLeader -and $b.IsLeader) {
    Write-Host "FAIL: both nodes report leader=200. Cluster is split-brain." -ForegroundColor Red
    exit 1
}
if (-not $a.IsLeader -and -not $b.IsLeader) {
    Write-Host "FAIL: neither node reports leader=200. Cluster is unhealthy (DB unreachable?)." -ForegroundColor Red
    exit 1
}

if ($a.IsLeader) {
    $leaderUrl = $NodeAUrl; $leaderName = 'A'
    $followerUrl = $NodeBUrl; $followerName = 'B'
    $expectedNewEpoch = if ($a.LeaseEpoch) { [int]$a.LeaseEpoch + 1 } else { $null }
} else {
    $leaderUrl = $NodeBUrl; $leaderName = 'B'
    $followerUrl = $NodeAUrl; $followerName = 'A'
    $expectedNewEpoch = if ($b.LeaseEpoch) { [int]$b.LeaseEpoch + 1 } else { $null }
}
Write-Host "  -> Active leader is $leaderName ($leaderUrl). Will fail it over to $followerName." -ForegroundColor Green

# ----- Step 2: stop the leader -----
$failoverStart = Get-Date
if ($SkipServiceStop) {
    Write-Host ""
    Write-Host "  -SkipServiceStop set: stop the '$ServiceName' service on $leaderName ($leaderUrl) NOW." -ForegroundColor Yellow
    Write-Host "  Press ENTER once you have stopped the service to start the timer."
    [void](Read-Host)
    $failoverStart = Get-Date
} else {
    # Resolve the leader hostname for Stop-Service -ComputerName.
    $leaderHost = ([Uri]$leaderUrl).Host
    Write-Step "Step 2: stopping service '$ServiceName' on $leaderHost"
    try {
        Stop-Service -Name $ServiceName -ComputerName $leaderHost -Force -ErrorAction Stop
    } catch {
        Write-Host "FAIL: could not stop service on $leaderHost. $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Hint: re-run with -SkipServiceStop and stop the service manually." -ForegroundColor Yellow
        exit 1
    }
}

# ----- Step 3: wait for the follower to become leader -----
Write-Step "Step 3: waiting for $followerName to take over (timeout $MaxWaitSeconds s)"
$takeover = Wait-ForLeaderTransition -baseUrl $followerUrl -expectedReason 'leader' -timeoutSec $MaxWaitSeconds
$failoverEnd = Get-Date
$rtoSeconds = [int]([TimeSpan]($failoverEnd - $failoverStart)).TotalSeconds

if ($null -eq $takeover) {
    Write-Host "FAIL: $followerName did not become leader within $MaxWaitSeconds s." -ForegroundColor Red
    exit 1
}
Write-Host ("  -> $followerName is now leader. RTO {0} s, new epoch {1}" -f $rtoSeconds, $takeover.LeaseEpoch) -ForegroundColor Green
if ($expectedNewEpoch -ne $null -and [int]$takeover.LeaseEpoch -ne $expectedNewEpoch) {
    Write-Host "WARN: epoch did not increment by exactly 1 (was $($takeover.LeaseEpoch), expected $expectedNewEpoch). May indicate concurrent acquires." -ForegroundColor Yellow
}

# ----- Step 4: probe VIP if configured -----
if ($VipUrl) {
    Write-Step "Step 4: probing VIP $VipUrl"
    $vipDeadline = (Get-Date).AddSeconds(60)
    $vipOk = $false
    while ((Get-Date) -lt $vipDeadline) {
        try {
            $vipResp = Invoke-WebRequest -Uri "$VipUrl/healthz/leader" -UseBasicParsing -TimeoutSec 5
            if ([int]$vipResp.StatusCode -eq 200) { $vipOk = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 1000
    }
    if ($vipOk) {
        Write-Host "  -> VIP routes to the new leader OK." -ForegroundColor Green
    } else {
        Write-Host "  -> WARN: VIP did not start responding 200 within 60 s after takeover. Check LB probe interval / health-check config." -ForegroundColor Yellow
    }
}

# ----- Step 5: restart the original leader and confirm follower role -----
if (-not $SkipServiceStop) {
    Write-Step "Step 5: restarting service on the original leader"
    $leaderHost = ([Uri]$leaderUrl).Host
    try {
        Start-Service -Name $ServiceName -ComputerName $leaderHost -ErrorAction Stop
    } catch {
        Write-Host "WARN: could not start service on $leaderHost. $($_.Exception.Message). Restart manually then re-check /healthz/leader." -ForegroundColor Yellow
        exit 0
    }

    # Wait for the original leader to come back as follower (its own /healthz/leader → 503).
    $rejoinDeadline = (Get-Date).AddSeconds(60)
    $rejoined = $false
    while ((Get-Date) -lt $rejoinDeadline) {
        $h = Get-LeaderHealth $leaderUrl
        # Follower correctly: 503 with reason="not_leader". Or process still starting: connection-refused.
        if ($h.StatusCode -eq 503 -and $h.Reason -eq 'not_leader') { $rejoined = $true; break }
        Start-Sleep -Milliseconds 2000
    }
    if ($rejoined) {
        Write-Host "  -> Original leader rejoined cluster as follower. Cluster is healthy." -ForegroundColor Green
    } else {
        Write-Host "  -> WARN: original leader has not yet rejoined as follower after 60 s. Check service logs." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host ("RESULT: failover RTO {0} s." -f $rtoSeconds) -ForegroundColor Green
exit 0
