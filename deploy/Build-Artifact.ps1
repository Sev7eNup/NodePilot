#requires -Version 5.1
<#
.SYNOPSIS
    Builds a production-ready NodePilot artifact zip (backend + SPA + template), and
    optionally the desktop installer alongside it.
.DESCRIPTION
    Runs "dotnet publish" on NodePilot.Api, builds the React SPA, merges wwwroot,
    copies the appsettings.Production.json.template, and packs everything into
    NodePilot-<version>.zip under .\out\.

    With -IncludeDesktopInstaller it then chains deploy\desktop\Build-DesktopInstaller.ps1
    and drops NodePilot-Desktop-Setup-<version>.exe into the same .\out\ directory, so one
    release build produces both shipping targets under ONE version. A SHA256SUMS file over
    everything produced is written either way.
.PARAMETER Version
    Version tag baked into the artifact filenames, and shared with the desktop installer.
    Defaults to the <Version> from Directory.Build.props.
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
.PARAMETER IncludeDesktopInstaller
    Also build the Electron desktop installer and place it next to the server zip in .\out\.
    Needs Inno Setup 6 and a PostgreSQL binaries folder; when either is missing the desktop
    step is SKIPPED with a warning and the server zip is still produced.
.PARAMETER PgBinariesPath
    PostgreSQL 16 "pgsql" directory (from the EDB zip distribution), passed through to the
    desktop installer build. Only read when -IncludeDesktopInstaller is set.
.PARAMETER IsccPath
    Inno Setup 6 compiler, passed through to the desktop installer build. Only read when
    -IncludeDesktopInstaller is set; defaults to the desktop script's own default.
.EXAMPLE
    .\deploy\Build-Artifact.ps1
.EXAMPLE
    .\deploy\Build-Artifact.ps1 -Version 2026.04.23 -Configuration Release
