#requires -Version 5.1
<#
.SYNOPSIS
    Builds a production-ready NodePilot artifact zip (backend + SPA + template), and
    optionally the desktop installer alongside it.
.DESCRIPTION
    Runs "dotnet publish" on NodePilot.Api, builds the React SPA and the documentation site,
    merges both into wwwroot (the docs land in wwwroot\docs, which the API serves at /docs),
    copies the appsettings.Production.json.template, and packs everything into
    NodePilot-<version>.zip under .\out\.

    With -IncludeDesktopInstaller it then chains deploy\desktop\Build-DesktopInstaller.ps1
    and drops NodePilot-Desktop-Setup-<version>.exe into the same .\out\ directory, so one
    release build produces both shipping targets under a single version. A SHA256SUMS file
    over everything produced is written either way.
.PARAMETER Version
    Version tag baked into the artifact filenames, and shared with the desktop installer.
    Defaults to the <Version> from Directory.Build.props.
.PARAMETER Configuration
    dotnet build configuration. Defaults to Release.
.PARAMETER RuntimeIdentifier
    .NET RID. Defaults to win-x64.
.PARAMETER SkipFrontend
    Skip both npm builds entirely - the app SPA and the documentation site (useful when only
    the backend changed and both dist/ directories are warm).
.PARAMETER SkipNpmCi
    Skip "npm ci" (which wipes node_modules) for both npm builds. Use when a running Vite
    dev-server or antivirus is holding file locks inside node_modules - the build then reuses
    the already-installed dependencies and just runs "npm run build".
.PARAMETER SigningCertificateThumbprint
    Code Signing certificate used for the detached CMS signature over the artifact manifest.
    Required unless AllowUnsignedDevelopmentArtifact is explicitly selected.
.PARAMETER AllowUnsignedDevelopmentArtifact
    Produces a local-only ZIP that production installers will reject. Never use for deployment.
.PARAMETER IncludeDesktopInstaller
    Also build the Electron desktop installer and place it next to the server zip in .\out\.
    Needs Inno Setup 6 and a PostgreSQL binaries folder; when either is missing the desktop
    step is skipped with a warning and the server zip is still produced.
.PARAMETER PgBinariesPath
    PostgreSQL 16 "pgsql" directory (from the EDB zip distribution). The desktop installer bundles
    the whole server runtime from it; the server setup takes only the psql client, so its wizard
    can create a PostgreSQL role and database the way it creates a SQL Server login. Optional for
    the server setup, which says on its readiness page when it was built without it. Release
    builds pass it.
.PARAMETER IsccPath
    Inno Setup 6 compiler, passed through to the desktop installer build. Only read when
    -IncludeDesktopInstaller is set; defaults to the desktop script's own default.
.PARAMETER IncludeServerInstaller
    Also build the GUI setup for the Windows-service deployment and place it next to the server
    zip in .\out\. Needs Inno Setup 6 and a signed artifact; when either is missing the step is
    skipped with a warning and the server zip is still produced.
.PARAMETER RuntimePayloadPath
    A pre-fetched ASP.NET Core runtime installer for the server setup payload. Downloaded and
    verified by deploy\Get-DotnetRuntimePayload.ps1 when omitted.
.PARAMETER InstallerSigningCertificateThumbprint
    Authenticode-sign every installer this run produces with this certificate, before the
    checksums are written. Signing afterwards by hand invalidates the SHA256SUMS entry for the
    .exe, which is why this is a build parameter rather than a follow-up step.
    Signing does not silence SmartScreen: the release certificate is self-signed and carries no
    reputation, so a downloaded installer raises the unrecognised-app prompt whether it is
    signed or not (docs/deployment-guide.md, "First run: the SmartScreen prompt").
.EXAMPLE
    .\deploy\Build-Artifact.ps1
.EXAMPLE
    .\deploy\Build-Artifact.ps1 -Version 2026.04.23 -Configuration Release
