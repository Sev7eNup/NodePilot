<#
.SYNOPSIS
  Installs the NodePilot test suite into a running instance, idempotently.

.DESCRIPTION
  Reads suite-manifest.json and publishes every workflow it lists into the shared folder
  resolved by path (default /Test_Workflows).

  Sequence notes that are easy to get wrong:
    * A workflow created through POST /api/workflows is already checked out by its
      creator, so a following /lock returns 409. New -> POST + publish; existing ->
      lock + publish.
    * The child workflow must exist and be enabled before its parents are published:
      forEach and startWorkflow resolve it by name and fail on a disabled child.
    * MaxConcurrentExecutions is not part of the publish body; it has its own endpoint.
    * Workflows whose profile is not requested are still installed, just left disabled,
      so they stay visible instead of silently missing.

.PARAMETER Profiles
  Which profiles to enable. Anything outside this list is installed disabled.

.PARAMETER RemoveLegacy
  Deletes the superseded Test/Muster/Dauertest workflows. Never automatic.

.PARAMETER ProbeUrl
  Target of the waitForCondition httpOk probe (global NP_TESTSUITE_PROBE_URL). Defaults to the
  instance's own /healthz/live, which works on a development host. An installed instance cannot
  probe itself by its own name, so there it has to point at another host.
