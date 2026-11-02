#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Get-TestSuiteWindowExecutions {
  [CmdletBinding()]
  param([Parameter(Mandatory)][datetime]$Since, [Parameter(Mandatory)][scriptblock]$ReadPage)
  $page = 1
  $seen = New-Object 'System.Collections.Generic.HashSet[string]'
  do {
    $response = & $ReadPage $page
    if ($null -eq $response -or -not $response.PSObject.Properties['items'] -or -not $response.PSObject.Properties['totalPages']) {
      throw 'The execution list did not return a paged response.'
    }
    $items = @($response.items)
    if ($items.Count -eq 0) { break }
    $beforeWindow = $false
    foreach ($execution in $items) {
      if (-not $execution.startedAt) { continue }
      if (([datetime]$execution.startedAt).ToUniversalTime() -lt $Since) { $beforeWindow = $true; continue }
      if ($execution.status -notin @('Pending', 'Running', 'Paused') -and $seen.Add([string]$execution.id)) { $execution }
    }
    $page++
  } while (-not $beforeWindow -and $page -le [int]$response.totalPages)
}

function Test-TestSuiteExecution {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)]$Entry,
    [Parameter(Mandatory)]$Execution,
    [object[]]$Cases = @(),
    [object[]]$Steps = @(),
    $FullExecution = $Execution
  )
  $expectedStatus = if ($Entry.contract -eq 'negative') { 'Failed' } else { 'Succeeded' }
  if ($Execution.status -ne $expectedStatus) {
    return [pscustomobject]@{ Verdict='FAIL'; Detail="expected $expectedStatus, got $($Execution.status)" }
  }
  if ($Entry.contract -eq 'negative') {
    $failureCases = @($Cases | Where-Object { $_.expectedOutcome -eq 'workflow-failure' })
    $expectedFailures = @($failureCases | ForEach-Object { $_.expectedFailure.stepId } | Sort-Object -Unique)
    $actualFailures = @($Steps | Where-Object { $_.status -eq 'Failed' } | ForEach-Object { $_.stepId } | Sort-Object -Unique)
    $missing = @($expectedFailures | Where-Object { $_ -notin $actualFailures })
    $unexpected = @($actualFailures | Where-Object { $_ -notin $expectedFailures })
    if ($expectedFailures.Count -eq 0 -or $missing.Count -gt 0 -or $unexpected.Count -gt 0) {
      return [pscustomobject]@{ Verdict='FAIL'; Detail="failed-step mismatch; missing: [$($missing -join ',')] unexpected: [$($unexpected -join ',')]" }
    }
    foreach ($case in $failureCases) {
      $needle = $case.expectedFailure.errorContains
      if ([string]::IsNullOrWhiteSpace($needle)) { continue }
      $step = $Steps | Where-Object { $_.stepId -eq $case.expectedFailure.stepId } | Select-Object -First 1
      if ($null -eq $step -or -not ([string]$step.errorOutput).Contains($needle)) {
        return [pscustomobject]@{ Verdict='FAIL'; Detail="step $($case.expectedFailure.stepId) failed, but not with '$needle'" }
      }
    }
  }
  foreach ($case in @($Cases | Where-Object { $_.PSObject.Properties['expectedStepStatus'] })) {
    $want = $case.expectedStepStatus
    $step = $Steps | Where-Object { $_.stepId -eq $want.stepId } | Select-Object -First 1
    $actual = if ($null -eq $step) { '(absent)' } else { [string]$step.status }
    if ($actual -ne $want.status) {
      return [pscustomobject]@{ Verdict='FAIL'; Detail="step $($want.stepId): expected $($want.status), got $actual" }
    }
  }
  $returnCases = @($Cases | Where-Object { $_.PSObject.Properties['expectedReturnData'] })
  if ($returnCases.Count -gt 0) {
    $returned = if ($FullExecution.returnData) { $FullExecution.returnData | ConvertFrom-Json } else { $null }
    foreach ($case in $returnCases) {
      foreach ($property in $case.expectedReturnData.PSObject.Properties) {
        $actual = if ($returned -and $returned.PSObject.Properties[$property.Name]) { [string]$returned.($property.Name) } else { $null }
        $expected = [string]$property.Value
        $matches = $null -ne $actual -and $(if ($expected.StartsWith('*')) { $actual.EndsWith($expected.Substring(1)) } else { $actual -eq $expected })
        if (-not $matches) {
          return [pscustomobject]@{ Verdict='FAIL'; Detail="returnData.$($property.Name) did not match '$expected'" }
        }
      }
    }
  }
  return [pscustomobject]@{ Verdict='PASS'; Detail=$expectedStatus }
}
