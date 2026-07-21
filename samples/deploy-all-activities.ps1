# Deploys the "all activities" showcase workflows (parent + child) into a running NodePilot API.
#
# Usage:
#   pwsh ./samples/deploy-all-activities.ps1                                  # prompts for credentials
#   pwsh ./samples/deploy-all-activities.ps1 -BaseUrl http://localhost:5000 -Username admin -Password 'pw'
#
# What it does:
#   1. POST /api/auth/login    -> JWT
#   2. POST /api/workflows     -> "Showcase Child"   (sub-workflow, must exist for startWorkflow)
#   3. POST /api/workflows     -> "Showcase All Activities"  (parent — uses scheduleTrigger as start)
#
# Notes:
#   - Re-running creates duplicates (no upsert).
#   - emailNotification will fail unless SMTP is configured (Smtp:Host etc.) — the
#     parent edges route past that failure via an explicit "On Failure" branch.

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$Username,
    [string]$Password
)

$ErrorActionPreference = "Stop"

if (-not $Username) { $Username = Read-Host "Username" }
if (-not $Password) {
    $sec = Read-Host "Password" -AsSecureString
    $Password = [System.Net.NetworkCredential]::new("", $sec).Password
}

$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$childPath  = Join-Path $here "all-activities-child.workflow.json"
$parentPath = Join-Path $here "all-activities-horizontal.workflow.json"

foreach ($p in @($childPath, $parentPath)) {
    if (-not (Test-Path $p)) { throw "Missing file: $p" }
}

Write-Host "==> Logging in to $BaseUrl as $Username" -ForegroundColor Cyan
$login = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/auth/login" `
    -ContentType "application/json" `
    -Body (@{ Username = $Username; Password = $Password } | ConvertTo-Json)

$token = $login.token
if (-not $token) { throw "Login response did not contain a token. Response: $($login | ConvertTo-Json -Depth 5)" }
$headers = @{ Authorization = "Bearer $token" }

function New-Workflow {
    param([string]$Name, [string]$Description, [string]$DefinitionPath)
    $definition = Get-Content -Raw -Path $DefinitionPath
    $body = @{
        Name           = $Name
        Description    = $Description
        DefinitionJson = $definition
    } | ConvertTo-Json -Depth 10
    Write-Host "==> Creating workflow '$Name'" -ForegroundColor Cyan
    $resp = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/workflows" `
        -Headers $headers -ContentType "application/json" -Body $body
    Write-Host "    id = $($resp.id)" -ForegroundColor Green
    return $resp
}

# Order matters semantically (parent calls child by name), but the engine only resolves
# the name at execution time, so technically both orders work. Create child first for clarity.
$child  = New-Workflow -Name "Showcase Child" `
                       -Description "Sub-workflow invoked by 'Showcase All Activities' via startWorkflow." `
                       -DefinitionPath $childPath

$parent = New-Workflow -Name "Showcase All Activities" `
                       -Description "Exercises every activity type, every edge condition (success/failed/always/disabled), and every junction mode (waitAll/waitAny/waitNofM). Started by scheduleTrigger." `
                       -DefinitionPath $parentPath

Write-Host ""
Write-Host "Done. Open the editor to inspect or run manually:" -ForegroundColor Green
Write-Host "  Parent: $BaseUrl/workflows/$($parent.id)"
Write-Host "  Child : $BaseUrl/workflows/$($child.id)"
