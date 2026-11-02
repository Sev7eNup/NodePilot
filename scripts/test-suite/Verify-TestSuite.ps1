<#
.SYNOPSIS
  Judges the NodePilot test suite against suite-manifest.json.

.DESCRIPTION
  The suite has two contracts and they cannot be judged the same way:

    positive  every step succeeds, so the execution must end Succeeded. The assertions
              live inside the workflow: a mismatched value throws in the assert node.
    negative  the workflow deliberately fails, so the execution must end Failed AND the
              set of failed step ids must be exactly the set the manifest declares. A run
              that fails for some other reason is a suite defect, not a passing negative.

  Cases carrying expectedReturnData are checked against the execution's stored returnData,
  because returnData is terminal and nothing inside the workflow can assert it.

.PARAMETER Once
  Start every enabled suite workflow now and judge those runs, instead of looking at what
  the cron already produced.

.PARAMETER WindowMinutes
  Judge the executions of the last N minutes. This is the acceptance mode.
.PARAMETER Profiles
  Required profiles. Disabled or missing workflows in these profiles fail verification.
#>
[CmdletBinding(DefaultParameterSetName = 'Window')]
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$User = 'admin',
  [Parameter(Mandatory)][string]$Password,
  [Parameter(ParameterSetName = 'Once')][switch]$Once,
  [Parameter(ParameterSetName = 'Window')][int]$WindowMinutes = 60,
  [int]$TimeoutSeconds = 300,
  [ValidateSet('continuous', 'integration', 'invasive')]
  [ValidateNotNullOrEmpty()][string[]]$Profiles = @('continuous')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestSuiteVerification.ps1')

$login = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/auth/login" `
  -ContentType 'application/json; charset=utf-8' `
  -Headers @{ 'X-Auth-Token-Response' = 'true' } `
  -Body ([Text.Encoding]::UTF8.GetBytes((@{ username = $User; password = $Password } | ConvertTo-Json -Compress)))
$h = @{ Authorization = "Bearer $($login.token)" }

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'suite-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$allWorkflows = Invoke-RestMethod -Uri "$BaseUrl/api/workflows/names" -Headers $h

function Get-Execution { param([string]$Id) Invoke-RestMethod -Uri "$BaseUrl/api/executions/$Id" -Headers $h }
function Get-Steps { param([string]$Id) @(Invoke-RestMethod -Uri "$BaseUrl/api/executions/$Id/steps" -Headers $h) }

$results = @()

foreach ($entry in @($manifest.workflows)) {
  if ($entry.profile -notin $Profiles) {
    $results += [pscustomobject]@{ Workflow=$entry.name; Verdict='SKIPPED'; Detail="profile $($entry.profile) was not requested" }
    continue
  }
  $wf = $allWorkflows | Where-Object { $_.name -eq $entry.name } | Select-Object -First 1
  if ($null -eq $wf) {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'MISSING'; Detail = 'not installed' }
    continue
  }
  $wf = Invoke-RestMethod -Uri "$BaseUrl/api/workflows/$($wf.id)" -Headers $h
  if (-not $wf.isEnabled) {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'FAIL'
      Detail = "required workflow is disabled (profile $($entry.profile))"
    }
    continue
  }
  # A workflow judged from its cadence cannot be started on demand: a hand-started run
  # carries none of the trigger parameters it exists to prove. Only the window mode,
  # which reads what the sources actually produced, can judge these.
  if ($entry.judgeBy -eq 'cadence' -and $Once) {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'SKIPPED'
      Detail = 'judged from its cadence, not from an on-demand run'
    }
    continue
  }
  # The child is driven by its parents; it has no cadence of its own to judge.
  if ($null -eq $entry.tier -and $entry.judgeBy -ne 'cadence') {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'SKIPPED'; Detail = 'driven by its parents' }
    continue
  }

  $executions = @()
  if ($Once) {
    $started = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/workflows/$($wf.id)/execute" `
      -Headers $h -ContentType 'application/json' `
      -Body (@{ parameters = @{}; timeoutSeconds = $TimeoutSeconds } | ConvertTo-Json)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
      Start-Sleep -Seconds 3
      $exec = Get-Execution -Id $started.id
    } while ($exec.status -in @('Pending', 'Running', 'Paused') -and (Get-Date) -lt $deadline)
    $executions = @($exec)
  }
  else {
    $since = (Get-Date).ToUniversalTime().AddMinutes(-$WindowMinutes)
    $executions = @(Get-TestSuiteWindowExecutions -Since $since -ReadPage {
      param($page)
      Invoke-RestMethod -Uri "$BaseUrl/api/executions?workflowId=$($wf.id)&page=$page&pageSize=50&terminalOnly=true&sortBy=started&sortDir=desc" -Headers $h
    })
    if ($executions.Count -eq 0) {
      $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'NO-RUNS'
        Detail = "no completed execution in the last $WindowMinutes min (cron $($entry.cron))"
      }
      continue
    }
  }

  $workflowCases = @($manifest.cases | Where-Object { $_.workflow -eq $entry.name })
  $needsSteps = $entry.contract -eq 'negative' -or @($workflowCases | Where-Object { $_.PSObject.Properties['expectedStepStatus'] }).Count -gt 0
  $needsFull = @($workflowCases | Where-Object { $_.PSObject.Properties['expectedReturnData'] }).Count -gt 0
  $verdict = 'PASS'
  $detail = "$($executions.Count) run(s) matched every contract"
  foreach ($execution in $executions) {
    $steps = if ($needsSteps) { @(Get-Steps -Id $execution.id) } else { @() }
    $full = if ($needsFull) { Get-Execution -Id $execution.id } else { $execution }
    $checked = Test-TestSuiteExecution -Entry $entry -Execution $execution -Cases $workflowCases -Steps @($steps) -FullExecution $full
    if ($checked.Verdict -ne 'PASS') {
      $verdict = 'FAIL'
      $detail = "execution $($execution.id): $($checked.Detail)"
      break
    }
  }

  $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = $verdict; Detail = $detail }
}

$results | Format-Table -AutoSize -Wrap | Out-String | Write-Host

$failed = @($results | Where-Object { $_.Verdict -in @('FAIL', 'MISSING', 'NO-RUNS') })
$passed = @($results | Where-Object { $_.Verdict -eq 'PASS' })
$skipped = @($results | Where-Object { $_.Verdict -eq 'SKIPPED' })
Write-Host "pass=$($passed.Count) fail=$($failed.Count) skipped=$($skipped.Count)"
if ($failed.Count -gt 0 -or $passed.Count -eq 0) {
  if ($passed.Count -eq 0) { Write-Host 'No required workflow was verified successfully.' }
  exit 1
}
