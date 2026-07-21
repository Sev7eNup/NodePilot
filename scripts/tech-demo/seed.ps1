<#
.SYNOPSIS
    Seeds the "NodePilot Tech-Demo -Full Showcase" (plus its child workflow) into a
    running NodePilot instance via POST /api/workflows. Does NOT use the import endpoint.

.DESCRIPTION
    Handles both first-time admin bootstrap (via X-Setup-Token header) and normal login.
    Reads main.json / child.json from this folder, embeds their contents as DefinitionJson
    string payloads in CreateWorkflowRequest, and POSTs to /api/workflows. Idempotent: if
    workflows with the same names already exist, prompts to keep/replace/abort (or pass
    -Force to replace without asking).

.EXAMPLE
    ./seed.ps1 -BaseUrl http://localhost:5000

.EXAMPLE
    ./seed.ps1 -BaseUrl http://localhost:5000 -AdminUser admin -AdminPassword 'Abcd1234!' -Force
#>
[CmdletBinding()]
param(
    [string]$BaseUrl       = "http://localhost:5000",
    [string]$AdminUser     = "admin",
    [string]$AdminPassword = "",
    [string]$ContentRoot   = "",
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$MainName   = "NodePilot Tech-Demo -Full Showcase"
$ChildName  = "NodePilot Tech-Demo -Child"
$XlName     = "NodePilot Tech-Demo XL"
$PlanarName = "NodePilot Tech-Demo - Planar Showcase"
$mainJsonPath   = Join-Path $PSScriptRoot 'main.json'
$childJsonPath  = Join-Path $PSScriptRoot 'child.json'
$xlJsonPath     = Join-Path $PSScriptRoot 'xl.json'
$planarJsonPath = Join-Path $PSScriptRoot 'planar.json'

if (-not (Test-Path $mainJsonPath))  { throw "main.json not found at $mainJsonPath" }
if (-not (Test-Path $childJsonPath)) { throw "child.json not found at $childJsonPath" }
$includeXl     = (Test-Path $xlJsonPath)
$includePlanar = (Test-Path $planarJsonPath)

function Invoke-JsonApi {
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Url,
        [object] $Body,
        [hashtable] $Headers = @{}
    )
    $params = @{
        Method      = $Method
        Uri         = $Url
        Headers     = $Headers
        ContentType = 'application/json; charset=utf-8'
    }
    if ($null -ne $Body) {
        $params.Body = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 20 -Compress }
    }
    try {
        return Invoke-RestMethod @params
    } catch {
        $errBody = $_.ErrorDetails.Message
        if (-not $errBody -and $_.Exception.Response) {
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                if ($stream) {
                    $reader = [System.IO.StreamReader]::new($stream)
                    $errBody = $reader.ReadToEnd()
                    $reader.Dispose()
                }
            } catch {}
        }
        Write-Host "HTTP $Method $Url failed: $($_.Exception.Message)" -ForegroundColor Red
        if ($errBody) { Write-Host "Response body: $errBody" -ForegroundColor DarkRed }
        throw
    }
}

function Resolve-SetupTokenPath {
    param([string] $ExplicitRoot)
    $candidates = @()
    if ($ExplicitRoot) { $candidates += (Join-Path $ExplicitRoot 'admin-setup.token') }
    $repoRoot = $null
    try { $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path } catch {}
    if ($repoRoot) {
        $candidates += (Join-Path $repoRoot 'src\NodePilot.Api\admin-setup.token')
        $candidates += (Join-Path $repoRoot 'admin-setup.token')
    }
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            return @{ Path = $c; Token = (Get-Content $c -Raw).Trim() }
        }
    }
    return $null
}

