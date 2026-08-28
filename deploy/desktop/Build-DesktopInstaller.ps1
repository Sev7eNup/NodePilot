#requires -Version 5.1
<#
.SYNOPSIS
    Builds the machine-wide, offline NodePilot desktop installer (.exe) for Windows 11 x64.

.DESCRIPTION
    Stages four payloads and compiles them with Inno Setup:
      app\     : self-contained .NET 10 API publish (win-x64) + the built SPA under wwwroot
      desktop\ : the packaged Electron 43.4.1 shell (Chromium + Node, shipped in full)
      pgsql\   : the bundled PostgreSQL binaries (from -PgBinariesPath)
      deploy\  : the provisioning / update / uninstall scripts + the appsettings template

    Requires: dotnet 10 SDK, Node/npm, Inno Setup 6 (ISCC.exe), and a PostgreSQL 16 binaries
    directory (the "pgsql" folder from the EDB zip distribution).

.EXAMPLE
    ./Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\Packages\pgsql' -Version 1.2.10
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
$PublishSettingsHygieneScript = Join-Path $RepoRoot 'deploy\Assert-PublishSettingsHygiene.ps1'
$DesktopRuntimeVersion = '10.0.11'
# The one place the bundled PostgreSQL major is written down. A cluster initialised by one major
# cannot be opened by another, and this package upgrades in place over an existing pgdata.
$DesktopPostgresMajorVersion = 16

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Assert-Tool([string] $name, [string] $probe) {
    if (-not (Get-Command $probe -ErrorAction SilentlyContinue)) { throw "Required tool '$name' not found on PATH ($probe)." }
}
# Runs a native tool with stderr demoted so a warning written to stderr does not escalate to a
# terminating error under $ErrorActionPreference='Stop'. Success is decided by the exit code alone.
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
if (-not (Test-Path -LiteralPath $PublishSettingsHygieneScript -PathType Leaf)) {
    throw "Publish settings hygiene helper missing: $PublishSettingsHygieneScript"
}
. $PublishSettingsHygieneScript
$resolvedIscc = Resolve-NodePilotIsccPath -Explicit $IsccPath
if (-not $resolvedIscc) {
    throw ("Inno Setup 6 compiler (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php " +
           "or pass -IsccPath. Probed: " + ((Get-NodePilotIsccCandidates) -join '; '))
}
$IsccPath = $resolvedIscc
if (-not (Test-Path -LiteralPath (Join-Path $PgBinariesPath 'bin\postgres.exe'))) {
    throw "PgBinariesPath does not look like a PostgreSQL install (no bin\postgres.exe): $PgBinariesPath"
}
# Checked here rather than after staging, so a wrong distribution fails before the publish, SPA and
# Electron builds. What ships is a byte copy of exactly these binaries.
Assert-DesktopPostgresPayload -PgRootPath $PgBinariesPath -ExpectedMajorVersion $DesktopPostgresMajorVersion

if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage, $OutputRoot | Out-Null

# --- icons -----------------------------------------------------------------------------------
# Derived from the tracked brand assets so the installer, Start-Menu entry, taskbar and tray all
# show the NodePilot logo. The generated files stay out of git (assets/.gitignore) while the sources
# are versioned, so a clean clone can rebuild them. The generator also emits assets/skins/<id>.* so
# the shell can recolor its window and tray icon with the SPA skin.
Write-Step 'Generating application icons from the brand assets'
# Launched through powershell.exe (Windows PowerShell 5.1) rather than dot-called in-process: the
# generator builds on GDI+ (`Add-Type -AssemblyName System.Drawing`), and that assembly name does
# not resolve the same way in PowerShell 7, which this script also runs under. Pinning the shell
# keeps the icon step independent of the console the build was started from.
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
# runtimes\win\lib\<tfm>\Modules, but the hosted runspace looks for them at $PSHOME\Modules, the
# directory holding System.Management.Automation.dll (the app root). Without this copy, scripts load
# their modules only where PowerShell 7 is installed system-wide, which this offline package cannot
# require.
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

# --- 1c. Operator clients -> <stage>\tools\{np,mcp} -------------------------------------------
# Self-contained, unlike the server artifact's framework-dependent copies: the desktop package
# installs offline with no prerequisites, so there is no shared .NET runtime to bind to, and a
# framework-dependent apphost cannot borrow the runtime sitting next to the API. Each client keeps
# its own directory so no publish overwrites another's assembly versions. nodepilot-mcp is shipped
# here because it is the only way to point an AI agent at a local installation.
Write-Step 'Publishing operator clients (np, nodepilot-mcp)'
foreach ($client in @(
        @{ Name = 'np';  Csproj = Join-Path $RepoRoot 'src\NodePilot.Cli\NodePilot.Cli.csproj'; Exe = 'np.exe' },
        @{ Name = 'mcp'; Csproj = Join-Path $RepoRoot 'src\NodePilot.Mcp\NodePilot.Mcp.csproj'; Exe = 'nodepilot-mcp.exe' })) {
    if (-not (Test-Path -LiteralPath $client.Csproj)) { throw "Client csproj not found: $($client.Csproj)" }
    $clientStage = Join-Path $Stage ("tools\" + $client.Name)
    Invoke-Tool {
        & dotnet publish $client.Csproj -c $Configuration -r win-x64 --self-contained true `
            "-p:RuntimeFrameworkVersion=$DesktopRuntimeVersion" `
            -p:UseAppHost=true -p:DebugType=embedded -o $clientStage
    } "dotnet publish failed for $($client.Csproj)."
    if (-not (Test-Path -LiteralPath (Join-Path $clientStage $client.Exe))) {
        throw "Expected $($client.Exe) in $clientStage, but it is missing."
    }
}

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
# libraries) and share (postgres.bki, timezone data, SQL bootstrap scripts, without which initdb
# fails). A stock EDB distribution also carries pgAdmin 4, doc, include and StackBuilder, none of
# which NodePilot uses; leaving them out keeps the installer much smaller.
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
Assert-NodePilotPublishSettingsHygiene -RootPath $Stage -RequiredBaseSettingsPath 'app\appsettings.json'
Write-Step 'Compiling installer (Inno Setup)'
Invoke-Tool {
    & $IsccPath "/DStageDir=$Stage" "/DAppVersion=$Version" "/DOutputDir=$OutputRoot" (Join-Path $PSScriptRoot 'NodePilot.iss')
} 'ISCC failed.'

$installer = Join-Path $OutputRoot "NodePilot-Desktop-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer not produced: $installer" }
Write-Host "Installer built: $installer" -ForegroundColor Green
Write-Host 'Sign it with your Authenticode certificate before distribution (signtool sign /fd SHA256 ...).' -ForegroundColor Yellow
