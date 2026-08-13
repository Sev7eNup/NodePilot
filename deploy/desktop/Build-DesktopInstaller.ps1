#requires -Version 5.1
<#
.SYNOPSIS
    Builds the machine-wide, offline NodePilot desktop installer (.exe) for Windows 11 x64.

.DESCRIPTION
    Stages four payloads and compiles them with Inno Setup:
      app\     : self-contained .NET 10 API publish (win-x64) + the built SPA under wwwroot
      desktop\ : the packaged Electron 43.4.0 shell (Chromium + Node, shipped in full)
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
    [string] $IsccPath,
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
$DesktopRuntimeVersion = '10.0.11'

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Assert-Tool([string] $name, [string] $probe) {
    if (-not (Get-Command $probe -ErrorAction SilentlyContinue)) { throw "Required tool '$name' not found on PATH ($probe)." }
}
# Runs a native tool with stderr demoted so a warning written to stderr (e.g. vite/rolldown's
# INVALID_ANNOTATION note, or dotnet/packager diagnostics) does not get escalated to a terminating
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
Assert-Tool 'node' 'node'
. (Join-Path $RepoRoot 'scripts\Assert-DotnetSdkPolicy.ps1')
Assert-NodePilotDotnetSdkPolicy -RepoRoot $RepoRoot
. (Join-Path $PSScriptRoot 'Resolve-IsccPath.ps1')
. (Join-Path $PSScriptRoot 'Assert-DesktopRuntimePayload.ps1')
$resolvedIscc = Resolve-NodePilotIsccPath -Explicit $IsccPath
if (-not $resolvedIscc) {
    throw ("Inno Setup 6 compiler (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php " +
           "or pass -IsccPath. Probed: " + ((Get-NodePilotIsccCandidates) -join '; '))
}
$IsccPath = $resolvedIscc
if (-not (Test-Path -LiteralPath (Join-Path $PgBinariesPath 'bin\postgres.exe'))) {
    throw "PgBinariesPath does not look like a PostgreSQL install (no bin\postgres.exe): $PgBinariesPath"
}

if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage, $OutputRoot | Out-Null

