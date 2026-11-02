#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'TestSuiteVerification.ps1')

$script:passed = 0
function Assert-Verification {
  param([bool]$Condition, [string]$Message)
  if (-not $Condition) { throw $Message }
  $script:passed++
}

$script:pages = @()
$now = [datetime]::UtcNow
$window = @(Get-TestSuiteWindowExecutions -Since $now.AddMinutes(-60) -ReadPage {
  param($page)
  $script:pages += $page
  if ($page -eq 1) {
    return [pscustomobject]@{ totalPages=3; items=@([pscustomobject]@{id='new';status='Succeeded';startedAt=$now}) }
  }
  return [pscustomobject]@{ totalPages=3; items=@(
    [pscustomobject]@{id='old';status='Succeeded';startedAt=$now.AddMinutes(-10)},
    [pscustomobject]@{id='outside';status='Succeeded';startedAt=$now.AddMinutes(-90)}) }
})
Assert-Verification (($window.id -join ',') -eq 'new,old') 'Window pagination omitted an older execution.'
Assert-Verification (($script:pages -join ',') -eq '1,2') 'Window pagination did not stop after crossing the sorted time boundary.'

$case = [pscustomobject]@{expectedOutcome='workflow-failure';expectedFailure=[pscustomobject]@{stepId='expected';errorContains='expected rejection'}}
$entry = [pscustomobject]@{contract='negative'}
$execution = [pscustomobject]@{status='Failed'}
$wrong = Test-TestSuiteExecution -Entry $entry -Execution $execution -Cases @($case) -Steps @([pscustomobject]@{stepId='wrong';status='Failed';errorOutput='different failure'})
Assert-Verification ($wrong.Verdict -eq 'FAIL') 'A failure at an undeclared node was accepted.'
$right = Test-TestSuiteExecution -Entry $entry -Execution $execution -Cases @($case) -Steps @([pscustomobject]@{stepId='expected';status='Failed';errorOutput='expected rejection'})
Assert-Verification ($right.Verdict -eq 'PASS') 'The declared negative contract was rejected.'

$scriptPath = (Join-Path $PSScriptRoot 'Verify-TestSuite.ps1').Replace("'", "''")
$fixture = @'
$ErrorActionPreference = 'Stop'
$scenario = '__SCENARIO__'
$workflow = @{name='required';profile='continuous';contract='positive';tier='A';cron='test';judgeBy='run'}
$case = @{workflow='required';expectedOutcome='success';expectedReturnData=@{value='correct'}}
if ($scenario -eq 'wrong-step') {
  $workflow.contract='negative'
  $case=@{workflow='required';expectedOutcome='workflow-failure';expectedFailure=@{stepId='expected';errorContains=''}}
}
$manifest=@{workflows=@($workflow,@{name='optional';profile='integration';contract='positive';tier='A';cron='test';judgeBy='run'});cases=@($case)} | ConvertTo-Json -Depth 8
function Get-Content { param($LiteralPath,[switch]$Raw,$Encoding) return $manifest }
function Invoke-RestMethod {
  param($Method,$Uri,$Headers,$ContentType,$Body)
  if ($Uri.EndsWith('/api/auth/login')) { return [pscustomobject]@{token='fixture'} }
  if ($Uri.EndsWith('/api/workflows/names')) { return @([pscustomobject]@{id='wf';name='required'}) }
  if ($Uri.EndsWith('/api/workflows/wf')) { return [pscustomobject]@{id='wf';isEnabled=($scenario -ne 'disabled')} }
  if ($Uri.Contains('/api/executions?')) {
    $status=if($scenario -eq 'wrong-step'){'Failed'}else{'Succeeded'}
    $ids=if($Uri -like '*page=1&*'){@('new')}else{@('old')}
    return [pscustomobject]@{totalPages=2;items=@($ids | ForEach-Object {[pscustomobject]@{id=$_;status=$status;startedAt=[datetime]::UtcNow.AddMinutes(-1)}})}
  }
  if ($Uri.EndsWith('/steps')) {
    $id=if($Uri -like '*/old/steps'){'unexpected'}else{'expected'}
    return @([pscustomobject]@{stepId=$id;status='Failed';errorOutput=''})
  }
  if ($Uri -like '*/api/executions/*') {
    $value=if($scenario -eq 'wrong-return' -and $Uri.EndsWith('/old')){'wrong'}else{'correct'}
    return [pscustomobject]@{returnData=(@{value=$value}|ConvertTo-Json -Compress)}
  }
  throw "Unexpected fixture request: $Uri"
}
& '__SCRIPT__' -BaseUrl 'http://fixture.invalid' -Password 'synthetic' -WindowMinutes 60
'@
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$previousModulePath = $env:PSModulePath
try {
  $env:PSModulePath = $null
  foreach ($scenario in @('disabled','wrong-step','wrong-return','valid')) {
    $command = $fixture.Replace('__SCENARIO__',$scenario).Replace('__SCRIPT__',$scriptPath)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $output = & $powershellPath -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand $encoded 2>&1 | Out-String
    $expectedExit = if ($scenario -eq 'valid') { 0 } else { 1 }
    Assert-Verification ($LASTEXITCODE -eq $expectedExit) "Verifier $scenario exited $LASTEXITCODE instead of $expectedExit`: $output"
    if ($scenario -in @('wrong-step','wrong-return')) {
      Assert-Verification ($output -match 'execution old') "Verifier $scenario did not inspect the older paginated execution: $output"
    }
  }
}
finally { $env:PSModulePath = $previousModulePath }
Write-Host "Test-suite verification checks passed: $script:passed"
