#requires -Version 5.1
<#
.SYNOPSIS
    Builds the machine-wide, offline NodePilot desktop installer (.exe) for Windows 11 x64.

.DESCRIPTION
    Stages four payloads and compiles them with Inno Setup:
      app\     : self-contained .NET 10 API publish (win-x64) + the built SPA under wwwroot
      desktop\ : the packaged Electron 43.2.0 shell (Chromium + Node, shipped in full)
      pgsql\   : the bundled PostgreSQL binaries (from -PgBinariesPath)
      deploy\  : the provisioning / update / uninstall scripts + the appsettings template

    Nothing here is run by Claude. Requires: dotnet 10 SDK, Node/npm, Inno Setup 6 (ISCC.exe),
    and a PostgreSQL 16 binaries directory (the "pgsql" folder from the EDB zip distribution).

.EXAMPLE
    ./Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\NodePilot-Postgres\pgsql' -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PgBinariesPath,
    [string] $Version = '1.0.0',
    [string] $Configuration = 'Release',
    [string] $IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [string] $OutputRoot = (Join-Path $PSScriptRoot 'out'),
    [switch] $SkipSpaBuild
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Stage        = Join-Path $OutputRoot 'stage'
$DesktopDir   = Join-Path $RepoRoot 'src\nodepilot-desktop'
$UiDir        = Join-Path $RepoRoot 'src\nodepilot-ui'
$ApiCsproj    = Join-Path $RepoRoot 'src\NodePilot.Api\NodePilot.Api.csproj'
$AssetsDir    = Join-Path $DesktopDir 'assets'

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Assert-Tool([string] $name, [string] $probe) {
    if (-not (Get-Command $probe -ErrorAction SilentlyContinue)) { throw "Required tool '$name' not found on PATH ($probe)." }
}
# Runs a native tool with stderr demoted so a warning written to stderr (e.g. vite/rolldown's
# INVALID_ANNOTATION note, or dotnet/forge diagnostics) does not get escalated to a terminating
# error under $ErrorActionPreference='Stop'. Success is decided solely by the exit code.
function Invoke-Tool([scriptblock] $Command, [string] $FailMessage) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { throw $FailMessage }
}

# --- pre-flight ------------------------------------------------------------------------------
Write-Step 'Pre-flight checks'
Assert-Tool 'dotnet' 'dotnet'
Assert-Tool 'npm' 'npm'
if (-not (Test-Path -LiteralPath $IsccPath)) { throw "Inno Setup compiler not found: $IsccPath. Install Inno Setup 6 or pass -IsccPath." }
if (-not (Test-Path -LiteralPath (Join-Path $PgBinariesPath 'bin\postgres.exe'))) {
    throw "PgBinariesPath does not look like a PostgreSQL install (no bin\postgres.exe): $PgBinariesPath"
}

if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage, $OutputRoot | Out-Null

# --- icons -----------------------------------------------------------------------------------
Write-Step 'Generating application icons'
New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null
Add-Type -AssemblyName System.Drawing
function New-AppBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::FromArgb(11, 16, 32))
    $accent = [System.Drawing.Color]::FromArgb(79, 124, 255)
    $pen = New-Object System.Drawing.Pen($accent, [Math]::Max(2, $size / 8))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    # A simple upward chevron ("pilot / navigate").
    $p1 = New-Object System.Drawing.Point([int]($size * 0.25), [int]($size * 0.62))
    $p2 = New-Object System.Drawing.Point([int]($size * 0.50), [int]($size * 0.38))
    $p3 = New-Object System.Drawing.Point([int]($size * 0.75), [int]($size * 0.62))
    $g.DrawLines($pen, @($p1, $p2, $p3))
    $g.Dispose()
    return $bmp
}
$icoPngPath = Join-Path $AssetsDir 'icon.png'
$trayPath   = Join-Path $AssetsDir 'tray.png'
$setupIco   = Join-Path $Stage 'setup-icon.ico'
$appIco     = Join-Path $AssetsDir 'icon.ico'

$big = New-AppBitmap 256
$big.Save($icoPngPath, [System.Drawing.Imaging.ImageFormat]::Png)
(New-AppBitmap 16).Save($trayPath, [System.Drawing.Imaging.ImageFormat]::Png)
# .ico for Forge (app/exe icon) and the installer.
$hicon = $big.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hicon)
$fs = [System.IO.File]::Create($appIco); $icon.Save($fs); $fs.Close()
Copy-Item -LiteralPath $appIco -Destination $setupIco -Force

