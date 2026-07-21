<#
.SYNOPSIS
    Kill -> Build -> Test -> Start  (NodePilot dev reset)
    Kills all running backend/frontend processes, builds both, runs all tests,
    then starts backend (http://localhost:5000) and frontend (http://localhost:5173).

.PARAMETER SkipTests
    Skip all test runs and go straight to start.

.PARAMETER SkipBuild
    Skip the build step (still runs tests and restarts processes).
#>
param(
    [switch]$SkipTests,
    [switch]$SkipBuild
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$apiDir  = Join-Path $root "src\NodePilot.Api"
$uiDir   = Join-Path $root "src\nodepilot-ui"
$logDir  = Join-Path $root ".dev-logs"

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

$ts = Get-Date -Format "yyyyMMdd-HHmmss"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ">>> $msg" -ForegroundColor Cyan
}
function Write-Ok([string]$msg)   { Write-Host "    [OK]   $msg" -ForegroundColor Green  }
function Write-Info([string]$msg) { Write-Host "    [INFO] $msg" -ForegroundColor DarkGray }
function Write-Fail([string]$msg) { Write-Host "    [FAIL] $msg" -ForegroundColor Red    }

function Stop-Port([int]$port) {
    $lines = netstat -ano | Where-Object { $_ -match "[:.]$port\s" }
    $killed = $false
    foreach ($line in $lines) {
        if ($line -match '\s+(\d+)\s*$') {
            $procId = [int]$Matches[1]
            if ($procId -gt 4) {
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                Write-Info "  Stopped PID $procId (port $port)"
                $killed = $true
            }
        }
    }
    if (-not $killed) { Write-Info "  Nothing on port $port" }
}

function Invoke-Checked([string]$desc, [scriptblock]$action) {
    Write-Step $desc
    $prevLocation = Get-Location
    try {
        & $action
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            throw "Exit code $LASTEXITCODE"
        }
        Write-Ok $desc
    } catch {
        Set-Location $prevLocation
        Write-Fail "$desc -- $_"
        Write-Host ""
        Write-Host "Aborting. Fix the error above and re-run dev-reset.ps1." -ForegroundColor Red
        exit 1
    } finally {
        Set-Location $prevLocation
    }
}

# ---------------------------------------------------------------------------
# 1. Kill existing processes
# ---------------------------------------------------------------------------

Write-Step "Killing existing processes"

Stop-Port 5000
Stop-Port 5173

# Kill leftover Vite / node processes (frontend dev server)
Get-Process -Name "node" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

# Brief pause so OS releases port bindings
Start-Sleep -Seconds 2
Write-Ok "Ports 5000 + 5173 cleared"

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------

if (-not $SkipBuild) {

    Invoke-Checked "Backend build (dotnet build)" {
        Set-Location $root
        $log = Join-Path $logDir "build-backend-$ts.log"
        dotnet build --no-incremental -c Debug | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed -- see $log" }
    }

    Invoke-Checked "Frontend dependencies check" {
        Set-Location $uiDir
        $viteBin = Join-Path $uiDir "node_modules\.bin\vite"
        if (-not (Test-Path $viteBin)) {
            Write-Info "node_modules missing or broken -- running npm install"
            $log = Join-Path $logDir "npm-install-$ts.log"
            cmd /c "npm install" | Tee-Object -FilePath $log
            if ($LASTEXITCODE -ne 0) { throw "npm install failed -- see $log" }
        } else {
            Write-Info "node_modules OK"
        }
    }

}

# ---------------------------------------------------------------------------
# 3. Tests
# ---------------------------------------------------------------------------

if (-not $SkipTests) {

    Invoke-Checked "Backend tests (dotnet test)" {
        Set-Location $root
        $log = Join-Path $logDir "test-backend-$ts.log"
        dotnet test --logger "console;verbosity=normal" | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "Backend tests failed -- see $log" }
    }

    Invoke-Checked "Frontend tests (npm run test:run)" {
        Set-Location $uiDir
        $log = Join-Path $logDir "test-frontend-$ts.log"
        cmd /c "npm run test:run" | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed -- see $log" }
    }

}

# ---------------------------------------------------------------------------
# 4. Start backend
# ---------------------------------------------------------------------------

Write-Step "Starting backend  -->  http://localhost:5000"

$beOut = Join-Path $logDir "backend-stdout-$ts.log"
$beErr = Join-Path $logDir "backend-stderr-$ts.log"

# Use --no-build so dotnet run reuses the build we already have
$beProc = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList "run", "--no-build", "--urls", "http://localhost:5000" `
    -WorkingDirectory $apiDir `
    -RedirectStandardOutput $beOut `
    -RedirectStandardError  $beErr `
    -PassThru `
    -NoNewWindow
Write-Ok "Backend  PID $($beProc.Id)  ->  $beOut"

# ---------------------------------------------------------------------------
# 5. Start frontend
# ---------------------------------------------------------------------------

Write-Step "Starting frontend  -->  http://localhost:5173"

$feOut = Join-Path $logDir "frontend-stdout-$ts.log"
$feErr = Join-Path $logDir "frontend-stderr-$ts.log"

$feProc = Start-Process `
    -FilePath "cmd.exe" `
    -ArgumentList "/c npm run dev" `
    -WorkingDirectory $uiDir `
    -RedirectStandardOutput $feOut `
    -RedirectStandardError  $feErr `
    -PassThru `
    -NoNewWindow
Write-Ok "Frontend PID $($feProc.Id)  ->  $feOut"

# ---------------------------------------------------------------------------
# 6. Wait for backend to be ready
# ---------------------------------------------------------------------------

Write-Step "Waiting for backend on :5000 ..."

$maxWait = 60
$waited  = 0
$up      = $false

do {
    Start-Sleep -Seconds 2
    $waited += 2
    $conn = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
    if ($conn) { $up = $true }
} until ($up -or $waited -ge $maxWait)

if (-not $up) {
    Write-Fail "Backend did not bind :5000 within $maxWait s"
    Write-Fail "Check log: $beOut"
    Write-Fail "Check err: $beErr"
    exit 1
}

Write-Ok "Backend is listening"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "+-----------------------------------------------+" -ForegroundColor Green
Write-Host "|   NodePilot dev environment ready             |" -ForegroundColor Green
Write-Host "|                                               |" -ForegroundColor Green
Write-Host "|   Backend   http://localhost:5000             |" -ForegroundColor Green
Write-Host "|   Frontend  http://localhost:5173             |" -ForegroundColor Green
Write-Host "|                                               |" -ForegroundColor Green
Write-Host "|   Backend  PID $($beProc.Id.ToString().PadRight(5))  log: .dev-logs\      |" -ForegroundColor Green
Write-Host "|   Frontend PID $($feProc.Id.ToString().PadRight(5))  log: .dev-logs\      |" -ForegroundColor Green
Write-Host "+-----------------------------------------------+" -ForegroundColor Green
Write-Host ""