.EXAMPLE
    # Full release drop: server zip + server setup + desktop installer + checksums, one version,
    # every installer Authenticode-signed before the checksums are written.
    .\deploy\Build-Artifact.ps1 -Version 1.2.0 -SigningCertificateThumbprint $tp `
        -IncludeServerInstaller -IncludeDesktopInstaller -PgBinariesPath 'C:\Packages\pgsql' `
        -InstallerSigningCertificateThumbprint $tp
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
    [switch]$IncludeServerInstaller,
    [string]$PgBinariesPath,
    [string]$IsccPath,
    [string]$RuntimePayloadPath,
    [string]$InstallerSigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
# Version 3.0 catches typos and uninitialised variables but stays compatible with the ErrorRecord
# objects that native-command wrappers (npm, dotnet) emit under PS7. Latest trips on an internal
# ".Statement" property access in the npm PS-shim path and aborts the build before npm runs.
Set-StrictMode -Version 3.0

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$ApiCsproj = Join-Path $RepoRoot 'src\NodePilot.Api\NodePilot.Api.csproj'
# Operator clients. Both are HTTP-only clients against the REST API, so they carry no server
# configuration and no privileged path. They ship with the artifact because np is the documented
# way to drive NodePilot from a script and nodepilot-mcp is the way to point an AI agent at an
# installation.
$CliCsproj = Join-Path $RepoRoot 'src\NodePilot.Cli\NodePilot.Cli.csproj'
$McpCsproj = Join-Path $RepoRoot 'src\NodePilot.Mcp\NodePilot.Mcp.csproj'
$SwitcherCsproj = Join-Path $RepoRoot 'src\NodePilot.EngineSwitcher\NodePilot.EngineSwitcher.csproj'
$UiDir = Join-Path $RepoRoot 'src\nodepilot-ui'
$DocsUiDir = Join-Path $RepoRoot 'src\nodepilot-docs-ui'
$OutDir = Join-Path $RepoRoot 'out'
$StageDir = Join-Path $OutDir 'artifact'
$TemplateSrc = Join-Path $PSScriptRoot 'templates\appsettings.Production.json.template'
$DeploymentTemplateTest = Join-Path $PSScriptRoot 'Test-DeploymentTemplates.ps1'
$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
$PublishSettingsHygieneScript = Join-Path $PSScriptRoot 'Assert-PublishSettingsHygiene.ps1'
$DesktopBuildScript = Join-Path $PSScriptRoot 'desktop\Build-DesktopInstaller.ps1'
$ServerBuildScript = Join-Path $PSScriptRoot 'server\Build-ServerInstaller.ps1'
$BuildPropsPath = Join-Path $RepoRoot 'Directory.Build.props'
$SdkPolicyScript = Join-Path $RepoRoot 'scripts\Assert-DotnetSdkPolicy.ps1'

# Directory.Build.props is the single source of the product version and also stamps the
# assemblies. Deriving the default from it keeps the server zip, the desktop installer and the
# compiled binaries on one number.
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