# --- 1. API (self-contained) -----------------------------------------------------------------
Write-Step 'Publishing API (self-contained win-x64)'
Invoke-Tool {
    & dotnet publish $ApiCsproj -c $Configuration -r win-x64 --self-contained true `
        -p:UseAppHost=true -p:DebugType=embedded -o (Join-Path $Stage 'app')
} 'dotnet publish failed.'

# --- 2. SPA -> wwwroot -----------------------------------------------------------------------
if (-not $SkipSpaBuild) {
    Write-Step 'Building SPA'
    Push-Location $UiDir
    try {
        # npm ci wipes node_modules first; a running vite dev server would EPERM-lock esbuild.
        # Reuse existing deps when present; a clean machine still gets a full install.
        if (-not (Test-Path -LiteralPath (Join-Path $UiDir 'node_modules'))) {
            Invoke-Tool { & npm.cmd ci } 'npm ci (ui) failed.'
        }
        Invoke-Tool { & npm.cmd run build } 'npm run build (ui) failed.'
    } finally { Pop-Location }
}
$spaDist = Join-Path $UiDir 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $spaDist 'index.html'))) { throw "SPA build missing: $spaDist\index.html" }
$wwwroot = Join-Path $Stage 'app\wwwroot'
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item -Path (Join-Path $spaDist '*') -Destination $wwwroot -Recurse -Force

# --- 3. Electron shell -----------------------------------------------------------------------
Write-Step 'Packaging Electron shell'
Push-Location $DesktopDir
try {
    Invoke-Tool { & npm.cmd ci } 'npm ci (desktop) failed.'
    Invoke-Tool { & npm.cmd run package } 'electron-forge package failed.'
} finally { Pop-Location }
$forgeOut = Join-Path $DesktopDir 'out\NodePilot-win32-x64'
if (-not (Test-Path -LiteralPath (Join-Path $forgeOut 'NodePilot.exe'))) { throw "Electron package missing: $forgeOut\NodePilot.exe" }
Copy-Item -Path $forgeOut -Destination (Join-Path $Stage 'desktop') -Recurse -Force
# Forge nests output under the platform folder name; flatten to stage\desktop.
$nested = Join-Path $Stage 'desktop\NodePilot-win32-x64'
if (Test-Path -LiteralPath $nested) {
    Copy-Item -Path (Join-Path $nested '*') -Destination (Join-Path $Stage 'desktop') -Recurse -Force
    Remove-Item -LiteralPath $nested -Recurse -Force
}

# --- 4. Postgres binaries --------------------------------------------------------------------
Write-Step 'Staging PostgreSQL binaries'
Copy-Item -Path $PgBinariesPath -Destination (Join-Path $Stage 'pgsql') -Recurse -Force
$stagedPg = Join-Path $Stage 'pgsql\NodePilot-win32-x64'   # guard against accidental nesting
if (Test-Path -LiteralPath $stagedPg) { throw 'Unexpected pgsql nesting.' }

# --- 5. deploy scripts -----------------------------------------------------------------------
Write-Step 'Staging deploy scripts'
$deployStage = Join-Path $Stage 'deploy'
New-Item -ItemType Directory -Force -Path $deployStage | Out-Null
foreach ($f in @('Provision-LocalDb.ps1', 'Update-Desktop.ps1', 'Uninstall-Desktop.ps1', 'appsettings.Desktop.json.template')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $deployStage -Force
}

# --- 6. compile installer --------------------------------------------------------------------
Write-Step 'Compiling installer (Inno Setup)'
Invoke-Tool {
    & $IsccPath "/DStageDir=$Stage" "/DAppVersion=$Version" "/DOutputDir=$OutputRoot" (Join-Path $PSScriptRoot 'NodePilot.iss')
} 'ISCC failed.'

$installer = Join-Path $OutputRoot "NodePilot-Desktop-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer not produced: $installer" }
Write-Host "Installer built: $installer" -ForegroundColor Green
Write-Host 'Sign it with your Authenticode certificate before distribution (signtool sign /fd SHA256 ...).' -ForegroundColor Yellow
