#requires -Version 5.1
# Host-safety contracts of the test suite: power-management side effects stay behind the
# invasive profile, trigger acknowledgements only write owned files, and the webhook secret
# stays a secret global across reinstalls.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$script:passed = 0
function Assert-Safety {
  param([bool]$Condition, [string]$Message)
  if (-not $Condition) { throw $Message }
  $script:passed++
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
function Read-Json { param([string]$RelativePath) Get-Content -LiteralPath (Join-Path $repoRoot $RelativePath) -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-WorkflowDefinitions {
  param($Bundle)
  if ($Bundle.PSObject.Properties['workflows']) { return @($Bundle.workflows | ForEach-Object { $_.definition }) }
  return @($Bundle)
}
function Get-LivePowerNodes {
  param($Definition)
  return @($Definition.nodes | Where-Object {
      $_.data.activityType -eq 'powerManagement' -and
      -not ($_.data.PSObject.Properties['disabled'] -and $_.data.disabled)
    })
}

$installerAst = [Management.Automation.Language.Parser]::ParseFile(
  (Join-Path $PSScriptRoot 'Install-TestSuite.ps1'), [ref]$null, [ref]$null)
function Import-InstallerFunction {
  param([string]$Name)
  $function = $installerAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
  if ($null -eq $function) { throw "Install-TestSuite.ps1 has no function $Name." }
  return $function.Extent.Text
}

# --- power management stays out of the default continuous profile ---------------------
$manifest = Read-Json 'scripts\test-suite\suite-manifest.json'
$powerWorkflows = @()
foreach ($entry in @($manifest.workflows)) {
  $definitions = Get-WorkflowDefinitions (Read-Json (Join-Path 'scripts\test-suite' ($entry.file -replace '/', '\')))
  if (@($definitions | ForEach-Object { Get-LivePowerNodes $_ }).Count -gt 0) { $powerWorkflows += $entry }
}
Assert-Safety ($powerWorkflows.Count -eq 1) "Expected exactly one suite workflow with a live powerManagement node, found $($powerWorkflows.Count)."
foreach ($entry in $powerWorkflows) {
  Assert-Safety ($entry.profile -eq 'invasive') "$($entry.name) runs powerManagement outside the invasive profile."
  Assert-Safety (@($entry.requires) -contains 'globals:NP_TESTSUITE_INVASIVE') "$($entry.name) does not require the invasive opt-in."
  Assert-Safety (@($entry.requires) -contains 'globals:NP_TESTSUITE_DISPOSABLE_HOST') "$($entry.name) does not require a disposable host."
}

$profilesParam = $installerAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Profiles' }
$defaultProfiles = @(& ([scriptblock]::Create($profilesParam.DefaultValue.Extent.Text)))
Assert-Safety (($defaultProfiles -join ',') -eq 'continuous') "The installer's default profiles changed to '$($defaultProfiles -join ',')'."

# The installer enables a workflow only when its profile was requested and every prerequisite holds.
. ([scriptblock]::Create((Import-InstallerFunction 'Test-Prerequisites')))
function Get-SettingValue { param($Key) throw "Unexpected setting lookup: $Key" }
$script:presentGlobals = @('NP_TESTSUITE_INVASIVE')
function Test-GlobalPresent { param([string]$Name) return $script:presentGlobals -contains $Name }
Assert-Safety (-not (Test-Prerequisites -Requires $powerWorkflows[0].requires)) 'The invasive opt-in alone enabled powerManagement.'
$script:presentGlobals = @('NP_TESTSUITE_INVASIVE', 'NP_TESTSUITE_DISPOSABLE_HOST')
Assert-Safety (Test-Prerequisites -Requires $powerWorkflows[0].requires) 'A declared disposable host did not satisfy the powerManagement prerequisites.'

# Demos and legacy bundles that run on a schedule or are called every minute carry no live abort.
foreach ($path in @('scripts\tech-demo\planar.json', 'scripts\tech-demo\xl.json',
                    'scripts\test-runbooks\10-powerManagement.json', 'scripts\test-master-all-activities.json')) {
  foreach ($definition in (Get-WorkflowDefinitions (Read-Json $path))) {
    Assert-Safety (@(Get-LivePowerNodes $definition).Count -eq 0) "$path runs a live powerManagement node."
  }
}
$muster = Read-Json 'scripts\muster-einzeltests.json'
$musterPower = @($muster.workflows | Where-Object { $_.name -like 'Test * powerManagement' })
Assert-Safety ($musterPower.Count -eq 1 -and @(Get-LivePowerNodes $musterPower[0].definition).Count -eq 0) 'The muster powerManagement test runs a live node.'
$continuousTargets = @((Read-Json 'scripts\continuous-test-1min\continuous-test-1min.workflows.json').workflows |
  ForEach-Object { $_.definition.nodes } | Where-Object { $_.data.activityType -eq 'startWorkflow' } |
  ForEach-Object { $_.data.config.workflowNameOrId })
Assert-Safety ($continuousTargets.Count -eq 30) "Expected 30 continuous-test calls, found $($continuousTargets.Count)."
Assert-Safety (@($continuousTargets | Where-Object { $_ -like '*powerManagement*' }).Count -eq 0) 'The continuous run still calls a powerManagement workflow.'

# --- trigger acknowledgements write only <ack root>\<kind>\<canonical guid> -------------
$generator = @'
import sys,json
sys.path.insert(0,sys.argv[1])
import spec_triggers as t
flows={"webhook":t.webhook_trigger_workflow(),"filewatcher":t.file_watcher_trigger_workflow(),
       "database":t.database_trigger_workflow(),"eventlog":t.event_log_trigger_workflow()}
print(json.dumps({"ackDir":t.ACK_DIR,"scripts":{k:[s.config["script"] for s in w.steps if s.id=="ack"][0] for k,w in flows.items()}}))
'@
$generatedJson = $generator | & python - $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not generate the trigger acknowledgement scripts.' }
$generated = $generatedJson | ConvertFrom-Json

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("np-ack-" + [guid]::NewGuid().ToString('N'))
$ackRoot = Join-Path $sandbox 'acks'
$outsideFile = Join-Path $sandbox 'outside.txt'
function ConvertTo-QuotedLiteral { param([string]$Value) "'" + $Value.Replace("'", "''") + "'" }
function Invoke-AckScript {
  param([string]$Kind, [string]$Cid)
  $values = @{
    '{{manual.cid}}'                        = $Cid
    '{{trg.param.dbSentinel}}'              = $Cid
    '{{trg.param.fileNameWithoutExtension}}' = $Cid
    '{{trg.param.fileAction}}'              = 'Created'
    '{{trg.param.eventMessage}}'            = "NodePilot test suite probe cid=$Cid"
  }
  $body = ([string]$generated.scripts.$Kind).Replace("'$($generated.ackDir)'", (ConvertTo-QuotedLiteral $ackRoot))
  foreach ($template in $values.Keys) { $body = $body.Replace($template, (ConvertTo-QuotedLiteral $values[$template])) }
  Assert-Safety (-not $body.Contains('{{')) "The $Kind ack script kept an unresolved template."
  & ([scriptblock]::Create($body + "`n" + '$ackedCid'))
}

New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
  Set-Content -LiteralPath $outsideFile -Value 'unchanged'
  foreach ($kind in @('webhook', 'filewatcher', 'database', 'eventlog')) {
    $cid = [guid]::NewGuid().ToString('D')
    $acked = Invoke-AckScript -Kind $kind -Cid $cid.ToUpperInvariant()
    Assert-Safety ($acked -eq $cid) "The $kind ack did not report the canonical correlation id."
    $written = @(Get-ChildItem -LiteralPath (Join-Path $ackRoot $kind) -File)
    Assert-Safety ($written.Count -eq 1 -and $written[0].Name -ceq $cid) "The $kind ack wrote '$($written.Name -join ',')' instead of its canonical id."
  }

  $guid = [guid]::NewGuid().ToString('D')
  $rejected = @('..\outside.txt', '..\..\outside.txt', $outsideFile, "$guid\..\..\outside.txt", "{$guid}", 'not-a-guid', ' ')
  foreach ($kind in @('webhook', 'database')) {
    foreach ($cid in $rejected) {
      $before = @(Get-ChildItem -LiteralPath $sandbox -Recurse -File).Count
      $failed = $false
      try { $null = Invoke-AckScript -Kind $kind -Cid $cid } catch { $failed = $true }
      Assert-Safety $failed "The $kind ack accepted correlation id '$cid'."
      Assert-Safety (@(Get-ChildItem -LiteralPath $sandbox -Recurse -File).Count -eq $before) "The $kind ack wrote a file for rejected id '$cid'."
    }
  }
  Assert-Safety ((Get-Content -LiteralPath $outsideFile -Raw).Trim() -eq 'unchanged') 'A rejected acknowledgement overwrote a file outside its directory.'
} finally {
  Remove-Item -LiteralPath $sandbox -Recurse -Force
}

# --- the webhook secret is a secret global and rotates on every install -----------------
. ([scriptblock]::Create((Import-InstallerFunction 'Set-Global')))
$secretStart = $installerAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$bytes' }, $true)
$secretEnd = $installerAst.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text -like '*$webhookSecret -notmatch*' }, $true)
$secretBlock = $installerAst.Extent.Text.Substring($secretStart.Extent.StartOffset, $secretEnd.Extent.EndOffset - $secretStart.Extent.StartOffset)
$script:calls = @()
function Invoke-NodePilotJson {
  param($Method, $Path, $Body)
  $script:calls += [pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body }
  return [pscustomobject]@{ id = 'created'; name = $Body.name; value = '***'; isSecret = $Body.isSecret }
}
$secrets = @()
foreach ($existing in @($null,
    [pscustomobject]@{ id = 'secret'; name = 'NP_TESTSUITE_WEBHOOK_SECRET'; value = '***'; isSecret = $true },
    [pscustomobject]@{ id = 'plain'; name = 'NP_TESTSUITE_WEBHOOK_SECRET'; value = 'PlainValueFromAnEarlierInstall0123456789'; isSecret = $false })) {
  $globals = @($existing | Where-Object { $null -ne $_ })
  $script:calls = @()
  . ([scriptblock]::Create($secretBlock))
  $expected = if ($null -eq $existing) { 'POST /api/global-variables' } else { "PUT /api/global-variables/$($existing.id)" }
  Assert-Safety ($script:calls.Count -eq 1 -and "$($script:calls[0].Method) $($script:calls[0].Path)" -eq $expected) "Install wrote the webhook secret with '$($script:calls.Method) $($script:calls.Path)' instead of '$expected'."
  Assert-Safety ($script:calls[0].Body.isSecret -eq $true) 'The webhook secret was stored as a plain global.'
  Assert-Safety ($script:calls[0].Body.value -ceq $webhookSecret -and $webhookSecret -match '^[A-Za-z0-9]{32,}$') 'The stored secret differs from the injected one.'
  $secrets += $webhookSecret
}
Assert-Safety (@($secrets | Sort-Object -Unique).Count -eq 3) 'A reinstall reused a webhook secret instead of rotating it.'

Write-Host "TestSuite safety: $script:passed assertions passed." -ForegroundColor Green
