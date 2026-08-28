#requires -Version 5.1

<#
.SYNOPSIS
    Builds NodePilot-Server-Setup-<version>.exe from an already-signed server artifact.
.DESCRIPTION
    Sibling of deploy\desktop\Build-DesktopInstaller.ps1. Stages the payload and calls ISCC.

    The payload carries the signed zip plus its detached manifest and signature unchanged, so the
    wizard verifies them at install time the same way a hand-run deployment does.

    The zip stays framework-dependent, like the released one. A self-contained copy would later be
    overwritten with framework-dependent binaries by an Update-NodePilot.ps1 run against a
    downloaded release. The ASP.NET Core runtime ships as Microsoft's own installer instead - see
    Get-DotnetRuntimePayload.ps1.
.PARAMETER ArtifactPath
    The signed NodePilot-<version>.zip. Its .manifest.json and .manifest.json.p7s must sit beside it.
.PARAMETER TrustedSignerThumbprint
    Compiled into the wizard so the installer can verify the payload without asking the operator
    for it. Also shown on the Ready page, so the pinned thumbprint stays visible.
.PARAMETER SignerCertificatePath
    Public part of the signing certificate, shipped so the wizard can offer to trust it.
.PARAMETER Version
    Installer version. Derived from the artifact file name when omitted.
.PARAMETER IsccPath
    Inno Setup compiler. Probed via Resolve-IsccPath.ps1 when omitted.
.PARAMETER RuntimeInstallerPath
    A pre-fetched ASP.NET Core runtime installer. Fetched and verified when omitted.
.PARAMETER PgBinariesPath
    A PostgreSQL distribution ("pgsql" from the EDB zip), same input the desktop installer takes.
    Only the psql client is taken from it, so the wizard can create the role and database on a
    PostgreSQL server the way it already can on SQL Server.

    Optional, unlike the desktop build. When omitted, the readiness page reports that the Postgres
    fix is unavailable in this build. Release builds pass it.
.PARAMETER OutputRoot
    Where the .exe lands. Defaults to deploy\server\out.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$TrustedSignerThumbprint,
    [string]$SignerCertificatePath,
    [string]$Version,
    [string]$IsccPath,
    [string]$RuntimeInstallerPath,
    [string]$PgBinariesPath,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$deployRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $deployRoot
$MinimumRuntimeVersion = [version]'10.0.11'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $scriptDirectory 'out' }

function Write-Step { param([string]$Text) Write-Host "[server-setup] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[server-setup] $Text" -ForegroundColor Gray }

function Invoke-Tool {
    <#
      Native tools write progress to stderr, which PowerShell escalates into a terminating error
      under $ErrorActionPreference = 'Stop'. Success is judged by the exit code instead.
    #>
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @())
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $FilePath @Arguments }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $LASTEXITCODE."
    }
}

# --- pre-flight -------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
    throw "Artifact not found: $ArtifactPath"
}
$ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
$artifactName = [IO.Path]::GetFileName($ArtifactPath)
foreach ($sidecar in "$ArtifactPath.manifest.json", "$ArtifactPath.manifest.json.p7s") {
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
        throw ("Missing '$([IO.Path]::GetFileName($sidecar))'. The server setup can only be built " +
               'from a signed artifact, because the wizard verifies the signature at install time.')
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $match = [regex]::Match($artifactName, '^NodePilot-(?<version>.+)\.zip$')
    if (-not $match.Success) {
        throw "Cannot derive -Version from '$artifactName'. Pass -Version explicitly."
    }
    $Version = $match.Groups['version'].Value
}

