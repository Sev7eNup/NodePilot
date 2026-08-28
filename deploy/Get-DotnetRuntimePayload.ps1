#requires -Version 5.1

<#
.SYNOPSIS
    Fetches and verifies the ASP.NET Core runtime installer that the server setup bundles.
.DESCRIPTION
    Build-time helper. The installer is never committed to the repository; it is downloaded,
    verified, cached, and staged into the payload.

    Which installer: the standalone ASP.NET Core runtime, never dotnet-hosting-*.exe. The Hosting
    Bundle rewires IIS and restarts W3SVC, which is unwanted on shared hosts such as an SCCM site
    server.

    Three independent checks before anything is staged:

      1. The SHA512 published in Microsoft's release metadata, which is the digest that metadata
         carries.
      2. A pin in runtime-payload.lock.json, so the payload is reproducible and any change to it
         shows up in a diff and has to be reviewed. The first run for a version writes the pin;
         later runs must match it.
      3. An Authenticode signature that is Valid and issued to Microsoft.

    The committed pin is what makes the vendor hash a pin at all: on its own that hash only proves
    the download matches what the same request was told to expect.
.PARAMETER Version
    Exact patched runtime version, e.g. '10.0.11'. Versions below 10.0.11 are rejected. Defaults
    to the latest 10.0.x in the release metadata.
.PARAMETER OutputDirectory
    Where the verified installer is staged. Created if missing.
.PARAMETER CachePath
    Download cache, so an offline build host works after the first fetch.
.PARAMETER LockFilePath
    The committed version-to-hash pin. Defaults to deploy/server/runtime-payload.lock.json.
.PARAMETER UpdateLockFile
    Allow writing a new or changed pin. Without it, a mismatch is a hard failure.
.OUTPUTS
    The full path of the staged installer.
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$CachePath,
    [string]$LockFilePath,
    [switch]$UpdateLockFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($CachePath)) {
    $CachePath = Join-Path $env:LOCALAPPDATA 'NodePilot\build-cache'
}
if ([string]::IsNullOrWhiteSpace($LockFilePath)) {
    $LockFilePath = Join-Path $scriptDirectory 'server\runtime-payload.lock.json'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptDirectory 'server\payload\runtime'
}

$ReleaseMetadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$MinimumSupportedRuntime = [version]'10.0.11'
# The metadata's 'name' field is unversioned ('aspnetcore-runtime-win-x64.exe'); the versioned
# file name lives in the URL. Match the standalone runtime exactly - dotnet-hosting-*.exe and the
# .zip / composite / targeting-pack entries all sit in the same list.
$RuntimeFileName = 'aspnetcore-runtime-win-x64.exe'

function Write-Step { param([string]$Text) Write-Host "[runtime] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[runtime] $Text" -ForegroundColor Gray }

function Get-Sha512 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToUpperInvariant()
}

function Read-LockFile {
    if (-not (Test-Path -LiteralPath $LockFilePath -PathType Leaf)) { return @{} }
    $raw = Get-Content -LiteralPath $LockFilePath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
    $parsed = $raw | ConvertFrom-Json
    $map = @{}
    foreach ($property in $parsed.PSObject.Properties) { $map[$property.Name] = [string]$property.Value }
    return $map
}

function Write-LockFile {
    param([Parameter(Mandatory)][hashtable]$Map)
    $ordered = [ordered]@{}
    foreach ($key in ($Map.Keys | Sort-Object)) { $ordered[$key] = $Map[$key] }
    $directory = Split-Path -Parent $LockFilePath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    ($ordered | ConvertTo-Json) | Set-Content -LiteralPath $LockFilePath -Encoding UTF8
}

# ---------------------------------------------------------------------------