function Invoke-NodePilotAuthenticodeSign {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Thumbprint
    )
    $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) {
        throw 'signtool.exe not found - install the Windows SDK or omit the Authenticode signing thumbprint.'
    }

    Write-Host "[build] Authenticode-sign $(Split-Path $Path -Leaf)" -ForegroundColor Cyan
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        & $signTool.FullName sign /sha1 $Thumbprint /fd SHA256 /td SHA256 `
            /tr 'http://timestamp.digicert.com' /d $Description $Path
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) { throw "signtool failed with exit code $exitCode." }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "$(Split-Path $Path -Leaf) is not signed by the requested certificate after signtool reported success."
    }
    Write-Host "         Signed by $($signature.SignerCertificate.Subject)" -ForegroundColor DarkGray
}

Write-Host "[build] Pre-flight checks" -ForegroundColor Cyan
Assert-RequiredTool -Name 'dotnet' -HowToInstall 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download.'
if (-not (Test-Path -LiteralPath $SdkPolicyScript -PathType Leaf)) { throw "SDK policy helper missing: $SdkPolicyScript" }
. $SdkPolicyScript
Assert-NodePilotDotnetSdkPolicy -RepoRoot $RepoRoot
if (-not $SkipFrontend) {
    Assert-RequiredTool -Name 'npm.cmd' -HowToInstall 'Install Node.js LTS from https://nodejs.org.'
}
# Required for the git-tracked source snapshot (knowledge\source) the AI Chat source reader serves.
Assert-RequiredTool -Name 'git' -HowToInstall 'Install Git for Windows from https://git-scm.com/download/win.'
Assert-RequiredTool -Name 'tar' -HowToInstall 'tar.exe ships with Windows 10 1803+ (bsdtar). Update Windows or add tar.exe to PATH.'
if (-not (Test-Path $ApiCsproj)) { throw "API csproj not found at $ApiCsproj" }
if (-not (Test-Path $CliCsproj)) { throw "CLI csproj not found at $CliCsproj" }
if (-not (Test-Path $McpCsproj)) { throw "MCP csproj not found at $McpCsproj" }
if (-not $SkipFrontend -and -not (Test-Path (Join-Path $UiDir 'package.json'))) {
    throw "UI project not found at $UiDir"
}
if (-not $SkipFrontend -and -not (Test-Path (Join-Path $DocsUiDir 'package.json'))) {
    throw "Docs site project not found at $DocsUiDir"
}
if (-not (Test-Path $TemplateSrc)) { throw "Template missing: $TemplateSrc" }
if (-not (Test-Path $DeploymentTemplateTest)) { throw "Deployment template test missing: $DeploymentTemplateTest" }
if (-not (Test-Path $ArtifactSecurityScript)) { throw "Artifact security helper missing: $ArtifactSecurityScript" }
. $ArtifactSecurityScript
if (-not (Test-Path $PublishSettingsHygieneScript)) { throw "Publish settings hygiene helper missing: $PublishSettingsHygieneScript" }
. $PublishSettingsHygieneScript

# --- desktop installer pre-flight ------------------------------------------------------------
# Decided before the server build so a missing Inno Setup surfaces immediately rather than after
# a long publish. Missing prerequisites downgrade to a skip, never to a failure: the server zip
# must stay buildable on a machine that has no Inno Setup and no PostgreSQL distribution.
$buildDesktop = $false
$desktopSkipReasons = @()
# Declared up front: both pre-flight blocks read it, and either can be the only one that runs.
# StrictMode makes an undeclared read a hard error rather than a silent $null.
$resolvedIscc = $null
if ($IncludeDesktopInstaller) {
    if (-not (Test-Path $DesktopBuildScript)) {
        $desktopSkipReasons += "Desktop build script missing: $DesktopBuildScript"
    }
    if (-not $PgBinariesPath) {
        $desktopSkipReasons += 'No -PgBinariesPath given. Point it at the "pgsql" folder of a PostgreSQL 16 zip distribution (https://www.enterprisedb.com/download-postgresql-binaries).'
    } elseif (-not (Test-Path -LiteralPath (Join-Path $PgBinariesPath 'bin\postgres.exe'))) {
        $desktopSkipReasons += "-PgBinariesPath does not look like a PostgreSQL install (no bin\postgres.exe): $PgBinariesPath"
    }
    # Same resolver the desktop build uses, so this pre-flight cannot disagree with it about
    # where ISCC.exe lives (notably the per-user install location).
    . (Join-Path $PSScriptRoot 'desktop\Resolve-IsccPath.ps1')
    $resolvedIscc = Resolve-NodePilotIsccPath -Explicit $IsccPath
    if (-not $resolvedIscc) {
        $desktopSkipReasons += ("Inno Setup 6 compiler (ISCC.exe) not found. Install it from " +
            "https://jrsoftware.org/isdl.php or pass -IsccPath. Probed: " + ((Get-NodePilotIsccCandidates) -join '; '))
    } else {
        # Pass the resolved path on explicitly so the desktop build does not have to probe again.
        $IsccPath = $resolvedIscc
    }

    if ($desktopSkipReasons.Count -eq 0) {
        $buildDesktop = $true
        Write-Host "         Desktop installer: will be built" -ForegroundColor DarkGray
    } else {
        Write-Warning "Desktop installer will be SKIPPED - the server artifact is still built:"
        foreach ($reason in $desktopSkipReasons) { Write-Warning "  - $reason" }
    }
}

# --- server installer pre-flight ---------------------------------------------------------------
# Same rule as above: decided before the publish, and a missing prerequisite is a skip, never a
# failure. The server zip has to stay buildable on a machine with no Inno Setup.
$buildServerInstaller = $false
$serverSkipReasons = @()
if ($IncludeServerInstaller) {
    if (-not (Test-Path $ServerBuildScript)) {
        $serverSkipReasons += "Server setup build script missing: $ServerBuildScript"
    }
    if ($AllowUnsignedDevelopmentArtifact) {
        # The wizard runs the same Assert-NodePilotSignedArtifact as the scripted path, so an
        # unsigned payload would produce an installer that refuses its own contents.
        $serverSkipReasons += 'The server setup embeds the signed artifact and verifies it at install time, so it cannot be built from an -AllowUnsignedDevelopmentArtifact run.'
    }
    if (-not $resolvedIscc) {
        # Resolve once; the desktop pre-flight may not have run at all.
        . (Join-Path $PSScriptRoot 'desktop\Resolve-IsccPath.ps1')
        $resolvedIscc = Resolve-NodePilotIsccPath -Explicit $IsccPath
    }
    if (-not $resolvedIscc) {
        $serverSkipReasons += ("Inno Setup 6 compiler (ISCC.exe) not found. Install it from " +
            "https://jrsoftware.org/isdl.php or pass -IsccPath. Probed: " + ((Get-NodePilotIsccCandidates) -join '; '))
    } else {
        $IsccPath = $resolvedIscc
    }

    if ($serverSkipReasons.Count -eq 0) {
        $buildServerInstaller = $true
        Write-Host "         Server setup: will be built" -ForegroundColor DarkGray
    } else {
        Write-Warning "Server setup will be SKIPPED - the server artifact is still built:"
        foreach ($reason in $serverSkipReasons) { Write-Warning "  - $reason" }
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
# cmdlet modules at all and every runScript fails to resolve core commands. Implicit WinPS
# compatibility is disabled. Same staging as
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

# --- Operator clients -> <stage>\tools\{np,mcp} -----------------------------------------------
# Each client publishes into its OWN directory rather than alongside NodePilot.Api.exe. They
# bring their own copies of shared dependencies, and merging three publishes into one folder
# lets whichever runs last decide the assembly versions the service then loads. Separate
# directories also keep Assert-NodePilotExtractedFiles' "exactly the signed contents" check
# readable, and give Install-NodePilot.ps1 one directory to put on PATH.
Write-Host "[build] dotnet publish operator clients (np, nodepilot-mcp)" -ForegroundColor Cyan
foreach ($client in @(
        @{ Name = 'np';           Csproj = $CliCsproj; Exe = 'np.exe' },
        @{ Name = 'mcp';          Csproj = $McpCsproj; Exe = 'nodepilot-mcp.exe' })) {
    $clientOut = Join-Path $StageDir ("tools\" + $client.Name)
    & dotnet publish $client.Csproj `
        --configuration $Configuration `
        --runtime $RuntimeIdentifier `
        --self-contained false `
        --output $clientOut `
        -p:UseAppHost=true `
        -p:DebugType=embedded
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($client.Csproj) with exit code $LASTEXITCODE" }
    $clientExe = Join-Path $clientOut $client.Exe
    if (-not (Test-Path -LiteralPath $clientExe)) {
        throw "Expected $($client.Exe) in $clientOut, but it is missing. Check publish output."
    }
}

# The engine switcher is a local WPF utility and must remain runnable on the server install even
# though that installer provisions only the ASP.NET Core runtime. Publish it self-contained and
# single-file, then use the exact same bytes inside the server artifact and as the standalone drop.
Write-Host "[build] dotnet publish engine switcher (self-contained)" -ForegroundColor Cyan
$switcherOut = Join-Path $StageDir 'tools\engine-switcher'
& dotnet publish $SwitcherCsproj `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $switcherOut `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $SwitcherCsproj with exit code $LASTEXITCODE" }
$switcherExe = Join-Path $switcherOut 'NodePilot.EngineSwitcher.exe'
if (-not (Test-Path -LiteralPath $switcherExe -PathType Leaf)) {
    throw "Expected NodePilot.EngineSwitcher.exe in $switcherOut, but it is missing."
}
if ($InstallerSigningCertificateThumbprint) {
    Invoke-NodePilotAuthenticodeSign -Path $switcherExe -Description 'NodePilot Engine Switcher' `
        -Thumbprint $InstallerSigningCertificateThumbprint
}
# The standalone drop is a zip, not the bare exe: the switcher reads engine-switcher.json from
# next to itself, and a machine without a NodePilot installation has nowhere else to take the
# template from. Signing happens above, so the zip carries the signed bytes.
$switcherTemplate = Join-Path $switcherOut 'engine-switcher.json'
if (-not (Test-Path -LiteralPath $switcherTemplate -PathType Leaf)) {
    throw "Expected engine-switcher.json in $switcherOut, but it is missing."
}
$standaloneSwitcher = Join-Path $OutDir "NodePilot-EngineSwitcher-$Version-win-x64.zip"
if (Test-Path -LiteralPath $standaloneSwitcher) { Remove-Item -LiteralPath $standaloneSwitcher -Force }
Compress-Archive -Path $switcherExe, $switcherTemplate -DestinationPath $standaloneSwitcher -Force

