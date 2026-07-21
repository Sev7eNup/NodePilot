[CmdletBinding()]
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$User = 'admin',
  [Parameter(Mandatory)]
  [string]$Password,
  [string]$DefinitionFile = (Join-Path $PSScriptRoot 'continuous-test-2min.workflows.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NodePilotJson {
  param(
    [Parameter(Mandatory)][string]$Method,
    [Parameter(Mandatory)][string]$Path,
    [object]$Body,
    [hashtable]$Headers = @{}
  )

  $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $Headers; ContentType = 'application/json; charset=utf-8' }
  if ($PSBoundParameters.ContainsKey('Body')) {
    # Windows PowerShell 5.1 otherwise sends JSON strings through its ANSI code page.
    # That silently changes names such as "Test <em dash> runScript" and breaks
    # startWorkflow name resolution after an apparently successful publish.
    $json = $Body | ConvertTo-Json -Depth 100 -Compress
    $params.Body = [Text.Encoding]::UTF8.GetBytes($json)
  }
  Invoke-RestMethod @params
}

if (-not (Test-Path -LiteralPath $DefinitionFile)) {
  throw "Workflow-Datei nicht gefunden: $DefinitionFile"
}

# The opt-in header is required because the API otherwise keeps the JWT in an httpOnly cookie.
$login = Invoke-NodePilotJson -Method POST -Path '/api/auth/login' -Body @{ username = $User; password = $Password } -Headers @{ 'X-Auth-Token-Response' = 'true' }
if ([string]::IsNullOrWhiteSpace($login.token)) {
  throw 'Login war erfolgreich, hat aber kein Bearer-Token geliefert.'
}
$headers = @{ Authorization = "Bearer $($login.token)" }

$bundle = Get-Content -LiteralPath $DefinitionFile -Raw | ConvertFrom-Json
if ($bundle.schema -ne 'nodepilot-workflow-export/v1' -or @($bundle.workflows).Count -ne 10) {
  throw 'Unerwartetes Workflow-Paket: erwartet werden genau 10 Dauertest-Orchestratoren.'
}

$existingResponse = Invoke-NodePilotJson -Method GET -Path '/api/workflows' -Headers $headers
$existing = if ($null -ne $existingResponse.PSObject.Properties['items']) {
  @($existingResponse.items)
}
else {
  @($existingResponse)
}

$bundleNames = @($bundle.workflows | ForEach-Object { $_.name })
$requiredTargets = @($bundle.workflows | ForEach-Object {
  $_.definition.nodes |
    Where-Object { $_.data.activityType -eq 'startWorkflow' } |
    ForEach-Object { $_.data.config.workflowNameOrId }
} | Where-Object { $_ -notin $bundleNames } | Sort-Object -Unique)
$missingTargets = @($requiredTargets | Where-Object { $_ -notin $existing.name })
if ($missingTargets.Count -gt 0) {
  throw "Diese vorausgesetzten Aktivitaets-Testworkflows fehlen: $($missingTargets -join ', '). Importiere zuerst scripts/muster-einzeltests.json und scripts/muster-kombinationen.json."
}

foreach ($workflow in @($bundle.workflows)) {
  $definitionJson = $workflow.definition | ConvertTo-Json -Depth 100 -Compress
  $body = @{ name = $workflow.name; description = $workflow.description; definitionJson = $definitionJson }
  $current = $existing | Where-Object { $_.name -eq $workflow.name } | Select-Object -First 1

  if ($null -eq $current) {
    $current = Invoke-NodePilotJson -Method POST -Path '/api/workflows' -Body $body -Headers $headers
    Write-Host "Erstellt: $($workflow.name)"
    $existing += $current
    if ($workflow.isEnabled -eq $true) {
      $current = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/publish" -Body $body -Headers $headers
      Write-Host "Veroeffentlicht und aktiviert: $($workflow.name)"
    }
  }
  else {
    # Existing workflows require an edit-lock. Publish saves the new definition, enables it,
    # and releases the lock atomically, so a re-run does not leave a workflow half-updated.
    Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/lock" -Headers $headers | Out-Null
    $current = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/publish" -Body $body -Headers $headers
    Write-Host "Aktualisiert und aktiviert: $($workflow.name)"
  }
}

Write-Host 'Fertig: 10 Orchestratoren sind sichtbar und aktiv. Alle zwei Minuten starten sie zusammen 30 vorhandene Aktivitaets-Testworkflows.'