# --- icons -----------------------------------------------------------------------------------
# Derived from the tracked brand assets so the installer, Start-Menu entry, taskbar and tray all
# show the real NodePilot logo. The generated files stay out of git (assets/.gitignore); the
# SOURCES are versioned, which is why a clean clone can always rebuild them. The generator also
# emits assets/skins/<id>.* so the shell can recolor its window + tray icon with the SPA skin.
Write-Step 'Generating application icons from the brand assets'
# Launched through powershell.exe (Windows PowerShell 5.1) rather than dot-called in-process,
# because the generator is built entirely on GDI+ (`Add-Type -AssemblyName System.Drawing`) and
# that assembly name does not resolve the same way outside Windows PowerShell. This script's
# `#requires -Version 5.1` admits PowerShell 7 too, so without pinning the shell the icon step
# would depend on which console the operator happened to start the build from. The npm script
# `nodepilot-desktop: icons` already pins it the same way.
$iconScript = Join-Path $RepoRoot 'scripts\generate-desktop-icons.ps1'
Invoke-Tool {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $iconScript `
        -SetupIconPath (Join-Path $Stage 'setup-icon.ico')
} 'Icon generation failed.'

# --- 1. API (self-contained) -----------------------------------------------------------------
Write-Step 'Publishing API (self-contained win-x64)'
$appStage = Join-Path $Stage 'app'
Invoke-Tool {
    & dotnet publish $ApiCsproj -c $Configuration -r win-x64 --self-contained true `
        "-p:RuntimeFrameworkVersion=$DesktopRuntimeVersion" `
        -p:UseAppHost=true -p:DebugType=embedded -o $appStage
} 'dotnet publish failed.'
Assert-DesktopRuntimePayload -AppPath $appStage -MinimumVersion ([version]$DesktopRuntimeVersion)

# --- 1b. PowerShell built-in modules -> <app>\Modules ----------------------------------------
# Microsoft.PowerShell.SDK ships its built-in modules (Utility, Management, CimCmdlets, ...) under
# runtimes\win\lib\<tfm>\Modules, but the hosted runspace looks for them at $PSHOME\Modules, where
# $PSHOME is the directory holding System.Management.Automation.dll (the app root). Without this
# copy every script fails with "the module could not be loaded ... compatible with the 'Core'
# edition" unless PowerShell 7 happens to be installed system-wide -- which the desktop package
# must not depend on (offline, zero prerequisites).
Write-Step 'Staging PowerShell built-in modules'
$psModuleSource = Get-ChildItem -Path (Join-Path $appStage 'runtimes\win\lib') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'Modules' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $psModuleSource) { throw "PowerShell built-in modules not found under $appStage\runtimes\win\lib\*\Modules." }
Copy-Item -Path $psModuleSource -Destination (Join-Path $appStage 'Modules') -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $appStage 'Modules\Microsoft.PowerShell.Utility'))) {
    throw 'Module staging failed: Microsoft.PowerShell.Utility missing under app\Modules.'
}
Write-Host ("    " + ((Get-ChildItem (Join-Path $appStage 'Modules') -Directory | Select-Object -ExpandProperty Name) -join ', '))

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
    Invoke-Tool { & npm.cmd run package } 'desktop packaging failed.'
} finally { Pop-Location }
$desktopPackageOut = Join-Path $DesktopDir 'out\NodePilot-win32-x64'
if (-not (Test-Path -LiteralPath (Join-Path $desktopPackageOut 'NodePilot.exe'))) { throw "Electron package missing: $desktopPackageOut\NodePilot.exe" }
$electronVersionGate = Join-Path $DesktopDir 'scripts\assert-electron-runtime-version.mjs'
$electronManifest = Join-Path $DesktopDir 'package.json'
Invoke-Tool {
    & node $electronVersionGate $desktopPackageOut $electronManifest
} 'Packaged Electron runtime version validation failed.'
Copy-Item -Path $desktopPackageOut -Destination (Join-Path $Stage 'desktop') -Recurse -Force
# Packager nests output under the platform folder name; flatten to stage\desktop.
$nested = Join-Path $Stage 'desktop\NodePilot-win32-x64'
if (Test-Path -LiteralPath $nested) {
    Copy-Item -Path (Join-Path $nested '*') -Destination (Join-Path $Stage 'desktop') -Recurse -Force
    Remove-Item -LiteralPath $nested -Recurse -Force
}

# --- 4. Postgres binaries --------------------------------------------------------------------
# Only the server runtime is shipped: bin (postgres/initdb/pg_ctl/psql/pg_dump...), lib (extension
# libraries) and share (postgres.bki, timezone data, SQL bootstrap scripts -- initdb fails without
# it). A stock EDB distribution also carries pgAdmin 4 (~630 MB, a GUI with its own Chromium), doc,
# include and StackBuilder, none of which NodePilot uses -- excluding them cuts the installer by
# roughly two thirds.
Write-Step 'Staging PostgreSQL binaries (server runtime only)'
$pgStage = Join-Path $Stage 'pgsql'
New-Item -ItemType Directory -Force -Path $pgStage | Out-Null
foreach ($part in @('bin', 'lib', 'share')) {
    $srcPart = Join-Path $PgBinariesPath $part
    if (-not (Test-Path -LiteralPath $srcPart)) { throw "PostgreSQL distribution is missing '$part': $srcPart" }
    Copy-Item -Path $srcPart -Destination (Join-Path $pgStage $part) -Recurse -Force
}
foreach ($required in @('bin\postgres.exe', 'bin\initdb.exe', 'bin\pg_ctl.exe', 'bin\psql.exe', 'bin\pg_dump.exe', 'share\postgres.bki')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pgStage $required))) { throw "PostgreSQL staging incomplete: $required missing." }
}
Write-Host ("    {0:N0} MB gestaged" -f ((Get-ChildItem $pgStage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

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