# Builds one npm workspace. Two of them ship: the app SPA and the documentation site, which the
# API serves at /docs so a disconnected installation still has its runbooks.
function Invoke-NodePilotWebBuild {
    param(
        [Parameter(Mandatory)][string]$ProjectDir,
        [Parameter(Mandatory)][string]$Label
    )
    # Invoke npm through cmd.exe to dodge the PS-shim (npm.ps1), which under PS 7 +
    # StrictMode throws PropertyNotFoundStrict on a ".Statement" property lookup
    # before the actual npm process is even started.
    Push-Location $ProjectDir
    try {
        if ($SkipNpmCi) {
            $nodeModules = Join-Path $ProjectDir 'node_modules'
            if (-not (Test-Path $nodeModules)) {
                throw "-SkipNpmCi was passed but $nodeModules does not exist. Drop the switch or run 'npm install' once."
            }
            Write-Host "[build] $Label - npm run build (skipping npm ci)" -ForegroundColor Cyan
        } else {
            Write-Host "[build] $Label - npm ci" -ForegroundColor Cyan
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
                throw ("npm ci failed for $Label with exit code $npmCiExit. If this is a file lock " +
                       "(EPERM on node_modules), stop any running Vite dev server / editor and retry, " +
                       "or re-run with -SkipNpmCi to reuse the current node_modules.")
            }
            Write-Host "[build] $Label - npm run build" -ForegroundColor Cyan
        }
        # Temporarily lower ErrorActionPreference so that Vite/Rolldown warnings on stderr
        # (e.g. the harmless [EVAL] warning from @protobufjs/inquire) don't trigger
        # PS 5.1's NativeCommandError -> terminating exception under Stop mode.
        # We still check $LASTEXITCODE to catch real npm failures.
        $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        cmd.exe /c 'npm run build'
        $npmBuildExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($npmBuildExit -ne 0) { throw "npm run build failed for $Label with exit code $npmBuildExit" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipFrontend) {
    Invoke-NodePilotWebBuild -ProjectDir $UiDir -Label 'app SPA'
    Invoke-NodePilotWebBuild -ProjectDir $DocsUiDir -Label 'docs site'
}

$DistDir = Join-Path $UiDir 'dist'
if (-not (Test-Path $DistDir)) {
    throw "Frontend build output not found at $DistDir. Run without -SkipFrontend or verify vite config."
}
$WwwRoot = Join-Path $StageDir 'wwwroot'
New-Item -ItemType Directory -Path $WwwRoot -Force | Out-Null
Write-Host "[build] Copy SPA → wwwroot" -ForegroundColor Cyan
Copy-Item (Join-Path $DistDir '*') $WwwRoot -Recurse -Force

# The docs bundle is built with a relative Vite base and routes in the URL fragment, so it drops
# into a subdirectory as-is. Install-NodePilot.ps1 verifies it arrived.
$DocsDistDir = Join-Path $DocsUiDir 'dist'
if (-not (Test-Path $DocsDistDir)) {
    throw "Docs site build output not found at $DocsDistDir. Run without -SkipFrontend or verify vite config."
}
$DocsWwwRoot = Join-Path $WwwRoot 'docs'
New-Item -ItemType Directory -Path $DocsWwwRoot -Force | Out-Null
Write-Host "[build] Copy docs site → wwwroot\docs" -ForegroundColor Cyan
Copy-Item (Join-Path $DocsDistDir '*') $DocsWwwRoot -Recurse -Force

# Ship a git-tracked source snapshot into knowledge\source so the global "AI Chat" knowledge
# assistant can serve source-code questions on a production Windows-service install. `git archive
# HEAD` emits ONLY tracked files -> bin/obj, node_modules, dist and every gitignored secret
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
    & git -C $RepoRoot archive --format=tar --output="$SnapshotTar" HEAD -- . `
        ':(exclude)src/NodePilot.Api/appsettings.Development.json'
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
$DocsIndexHtml = Join-Path $DocsWwwRoot 'index.html'
if (-not (Test-Path $DocsIndexHtml)) {
    throw "Expected wwwroot\docs\index.html in staging, but it's missing. Check the docs-ui Vite build."
}

Write-Host "[build] Generate extracted-file manifest" -ForegroundColor Cyan
Assert-NodePilotPublishSettingsHygiene -RootPath $StageDir
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

# --- server setup installer -------------------------------------------------------------------
# Runs AFTER the manifest signature exists: the setup embeds the signed zip plus its sidecars and
# verifies them at install time, so it cannot be built before there is a signature to embed.
$serverInstaller = $null
if ($buildServerInstaller) {
    Write-Host ""
    Write-Host "[build] Server setup (GUI installer for the Windows service)" -ForegroundColor Cyan
    $serverArgs = @{
        ArtifactPath            = $ZipPath
        TrustedSignerThumbprint = $SigningCertificateThumbprint
        Version                 = $Version
    }
    if ($IsccPath) { $serverArgs['IsccPath'] = $IsccPath }
    if ($RuntimePayloadPath) { $serverArgs['RuntimeInstallerPath'] = $RuntimePayloadPath }
    # The same input the desktop build takes, and optional here: the server setup only lifts the
    # psql CLIENT out of it so the wizard can create a PostgreSQL role and database the way it
    # already creates a SQL Server login. Without it the installer is built exactly as before and
    # says on its readiness page that the fix is unavailable in this build.
    if ($PgBinariesPath) { $serverArgs['PgBinariesPath'] = $PgBinariesPath }
    & $ServerBuildScript @serverArgs | Out-Null

    $serverOut = Join-Path (Split-Path $ServerBuildScript -Parent) "out\NodePilot-Server-Setup-$Version.exe"
    if (-not (Test-Path -LiteralPath $serverOut)) {
        throw "Server setup build reported success but the installer is missing: $serverOut"
    }
    $serverInstaller = Join-Path $OutDir "NodePilot-Server-Setup-$Version.exe"
    Copy-Item -LiteralPath $serverOut -Destination $serverInstaller -Force
    Write-Host "         Copied -> $serverInstaller" -ForegroundColor DarkGray
}

# --- Authenticode signing ---------------------------------------------------------------------
# Signing has to happen HERE, before the checksum step. Signing an .exe afterwards rewrites the
# file and silently invalidates its SHA256SUMS entry - a downloader following the verification
# instructions would then be told the artifact is corrupt.
#
# One loop over every installer this run produced, rather than a block per target: a second
# hand-maintained copy is how the two drift apart, and the ordering contract only pins one of them.
$installersToSign = @()
if ($desktopInstaller) { $installersToSign += @{ Path = $desktopInstaller; Description = 'NodePilot Desktop' } }
if ($serverInstaller) { $installersToSign += @{ Path = $serverInstaller; Description = 'NodePilot Server Setup' } }

if ($InstallerSigningCertificateThumbprint -and $installersToSign.Count -gt 0) {
    foreach ($target in $installersToSign) {
        Invoke-NodePilotAuthenticodeSign -Path $target.Path -Description $target.Description `
            -Thumbprint $InstallerSigningCertificateThumbprint
    }
}