$normalizedThumbprint = ($TrustedSignerThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($normalizedThumbprint.Length -ne 40) {
    throw "-TrustedSignerThumbprint must be 40 hexadecimal characters, got '$TrustedSignerThumbprint'."
}

. (Join-Path $deployRoot 'desktop\Resolve-IsccPath.ps1')
$resolvedIscc = Resolve-NodePilotIsccPath -Explicit $IsccPath
if (-not $resolvedIscc) {
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php.'
}
Write-Info "  ISCC: $resolvedIscc"

# --- stage ------------------------------------------------------------------------------------

# Two staging trees on purpose:
#
#   payload\  everything setup needs while it runs, shipped with Inno's dontcopy flag and
#             extracted to {tmp} - the readiness page and PrepareToInstall both run before a
#             single file has been installed.
#   deploy\   the copy that stays on disk, for the uninstaller and for later manual use.
#
# They must not share source paths. Inno deduplicates identical source files, so listing the same
# file both dontcopy and with a DestDir collapses into one entry and drops the dontcopy variant.
$stage = Join-Path $scriptDirectory 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($directory in 'payload', 'deploy\templates') {
    [void](New-Item -ItemType Directory -Path (Join-Path $stage $directory) -Force)
}

Write-Step 'Staging the signed artifact'
foreach ($file in $ArtifactPath, "$ArtifactPath.manifest.json", "$ArtifactPath.manifest.json.p7s") {
    Copy-Item -LiteralPath $file -Destination (Join-Path $stage 'payload') -Force
}

Write-Step 'Staging the deployment scripts'
# Everything the wizard's adapter dot-sources or shells out to, and nothing else. Test-* scripts
# and the build scripts are development-host only.
$deployScripts = @(
    'ArtifactSecurity.ps1'
    'Preflight.ps1'
    'ServiceControl.ps1'
    'SetupContract.ps1'
    'Invoke-NodePilotSetup.ps1'
    'Install-NodePilot.ps1'
    'Update-NodePilot.ps1'
    'Uninstall-NodePilot.ps1'
    'Provision-NodePilotDatabase.ps1'
    'Provision-NodePilotPostgres.ps1'
    'New-NodePilotSelfSignedCertificate.ps1'
)
foreach ($script in $deployScripts) {
    $source = Join-Path $deployRoot $script
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing deployment script: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'deploy') -Force
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'payload') -Force
}
Copy-Item -LiteralPath (Join-Path $deployRoot 'templates\appsettings.Production.json.template') `
    -Destination (Join-Path $stage 'deploy\templates') -Force

Write-Step 'Staging the ASP.NET Core runtime installer'
if ([string]::IsNullOrWhiteSpace($RuntimeInstallerPath)) {
    $RuntimeInstallerPath = & (Join-Path $deployRoot 'Get-DotnetRuntimePayload.ps1') `
        -OutputDirectory (Join-Path $stage 'payload')
}
else {
    if (-not (Test-Path -LiteralPath $RuntimeInstallerPath -PathType Leaf)) {
        throw "Runtime installer not found: $RuntimeInstallerPath"
    }
    Copy-Item -LiteralPath $RuntimeInstallerPath -Destination (Join-Path $stage 'payload') -Force
}
$stagedRuntime = Get-ChildItem -LiteralPath (Join-Path $stage 'payload') -Filter 'aspnetcore-runtime-*.exe' |
    Select-Object -First 1
if (-not $stagedRuntime) { throw 'No ASP.NET Core runtime installer was staged.' }
$runtimeNameMatch = [regex]::Match(
    $stagedRuntime.Name,
    '^aspnetcore-runtime-(?<version>\d+\.\d+\.\d+)-win-x64\.exe$')
if (-not $runtimeNameMatch.Success) {
    throw "Staged runtime installer has an unrecognised name: $($stagedRuntime.Name)"
}
$stagedRuntimeVersion = [version]$runtimeNameMatch.Groups['version'].Value
if ($stagedRuntimeVersion -lt $MinimumRuntimeVersion) {
    throw "Staged ASP.NET Core runtime $stagedRuntimeVersion is below the security floor $MinimumRuntimeVersion."
}
Write-Info "  $($stagedRuntime.Name)"

