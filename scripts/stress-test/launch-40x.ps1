[CmdletBinding()]
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$User    = 'admin',
  [string]$Password = 'admin123',
  [string]$WorkflowName = 'Stress-Test',
  [int]$Parallel = 40,
  [string]$DefinitionFile
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $DefinitionFile) { $DefinitionFile = Join-Path $PSScriptRoot 'main.json' }

function Log($msg) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg) }

# 1) Login
Log "Login $User @ $BaseUrl"
$loginBody = @{ username = $User; password = $Password } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' -Body $loginBody
$token = $loginResp.token
$headers = @{ Authorization = "Bearer $token" }

# 2) Find or create workflow
Log "Lookup workflow '$WorkflowName'"
$existing = Invoke-RestMethod -Method GET -Uri "$BaseUrl/api/workflows" -Headers $headers
$wf = $existing | Where-Object { $_.name -eq $WorkflowName } | Select-Object -First 1

$defJson = Get-Content -Raw -LiteralPath $DefinitionFile
if ($wf) {
  Log "Update existing workflow id=$($wf.id)"
  $body = @{ name = $WorkflowName; description = 'Ad-hoc load-test workflow'; definitionJson = $defJson; isEnabled = $true } | ConvertTo-Json -Depth 50
  $wf = Invoke-RestMethod -Method PUT -Uri "$BaseUrl/api/workflows/$($wf.id)" -Headers $headers -ContentType 'application/json' -Body $body
} else {
  Log "Create new workflow"
  $body = @{ name = $WorkflowName; description = 'Ad-hoc load-test workflow'; definitionJson = $defJson; isEnabled = $true } | ConvertTo-Json -Depth 50
  $wf = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/workflows" -Headers $headers -ContentType 'application/json' -Body $body
}
Log "Workflow id=$($wf.id) name=$($wf.name)"

# 3) Fire N parallel via RunspacePool
Log "Firing $Parallel parallel executions"
$executeUri = "$BaseUrl/api/workflows/$($wf.id)/execute"
$execBody = '{"parameters":{"label":"40x-parallel"}}'

$iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
$pool = [runspacefactory]::CreateRunspacePool(1, $Parallel, $iss, $Host)
$pool.Open()

$launchSw = [Diagnostics.Stopwatch]::StartNew()
$jobs = foreach ($i in 1..$Parallel) {
  $ps = [powershell]::Create()
  $ps.RunspacePool = $pool
  [void]$ps.AddScript({
    param($uri, $body, $token, $idx)
    try {
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $resp = Invoke-RestMethod -Method POST -Uri $uri -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body -TimeoutSec 30
      $sw.Stop()
      [pscustomobject]@{ idx = $idx; ok = $true; execId = $resp.id; status = $resp.status; ms = $sw.ElapsedMilliseconds; err = $null }
    } catch {
      [pscustomobject]@{ idx = $idx; ok = $false; execId = $null; status = $null; ms = -1; err = $_.Exception.Message }
    }
  }).AddArgument($executeUri).AddArgument($execBody).AddArgument($token).AddArgument($i)
  [pscustomobject]@{ ps = $ps; handle = $ps.BeginInvoke() }
}

# Collect launch results
$launchResults = foreach ($j in $jobs) {
  $r = $j.ps.EndInvoke($j.handle)
  $j.ps.Dispose()
  $r
}
$pool.Close(); $pool.Dispose()
$launchSw.Stop()

$accepted = $launchResults | Where-Object ok
$failed   = $launchResults | Where-Object { -not $_.ok }
Log ("Launched: accepted={0} failed={1} launch-wall={2}ms" -f $accepted.Count, $failed.Count, $launchSw.ElapsedMilliseconds)
if ($failed) {
  $failed | Select-Object -First 5 | ForEach-Object { Log ("  FAIL idx={0}: {1}" -f $_.idx, $_.err) }
}

$execIds = @($accepted | ForEach-Object { $_.execId })
if ($execIds.Count -eq 0) {
  Log "No executions accepted -- aborting"; exit 1
}

