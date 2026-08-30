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
#>
[CmdletBinding(DefaultParameterSetName = 'Window')]
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$User = 'admin',
  [Parameter(Mandatory)][string]$Password,
  [Parameter(ParameterSetName = 'Once')][switch]$Once,
  [Parameter(ParameterSetName = 'Window')][int]$WindowMinutes = 60,
  [int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$login = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/auth/login" `
  -ContentType 'application/json; charset=utf-8' `
  -Headers @{ 'X-Auth-Token-Response' = 'true' } `
  -Body ([Text.Encoding]::UTF8.GetBytes((@{ username = $User; password = $Password } | ConvertTo-Json -Compress)))
$h = @{ Authorization = "Bearer $($login.token)" }

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'suite-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json

$allWorkflows = Invoke-RestMethod -Uri "$BaseUrl/api/workflows" -Headers $h
if ($null -ne $allWorkflows.PSObject.Properties['items']) { $allWorkflows = $allWorkflows.items }

function Get-Execution { param([string]$Id) Invoke-RestMethod -Uri "$BaseUrl/api/executions/$Id" -Headers $h }
function Get-Steps { param([string]$Id) @(Invoke-RestMethod -Uri "$BaseUrl/api/executions/$Id/steps" -Headers $h) }

function Test-ExpectedValue {
  param([string]$Actual, [string]$Expected)
  # A leading '*' means "ends with"; used for the truncation marker whose prefix is random.
  if ($Expected.StartsWith('*')) { return $Actual.EndsWith($Expected.Substring(1)) }
  return $Actual -eq $Expected
}

$results = @()

foreach ($entry in @($manifest.workflows)) {
  $wf = $allWorkflows | Where-Object { $_.name -eq $entry.name } | Select-Object -First 1
  if ($null -eq $wf) {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'MISSING'; Detail = 'not installed' }
    continue
  }
  if (-not $wf.isEnabled) {
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'SKIPPED'
      Detail = "disabled (profile $($entry.profile))"
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
    $recent = Invoke-RestMethod -Uri "$BaseUrl/api/executions?workflowId=$($wf.id)&pageSize=50" -Headers $h
    if ($null -ne $recent.PSObject.Properties['items']) { $recent = $recent.items }
    $executions = @($recent | Where-Object {
        $_.startedAt -and ([datetime]$_.startedAt).ToUniversalTime() -ge $since -and
        $_.status -notin @('Pending', 'Running', 'Paused')
      })
    if ($executions.Count -eq 0) {
      $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'NO-RUNS'
        Detail = "no completed execution in the last $WindowMinutes min (cron $($entry.cron))"
      }
      continue
    }
  }

  $expectedStatus = if ($entry.contract -eq 'negative') { 'Failed' } else { 'Succeeded' }
  $bad = @($executions | Where-Object { $_.status -ne $expectedStatus })
  if ($bad.Count -gt 0) {
    $sample = $bad[0]
    $detail = "expected $expectedStatus, got $($sample.status)"
    if ($sample.errorMessage) { $detail += " - $($sample.errorMessage)" }
    $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = 'FAIL'; Detail = $detail }
    continue
  }

  $detail = "$($executions.Count) run(s) $expectedStatus"
  $verdict = 'PASS'

  if ($entry.contract -eq 'negative') {
    # A negative run has to fail for the declared reason. Anything else is a defect that
    # a bare status check would happily wave through.
    $expectedFailures = @($manifest.cases |
        Where-Object { $_.workflow -eq $entry.name -and $_.expectedOutcome -eq 'workflow-failure' } |
        ForEach-Object { $_.expectedFailure.stepId } | Sort-Object -Unique)
    $steps = Get-Steps -Id $executions[0].id
    $actualFailures = @($steps | Where-Object { $_.status -eq 'Failed' } |
        ForEach-Object { $_.stepId } | Sort-Object -Unique)
    $missing = @($expectedFailures | Where-Object { $_ -notin $actualFailures })
    $unexpected = @($actualFailures | Where-Object { $_ -notin $expectedFailures })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
      $verdict = 'FAIL'
      $detail = "failed-step mismatch; missing: [$($missing -join ',')] unexpected: [$($unexpected -join ',')]"
    }
    else {
      $detail = "$($expectedFailures.Count) expected failure(s), no others"
      # errorContains is optional: many rejections surface a locale-dependent PowerShell
      # message, so only the cases that name a stable product string are matched on text.
      foreach ($case in @($manifest.cases |
          Where-Object { $_.workflow -eq $entry.name -and $_.expectedOutcome -eq 'workflow-failure' })) {
        $needle = $case.expectedFailure.errorContains
        if ([string]::IsNullOrWhiteSpace($needle)) { continue }
        $step = $steps | Where-Object { $_.stepId -eq $case.expectedFailure.stepId } | Select-Object -First 1
        if ($null -eq $step -or [string]$step.errorOutput -notlike "*$needle*") {
          $verdict = 'FAIL'
          $detail = "step $($case.expectedFailure.stepId) failed, but not with '$needle'"
        }
      }
    }
  }

  # Some outcomes are only visible as a step status: a branch that must be Skipped because
  # its edge is disabled or its condition is false cannot be asserted from inside the
  # workflow, because a skipped step leaves no result to reference.
  $statusCases = @($manifest.cases |
      Where-Object { $_.workflow -eq $entry.name -and $_.PSObject.Properties['expectedStepStatus'] })
  if ($verdict -eq 'PASS' -and $statusCases.Count -gt 0) {
    $steps = Get-Steps -Id $executions[0].id
    foreach ($case in $statusCases) {
      $want = $case.expectedStepStatus
      $step = $steps | Where-Object { $_.stepId -eq $want.stepId } | Select-Object -First 1
      $actual = if ($null -eq $step) { '(absent)' } else { [string]$step.status }
      if ($actual -ne $want.status) {
        $verdict = 'FAIL'
        $detail = "step $($want.stepId): expected $($want.status), got $actual"
      }
    }
  }

  $returnCases = @($manifest.cases |
      Where-Object { $_.workflow -eq $entry.name -and $_.PSObject.Properties['expectedReturnData'] })
  if ($verdict -eq 'PASS' -and $returnCases.Count -gt 0) {
    $full = Get-Execution -Id $executions[0].id
    $returned = $null
    if ($full.returnData) { $returned = $full.returnData | ConvertFrom-Json }
    foreach ($case in $returnCases) {
      foreach ($prop in $case.expectedReturnData.PSObject.Properties) {
        $actual = if ($returned -and $returned.PSObject.Properties[$prop.Name]) {
          [string]$returned.$($prop.Name)
        }
        else { $null }
        if ($null -eq $actual -or -not (Test-ExpectedValue -Actual $actual -Expected ([string]$prop.Value))) {
          $verdict = 'FAIL'
          $shown = if ($null -eq $actual) { '(absent)' } elseif ($actual.Length -gt 60) { $actual.Substring(0, 60) + '...' } else { $actual }
          $detail = "returnData.$($prop.Name): expected '$($prop.Value)', got '$shown'"
        }
      }
    }
  }

  $results += [pscustomobject]@{ Workflow = $entry.name; Verdict = $verdict; Detail = $detail }
}

$results | Format-Table -AutoSize -Wrap | Out-String | Write-Host

$failed = @($results | Where-Object { $_.Verdict -in @('FAIL', 'MISSING', 'NO-RUNS') })
$passed = @($results | Where-Object { $_.Verdict -eq 'PASS' })
$skipped = @($results | Where-Object { $_.Verdict -eq 'SKIPPED' })
Write-Host "pass=$($passed.Count) fail=$($failed.Count) skipped=$($skipped.Count)"
if ($failed.Count -gt 0) { exit 1 }
