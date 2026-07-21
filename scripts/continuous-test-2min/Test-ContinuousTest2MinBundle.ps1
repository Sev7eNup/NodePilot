[CmdletBinding()]
param(
  [string]$DefinitionFile = (Join-Path $PSScriptRoot 'continuous-test-2min.workflows.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bundle = Get-Content -LiteralPath $DefinitionFile -Raw | ConvertFrom-Json
$errors = [System.Collections.Generic.List[string]]::new()

if (@($bundle.workflows).Count -ne 10) {
  $errors.Add("Expected 10 Workflows, found $(@($bundle.workflows).Count).")
}

foreach ($workflow in @($bundle.workflows)) {
  $nodes = @($workflow.definition.nodes)
  $edges = @($workflow.definition.edges)
  $nodeIds = @($nodes | ForEach-Object { $_.id })
  $junctions = @($nodes | Where-Object { $_.data.activityType -eq 'junction' })
  $durationHolds = @($nodes | Where-Object {
    $_.id -eq 'durationHold' -and
    $_.data.activityType -eq 'delay' -and
    [int]$_.data.config.seconds -ge 20
  })

  if ($junctions.Count -ne 1) {
    $errors.Add("$($workflow.name): expected one Junction, found $($junctions.Count).")
  }

  foreach ($junction in $junctions) {
    if ($junction.type -ne 'activity') {
      $errors.Add("$($workflow.name): Junction '$($junction.id)' must use canvas node type 'activity', found '$($junction.type)'.")
    }
    $incoming = @($edges | Where-Object { $_.target -eq $junction.id })
    $outgoing = @($edges | Where-Object { $_.source -eq $junction.id })
    if ($incoming.Count -ne 4 -or $outgoing.Count -ne 1) {
      $errors.Add("$($workflow.name): Junction '$($junction.id)' needs 4 incoming and 1 outgoing edge, found $($incoming.Count)/$($outgoing.Count).")
    }
  }

  if ($durationHolds.Count -ne 1) {
    $errors.Add("$($workflow.name): expected one durationHold delay of at least 20 seconds.")
  }
  elseif (-not ($edges | Where-Object { $_.source -eq 'join' -and $_.target -eq 'durationHold' }) -or
          -not ($edges | Where-Object { $_.source -eq 'durationHold' -and $_.target -eq 'summary' })) {
    $errors.Add("$($workflow.name): durationHold must connect Junction to summary.")
  }

  foreach ($edge in $edges) {
    if ($edge.source -notin $nodeIds -or $edge.target -notin $nodeIds) {
      $errors.Add("$($workflow.name): edge '$($edge.id)' references a missing node.")
    }
  }
}

if ($errors.Count -gt 0) {
  throw ($errors -join [Environment]::NewLine)
}

Write-Host 'Continuous-test bundle structure is valid.'
