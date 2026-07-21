#requires -Version 5.1
<#
.SYNOPSIS
    Builds a production-ready NodePilot artifact zip (backend + SPA + template).
.DESCRIPTION
    Runs "dotnet publish" on NodePilot.Api, builds the React SPA, merges wwwroot,
    copies the appsettings.Production.json.template, and packs everything into
    NodePilot-<version>.zip under .\out\.
.PARAMETER Version
    Version tag baked into the zip filename. Defaults to yyyyMMdd-HHmmss.
.PARAMETER Configuration
    dotnet build configuration. Defaults to Release.
.PARAMETER RuntimeIdentifier
    .NET RID. Defaults to win-x64.
.PARAMETER SkipFrontend
    Skip the npm build entirely (useful when only the backend changed and dist/ is warm).
.PARAMETER SkipNpmCi
    Skip "npm ci" (which wipes node_modules). Use when a running Vite dev-server or
    antivirus is holding file locks inside node_modules - the build then reuses the
    already-installed dependencies and just runs "npm run build".
.PARAMETER SigningCertificateThumbprint
    Code Signing certificate used for the detached CMS signature over the artifact manifest.
    Required unless AllowUnsignedDevelopmentArtifact is explicitly selected.
.PARAMETER AllowUnsignedDevelopmentArtifact
    Produces a local-only ZIP that production installers will reject. Never use for deployment.
.EXAMPLE
    .\deploy\Build-Artifact.ps1
.EXAMPLE
    .\deploy\Build-Artifact.ps1 -Version 2026.04.23 -Configuration Release
#>

[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [string]$Version = (Get-Date -Format "yyyyMMdd-HHmmss"),
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipFrontend,
    [switch]$SkipNpmCi,
    [Parameter(Mandatory, ParameterSetName = 'Signed')][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'UnsignedDevelopment')][switch]$AllowUnsignedDevelopmentArtifact
)

$ErrorActionPreference = 'Stop'
# Version 3.0 catches typos / uninitialised vars but stays compatible with the
# ErrorRecord objects that native-command wrappers (npm, dotnet) emit under PS7.
# `Latest` trips on an internal ".Statement" property access somewhere in the
# npm PS-shim path and aborts the build before npm even runs.
Set-StrictMode -Version 3.0

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$ApiCsproj = Join-Path $RepoRoot 'src\NodePilot.Api\NodePilot.Api.csproj'
$UiDir = Join-Path $RepoRoot 'src\nodepilot-ui'
$OutDir = Join-Path $RepoRoot 'out'
$StageDir = Join-Path $OutDir 'artifact'
$TemplateSrc = Join-Path $PSScriptRoot 'templates\appsettings.Production.json.template'
$DeploymentTemplateTest = Join-Path $PSScriptRoot 'Test-DeploymentTemplates.ps1'
$ZipPath = Join-Path $OutDir ("NodePilot-$Version.zip")
$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'

function Assert-RequiredTool {
    param([string]$Name, [string]$HowToInstall)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' not found on PATH. $HowToInstall"
    }
}

Write-Host "[build] Pre-flight checks" -ForegroundColor Cyan
Assert-RequiredTool -Name 'dotnet' -HowToInstall 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download.'
if (-not $SkipFrontend) {
    Assert-RequiredTool -Name 'npm.cmd' -HowToInstall 'Install Node.js LTS from https://nodejs.org.'
}
# Required for the git-tracked source snapshot (knowledge\source) the AI Chat source reader serves.
Assert-RequiredTool -Name 'git' -HowToInstall 'Install Git for Windows from https://git-scm.com/download/win.'
Assert-RequiredTool -Name 'tar' -HowToInstall 'tar.exe ships with Windows 10 1803+ (bsdtar). Update Windows or add tar.exe to PATH.'
if (-not (Test-Path $ApiCsproj)) { throw "API csproj not found at $ApiCsproj" }
if (-not $SkipFrontend -and -not (Test-Path (Join-Path $UiDir 'package.json'))) {
    throw "UI project not found at $UiDir"
}
if (-not (Test-Path $TemplateSrc)) { throw "Template missing: $TemplateSrc" }
if (-not (Test-Path $DeploymentTemplateTest)) { throw "Deployment template test missing: $DeploymentTemplateTest" }
if (-not (Test-Path $ArtifactSecurityScript)) { throw "Artifact security helper missing: $ArtifactSecurityScript" }
. $ArtifactSecurityScript

Write-Host "[build] Validate deployment templates" -ForegroundColor Cyan
& $DeploymentTemplateTest

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir | Out-Null
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

Write-Host "[build] dotnet publish ($Configuration/$RuntimeIdentifier)" -ForegroundColor Cyan
& dotnet publish $ApiCsproj `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained false `
    --output $StageDir `
    -p:UseAppHost=true `
    -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (-not $SkipFrontend) {
    # Invoke npm through cmd.exe to dodge the PS-shim (npm.ps1), which under PS 7 +
    # StrictMode throws PropertyNotFoundStrict on a ".Statement" property lookup
    # before the actual npm process is even started.
    Push-Location $UiDir
    try {
        if ($SkipNpmCi) {
            $nodeModules = Join-Path $UiDir 'node_modules'
            if (-not (Test-Path $nodeModules)) {
                throw "-SkipNpmCi was passed but $nodeModules does not exist. Drop the switch or run 'npm install' once."
            }
            Write-Host "[build] npm run build (skipping npm ci)" -ForegroundColor Cyan
        } else {
            Write-Host "[build] npm ci" -ForegroundColor Cyan
            cmd.exe /c 'npm ci'
            if ($LASTEXITCODE -ne 0) {
                throw ("npm ci failed with exit code $LASTEXITCODE. If this is a file lock (EPERM on " +
                       "node_modules), stop any running Vite dev server / editor and retry, or re-run " +
                       "with -SkipNpmCi to reuse the current node_modules.")
            }
            Write-Host "[build] npm run build" -ForegroundColor Cyan
        }
        # Temporarily lower ErrorActionPreference so that Vite/Rolldown warnings on stderr
        # (e.g. the harmless [EVAL] warning from @protobufjs/inquire) don't trigger
        # PS 5.1's NativeCommandError → terminating exception under Stop mode.
        # We still check $LASTEXITCODE to catch real npm failures.
        $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        cmd.exe /c 'npm run build'
        $npmBuildExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($npmBuildExit -ne 0) { throw "npm run build failed with exit code $npmBuildExit" }
    } finally {
        Pop-Location
    }
}