# 4) Poll until all terminal, with CPU sampling
Log "Polling $($execIds.Count) executions"
$apiPid = (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
$proc = if ($apiPid) { Get-Process -Id $apiPid -ErrorAction SilentlyContinue } else { $null }
if ($proc) { Log "API process pid=$apiPid name=$($proc.ProcessName)" }

$pollSw = [Diagnostics.Stopwatch]::StartNew()
$deadline = (Get-Date).AddMinutes(5)
$status = @{}
foreach ($id in $execIds) { $status[$id] = 'Unknown' }

$terminal = @('Completed','Failed','Cancelled','TimedOut','PartialFailure')
$lastLog = [Diagnostics.Stopwatch]::StartNew()
$cpuSamples = New-Object System.Collections.Generic.List[object]

while ((Get-Date) -lt $deadline) {
  $pending = $status.GetEnumerator() | Where-Object { $_.Value -notin $terminal } | ForEach-Object { $_.Key }
  if (-not $pending -or $pending.Count -eq 0) { break }

  # Sample process CPU/WS (point-in-time CPU total; rate calc done post-hoc)
  if ($proc) {
    $proc.Refresh()
    $cpuSamples.Add([pscustomobject]@{
      t = Get-Date
      totalCpuSec = $proc.TotalProcessorTime.TotalSeconds
      wsMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
      threads = $proc.Threads.Count
    })
  }

  # Batch-poll all executions list, then map
  try {
    $all = Invoke-RestMethod -Method GET -Uri "$BaseUrl/api/executions?pageSize=200" -Headers $headers -TimeoutSec 10
    $items = if ($all.items) { $all.items } else { $all }
    foreach ($item in $items) {
      if ($status.ContainsKey($item.id)) { $status[$item.id] = $item.status }
    }
  } catch {
    Log ("poll err: {0}" -f $_.Exception.Message)
  }

  if ($lastLog.ElapsedMilliseconds -ge 5000) {
    $counts = @{}
    foreach ($v in $status.Values) { if (-not $counts.ContainsKey($v)) { $counts[$v] = 0 }; $counts[$v]++ }
    $summary = ($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    $cpuLine = ''
    if ($cpuSamples.Count -ge 2) {
      $s1 = $cpuSamples[$cpuSamples.Count - 2]
      $s2 = $cpuSamples[$cpuSamples.Count - 1]
      $dt = ($s2.t - $s1.t).TotalSeconds
      if ($dt -gt 0) {
        $dCpu = $s2.totalCpuSec - $s1.totalCpuSec
        $pct = [math]::Round(($dCpu / $dt) * 100, 1)
        $cpuLine = ' cpu=' + $pct + ' pct (cores normalized) ws=' + $s2.wsMB + 'MB threads=' + $s2.threads
      }
    }
    Log ("t+{0}s  {1}{2}" -f [math]::Round($pollSw.Elapsed.TotalSeconds,1), $summary, $cpuLine)
    $lastLog.Restart()
  }

  Start-Sleep -Milliseconds 1500
}
$pollSw.Stop()

# 5) Final summary
$final = @{}
foreach ($v in $status.Values) { if (-not $final.ContainsKey($v)) { $final[$v] = 0 }; $final[$v]++ }
Log "=== FINAL ==="
$final.GetEnumerator() | Sort-Object Name | ForEach-Object { Log ("  {0}: {1}" -f $_.Key, $_.Value) }
Log ("total-wall-poll={0}s executions-launched={1}" -f [math]::Round($pollSw.Elapsed.TotalSeconds,1), $execIds.Count)

# Duration stats via individual detail calls (only for completed)
$durations = New-Object System.Collections.Generic.List[double]
foreach ($id in $execIds) {
  try {
    $d = Invoke-RestMethod -Method GET -Uri "$BaseUrl/api/executions/$id" -Headers $headers -TimeoutSec 10
    if ($d.startedAt -and $d.completedAt) {
      $sec = ([datetime]$d.completedAt - [datetime]$d.startedAt).TotalSeconds
      $durations.Add($sec)
    }
  } catch {}
}
if ($durations.Count -gt 0) {
  $sorted = $durations | Sort-Object
  $p50 = $sorted[[int]($durations.Count * 0.5)]
  $p95 = $sorted[[math]::Min([int]($durations.Count * 0.95), $durations.Count - 1)]
  $mx  = $sorted[$durations.Count - 1]
  $mn  = $sorted[0]
  $rMn = [math]::Round($mn,1); $rP50 = [math]::Round($p50,1); $rP95 = [math]::Round($p95,1); $rMx = [math]::Round($mx,1)
  Log ('per-execution seconds: min=' + $rMn + ' p50=' + $rP50 + ' p95=' + $rP95 + ' max=' + $rMx)
}

if ($cpuSamples.Count -ge 2) {
  $first = $cpuSamples[0]; $last = $cpuSamples[$cpuSamples.Count - 1]
  $dt = ($last.t - $first.t).TotalSeconds
  $dCpu = $last.totalCpuSec - $first.totalCpuSec
  if ($dt -gt 0) {
    $avg = [math]::Round(($dCpu / $dt) * 100, 1)
    $cores = [Environment]::ProcessorCount
    $perCore = [math]::Round($avg / $cores, 1)
    $msg = 'avg API CPU during poll: ' + $avg + ' pct total (' + $perCore + ' pct per-core, ' + $cores + ' cores)'
    Log $msg
  }
  $maxWs = ($cpuSamples | Measure-Object wsMB -Maximum).Maximum
  $maxTh = ($cpuSamples | Measure-Object threads -Maximum).Maximum
  Log ('peak ws=' + $maxWs + 'MB peak threads=' + $maxTh)
}
