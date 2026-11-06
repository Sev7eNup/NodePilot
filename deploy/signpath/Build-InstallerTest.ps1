#requires -Version 5.1
<#
.SYNOPSIS
    Builds both installers on a disposable GitHub runner with a temporary CMS signer.
.DESCRIPTION
    Test artifacts only. The CMS private key never leaves the runner. SignPath applies
    Authenticode to the returned installers in subsequent workflow steps.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 3.0

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer signing tests must run on a disposable GitHub-hosted runner.'
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$toolRoot = Join-Path $env:RUNNER_TEMP ('nodepilot-signpath-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $toolRoot)

function Get-PinnedDownload {
    param([string]$Uri, [string]$Path, [string]$Sha256)
    Write-Host "[signpath-test] Downloading $(Split-Path $Path -Leaf)"
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Downloaded build input has an unexpected SHA-256: $Uri"
    }
    Write-Host "[signpath-test] Download verified: $(Split-Path $Path -Leaf)"
}

$innoInstaller = Join-Path $toolRoot 'innosetup-6.7.4.exe'
Get-PinnedDownload `
    -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' `
    -Path $innoInstaller `
    -Sha256 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
if ((Get-AuthenticodeSignature -LiteralPath $innoInstaller).Status -ne 'Valid') {
    throw 'The pinned Inno Setup installer does not have a valid Authenticode signature.'
}
$innoRoot = Join-Path $toolRoot 'inno'
Write-Host '[signpath-test] Installing the pinned Inno Setup compiler'
$process = Start-Process -FilePath $innoInstaller -WindowStyle Hidden -Wait -PassThru `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $innoRoot + '"'))
if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed: $($process.ExitCode)" }
$iscc = Join-Path $innoRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'ISCC.exe is missing after installation.' }

$pgArchive = Join-Path $toolRoot 'postgresql-16.15-1-windows-x64-binaries.zip'
Get-PinnedDownload `
    -Uri 'https://get.enterprisedb.com/postgresql/postgresql-16.15-1-windows-x64-binaries.zip' `
    -Path $pgArchive `
    -Sha256 '25e6fcdfb8caec38691bf461125e7564508760666f7b8e5dc6a5f0818f58f81e'
Write-Host '[signpath-test] Extracting PostgreSQL binaries'
# Avoid the per-entry PowerShell overhead for the EDB archive's 20,000+ files.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($pgArchive, (Join-Path $toolRoot 'postgresql'))
$pgRoot = Join-Path $toolRoot 'postgresql\pgsql'
. (Join-Path $repoRoot 'deploy\desktop\Assert-DesktopRuntimePayload.ps1')
Assert-DesktopPostgresPayload -PgRootPath $pgRoot -ExpectedMajorVersion 16

# Use an explicitly reviewed pair from runtime-payload.lock.json instead of the
# downloader's latest-version default, which can advance beyond the lockfile.
$runtimeRoot = Join-Path $toolRoot 'dotnet-runtime'
Write-Host '[signpath-test] Staging the locked .NET 10.0.13 runtime installers'
[void](& (Join-Path $repoRoot 'deploy\Get-DotnetRuntimePayload.ps1') -Version '10.0.13' `
    -OutputDirectory $runtimeRoot -CachePath (Join-Path $toolRoot 'runtime-cache'))
Write-Host '[signpath-test] Installer prerequisites are ready'

[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) { throw 'Directory.Build.props does not declare a product version.' }
$version = $versionNode.InnerText
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'The product version is not a numeric installer version.' }

$certificate = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=NodePilot CI TEST ONLY manifest signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter ([DateTime]::Now.AddDays(7))
try {
    Write-Host "[signpath-test] Building server and desktop setup EXEs for $version"
    & (Join-Path $repoRoot 'deploy\Build-Artifact.ps1') -Version $version `
        -SigningCertificateThumbprint $certificate.Thumbprint `
        -IncludeServerInstaller -IncludeDesktopInstaller -PgBinariesPath $pgRoot -IsccPath $iscc `
        -RuntimePayloadPath $runtimeRoot

    $outRoot = Join-Path $repoRoot 'out'
    foreach ($name in @(
        "NodePilot-Server-Setup-$version.exe", "NodePilot-Desktop-Setup-$version.exe",
        "NodePilot-$version.zip", "NodePilot-$version.zip.manifest.json",
        "NodePilot-$version.zip.manifest.json.p7s", 'nodepilot-release-signing.cer'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $outRoot $name) -PathType Leaf)) {
            throw "Required test-build output is missing: $name"
        }
    }
    . (Join-Path $repoRoot 'deploy\ArtifactSecurity.ps1')
    [void](Assert-NodePilotSignedArtifact -ArtifactPath (Join-Path $outRoot "NodePilot-$version.zip") `
        -TrustedSignerThumbprint $certificate.Thumbprint)

    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding ascii
    "cms-thumbprint=$($certificate.Thumbprint)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding ascii
    'TEST ONLY: CMS manifests use a seven-day runner-local certificate. No production key was used.' |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding ascii
} finally {
    Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $certificate.Thumbprint) -Force
}