# The psql client only: the files it loads according to its import table, not the whole bin\
# folder, most of which psql never touches. Staged flat into payload\ rather than into a
# subfolder, because the [Files] entry that carries the payload is "payload\*" with no
# recursesubdirs, so a subdirectory would compile without complaint and not be extracted.
$pgClientFiles = @(
    'psql.exe'
    'LIBPQ.dll'
    'libssl-3-x64.dll'
    'libcrypto-3-x64.dll'
    'libintl-9.dll'
    'libiconv-2.dll'
    'libwinpthread-1.dll'
)
if ([string]::IsNullOrWhiteSpace($PgBinariesPath)) {
    Write-Step 'Staging the PostgreSQL client: skipped (-PgBinariesPath not given)'
    Write-Info '  This build cannot create a PostgreSQL role and database; the readiness page will say so.'
}
else {
    Write-Step 'Staging the PostgreSQL client (psql only)'
    $pgBin = Join-Path $PgBinariesPath 'bin'
    if (-not (Test-Path -LiteralPath (Join-Path $pgBin 'psql.exe') -PathType Leaf)) {
        throw "PgBinariesPath does not look like a PostgreSQL distribution (no bin\psql.exe): $PgBinariesPath"
    }
    $staged = 0
    foreach ($file in $pgClientFiles) {
        $source = Join-Path $pgBin $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw ("PostgreSQL client staging incomplete: $file is missing from $pgBin. psql will not " +
                   'start without it, and a wizard that ships a broken client is worse than one that ' +
                   'ships none.')
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'payload') -Force
        $staged += (Get-Item -LiteralPath $source).Length
    }
    Write-Info ("  {0} files, {1:N1} MB" -f $pgClientFiles.Count, ($staged / 1MB))
}

Write-Step 'Staging the publisher certificate'
if ([string]::IsNullOrWhiteSpace($SignerCertificatePath)) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $normalizedThumbprint } | Select-Object -First 1
    if (-not $certificate) {
        throw ("No certificate with thumbprint $normalizedThumbprint found in the local stores, and " +
               '-SignerCertificatePath was not given. The wizard needs the public part so it can ' +
               'offer to trust the publisher.')
    }
    $exported = Join-Path $stage 'payload\nodepilot-release-signing.cer'
    [IO.File]::WriteAllBytes($exported, $certificate.Export('Cert'))
}
else {
    Copy-Item -LiteralPath $SignerCertificatePath `
        -Destination (Join-Path $stage 'payload\nodepilot-release-signing.cer') -Force
}

Write-Step 'Staging the icon and licence'
# Launched through powershell.exe explicitly: the generator uses System.Drawing, which only
# resolves under Windows PowerShell 5.1.
Invoke-Tool -FilePath 'powershell.exe' -Arguments @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $repoRoot 'scripts\generate-desktop-icons.ps1'),
    '-SetupIconPath', (Join-Path $stage 'setup-icon.ico'))
if (-not (Test-Path -LiteralPath (Join-Path $stage 'setup-icon.ico'))) {
    throw 'The icon generator did not produce setup-icon.ico.'
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt') -Force

# --- compile ----------------------------------------------------------------------------------

[void](New-Item -ItemType Directory -Path $OutputRoot -Force)
Write-Step "Compiling NodePilot-Server-Setup-$Version.exe"
Invoke-Tool -FilePath $resolvedIscc -Arguments @(
    "/DStageDir=$stage",
    "/DAppVersion=$Version",
    "/DOutputDir=$OutputRoot",
    "/DSignerThumbprint=$normalizedThumbprint",
    "/DArtifactFileName=$artifactName",
    # Exact name rather than a wildcard: ExtractTemporaryFiles throws when a pattern matches
    # nothing, which is hard to diagnose on a target machine.
    "/DRuntimeFileName=$($stagedRuntime.Name)",
    (Join-Path $scriptDirectory 'NodePilotServer.iss'))

$installer = Join-Path $OutputRoot "NodePilot-Server-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "ISCC reported success but $installer does not exist."
}
Write-Step ("Built {0} ({1:N1} MB)" -f $installer, ((Get-Item $installer).Length / 1MB))
Write-Info '  Authenticode-sign it before distribution (Build-Artifact.ps1 does this for you).'
return $installer