# --- operator scripts -------------------------------------------------------------------------
# The deployment scripts, as their own small archive covered by SHA256SUMS.
#
# Option A of docs/deployment-guide.md ("download the published release") listed the artifact and
# then told the reader to run .\deploy\Install-NodePilot.ps1 - a file that appeared in no download.
# The scripts do travel inside the artifact, but only buried under knowledge\source\ for the AI
# assistant, and taking them from there would be worse than useless: you would have to extract the
# UNVERIFIED archive to obtain the very script whose job is to verify it. Shipping them separately
# keeps the signature check meaningful - verify this zip against SHA256SUMS, then let the script it
# contains verify the artifact.
Write-Host "[build] Pack deployment scripts" -ForegroundColor Cyan
$DeployScriptsZip = Join-Path $OutDir "NodePilot-Deploy-Scripts-$Version.zip"
$DeployStage = Join-Path $OutDir "deploy-scripts-stage"
if (Test-Path $DeployStage) { Remove-Item $DeployStage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $DeployStage 'deploy\templates') -Force | Out-Null
# Entry points plus every file they dot-source. Keep this list in step with the `Join-Path
# $PSScriptRoot` references in the three entry-point scripts - a missing helper turns into a
# "script not found" throw on the target machine, after the operator has already started.
$deployScriptFiles = @(
    'Install-NodePilot.ps1'              # entry point
    'Update-NodePilot.ps1'               # entry point
    'Uninstall-NodePilot.ps1'            # entry point
    'ArtifactSecurity.ps1'               # dot-sourced by install + update
    'Preflight.ps1'                      # dot-sourced by install
    'ServiceControl.ps1'                 # dot-sourced by install + update
    'MachinePath.ps1'                    # dot-sourced by install + update + uninstall
    'Provision-NodePilotDatabase.ps1'    # optional: create the SQL Server login/database
    'Provision-NodePilotPostgres.ps1'    # optional: same for PostgreSQL
    'New-NodePilotSelfSignedCertificate.ps1'  # optional: lab certificates
    'README.md'                          # the operator reference
)
foreach ($name in $deployScriptFiles) {
    $source = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $source)) { throw "Deployment script missing from the build: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $DeployStage 'deploy') -Force
}
Copy-Item -Path (Join-Path $PSScriptRoot 'templates\*') -Destination (Join-Path $DeployStage 'deploy\templates') -Force
if (Test-Path $DeployScriptsZip) { Remove-Item $DeployScriptsZip -Force }
Compress-Archive -Path (Join-Path $DeployStage 'deploy') -DestinationPath $DeployScriptsZip -Force
Remove-Item $DeployStage -Recurse -Force
Write-Host "         $(Split-Path $DeployScriptsZip -Leaf) ($($deployScriptFiles.Count) scripts + templates)" -ForegroundColor DarkGray

