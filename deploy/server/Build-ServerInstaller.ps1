#requires -Version 5.1

<#
.SYNOPSIS
    Builds NodePilot-Server-Setup-<version>.exe from an already-signed server artifact.
.DESCRIPTION
    Sibling of deploy\desktop\Build-DesktopInstaller.ps1. Stages the payload and calls ISCC.

    The payload carries the SIGNED zip plus its detached manifest and signature, unchanged. The
    wizard verifies them at install time exactly as a hand-run deployment would - the GUI path
    does not get a weaker trust chain than the scripted one, it just makes the publisher decision
    easier to make.

    The zip stays framework-dependent, like the released one. Publishing a self-contained copy
    for the installer would mean a later Update-NodePilot.ps1 run with a downloaded release
    overwrote a self-contained installation with framework-dependent binaries. The ASP.NET Core
    runtime is bundled as Microsoft's own installer instead - see Get-DotnetRuntimePayload.ps1.
.PARAMETER ArtifactPath
    The signed NodePilot-<version>.zip. Its .manifest.json and .manifest.json.p7s must sit beside it.
.PARAMETER TrustedSignerThumbprint
    Compiled into the wizard so the installer can verify the payload without asking the operator
    for it. Also shown on the Ready page: silently pinning a thumbprint nobody ever saw would be
    worse than today's explicit parameter.
.PARAMETER SignerCertificatePath
    Public part of the signing certificate, shipped so the wizard can offer to trust it.
.PARAMETER Version
    Installer version. Derived from the artifact file name when omitted.
.PARAMETER IsccPath
    Inno Setup compiler. Probed via Resolve-IsccPath.ps1 when omitted.
.PARAMETER RuntimeInstallerPath
    A pre-fetched ASP.NET Core runtime installer. Fetched and verified when omitted.
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
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$deployRoot = Split-Path -Parent $scriptDirectory
$repoRoot = Split-Path -Parent $deployRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $scriptDirectory 'out' }

function Write-Step { param([string]$Text) Write-Host "[server-setup] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[server-setup] $Text" -ForegroundColor Gray }

function Invoke-Tool {
    <#
      Native tools write progress to stderr, which PowerShell would otherwise escalate into a
      terminating error under $ErrorActionPreference = 'Stop'. Judge success by the exit code, the
      way Build-DesktopInstaller.ps1 does.
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
#   payload\  everything setup needs while it RUNS, shipped with Inno's dontcopy flag and
#             extracted to {tmp} - the readiness page and PrepareToInstall both run before a
#             single file has been installed.
#   deploy\   the copy that stays on disk, for the uninstaller and for later manual use.
#
# They must not share source paths. Inno deduplicates identical source files, so listing the same
# file both dontcopy and with a DestDir collapses into one entry and the dontcopy variant silently
# disappears - observed as scripts landing in a literal "{app}\deploy" folder under {tmp}.
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
    'SetupContract.ps1'
    'Invoke-NodePilotSetup.ps1'
    'Install-NodePilot.ps1'
    'Update-NodePilot.ps1'
    'Uninstall-NodePilot.ps1'
    'Provision-NodePilotDatabase.ps1'
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
Write-Info "  $($stagedRuntime.Name)"

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
    # nothing, and debugging "no files matching aspnetcore-runtime-*.exe" on a target machine is a
    # poor use of anyone's evening.
    "/DRuntimeFileName=$($stagedRuntime.Name)",
    (Join-Path $scriptDirectory 'NodePilotServer.iss'))

$installer = Join-Path $OutputRoot "NodePilot-Server-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "ISCC reported success but $installer does not exist."
}
Write-Step ("Built {0} ({1:N1} MB)" -f $installer, ((Get-Item $installer).Length / 1MB))
Write-Info '  Authenticode-sign it before distribution (Build-Artifact.ps1 does this for you).'
return $installer
