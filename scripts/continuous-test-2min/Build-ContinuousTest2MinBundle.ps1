[CmdletBinding()]
param(
  [string]$DefinitionFile = (Join-Path $PSScriptRoot 'continuous-test-2min.workflows.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dash = [char]0x2014
$targets = @(
  "Test $dash runScript",
  "Test $dash fileOperation",
  "Test $dash folderOperation",
  "Test $dash serviceManagement",
  "Test $dash registryOperation",
  "Test $dash wmiQuery",
  "Test $dash startProgram",
  "Test $dash restApi",
  "Test $dash sql",
  "Test $dash xmlQuery",
  "Test $dash jsonQuery",
  "Test $dash emailNotification",
  "Test $dash generateText",
  "Test $dash llmQuery",
  "Test $dash fileHash",
  "Test $dash zipOperation",
  "Test $dash textFileEdit",
  "Test $dash waitForCondition",
  "Test $dash scheduledTask",
  "Test $dash powerManagement",
  "Test $dash decision",
  "Test $dash delay",
  "Test $dash log",
  "Test $dash returnData",
  "Test $dash junction",
  "Test $dash forEach",
  "Test $dash startWorkflow",
  "Test $dash manualTrigger",
  "Muster $dash Variable-Pipe",
  "Muster $dash Sub-Workflow (Parent)"
)

$bundle = Get-Content -LiteralPath $DefinitionFile -Raw | ConvertFrom-Json
$parents = @($bundle.workflows | Where-Object { $_.name -like '[[]Dauertest 2m[]]*' } | Sort-Object name)
if ($parents.Count -ne 10 -or $targets.Count -ne 30) {
  throw 'Erwartet werden genau 10 Eltern-Workflows und 30 Ziel-Workflows.'
}

for ($parentIndex = 0; $parentIndex -lt $parents.Count; $parentIndex++) {
  $workflow = $parents[$parentIndex]
  $definition = $workflow.definition
  $definition.nodes = @($definition.nodes | Where-Object {
    $_.id -notin @('childToken', 'childParser') -and
    $_.id -notlike 'existingCall*' -and
    $_.id -ne 'durationHold'
  })
  $definition.edges = @($definition.edges | Where-Object {
    $_.source -notin @('childToken', 'childParser') -and
    $_.target -notin @('childToken', 'childParser') -and
    $_.source -notlike 'existingCall*' -and
    $_.target -notlike 'existingCall*' -and
    $_.source -ne 'durationHold' -and
    $_.target -ne 'durationHold' -and
    -not ($_.source -eq 'join' -and $_.target -eq 'summary')
  })

  $assignedTargets = @($targets[($parentIndex * 3)..(($parentIndex * 3) + 2)])
  for ($callIndex = 0; $callIndex -lt 3; $callIndex++) {
    $callNumber = $callIndex + 1
    $nodeId = "existingCall$callNumber"
    $targetName = $assignedTargets[$callIndex]
    $definition.nodes += [pscustomobject]@{
      id = $nodeId
      type = 'activity'
      position = [pscustomobject]@{ x = 1120; y = 380 + ($callIndex * 260) }
      data = [pscustomobject]@{
        label = "startWorkflow: $targetName"
        activityType = 'startWorkflow'
        outputVariable = $nodeId
        config = [pscustomobject]@{
          workflowNameOrId = $targetName
          waitForCompletion = $false
          parameters = [pscustomobject]@{}
        }
      }
    }
    $definition.edges += [pscustomobject]@{
      id = "e-call-$callNumber"
      source = 'work'
      target = $nodeId
      type = 'labeled'
      data = [pscustomobject]@{ label = 'On Success'; condition = 'work.success' }
    }
    $definition.edges += [pscustomobject]@{
      id = "e-call-$callNumber-join"
      source = $nodeId
      target = 'join'
      type = 'labeled'
      data = [pscustomobject]@{ label = 'On Success'; condition = "$nodeId.success" }
    }
  }

  $join = $definition.nodes | Where-Object id -eq 'join' | Select-Object -First 1
  # Junction is an Activity in the persisted canvas schema. Using "junction" as
  # the React Flow node type renders an unknown-node placeholder without handles,
  # so otherwise-valid incoming and outgoing edges disappear in the designer.
  $join.type = 'activity'
  $join.data.label = 'Junction: waitAll (4)'

  $definition.nodes += [pscustomobject]@{
    id = 'durationHold'
    type = 'activity'
    position = [pscustomobject]@{ x = 1880; y = 440 }
    data = [pscustomobject]@{
      label = 'delay: minimum runtime 20s'
      activityType = 'delay'
      outputVariable = 'durationHold'
      config = [pscustomobject]@{ seconds = 20 }
    }
  }
  $definition.edges += [pscustomobject]@{
    id = 'e-duration-hold'
    source = 'join'
    target = 'durationHold'
    type = 'labeled'
    data = [pscustomobject]@{ label = 'Always' }
  }
  $definition.edges += [pscustomobject]@{
    id = 'e-duration-summary'
    source = 'durationHold'
    target = 'summary'
    type = 'labeled'
    data = [pscustomobject]@{ label = 'On Success'; condition = 'durationHold.success' }
  }

  $summary = $definition.nodes | Where-Object id -eq 'summary' | Select-Object -First 1
  $summary.position.x = 2240
  $summary.data.config.message = "[Dauertest $($parentIndex + 1)] query={{query.param.count}} dispatched=3 targets=$($assignedTargets -join ' | ')"
  $return = $definition.nodes | Where-Object id -eq 'return' | Select-Object -First 1
  $return.position.x = 2600
  $return.data.config.data = [pscustomobject]@{
    scenario = $return.data.config.data.scenario
    queryMatches = '{{query.param.count}}'
    dispatchedJobs = '3'
    minimumDurationSeconds = '20'
    targets = $assignedTargets -join ' | '
  }
  $baseDescription = $workflow.description -replace ' Startet drei vorhandene Aktivitaets-Testworkflows.*$', ''
  $baseDescription = $baseDescription -replace ' Der Eltern-Workflow bleibt mindestens 20 Sekunden aktiv\.$', ''
  $workflow.description = ($baseDescription -replace ' sowie zwei synchronen Child-Calls\.$', '.') +
    " Startet drei vorhandene Aktivitaets-Testworkflows parallel (Fire-and-Forget): $($assignedTargets -join ', ')." +
    ' Der Eltern-Workflow bleibt mindestens 20 Sekunden aktiv.'
}

$bundle.workflows = $parents
$bundle.exportedAt = '2026-07-14T00:00:00Z'
$bundle | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $DefinitionFile -Encoding UTF8
Write-Host "Erzeugt: 10 Orchestratoren mit 30 Aufrufen bestehender Workflows in $DefinitionFile"