# --- publisher certificate --------------------------------------------------------------------
# The public half of the ARTIFACT signer - the certificate whose thumbprint a deployer passes to
# Install-NodePilot.ps1 as -TrustedArtifactSignerThumbprint. docs/deployment-guide.md tells the
# downloader to read the thumbprint out of this file and compare it against the release notes, and
# calls that comparison "the trust decision".
#
# Produce the trust artifact as a build output and include it in SHA256SUMS with the archives.
$publisherCertPath = $null
if (-not $AllowUnsignedDevelopmentArtifact) {
    Write-Host "[build] Export publisher certificate" -ForegroundColor Cyan
    $normalizedSigner = ($SigningCertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $publisherCert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $normalizedSigner } | Select-Object -First 1
    if (-not $publisherCert) {
        throw ("No certificate with thumbprint $normalizedSigner found in the local stores. The " +
               "artifact was signed with it, so its public half must be exportable for the release.")
    }
    $publisherCertPath = Join-Path $OutDir 'nodepilot-release-signing.cer'
    [IO.File]::WriteAllBytes($publisherCertPath, $publisherCert.Export('Cert'))
    Write-Host "         $(Split-Path $publisherCertPath -Leaf) - $($publisherCert.Subject)" -ForegroundColor DarkGray
    Write-Host "         Thumbprint: $($publisherCert.Thumbprint) (publish this in the release notes)" -ForegroundColor DarkGray
}