$DistDir = Join-Path $UiDir 'dist'
if (-not (Test-Path $DistDir)) {
    throw "Frontend build output not found at $DistDir. Run without -SkipFrontend or verify vite config."
}
$WwwRoot = Join-Path $StageDir 'wwwroot'
New-Item -ItemType Directory -Path $WwwRoot -Force | Out-Null
Write-Host "[build] Copy SPA → wwwroot" -ForegroundColor Cyan
Copy-Item (Join-Path $DistDir '*') $WwwRoot -Recurse -Force

# Ship a git-tracked source snapshot into knowledge\source so the global "AI Chat" knowledge
# assistant can serve source-code questions on a production Windows-service install. `git archive
# HEAD` emits ONLY tracked files → bin/obj, node_modules, dist and every gitignored secret
# (jwt-secret.key, appsettings.runtime.json, *.pfx/*.pem, .env, data-protection-keys/) fall out
# automatically. The reader applies a read-time DENY-list on top (the authoritative secret guard).
# The whole tracked tree ships (incl. deploy/, scripts/, tests/) — see docs/ai-features.md.
Write-Host "[build] Snapshot git-tracked source → knowledge\source" -ForegroundColor Cyan
$KnowledgeSource = Join-Path $StageDir 'knowledge\source'
New-Item -ItemType Directory -Path $KnowledgeSource -Force | Out-Null
$SnapshotTar = Join-Path $OutDir 'source-snapshot.tar'
if (Test-Path $SnapshotTar) { Remove-Item $SnapshotTar -Force }
# Native git/tar write progress to stderr; relax Stop so PS 5.1 doesn't turn that into a
# terminating error. Success/failure is read from $LASTEXITCODE.
$prevSnapEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
    & git -C $RepoRoot archive --format=tar --output="$SnapshotTar" HEAD
    if ($LASTEXITCODE -ne 0) { throw "git archive failed (exit $LASTEXITCODE)" }
    & tar -x -f "$SnapshotTar" -C "$KnowledgeSource"
    if ($LASTEXITCODE -ne 0) { throw "tar extract failed (exit $LASTEXITCODE)" }
} finally {
    $ErrorActionPreference = $prevSnapEap
    if (Test-Path $SnapshotTar) { Remove-Item $SnapshotTar -Force }
}
$srcFiles = @(Get-ChildItem -Path $KnowledgeSource -Recurse -File)
$srcMb = if ($srcFiles.Count -gt 0) { [Math]::Round((($srcFiles | Measure-Object -Property Length -Sum).Sum / 1MB), 1) } else { 0 }
Write-Host "         Source snapshot: $($srcFiles.Count) files, $srcMb MB" -ForegroundColor DarkGray

Write-Host "[build] Include appsettings.Production.json.template" -ForegroundColor Cyan
Copy-Item $TemplateSrc (Join-Path $StageDir 'appsettings.Production.json.template') -Force

$VersionFile = Join-Path $StageDir 'VERSION.txt'
"NodePilot artifact`r`nVersion : $Version`r`nBuilt   : $((Get-Date).ToString('o'))`r`nRID     : $RuntimeIdentifier" |
    Out-File -FilePath $VersionFile -Encoding ascii -Force

$ApiExe = Join-Path $StageDir 'NodePilot.Api.exe'
if (-not (Test-Path $ApiExe)) {
    throw "Expected NodePilot.Api.exe in staging, but it's missing. Check publish output."
}
$IndexHtml = Join-Path $WwwRoot 'index.html'
if (-not (Test-Path $IndexHtml)) {
    throw "Expected wwwroot\index.html in staging, but it's missing. Check Vite build."
}

Write-Host "[build] Generate extracted-file manifest" -ForegroundColor Cyan
[void](New-NodePilotExtractedFileManifest -RootPath $StageDir)

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Write-Host "[build] Compress → $ZipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $StageDir '*') -DestinationPath $ZipPath -Force

if ($AllowUnsignedDevelopmentArtifact) {
    Write-Warning "Unsigned development artifact created. Install-NodePilot.ps1 and Update-NodePilot.ps1 will reject it."
} else {
    Write-Host "[build] Sign artifact manifest" -ForegroundColor Cyan
    $signed = New-NodePilotSignedArtifactManifest `
        -ArtifactPath $ZipPath `
        -Version $Version `
        -SigningCertificateThumbprint $SigningCertificateThumbprint
    Write-Host "         Manifest : $($signed.ManifestPath)"
    Write-Host "         Signature: $($signed.SignaturePath)"
}

$sizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "[build] Done: $ZipPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "         Version: $Version"
Write-Host "         Deploy with: .\deploy\Install-NodePilot.ps1 -ArtifactPath '$ZipPath' ..."
