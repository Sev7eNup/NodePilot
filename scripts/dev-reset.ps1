<#
.SYNOPSIS
    NodePilot dev reset: kill, build, start.
    Kills all running backend/frontend processes, builds both, then starts
    backend (http://localhost:5000) and frontend (http://localhost:5173).
    Tests are not run here -- use scripts/nightly-tests.ps1 or a scoped
    dotnet test / vitest run.

.PARAMETER SkipBuild
    Skip the build step (still restarts processes).
#>
param(
    [switch]$SkipBuild = $false
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

function Test-DevProcess {
    param($Process, [string]$ApiDirectory, [string]$UiDirectory)

    if ($Process.Name -eq 'NodePilot.Api.exe' -and $Process.ExecutablePath) {
        $apiBin = [IO.Path]::GetFullPath((Join-Path $ApiDirectory 'bin')).TrimEnd('\') + '\'
        return [IO.Path]::GetFullPath($Process.ExecutablePath).StartsWith($apiBin, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($Process.Name -ne 'node.exe' -or -not $Process.CommandLine) { return $false }
    $vitePath = [IO.Path]::GetFullPath((Join-Path $UiDirectory 'node_modules\vite\bin\vite.js'))
    foreach ($match in [regex]::Matches($Process.CommandLine, '"([^"]+)"|(\S+)')) {
        $argument = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        if (-not [IO.Path]::IsPathRooted($argument)) { continue }
        try {
            if ([IO.Path]::GetFullPath($argument).Equals($vitePath, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        } catch { }
    }
    return $false
}

function Get-DevProcessesToStop {
    param([int[]]$Ports, [string]$ApiDirectory, [string]$UiDirectory)

    $owned = @(Get-CimInstance Win32_Process | Where-Object {
        Test-DevProcess -Process $_ -ApiDirectory $ApiDirectory -UiDirectory $UiDirectory
    })
    $ownedIds = @($owned | ForEach-Object { [int]$_.ProcessId })
    foreach ($connection in @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -in $Ports })) {
        if ([int]$connection.OwningProcess -notin $ownedIds) {
            throw "Port $($connection.LocalPort) belongs to PID $($connection.OwningProcess), which is not a verified NodePilot development process. No processes were stopped."
        }
    }
    return $owned
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

# Validate every listener before stopping either service; also collect this checkout's orphaned Vite processes.
$devProcesses = @(Get-DevProcessesToStop -Ports @(5000, 5173) -ApiDirectory $apiDir -UiDirectory $uiDir)
foreach ($devProcess in $devProcesses) {
    Stop-Process -Id $devProcess.ProcessId -Force -ErrorAction Stop
    Write-Info "  Stopped development PID $($devProcess.ProcessId)"
}

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
# 3. Start backend
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
    -WindowStyle Hidden
Write-Ok "Backend  PID $($beProc.Id)  ->  $beOut"

# ---------------------------------------------------------------------------
# 4. Start frontend
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
    -WindowStyle Hidden
Write-Ok "Frontend PID $($feProc.Id)  ->  $feOut"

# ---------------------------------------------------------------------------
# 5. Wait for backend to be ready
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