.EXAMPLE
    # Full release drop: server zip + desktop installer + checksums, one version.
    .\deploy\Build-Artifact.ps1 -Version 1.0.1 -SigningCertificateThumbprint $tp `
        -IncludeDesktopInstaller -PgBinariesPath 'C:\Packages\pgsql'
#>

[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipFrontend,
    [switch]$SkipNpmCi,
    [Parameter(Mandatory, ParameterSetName = 'Signed')][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'UnsignedDevelopment')][switch]$AllowUnsignedDevelopmentArtifact,
    [switch]$IncludeDesktopInstaller,
    [string]$PgBinariesPath,
    [string]$IsccPath
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
$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
$DesktopBuildScript = Join-Path $PSScriptRoot 'desktop\Build-DesktopInstaller.ps1'
$BuildPropsPath = Join-Path $RepoRoot 'Directory.Build.props'

# Directory.Build.props is the single source of the product version (it also stamps the
# assemblies). Deriving the default from it keeps the server zip, the desktop installer and
# the compiled binaries on ONE number instead of the timestamp-vs-hand-typed drift that
# produced NodePilot-1.0.0-lab3.zip next to NodePilot-Desktop-Setup-1.0.3.exe.
if (-not $Version) {
    if (-not (Test-Path $BuildPropsPath)) { throw "Cannot derive -Version: $BuildPropsPath not found. Pass -Version explicitly." }
    $versionMatch = [regex]::Match((Get-Content $BuildPropsPath -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $versionMatch.Success) { throw "Cannot derive -Version: no <Version> element in $BuildPropsPath. Pass -Version explicitly." }
    $Version = $versionMatch.Groups[1].Value
    Write-Host "[build] Version $Version (from Directory.Build.props)" -ForegroundColor DarkGray
}

$ZipPath = Join-Path $OutDir ("NodePilot-$Version.zip")
$ChecksumPath = Join-Path $OutDir ("NodePilot-$Version.SHA256SUMS.txt")

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

# --- desktop installer pre-flight ------------------------------------------------------------
# Decided BEFORE the server build so a missing Inno Setup surfaces in second one rather than
# after a ten-minute publish. Missing prerequisites downgrade to a skip, never to a failure:
# the server zip must stay buildable on a machine that has no Inno Setup and no Postgres
# distribution lying around.
$buildDesktop = $false
$desktopSkipReasons = @()
if ($IncludeDesktopInstaller) {
    if (-not (Test-Path $DesktopBuildScript)) {
        $desktopSkipReasons += "Desktop build script missing: $DesktopBuildScript"
    }
    if (-not $PgBinariesPath) {
        $desktopSkipReasons += 'No -PgBinariesPath given. Point it at the "pgsql" folder of a PostgreSQL 16 zip distribution (https://www.enterprisedb.com/download-postgresql-binaries).'
    } elseif (-not (Test-Path -LiteralPath (Join-Path $PgBinariesPath 'bin\postgres.exe'))) {
        $desktopSkipReasons += "-PgBinariesPath does not look like a PostgreSQL install (no bin\postgres.exe): $PgBinariesPath"
    }
    $effectiveIscc = if ($IsccPath) { $IsccPath } else { 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
    if (-not (Test-Path -LiteralPath $effectiveIscc)) {
        $desktopSkipReasons += "Inno Setup 6 compiler not found at '$effectiveIscc'. Install it from https://jrsoftware.org/isdl.php or pass -IsccPath."
    }

    if ($desktopSkipReasons.Count -eq 0) {
        $buildDesktop = $true
        Write-Host "         Desktop installer: will be built" -ForegroundColor DarkGray
    } else {
        Write-Warning "Desktop installer will be SKIPPED - the server artifact is still built:"
        foreach ($reason in $desktopSkipReasons) { Write-Warning "  - $reason" }
    }
}

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

# --- PowerShell built-in modules -> <stage>\Modules -------------------------------------------
# Microsoft.PowerShell.SDK ships its built-in modules (Utility, Management, CimCmdlets, ...)
# under runtimes\win\lib\<tfm>\Modules, but the hosted runspace pool resolves them via
# $PSHOME\Modules, where $PSHOME is the directory holding System.Management.Automation.dll —
# after publish that is the stage root. Without this copy the in-process engine finds no
# cmdlet modules at all and every runScript fails with "The term 'Write-Output' is not
# recognized" (server-lab finding 2026-08-01; implicit WinPS compat used to mask this by
# delegating to powershell.exe and is deliberately disabled). Same staging as
# deploy\desktop\Build-DesktopInstaller.ps1.
Write-Host "[build] Staging PowerShell built-in modules" -ForegroundColor Cyan
$psModuleSource = Get-ChildItem -Path (Join-Path $StageDir 'runtimes\win\lib') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'Modules' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $psModuleSource) { throw "PowerShell built-in modules not found under $StageDir\runtimes\win\lib\*\Modules." }
Copy-Item -Path $psModuleSource -Destination (Join-Path $StageDir 'Modules') -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $StageDir 'Modules\Microsoft.PowerShell.Utility'))) {
    throw 'Module staging failed: Microsoft.PowerShell.Utility missing under <stage>\Modules.'
}

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
            # Same stderr guard as `npm run build` below: npm emits warnings (e.g. EBADENGINE
            # from transitive deps) on stderr, which PS 5.1 turns into a terminating
            # NativeCommandError under Stop mode even though npm exits 0. Worse, the abort
            # happens mid-install and leaves node_modules half-wiped. Exit code stays the
            # source of truth.
            $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            cmd.exe /c 'npm ci'
            $npmCiExit = $LASTEXITCODE
            $ErrorActionPreference = $prevEap
            if ($npmCiExit -ne 0) {
                throw ("npm ci failed with exit code $npmCiExit. If this is a file lock (EPERM on " +
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

# --- desktop installer ------------------------------------------------------------------------
# Chained rather than duplicated: the desktop script owns its own staging (self-contained publish,
# Electron package, Postgres subset, Inno Setup). It gets -SkipSpaBuild because the SPA was just
# built above and both targets consume the same src\nodepilot-ui\dist. The two dotnet publishes
# stay separate on purpose - the server zip is framework-dependent, the desktop payload is not.
$desktopInstaller = $null
if ($buildDesktop) {
    Write-Host ""
    Write-Host "[build] Desktop installer (this takes a while - Electron + Postgres + Inno Setup)" -ForegroundColor Cyan
    $desktopArgs = @{
        PgBinariesPath = $PgBinariesPath
        Version        = $Version
        Configuration  = $Configuration
        SkipSpaBuild   = $true
    }
    if ($IsccPath) { $desktopArgs['IsccPath'] = $IsccPath }
    & $DesktopBuildScript @desktopArgs

    $desktopOut = Join-Path (Split-Path $DesktopBuildScript -Parent) "out\NodePilot-Desktop-Setup-$Version.exe"
    if (-not (Test-Path -LiteralPath $desktopOut)) {
        throw "Desktop build reported success but the installer is missing: $desktopOut"
    }
    $desktopInstaller = Join-Path $OutDir "NodePilot-Desktop-Setup-$Version.exe"
    Copy-Item -LiteralPath $desktopOut -Destination $desktopInstaller -Force
    Write-Host "         Copied → $desktopInstaller" -ForegroundColor DarkGray
}

# --- checksums --------------------------------------------------------------------------------
# One file covering everything this run produced, so a downloader can verify the drop without
# knowing which pieces are supposed to exist.
Write-Host "[build] Write SHA256SUMS" -ForegroundColor Cyan
$artifacts = @($ZipPath)
if (-not $AllowUnsignedDevelopmentArtifact) {
    $artifacts += "$ZipPath.manifest.json"
    $artifacts += "$ZipPath.manifest.json.p7s"
}
if ($desktopInstaller) { $artifacts += $desktopInstaller }
$checksumLines = foreach ($artifact in $artifacts) {
    if (-not (Test-Path -LiteralPath $artifact)) { throw "Checksum target missing: $artifact" }
    # "<hash>  <name>" - two spaces, the sha256sum/certutil-compatible layout.
    '{0}  {1}' -f (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $artifact -Leaf)
}
$checksumLines | Out-File -FilePath $ChecksumPath -Encoding ascii -Force

# --- summary ----------------------------------------------------------------------------------
$sizeMb = [Math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "[build] Done - version $Version" -ForegroundColor Green
Write-Host "         $(Split-Path $ZipPath -Leaf) ($sizeMb MB)"
if (-not $AllowUnsignedDevelopmentArtifact) {
    Write-Host "         $(Split-Path $ZipPath -Leaf).manifest.json + .p7s"
}
if ($desktopInstaller) {
    $desktopMb = [Math]::Round((Get-Item $desktopInstaller).Length / 1MB, 1)
    Write-Host "         $(Split-Path $desktopInstaller -Leaf) ($desktopMb MB)"
}
Write-Host "         $(Split-Path $ChecksumPath -Leaf)"
Write-Host "         all under $OutDir"
Write-Host ""
Write-Host "         Deploy the server with: .\deploy\Install-NodePilot.ps1 -ArtifactPath '$ZipPath' ..."
if ($desktopInstaller) {
    Write-Host "         Authenticode-sign the installer before distribution: signtool sign /fd SHA256 ... '$desktopInstaller'" -ForegroundColor Yellow
}
if ($IncludeDesktopInstaller -and -not $buildDesktop) {
    Write-Host "         Desktop installer was SKIPPED - see the warnings above." -ForegroundColor Yellow
}