#>
[CmdletBinding()]
param(
  [string]$BaseUrl = 'http://localhost:5000',
  [string]$User = 'admin',
  [Parameter(Mandatory)][string]$Password,
  [string]$FolderPath = '/Test_Workflows',
  [ValidateSet('continuous', 'integration', 'invasive')]
  [string[]]$Profiles = @('continuous'),
  [switch]$ForceUnlock,
  [switch]$RemoveLegacy,
  [string]$ProbeUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Headers = @{}

function Invoke-NodePilotJson {
  param(
    [Parameter(Mandatory)][string]$Method,
    [Parameter(Mandatory)][string]$Path,
    [object]$Body
  )
  $params = @{
    Method      = $Method
    Uri         = "$BaseUrl$Path"
    Headers     = $script:Headers
    ContentType = 'application/json; charset=utf-8'
  }
  if ($PSBoundParameters.ContainsKey('Body')) {
    # Windows PowerShell 5.1 sends JSON strings through its ANSI code page, which
    # corrupts non-ASCII workflow names. Send explicit UTF-8 bytes instead.
    $json = $Body | ConvertTo-Json -Depth 100 -Compress
    $params.Body = [Text.Encoding]::UTF8.GetBytes($json)
  }
  Invoke-RestMethod @params
}

function Get-WorkflowList {
  $response = Invoke-NodePilotJson -Method GET -Path '/api/workflows'
  if ($null -ne $response.PSObject.Properties['items']) { return @($response.items) }
  return @($response)
}

# --- login -------------------------------------------------------------------------
# Without this opt-in header the API returns the JWT only as an httpOnly cookie, and a
# cookie session would drag CSRF handling into every mutating call.
$login = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/auth/login" `
  -ContentType 'application/json; charset=utf-8' `
  -Headers @{ 'X-Auth-Token-Response' = 'true' } `
  -Body ([Text.Encoding]::UTF8.GetBytes((@{ username = $User; password = $Password } | ConvertTo-Json -Compress)))
if ([string]::IsNullOrWhiteSpace($login.token)) {
  throw 'Login succeeded but returned no bearer token.'
}
$script:Headers = @{ Authorization = "Bearer $($login.token)" }

# --- manifest ----------------------------------------------------------------------
$manifestPath = Join-Path $PSScriptRoot 'suite-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
  throw "suite-manifest.json not found. Run: python scripts/test-suite/build_suite.py"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "Manifest: $(@($manifest.workflows).Count) workflows, $(@($manifest.cases).Count) cases"

# --- target folder -----------------------------------------------------------------
$folders = Invoke-NodePilotJson -Method GET -Path '/api/shared-workflow-folders'
$folder = $folders | Where-Object { $_.path -eq $FolderPath } | Select-Object -First 1
if ($null -eq $folder) {
  $leaf = $FolderPath.TrimStart('/')
  if ($leaf.Contains('/')) { throw "Only a top-level target folder is supported, got '$FolderPath'." }
  $root = $folders | Where-Object { $_.depth -eq 0 } | Select-Object -First 1
  $folder = Invoke-NodePilotJson -Method POST -Path '/api/shared-workflow-folders' `
    -Body @{ name = $leaf; parentFolderId = $root.id }
  Write-Host "Created folder $FolderPath"
}
$folderId = $folder.id

# --- collision check before any mutation -------------------------------------------
# @() at the assignment, not just inside the function: PowerShell unrolls a returned
# collection, so an empty instance would make $existing a scalar and the later `+=` fail.
$existing = @(Get-WorkflowList)
$suiteNames = @($manifest.workflows | ForEach-Object { $_.name })
$ambiguous = @($existing | Where-Object { $_.name -in $suiteNames } |
  Group-Object -Property name | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
if ($ambiguous.Count -gt 0) {
  throw "Ambiguous workflow names already present, resolve them first: $($ambiguous -join ', ')"
}

# --- global variables the suite reads ------------------------------------------------
# Assign first, wrap second. Invoke-RestMethod hands a JSON array over as a single
# object, so @(Invoke-...) collects one element that is itself the array - and every
# later .name / .value access then silently enumerates the whole collection.
$globalsResponse = Invoke-NodePilotJson -Method GET -Path '/api/global-variables'
$globals = @($globalsResponse)
function Test-GlobalPresent { param([string]$Name) return @($globals | Where-Object { $_.name -eq $Name }).Count -gt 0 }

# The instance's own health URL, used by the restApi cases. An installed instance listens on
# HTTPS on a port chosen at setup, so seed a sensible dev default and leave any existing value
# alone. The invasive switches are deliberately NOT seeded - they are the opt-in a host owner
# has to make explicitly.
if (-not (Test-GlobalPresent -Name 'NP_TESTSUITE_SELF_URL')) {
  $created = Invoke-NodePilotJson -Method POST -Path '/api/global-variables' -Body @{
    name        = 'NP_TESTSUITE_SELF_URL'
    value       = "$BaseUrl/healthz/live"
    isSecret    = $false
    description = 'TestSuite: the instance''s own health URL, used by the restApi cases.'
  }
  $globals += $created
  Write-Host "seeded global      : NP_TESTSUITE_SELF_URL = $BaseUrl/healthz/live"
}
# An authenticated, method-constrained route. The negative contract uses it to prove a
# write verb really left the client: GET answers 401 while PUT/PATCH/DELETE fall through
# to the /api catch-all and answer 404.
if (-not (Test-GlobalPresent -Name 'NP_TESTSUITE_API_URL')) {
  $created = Invoke-NodePilotJson -Method POST -Path '/api/global-variables' -Body @{
    name        = 'NP_TESTSUITE_API_URL'
    value       = "$BaseUrl/api/workflows"
    isSecret    = $false
    description = 'TestSuite: authenticated route used by the negative restApi cases.'
  }
  $globals += $created
  Write-Host "seeded global      : NP_TESTSUITE_API_URL = $BaseUrl/api/workflows"
}

function Get-GlobalValue {
  param([string]$Name)
  # Deliberately not a pipeline: accessing .value on an accidental collection silently
  # returns every global's value joined together, which is how an unrelated variable's
  # contents could otherwise end up injected into a workflow file.
  foreach ($g in $globals) {
    if ($g.name -eq $Name) { return [string]$g.value }
  }
  return $null
}

function Set-Global {
  param([string]$Name, [string]$Value, [string]$Description)
  $existing = $null
  foreach ($g in $globals) { if ($g.name -eq $Name) { $existing = $g; break } }
  $body = @{ name = $Name; value = $Value; isSecret = $false; description = $Description }
  if ($null -eq $existing) {
    $script:globals += Invoke-NodePilotJson -Method POST -Path '/api/global-variables' -Body $body
    Write-Host "seeded global      : $Name"
  }
  elseif ($existing.value -ne $Value) {
    $null = Invoke-NodePilotJson -Method PUT -Path "/api/global-variables/$($existing.id)" -Body $body
    $existing.value = $Value
    Write-Host "updated global     : $Name"
  }
}

# The httpOk probe resolves its host and refuses link-local addresses, and Windows resolves a
# machine's own name to those as well. An installed instance therefore probes another host.
if ($ProbeUrl) {
  Set-Global -Name 'NP_TESTSUITE_PROBE_URL' -Value $ProbeUrl -Description 'TestSuite: URL the waitForCondition httpOk probe targets.'
}
elseif (-not (Test-GlobalPresent -Name 'NP_TESTSUITE_PROBE_URL')) {
  Set-Global -Name 'NP_TESTSUITE_PROBE_URL' -Value "$BaseUrl/healthz/live" -Description 'TestSuite: URL the waitForCondition httpOk probe targets.'
}

# The webhook trigger needs a shared secret, and a secret has no business sitting in a
# committed workflow file. It is generated once here and injected into the definition at
# install time; the driver reads the same value back through {{globals....}}.
$webhookSecret = Get-GlobalValue -Name 'NP_TESTSUITE_WEBHOOK_SECRET'
if ([string]::IsNullOrWhiteSpace($webhookSecret)) {
  $bytes = [byte[]]::new(36)
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $webhookSecret = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', '')
  Set-Global -Name 'NP_TESTSUITE_WEBHOOK_SECRET' -Value $webhookSecret `
    -Description 'TestSuite: shared secret between the webhook trigger and its driver.'
}
# Last line of defence before the value is written into a workflow definition.
if ($webhookSecret -notmatch '^[A-Za-z0-9]{32,}$') {
  throw "NP_TESTSUITE_WEBHOOK_SECRET does not look like a single opaque token; refusing to inject it."
}

$script:settingsSections = $null

# Reads one effective config value through the admin settings API. The response is a list of
# { sectionPath, payload, ... }; the payload serialises its keys in camelCase. Returns $null
# when the section or the property does not exist.
function Get-SettingValue {
  param([Parameter(Mandatory)][string]$Key)
  if ($null -eq $script:settingsSections) {
    $response = Invoke-NodePilotJson -Method GET -Path '/api/admin/settings'
    # PowerShell can hand a JSON array back as one nested element. `+=` concatenates an array's
    # items, so this loop flattens that case and leaves a already-flat response untouched.
    $flat = @()
    foreach ($item in $response) { $flat += $item }
    $script:settingsSections = $flat
  }
  $section = $null
  $rest = $null
  foreach ($s in $script:settingsSections) {
    $prefix = $s.sectionPath + ':'
    if (-not $Key.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    # Longest matching sectionPath wins, so a nested section beats its parent.
    if ($null -eq $section -or $s.sectionPath.Length -gt $section.sectionPath.Length) {
      $section = $s
      $rest = $Key.Substring($prefix.Length)
    }
  }
  if ($null -eq $section) { return $null }
  $node = $section.payload
  foreach ($part in $rest.Split(':')) {
    if ($null -eq $node) { return $null }
    $name = $part.Substring(0, 1).ToLowerInvariant() + $part.Substring(1)
    $prop = $node.PSObject.Properties[$name]
    if ($null -eq $prop) { return $null }
    $node = $prop.Value
  }
  return $node
}

function Test-Prerequisites {
  param([string[]]$Requires)
  foreach ($req in @($Requires)) {
    if ([string]::IsNullOrWhiteSpace($req)) { continue }
    if ($req.StartsWith('globals:')) {
      $name = ($req.Substring(8) -split '=')[0]
      if (-not (Test-GlobalPresent -Name $name)) { return $false }
      continue
    }
    # Only `config:<Key>=<scalar>` is machine-checkable. Entries that name a key without a
    # value, or add prose after it, describe a shape the installer cannot verify and stay
    # assumptions - otherwise every such workflow would ship disabled for no reason.
    if ($req.StartsWith('config:')) {
      $spec = $req.Substring(7)
      $eq = $spec.IndexOf('=')
      if ($eq -lt 0) { continue }
      $key = $spec.Substring(0, $eq).Trim()
      $want = $spec.Substring($eq + 1).Trim()
      if ($key -match '\s' -or $want -match '\s') { continue }
      $actual = Get-SettingValue -Key $key
      if ($null -eq $actual) { return $false }
      # PowerShell renders booleans as True/False while the manifest writes true/false.
      if (-not [string]::Equals([string]$actual, $want, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
      }
      continue
    }
    # The event-log workflows need a source that only an elevated one-time step can create.
    if ($req -match '^event source (\S+) registered') {
      $source = $Matches[1]
      try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($source)) { return $false }
      }
      catch {
        # Unreadable is not proof of presence; leave the workflow disabled.
        return $false
      }
      continue
    }
  }
  return $true
}

# --- custom activity ------------------------------------------------------------------
# Must exist AND be enabled before the workflow that references it is published: a node
# pointing at a disabled definition fails outright. Created definitions start disabled,
# so the enable is a separate, Admin-only call.
$customDefinitionId = $null
$customPath = Join-Path $PSScriptRoot 'custom-activity-definition.json'
if (Test-Path -LiteralPath $customPath) {
  $customDef = Get-Content -LiteralPath $customPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $listed = Invoke-NodePilotJson -Method GET -Path '/api/custom-activities?includeDisabled=true'
  $existingCustom = $null
  foreach ($ca in @($listed)) { if ($ca.key -eq $customDef.key) { $existingCustom = $ca; break } }

  if ($null -eq $existingCustom) {
    $saved = Invoke-NodePilotJson -Method POST -Path '/api/custom-activities' -Body $customDef
    $customDefinitionId = $saved.definition.id
    Write-Host "created custom act.: $($customDef.key)"
  }
  else {
    $customDefinitionId = $existingCustom.id
    # Keep the installed definition in step with the generated one. Update carries the
    # concurrency token, and an enabled definition is Admin-only to change.
    $current = Invoke-NodePilotJson -Method GET -Path "/api/custom-activities/$customDefinitionId"
    if ($current.scriptTemplate -ne $customDef.scriptTemplate) {
      $update = @{}
      foreach ($prop in $customDef.PSObject.Properties) {
        if ($prop.Name -ne 'key') { $update[$prop.Name] = $prop.Value }
      }
      $update['concurrencyToken'] = $current.concurrencyToken
      $update['changeNote'] = 'Regenerated by Install-TestSuite.ps1'
      $null = Invoke-NodePilotJson -Method PUT -Path "/api/custom-activities/$customDefinitionId" -Body $update
      Write-Host "updated custom act.: $($customDef.key)"
    }
  }

  $detail = Invoke-NodePilotJson -Method GET -Path "/api/custom-activities/$customDefinitionId"
  if (-not $detail.isEnabled) {
    $null = Invoke-NodePilotJson -Method POST -Path "/api/custom-activities/$customDefinitionId/enable"
    Write-Host "enabled custom act.: $($customDef.key)"
  }
}

# --- install -------------------------------------------------------------------------
# Manifest order is generation order, and the child workflow is number 0, so parents are
# never published before the child they resolve by name.
foreach ($entry in @($manifest.workflows)) {
  $file = Join-Path $PSScriptRoot ($entry.file -replace '/', '\')
  if (-not (Test-Path -LiteralPath $file)) { throw "Missing workflow file: $file" }
  $raw = Get-Content -LiteralPath $file -Raw -Encoding UTF8
  # The webhook secret is never committed. The placeholder in the generated JSON is
  # replaced with the value of the global, which is created once from a CSPRNG.
  if ($raw.Contains('__TESTSUITE_WEBHOOK_SECRET__')) {
    $raw = $raw.Replace('__TESTSUITE_WEBHOOK_SECRET__', $webhookSecret)
  }
  if ($raw.Contains('__TESTSUITE_CUSTOM_DEFINITION_ID__')) {
    if ([string]::IsNullOrWhiteSpace($customDefinitionId)) {
      throw "A workflow references the custom activity but its definition was not installed."
    }
    $raw = $raw.Replace('__TESTSUITE_CUSTOM_DEFINITION_ID__', [string]$customDefinitionId)
  }
  $bundle = $raw | ConvertFrom-Json
  $wf = $bundle.workflows[0]

  $wantEnabled = ($entry.profile -in $Profiles) -and (Test-Prerequisites -Requires $entry.requires)
  $definitionJson = $wf.definition | ConvertTo-Json -Depth 100 -Compress
  $body = @{
    name           = $wf.name
    description    = $wf.description
    definitionJson = $definitionJson
    folderId       = $folderId
  }

  $current = $existing | Where-Object { $_.name -eq $wf.name } | Select-Object -First 1
  if ($null -eq $current) {
    $current = Invoke-NodePilotJson -Method POST -Path '/api/workflows' -Body $body
    $existing += $current
    if ($wantEnabled) {
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/publish" -Body $body
      Write-Host "created + published : $($wf.name)"
    }
    else {
      # POST leaves the creator holding the edit lock; release it so the parked workflow
      # is not stuck behind a lock nobody expects.
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/unlock"
      Write-Host "created (disabled)  : $($wf.name)  [profile $($entry.profile) not requested or prerequisite missing]"
    }
  }
  else {
    # The login response is flat: { token, userId, username, role, expiresAt }.
    $lockedByMe = $current.checkedOutByUserId -and $current.checkedOutByUserId -eq $login.userId
    if ($current.checkedOutByUserId -and -not $lockedByMe) {
      if (-not $ForceUnlock) {
        throw "'$($wf.name)' is checked out by $($current.checkedOutByUserName). Re-run with -ForceUnlock to take it over."
      }
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/force-unlock"
      $lockedByMe = $false
    }
    # /lock answers 409 when the workflow is already checked out, including by this caller -
    # what an interrupted earlier run leaves behind. Publish only needs the lock, not a new one.
    if (-not $lockedByMe) {
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/lock"
    }
    if ($wantEnabled) {
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/publish" -Body $body
      Write-Host "updated + published : $($wf.name)"
    }
    else {
      $null = Invoke-NodePilotJson -Method PUT -Path "/api/workflows/$($current.id)" -Body $body
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/disable"
      $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($current.id)/unlock"
      Write-Host "updated (disabled)  : $($wf.name)"
    }
  }

  # Serialising each suite workflow is what keeps two overlapping runs from colliding on
  # the same sandbox. It is not part of the publish body, so it needs its own call.
  $null = Invoke-NodePilotJson -Method PUT -Path "/api/workflows/$($current.id)/concurrency-limit" `
    -Body @{ maxConcurrentExecutions = 1 }
}

# --- webhook endpoint --------------------------------------------------------------
# Addressed by workflow id rather than name: the route accepts either, and a name like
# "[TestSuite] trigger: webhook" would have to survive URL encoding on every hop.
$hookWorkflow = $existing | Where-Object { $_.name -eq '[TestSuite] trigger: webhook' } | Select-Object -First 1
if ($null -ne $hookWorkflow) {
  Set-Global -Name 'NP_TESTSUITE_WEBHOOK_URL' `
    -Value "$BaseUrl/api/webhooks/$($hookWorkflow.id)/suite" `
    -Description 'TestSuite: endpoint the trigger driver posts to.'
}

# --- legacy removal (never automatic) ------------------------------------------------
if ($RemoveLegacy) {
  $legacyPatterns = @('^Test — ', '^\[Dauertest 1m\] ', '^Dauertest — ', '^Muster — ', '^Muster Test: ')
  $legacy = @(Get-WorkflowList | Where-Object {
      $name = $_.name
      @($legacyPatterns | Where-Object { $name -match $_ }).Count -gt 0
    })
  foreach ($old in $legacy) {
    $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($old.id)/cancel-all"
    $null = Invoke-NodePilotJson -Method POST -Path "/api/workflows/$($old.id)/disable"
    $null = Invoke-NodePilotJson -Method DELETE -Path "/api/workflows/$($old.id)"
    Write-Host "removed legacy      : $($old.name)"
  }
  Write-Host "Removed $($legacy.Count) superseded workflows."
}

Write-Host ''
Write-Host "Done. Folder $FolderPath, profiles enabled: $($Profiles -join ', ')"