# --- checksums --------------------------------------------------------------------------------
# One file covering everything this run produced, so a downloader can verify the drop without
# knowing which pieces are supposed to exist.
Write-Host "[build] Write SHA256SUMS" -ForegroundColor Cyan
$artifacts = @($ZipPath, $DeployScriptsZip, $standaloneSwitcher)
if (-not $AllowUnsignedDevelopmentArtifact) {
    $artifacts += "$ZipPath.manifest.json"
    $artifacts += "$ZipPath.manifest.json.p7s"
    $artifacts += $publisherCertPath
}
if ($desktopInstaller) { $artifacts += $desktopInstaller }
if ($serverInstaller) { $artifacts += $serverInstaller }
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
Write-Host "         $(Split-Path $DeployScriptsZip -Leaf)"
Write-Host "         $(Split-Path $standaloneSwitcher -Leaf)"
if (-not $AllowUnsignedDevelopmentArtifact) {
    Write-Host "         $(Split-Path $ZipPath -Leaf).manifest.json + .p7s"
    Write-Host "         nodepilot-release-signing.cer (attach to the release; its thumbprint goes in the notes)"
}
if ($desktopInstaller) {
    $desktopMb = [Math]::Round((Get-Item $desktopInstaller).Length / 1MB, 1)
    Write-Host "         $(Split-Path $desktopInstaller -Leaf) ($desktopMb MB)"
}
if ($serverInstaller) {
    $serverMb = [Math]::Round((Get-Item $serverInstaller).Length / 1MB, 1)
    Write-Host "         $(Split-Path $serverInstaller -Leaf) ($serverMb MB)"
}
Write-Host "         $(Split-Path $ChecksumPath -Leaf)"
Write-Host "         all under $OutDir"
Write-Host ""
Write-Host "         Deploy the server with: .\deploy\Install-NodePilot.ps1 -ArtifactPath '$ZipPath' ..."
if ($serverInstaller) {
    Write-Host "         ...or hand an operator NodePilot-Server-Setup-$Version.exe instead."
}
if ($installersToSign.Count -gt 0 -and -not $InstallerSigningCertificateThumbprint) {
    Write-Host "         The installers are UNSIGNED. Re-run with -InstallerSigningCertificateThumbprint to sign them;" -ForegroundColor Yellow
    Write-Host "         signing by hand afterwards would invalidate their entries in $(Split-Path $ChecksumPath -Leaf)." -ForegroundColor Yellow
}
if ($IncludeDesktopInstaller -and -not $buildDesktop) {
    Write-Host "         Desktop installer was SKIPPED - see the warnings above." -ForegroundColor Yellow
}
if ($IncludeServerInstaller -and -not $buildServerInstaller) {
    Write-Host "         Server setup was SKIPPED - see the warnings above." -ForegroundColor Yellow
}