# TLS 1.2 must be forced: Windows PowerShell 5.1 still defaults to the legacy protocol list and
# the Microsoft CDN refuses it. Never relax certificate validation here - this is a supply-chain
# boundary, not a convenience download.
[Net.ServicePointManager]::SecurityProtocol =
    [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11

Write-Step "Reading release metadata"
$metadata = Invoke-RestMethod -Uri $ReleaseMetadataUrl -UseBasicParsing

$candidates = @()
foreach ($release in $metadata.releases) {
    if ($release.PSObject.Properties.Name -notcontains 'aspnetcore-runtime') { continue }
    $runtime = $release.'aspnetcore-runtime'
    if (-not $runtime -or -not $runtime.files) { continue }
    $file = $runtime.files | Where-Object { $_.name -eq $RuntimeFileName } | Select-Object -First 1
    if (-not $file) { continue }
    $candidates += [pscustomobject]@{
        Version = [string]$runtime.version
        # From the URL, not from 'name': the metadata's name field carries no version.
        Name    = [string]([uri]$file.url).Segments[-1]
        Url     = [string]$file.url
        Hash    = ([string]$file.hash).ToUpperInvariant()
    }
}
if ($candidates.Count -eq 0) {
    throw "No '$RuntimeFileName' entry found in the release metadata at $ReleaseMetadataUrl."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    # The metadata lists newest first, but sort explicitly rather than trust document order.
    $selected = $candidates | Sort-Object { [version]($_.Version -replace '-.*$', '') } -Descending |
        Select-Object -First 1
    $Version = $selected.Version
}
else {
    $selected = $candidates | Where-Object { $_.Version -eq $Version } | Select-Object -First 1
    if (-not $selected) {
        throw "Runtime version '$Version' is not in the release metadata. Available: $(($candidates.Version | Select-Object -First 8) -join ', ')."
    }
}
$stableVersionText = [string]$selected.Version
if ($stableVersionText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Runtime version '$stableVersionText' is not a stable three-part release."
}
$selectedVersion = [version]$stableVersionText
if ($selectedVersion -lt $MinimumSupportedRuntime) {
    throw "Runtime version '$selectedVersion' is below the security floor $MinimumSupportedRuntime."
}
Write-Info "  Selected ASP.NET Core runtime $($selected.Version) ($($selected.Name))"

if ($selected.Hash.Length -ne 128) {
    throw ("Release metadata published a $($selected.Hash.Length)-character digest for " +
           "$($selected.Name); expected a 128-character SHA512. Refusing to verify against a " +
           'digest whose algorithm is not what this script compares.')
}

$lock = Read-LockFile
$lockKey = $selected.Name

if (-not (Test-Path -LiteralPath $CachePath -PathType Container)) {
    [void](New-Item -ItemType Directory -Path $CachePath -Force)
}
$cachedFile = Join-Path $CachePath $selected.Name

if (Test-Path -LiteralPath $cachedFile -PathType Leaf) {
    Write-Info "  Using cached download: $cachedFile"
}
else {
    Write-Step "Downloading $($selected.Url)"
    $temporary = "$cachedFile.partial"
    Invoke-WebRequest -Uri $selected.Url -OutFile $temporary -UseBasicParsing
    Move-Item -LiteralPath $temporary -Destination $cachedFile -Force
}

$actualSha512 = Get-Sha512 -Path $cachedFile

# Check 1: against what Microsoft published for this exact file.
if ($actualSha512 -ne $selected.Hash) {
    Remove-Item -LiteralPath $cachedFile -Force -ErrorAction SilentlyContinue
    throw ("Downloaded $($selected.Name) does not match the SHA512 in the release metadata." +
           [Environment]::NewLine + "  published  : $($selected.Hash)" +
           [Environment]::NewLine + "  downloaded : $actualSha512" +
           [Environment]::NewLine + 'The cached copy has been deleted. Do not ship this build.')
}
Write-Info '  Matches the SHA512 published in the release metadata.'

# Check 2: against the committed pin, which is what makes the payload reproducible.
if ($lock.ContainsKey($lockKey)) {
    if ($lock[$lockKey] -ne $actualSha512) {
        if (-not $UpdateLockFile) {
            throw ("Runtime payload hash mismatch for $lockKey." + [Environment]::NewLine +
                   "  pinned in $LockFilePath : $($lock[$lockKey])" + [Environment]::NewLine +
                   "  downloaded             : $actualSha512" + [Environment]::NewLine +
                   'Re-run with -UpdateLockFile only after establishing why the published file changed.')
        }
        Write-Info '  Pin differs; -UpdateLockFile was passed, so the lock file is being rewritten.'
        $lock[$lockKey] = $actualSha512
        Write-LockFile -Map $lock
    }
    else {
        Write-Info '  Matches the committed pin.'
    }
}
else {
    if (-not $UpdateLockFile) {
        throw ("No pin for $lockKey in $LockFilePath. Re-run with -UpdateLockFile to record it, " +
               'then review the change before committing it.')
    }
    Write-Info "  Recording a new pin for $lockKey."
    $lock[$lockKey] = $actualSha512
    Write-LockFile -Map $lock
}

Write-Step 'Verifying the Authenticode signature'
$signature = Get-AuthenticodeSignature -LiteralPath $cachedFile
if ($signature.Status -ne 'Valid') {
    throw "Runtime installer signature is '$($signature.Status)', expected 'Valid'."
}
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw "Runtime installer is not signed by Microsoft Corporation (subject: $($signature.SignerCertificate.Subject))."
}
Write-Info "  Signed by $($signature.SignerCertificate.Subject)"

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    [void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
}
$staged = Join-Path $OutputDirectory $selected.Name
Copy-Item -LiteralPath $cachedFile -Destination $staged -Force
Write-Step "Staged $staged"
return $staged
