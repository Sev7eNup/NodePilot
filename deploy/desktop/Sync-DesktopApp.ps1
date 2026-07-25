#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Pushes local code changes into an ALREADY INSTALLED NodePilot desktop app in ~1 minute,
    instead of rebuilding the full installer (~10-15 minutes).

.DESCRIPTION
    The installed app is just files under <InstallPath> plus two Windows services, so a code change
    only needs the changed artefacts copied in and (for backend changes) a service restart. Rebuild
    the installer only when you want to DISTRIBUTE.

    Which loop to use for what:

      Electron shell   ->  `npm start` in src/nodepilot-desktop. It runs from source against the
                           installed backend (it reads %ProgramData%\NodePilot\desktop.json), so no
                           packaging is involved at all. This script does NOT sync the shell: the
                           packaged app lives in an asar archive that cannot be patched sensibly.
      Backend / SPA    ->  normal dev mode (port 5000 + Vite 5173) for day-to-day work.
      Backend / SPA    ->  THIS SCRIPT, when you specifically want to test the packaged desktop
                           app (service identity = LocalSystem, bundled Postgres, loopback TLS).
      Distribution     ->  Build-DesktopInstaller.ps1.

.EXAMPLE
    ./Sync-DesktopApp.ps1 -Component spa      # SPA only, no service restart (~30 s)
.EXAMPLE
    ./Sync-DesktopApp.ps1 -Component api      # backend, stops/starts the service (~1 min)
.EXAMPLE
    ./Sync-DesktopApp.ps1 -Component all
#>
[CmdletBinding()]
param(
    [ValidateSet('api', 'spa', 'shell', 'all')]
    [string] $Component = 'all',
    [string] $InstallPath = 'C:\Program Files\NodePilot',
    [string] $DataPath = (Join-Path $env:ProgramData 'NodePilot'),
    [string] $ApiServiceName = 'NodePilot',
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$UiDir     = Join-Path $RepoRoot 'src\nodepilot-ui'
$ApiCsproj = Join-Path $RepoRoot 'src\NodePilot.Api\NodePilot.Api.csproj'
$AppDir    = Join-Path $InstallPath 'app'
$PublishTmp = Join-Path $env:TEMP 'nodepilot-sync-publish'

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

# Native tools write progress/warnings to stderr; under $ErrorActionPreference='Stop' PowerShell 5.1
# would escalate those to terminating errors even on exit code 0 (vite/rolldown does exactly that).
function Invoke-Tool([scriptblock] $Command, [string] $FailMessage, [int[]] $OkExitCodes = @(0)) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $prev }
    if ($OkExitCodes -notcontains $LASTEXITCODE) { throw "$FailMessage (exit $LASTEXITCODE)" }
}

if (-not (Test-Path -LiteralPath $AppDir)) {
    throw "No desktop installation found at $AppDir. Install it first with the installer built by Build-DesktopInstaller.ps1."
}

function Get-Origin {
    $desktopJson = Join-Path $DataPath 'desktop.json'
    if (-not (Test-Path -LiteralPath $desktopJson)) { return $null }
    try { return (Get-Content -LiteralPath $desktopJson -Raw | ConvertFrom-Json).origin } catch { return $null }
}

function Wait-Healthy([string] $origin) {
    if (-not $origin) { return $true }
    # curl.exe, not Invoke-WebRequest: PS 5.1 negotiates poorly against the loopback TLS endpoint
    # and reports a healthy API as unreachable.
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (-not (Test-Path -LiteralPath $curl)) { return $true }
    for ($i = 0; $i -lt 45; $i++) {
        $code = (& $curl -sk --http1.1 -o NUL -w '%{http_code}' "$origin/healthz/ready" 2>$null)
        if ("$code".Trim() -eq '200') { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

$origin = Get-Origin
$didBackend = $false

# --- SPA ------------------------------------------------------------------------------------
if ($Component -in @('spa', 'all')) {
    Write-Step 'Building SPA'
    Push-Location $UiDir
    try { Invoke-Tool { & npm.cmd run build } 'npm run build (ui) failed.' } finally { Pop-Location }

    $dist = Join-Path $UiDir 'dist'
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html'))) { throw "SPA build produced no index.html in $dist." }

    Write-Step 'Syncing SPA into the installation'
    # /MIR is safe here: wwwroot holds nothing but SPA output, and mirroring removes stale
    # content-hashed chunks from previous builds.
    Invoke-Tool { & robocopy.exe $dist (Join-Path $AppDir 'wwwroot') /MIR /NFL /NDL /NJH /NJS /NP } `
        'robocopy (SPA) failed.' -OkExitCodes @(0, 1, 2, 3)
    Write-Host '    done - reload the NodePilot window (Ctrl+R), no service restart needed.'
}

# --- API ------------------------------------------------------------------------------------
if ($Component -in @('api', 'all')) {
    Write-Step 'Publishing API (incremental)'
    Invoke-Tool {
        & dotnet publish $ApiCsproj -c $Configuration -r win-x64 --self-contained true `
            -p:UseAppHost=true -p:DebugType=embedded -o $PublishTmp
    } 'dotnet publish failed.'

    Write-Step "Stopping service '$ApiServiceName'"
    & sc.exe stop $ApiServiceName | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        $svc = Get-Service $ApiServiceName -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') { break }
        Start-Sleep -Seconds 1
    }

    Write-Step 'Syncing binaries into the installation'
    # /E (not /MIR): the installed app additionally contains wwwroot\ and Modules\ (the PowerShell
    # built-in modules staged at build time). Mirroring would delete both and break runScript.
    Invoke-Tool { & robocopy.exe $PublishTmp $AppDir /E /NFL /NDL /NJH /NJS /NP } `
        'robocopy (API) failed.' -OkExitCodes @(0, 1, 2, 3)

    Write-Step "Starting service '$ApiServiceName'"
    & sc.exe start $ApiServiceName | Out-Null
    $didBackend = $true
}

# --- Shell ----------------------------------------------------------------------------------
if ($Component -in @('shell', 'all')) {
    Write-Step 'Electron shell'
    Write-Host '    Not synced by design - the packaged shell lives inside app.asar.'
    Write-Host '    For shell development run it straight from source (fastest loop, no packaging):'
    Write-Host '        cd src\nodepilot-desktop; npm start' -ForegroundColor Yellow
    Write-Host '    It reads %ProgramData%\NodePilot\desktop.json and attaches to this installation.'
}

# --- Verify ---------------------------------------------------------------------------------
if ($didBackend) {
    Write-Step 'Waiting for readiness'
    if (Wait-Healthy $origin) {
        Write-Host "Sync complete. $origin is healthy." -ForegroundColor Green
    } else {
        Write-Warning "Service did not report /healthz/ready. Check $DataPath\logs."
        exit 1
    }
} else {
    Write-Host 'Sync complete.' -ForegroundColor Green
}
exit 0