function Read-Password {
    param([string] $Prompt)
    $sec = Read-Host $Prompt -AsSecureString
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($ptr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# --- 1) Authenticate -----------------------------------------------------
$setup = Resolve-SetupTokenPath -ExplicitRoot $ContentRoot
$headers = @{}
$loginBody = $null

if ($setup) {
    Write-Host "Bootstrap mode: setup token found at $($setup.Path)" -ForegroundColor Yellow
    if (-not $AdminPassword) {
        $AdminPassword = Read-Password "Choose admin password (min 8 chars, creates '$AdminUser')"
    }
    if ($AdminPassword.Length -lt 8) { throw "Password must be at least 8 characters." }
    $headers['X-Setup-Token'] = $setup.Token
} else {
    if (-not $AdminPassword) {
        $AdminPassword = Read-Password "Password for '$AdminUser'"
    }
}

$loginBody = @{ username = $AdminUser; password = $AdminPassword } | ConvertTo-Json -Compress
Write-Host "Logging in at $BaseUrl/api/auth/login..."
$login = Invoke-JsonApi -Method POST -Url "$BaseUrl/api/auth/login" -Body $loginBody -Headers $headers
$token = $login.token
$authHeaders = @{ Authorization = "Bearer $token" }
Write-Host "Logged in as $($login.username) (role=$($login.role))" -ForegroundColor Green

if (@('Admin','Operator') -notcontains $login.role) {
    throw "Role '$($login.role)' cannot create workflows. Need Admin or Operator."
}

# --- 2) Check for existing workflows -------------------------------------
$existing = Invoke-JsonApi -Method GET -Url "$BaseUrl/api/workflows" -Headers $authHeaders
$existingMain   = $existing | Where-Object { $_.name -eq $MainName }  | Select-Object -First 1
$existingChild  = $existing | Where-Object { $_.name -eq $ChildName } | Select-Object -First 1
$existingXl     = if ($includeXl)     { $existing | Where-Object { $_.name -eq $XlName }     | Select-Object -First 1 } else { $null }
$existingPlanar = if ($includePlanar) { $existing | Where-Object { $_.name -eq $PlanarName } | Select-Object -First 1 } else { $null }

function Resolve-Conflict {
    param([string] $Name)
    if ($Force) { return 'r' }
    Write-Host "Workflow '$Name' already exists." -ForegroundColor Yellow
    $ans = (Read-Host "[k]eep existing, [r]eplace, [a]bort? (default k)").Trim().ToLower()
    if ([string]::IsNullOrEmpty($ans)) { return 'k' }
    return $ans[0]
}

# --- 3) Resolve conflicts ------------------------------------------------
if ($existingChild) {
    switch (Resolve-Conflict $ChildName) {
        'a' { throw "Aborted by user." }
        'r' {
            Invoke-JsonApi -Method DELETE -Url "$BaseUrl/api/workflows/$($existingChild.id)" -Headers $authHeaders | Out-Null
            Write-Host "Deleted child workflow $($existingChild.id)" -ForegroundColor DarkYellow
            $existingChild = $null
        }
    }
}
if ($existingMain) {
    switch (Resolve-Conflict $MainName) {
        'a' { throw "Aborted by user." }
        'r' {
            Invoke-JsonApi -Method DELETE -Url "$BaseUrl/api/workflows/$($existingMain.id)" -Headers $authHeaders | Out-Null
            Write-Host "Deleted main workflow $($existingMain.id)" -ForegroundColor DarkYellow
            $existingMain = $null
        }
    }
}
if ($existingXl) {
    switch (Resolve-Conflict $XlName) {
        'a' { throw "Aborted by user." }
        'r' {
            Invoke-JsonApi -Method DELETE -Url "$BaseUrl/api/workflows/$($existingXl.id)" -Headers $authHeaders | Out-Null
            Write-Host "Deleted XL workflow $($existingXl.id)" -ForegroundColor DarkYellow
            $existingXl = $null
        }
    }
}
if ($existingPlanar) {
    switch (Resolve-Conflict $PlanarName) {
        'a' { throw "Aborted by user." }
        'r' {
            Invoke-JsonApi -Method DELETE -Url "$BaseUrl/api/workflows/$($existingPlanar.id)" -Headers $authHeaders | Out-Null
            Write-Host "Deleted Planar workflow $($existingPlanar.id)" -ForegroundColor DarkYellow
            $existingPlanar = $null
        }
    }
}

# --- 4) POST child first (main references it by name) -------------------
$childDefString = Get-Content $childJsonPath -Raw -Encoding UTF8
if ($existingChild) {
    $childId = $existingChild.id
    Write-Host "Kept existing child: $childId" -ForegroundColor Cyan
} else {
    $childReq = @{
        name           = $ChildName
        description    = "Demo sub-workflow invoked by 'Full Showcase' via startWorkflow. Logs caller info and returns data."
        definitionJson = $childDefString
    }
    $childResp = Invoke-JsonApi -Method POST -Url "$BaseUrl/api/workflows" -Headers $authHeaders -Body $childReq
    $childId = $childResp.id
    Write-Host "Created child workflow: $childId" -ForegroundColor Green
}

# --- 5) POST main --------------------------------------------------------
$mainDefString = Get-Content $mainJsonPath -Raw -Encoding UTF8
if ($existingMain) {
    $mainId = $existingMain.id
    Write-Host "Kept existing main: $mainId" -ForegroundColor Cyan
} else {
    $mainReq = @{
        name           = $MainName
        description    = "Full tech-demo: 17 activities, 14 edge operators, AND/OR/NOT groups, all 3 junction modes, retry, step-timeouts, node-level disabled, breakpoint, disabled edge, sub-workflow."
        definitionJson = $mainDefString
    }
    $mainResp = Invoke-JsonApi -Method POST -Url "$BaseUrl/api/workflows" -Headers $authHeaders -Body $mainReq
    $mainId = $mainResp.id
    Write-Host "Created main workflow: $mainId" -ForegroundColor Green
}

# --- 5b) POST XL if xl.json is present -----------------------------------
$xlId = $null
if ($includeXl) {
    $xlDefString = Get-Content $xlJsonPath -Raw -Encoding UTF8
    if ($existingXl) {
        $xlId = $existingXl.id
        Write-Host "Kept existing XL: $xlId" -ForegroundColor Cyan
    } else {
        $xlReq = @{
            name           = $XlName
            description    = "XL showcase: 3 swim-lanes, ~114 nodes, ~190 edges. All 19 activities, all 14 edge operators, all 3 junction modes, breakpoints, disabled nodes, disabled edges, sub-workflow calls. See xl.README.md."
            definitionJson = $xlDefString
        }
        $xlResp = Invoke-JsonApi -Method POST -Url "$BaseUrl/api/workflows" -Headers $authHeaders -Body $xlReq
        $xlId = $xlResp.id
        Write-Host "Created XL workflow: $xlId" -ForegroundColor Green
    }
}

# --- 5c) POST Planar if planar.json is present ---------------------------
$planarId = $null
if ($includePlanar) {
    $planarDefString = Get-Content $planarJsonPath -Raw -Encoding UTF8
    if ($existingPlanar) {
        $planarId = $existingPlanar.id
        Write-Host "Kept existing Planar: $planarId" -ForegroundColor Cyan
    } else {
        $planarReq = @{
            name           = $PlanarName
            description    = "Planar showcase: 50 nodes, 70 edges, strictly crossing-free layout (every edge spans exactly one x-column). Covers 19 activity types, all 14 edge operators, all 3 junction modes, retry, breakpoints, disabled node, disabled edge, forEach, fire-and-forget sub-workflow."
            definitionJson = $planarDefString
        }
        $planarResp = Invoke-JsonApi -Method POST -Url "$BaseUrl/api/workflows" -Headers $authHeaders -Body $planarReq
        $planarId = $planarResp.id
        Write-Host "Created Planar workflow: $planarId" -ForegroundColor Green
    }
}

# --- 6) Summary ----------------------------------------------------------
Write-Host ""
Write-Host "=== Tech-Demo seeded ===" -ForegroundColor Green
Write-Host "  Child:  $childId  ($ChildName)"
Write-Host "  Main:   $mainId   ($MainName)"
if ($xlId)     { Write-Host "  XL:     $xlId   ($XlName)" }
if ($planarId) { Write-Host "  Planar: $planarId   ($PlanarName)" }
Write-Host ""
$uiUrl = if ($BaseUrl -match 'localhost:5000') {
    "http://localhost:5173/workflows/$mainId"
} else {
    "$BaseUrl/workflows/$mainId"
}
Write-Host "Open in UI:" -ForegroundColor Cyan
Write-Host "  $uiUrl"
if ($xlId) {
    $xlUi = if ($BaseUrl -match 'localhost:5000') { "http://localhost:5173/workflows/$xlId" } else { "$BaseUrl/workflows/$xlId" }
    Write-Host "  $xlUi  (XL)"
}
if ($planarId) {
    $planarUi = if ($BaseUrl -match 'localhost:5000') { "http://localhost:5173/workflows/$planarId" } else { "$BaseUrl/workflows/$planarId" }
    Write-Host "  $planarUi  (Planar)"
}
Write-Host ""
Write-Host "Run from UI: click 'Run', accept defaults" -NoNewline
Write-Host " (env=staging, threshold=80, dryRun=false, pattern=Windows.*)." -ForegroundColor DarkGray
