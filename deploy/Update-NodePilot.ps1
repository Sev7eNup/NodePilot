#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Transactional in-place upgrade of NodePilot.
.DESCRIPTION
    Verifies and pre-extracts the signed artifact before stopping the service. The current
    binaries are backed up while the service is still running; appsettings.Production.json is
    never copied to a backup and remains only in memory. Any failure after mutation starts rolls
    binaries and configuration back before the old service is restarted. The database schema is
    not rolled back: the new version migrates it forward on its first start, so a database backup
    taken before the update is the operator's way back.

    A successful update leaves the service running, regardless of whether it was running when the
    script was invoked. A failed update restores the pre-update state instead.
.PARAMETER ArtifactPath
    Path to the new NodePilot-*.zip.
.PARAMETER TrustedArtifactSignerThumbprint
    Pinned Code Signing certificate thumbprint used to verify the detached CMS signature.
.PARAMETER ServiceName
    Windows Service name. Default: NodePilot.
.PARAMETER InstallPath
    Install directory. Default: C:\Program Files\NodePilot.
.PARAMETER DataPath
    Writable data directory. Its logs subdirectory is read for the start diagnostics.
.PARAMETER HttpsPort
    Port used for the health probe after restart. Defaults to the port in the installed
    appsettings.Production.json (Kestrel:Https:HttpsPort), falling back to 443 when that key is
    absent. Pass explicitly to override. The probe is HTTPS pinned to the certificate thumbprint
    configured there (Kestrel:Https:CertificateThumbprint); with Kestrel:Https:Enabled off the
    service terminates no TLS and the probe uses plain HTTP on this port. When the configuration
    cannot be read or names no usable thumbprint, the update warns and probes HTTPS without the pin.
.PARAMETER KeepBackupCount
    Number of binary-only backups to retain. Default: 3.
.PARAMETER AllowDowngrade
    Install an artifact whose version is older than the installed one (VERSION.txt in the install
    directory). Without this switch such an update stops before anything is changed. A downgrade
    does not roll back the database schema; the older version runs against the newer schema.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$TrustedArtifactSignerThumbprint,
    [string]$ServiceName = 'NodePilot',
    [string]$InstallPath = 'C:\Program Files\NodePilot',
    [string]$DataPath = 'C:\ProgramData\NodePilot',
    [int]$HttpsPort = 443,
    [int]$KeepBackupCount = 3,
    [switch]$AllowDowngrade,
    [scriptblock]$ProgressCallback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
if (-not (Test-Path -LiteralPath $ArtifactSecurityScript -PathType Leaf)) {
    throw "Artifact security helper not found: $ArtifactSecurityScript"
}
. $ArtifactSecurityScript
. (Join-Path $PSScriptRoot 'DeploymentProgress.ps1')

# Shared with Install-NodePilot.ps1: a service the SCM calls stopped can still have a live
# process holding its own binaries, and waiting that out is this script's job, not the operator's.
$ServiceControlScript = Join-Path $PSScriptRoot 'ServiceControl.ps1'
if (-not (Test-Path -LiteralPath $ServiceControlScript -PathType Leaf)) {
    throw "Service control helper not found: $ServiceControlScript"
}
. $ServiceControlScript

# The wizard reaches its readiness page only on the install path, so an update is the one route
# onto a host where nothing has ever checked whether the new binaries can run there at all.
$PreflightScript = Join-Path $PSScriptRoot 'Preflight.ps1'
if (-not (Test-Path -LiteralPath $PreflightScript -PathType Leaf)) {
    throw "Pre-flight helper not found: $PreflightScript"
}
. $PreflightScript

# The readiness probe pins the configured certificate, the same transport the installer uses.
. (Join-Path $PSScriptRoot 'LocalServerHttp.ps1')

function Write-Step {
    param([string]$Text, [string]$Phase = 'configure')
    Write-Host "[update] $Text" -ForegroundColor Cyan
    Send-NodePilotProgress -Callback $ProgressCallback -Phase $Phase -Message $Text
}
function Write-Info { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Gray }
function Write-Ok   { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Yellow }

# Release versions are numeric with an optional suffix ('1.5.1', '2026.04.23', '1.2.13-audit1').
# Only the numeric part is compared, so a suffix on the same number does not count as older.
# Returns $null when the text holds no such version.
function ConvertTo-NodePilotComparableVersion {
    param([string]$Text)
    $match = [regex]::Match([string]$Text, '^\s*v?(\d{1,9}(?:\.\d{1,9}){1,3})(?:[-+][0-9A-Za-z.+-]*)?\s*$')
    if (-not $match.Success) { return $null }
    $parts = @($match.Groups[1].Value.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    return New-Object System.Version -ArgumentList $parts[0], $parts[1], $parts[2], $parts[3]
}

# Every artifact ships VERSION.txt ("Version : <x>"), so the install directory names its own version.
function Get-NodePilotInstalledVersionText {
    param([Parameter(Mandatory)][string]$InstallPath)
    $versionFile = Join-Path $InstallPath 'VERSION.txt'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { return $null }
    $match = [regex]::Match((Get-Content -LiteralPath $versionFile -Raw), '(?m)^\s*Version\s*:\s*(\S+)\s*$')
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Resolve-ServiceAclIdentity {
    param([Parameter(Mandatory)][string]$Name)
    $account = $null
    try { $account = (Get-WmiObject Win32_Service -Filter "Name='$Name'" -ErrorAction Stop).StartName } catch {}
    if (-not $account) {
        try { $account = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop).StartName } catch {}
    }
    if (-not $account) {
        throw "Could not resolve the service account for '$Name'; refusing to read or rewrite production configuration."
    }
    if ($account.Trim().ToLowerInvariant() -in @('localsystem', '.\localsystem', 'system')) {
        return 'NT AUTHORITY\SYSTEM'
    }
    return $account
}

function Set-RestrictedSettingsAcl {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ServiceAccount)
    Set-NodePilotRestrictedFileAcl `
        -Path $Path `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:($ServiceAccount -eq 'NT AUTHORITY\SYSTEM')
}

function Write-RestrictedSettings {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][byte[]]$Content, [Parameter(Mandatory)][string]$ServiceAccount)
    Write-NodePilotRestrictedFile `
        -Path $Path `
        -Content $Content `
        -ServiceAccount $ServiceAccount `
        -SkipServiceRule:($ServiceAccount -eq 'NT AUTHORITY\SYSTEM')
}

function Stop-ServiceAndVerify {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSeconds = 30)
    if ((Get-Service -Name $Name -ErrorAction Stop).Status -eq 'Stopped') { return }
    Stop-Service -Name $Name -Force -ErrorAction Stop
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Service -Name $Name -ErrorAction Stop).Status -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Service '$Name' did not stop within ${TimeoutSeconds}s; installed files were not changed."
}

# Reads what the readiness probe needs from the installed configuration: Kestrel:Https:HttpsPort
# (0 when absent) and the thumbprint of the certificate Kestrel serves, which the probe pins. The
# thumbprint is empty when Kestrel:Https is off, because the service then terminates no TLS.
function Get-NodePilotProbeSettings {
    param([Parameter(Mandatory)][byte[]]$SettingsBytes)
    # UTF-8 configuration written by Windows PowerShell 5.1 commonly has a BOM. The byte decoder
    # preserves it, while ConvertFrom-Json rejects it as an invalid token.
    $settings = [Text.Encoding]::UTF8.GetString($SettingsBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    $https = $null
    if ($settings.PSObject.Properties['Kestrel'] -and $settings.Kestrel -and
        $settings.Kestrel.PSObject.Properties['Https']) {
        $https = $settings.Kestrel.Https
    }
    $port = 0
    $thumbprint = ''
    if ($https) {
        if ($https.PSObject.Properties['HttpsPort']) { $port = [int]$https.HttpsPort }
        if ($https.PSObject.Properties['Enabled'] -and [string]$https.Enabled -eq 'true') {
            $raw = if ($https.PSObject.Properties['CertificateThumbprint']) { [string]$https.CertificateThumbprint } else { '' }
            # The service normalises the same way: a value copied from certmgr carries spaces and
            # invisible direction marks.
            $thumbprint = ($raw -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
            if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
                throw "Kestrel:Https is enabled, but CertificateThumbprint '$raw' is not a certificate thumbprint."
            }
        }
    }
    return [pscustomobject]@{ HttpsPort = $port; CertificateThumbprint = $thumbprint }
}

# Polls /healthz/ready on loopback until it answers 200 or the budget is used up. With a thumbprint
# the request is HTTPS pinned to that certificate; without one the service serves plain HTTP.
function Wait-NodePilotReady {
    param(
        [Parameter(Mandatory)][int]$Port,
        [AllowEmptyString()][string]$CertificateThumbprint = '',
        [switch]$SkipCertificateCheck,
        [int]$TimeoutSeconds = 60
    )
    $scheme = if ($CertificateThumbprint -or $SkipCertificateCheck) { 'https' } else { 'http' }
    $probeUrl = "${scheme}://localhost:$Port/healthz/ready"
    Write-Info "Probing $probeUrl (up to ${TimeoutSeconds}s)..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = if ($CertificateThumbprint) {
                Invoke-NodePilotLocalHttps -Uri $probeUrl -CertificateThumbprint $CertificateThumbprint -TimeoutSeconds 5
            } elseif ($SkipCertificateCheck) {
                Invoke-NodePilotLocalHttps -Uri $probeUrl -SkipCertificateCheck -TimeoutSeconds 5
            } else {
                Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 5
            }
            if ($response.StatusCode -eq 200) { return $true }
        }
        catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

$artifactLock = $null
$artifactStage = $null
$settingsBytes = $null
$backup = $null
$installTouched = $false
$probeThumbprint = ''
$probeUnpinned = $false

try {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
        throw "Artifact not found: $ArtifactPath"
    }
    $ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
    $artifactLock = [IO.File]::Open(
        $ArtifactPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $verifiedArtifact = Assert-NodePilotSignedArtifact `
        -ArtifactPath $ArtifactPath `
        -TrustedSignerThumbprint $TrustedArtifactSignerThumbprint `
        -ArtifactStream $artifactLock
    Write-Ok "Verified signed artifact version $($verifiedArtifact.Version), signer $($verifiedArtifact.SignerThumbprint)."

    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
        throw "Install path not found: $InstallPath - run Install-NodePilot.ps1 for a fresh install."
    }
    $settingsPath = Join-Path $InstallPath 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "No appsettings.Production.json at $settingsPath. Refusing to upgrade a non-installer layout."
    }

    # Before anything is hardened, backed up or stopped: an older artifact needs an explicit
    # -AllowDowngrade, because the database schema stays at the newer version.
    $installedVersionText = Get-NodePilotInstalledVersionText -InstallPath $InstallPath
    $installedVersion = ConvertTo-NodePilotComparableVersion -Text $installedVersionText
    $artifactVersion = ConvertTo-NodePilotComparableVersion -Text $verifiedArtifact.Version
    if ($null -eq $installedVersion -or $null -eq $artifactVersion) {
        Write-Warn ("Could not compare the installed version ('$installedVersionText') with the artifact " +
                    "version ('$($verifiedArtifact.Version)'); the downgrade check is skipped.")
    } elseif ($artifactVersion -lt $installedVersion) {
        if (-not $AllowDowngrade) {
            throw ("Artifact version $($verifiedArtifact.Version) is older than the installed version " +
                   "$installedVersionText. Nothing was changed. Re-run with -AllowDowngrade to install it " +
                   'anyway; the database schema is not rolled back.')
        }
        Write-Warn "Downgrading from $installedVersionText to $($verifiedArtifact.Version) (-AllowDowngrade)."
    }

    # An operator who installed without the source snapshot must not get it back through an
    # update. Read before the wipe, applied after the new artifact has been verified. The
    # installation on disk is the source of truth here rather than the installation marker, which
    # is machine-wide and would be wrong for a second instance on the same host.
    $keepSourceSnapshot = Test-NodePilotSourceSnapshotPresent -InstallPath $InstallPath
    if (-not $keepSourceSnapshot) {
        Write-Info "  This installation carries no knowledge\source; the update will not add one."
    }

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$ServiceName' not found. Nothing to update." }
    $serviceWasRunning = $svc.Status -ne 'Stopped'
    $svcAccount = Resolve-ServiceAclIdentity -Name $ServiceName
    # Secure the destination before snapshotting or copying any binaries. A safe-looking DACL
    # with an untrusted owner is mutable, and hardening only after copying leaves a swap window.
    Assert-NodePilotInstallRootHardenedOrRepair -Path $InstallPath -ServiceAccount $svcAccount
    Set-RestrictedSettingsAcl -Path $settingsPath -ServiceAccount $svcAccount
    $settingsBytes = [IO.File]::ReadAllBytes($settingsPath)

    # The service refuses to read its JWT key when the data directory grants write access to a
    # principal it does not trust. Same check and repair as the installer, before anything changes.
    if (Test-Path -LiteralPath $DataPath -PathType Container) {
        Assert-ServiceDirectoryAclUsable -Path $DataPath -ServiceAccount $svcAccount `
            -SkipServiceRule:($svcAccount -eq 'NT AUTHORITY\SYSTEM') -Label "The data directory '$DataPath'"
    }

    # The health probe follows the installed configuration: its port (the 443 default fails on any
    # host where IIS owns 443, such as SCCM or WSUS; an explicit -HttpsPort still wins) and the
    # certificate thumbprint it pins. Read before anything is stopped.
    try {
        $probeSettings = Get-NodePilotProbeSettings -SettingsBytes $settingsBytes
    } catch {
        # Only the health verdict depends on the pin, and the probe sends no credentials, so an
        # unreadable configuration does not block the update.
        Write-Warn ("Could not read Kestrel:Https from $settingsPath ($($_.Exception.Message)); " +
                    "the health probe checks HTTPS on port $HttpsPort without the certificate pin. " +
                    'Pass -HttpsPort explicitly if the service listens elsewhere.')
        $probeSettings = [pscustomobject]@{ HttpsPort = 0; CertificateThumbprint = '' }
        $probeUnpinned = $true
    }
    if (-not $PSBoundParameters.ContainsKey('HttpsPort') -and
        $probeSettings.HttpsPort -gt 0 -and $probeSettings.HttpsPort -ne $HttpsPort) {
        Write-Info "Health probe follows the installed configuration: port $($probeSettings.HttpsPort)."
        $HttpsPort = $probeSettings.HttpsPort
    }
    $probeThumbprint = $probeSettings.CertificateThumbprint
    if (-not $probeThumbprint -and -not $probeUnpinned) {
        Write-Info "Kestrel:Https is not enabled; the health probe uses plain HTTP on port $HttpsPort."
    }

    # Reject a malformed signed ZIP before stopping the service or touching the installation.
    #
    # The step is announced because it is the longest stretch of the whole update: every file is
    # expanded to staging and then hashed against the signed manifest, which takes minutes on a
    # machine whose real-time scanner inspects every file created under %TEMP% - see
    # docs/av-exclusions.md. Without a progress line an operator reads that as a hang.
    Write-Step 'Extracting artifact'
    $artifactStage = Expand-NodePilotArtifactToStaging -ArtifactPath $ArtifactPath
    Write-Info "Verified restricted staging: $artifactStage"

    # Before anything is stopped, backed up or wiped: can this host run what is in the staging
    # directory? The artifact is framework-dependent, so a build made against a newer runtime than
    # the host carries fails at the apphost, before managed code - the SCM then reports its generic
    # "cannot be started" and neither the event log nor the NodePilot log holds a reason.
    #
    # Checked here rather than against the fixed floor in Test-NodePilotDotNetRuntime because the
    # floor cannot move with the build: the version that matters is the one this artifact asks for.
    Write-Step 'Checking the runtime against this build'
    $runtimeRequirement = @(Get-NodePilotArtifactRuntimeRequirement -RootPath $artifactStage)
    $runtimeVerdict = Test-NodePilotArtifactRuntime -Requirement $runtimeRequirement
    if ($runtimeVerdict.Status -eq 'Fail') {
        # The setup runs this script out of its payload directory, which can already hold the
        # matching runtime installer. Naming it saves a download on a host without internet access.
        $bundledRuntime = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'aspnetcore-runtime-*.exe' `
            -File -ErrorAction SilentlyContinue)
        $bundledHint = if ($bundledRuntime.Count -eq 1) {
            " A matching installer ships with this setup: $($bundledRuntime[0].FullName)."
        } else { '' }
        throw ($runtimeVerdict.AbortMessage + $bundledHint +
               ' Nothing was changed; the installation is untouched.')
    }
    Write-Ok "  $($runtimeVerdict.Detail)"

    # Installed binaries are immutable. Backing them up while the service still runs ensures a
    # disk/ACL failure here cannot leave an otherwise healthy service stopped. The backup is
    # restored as service binaries, so it gets the install root's protection, not its parent's.
    Write-Step 'Backing up current install'
    $backup = New-NodePilotBinaryBackup -InstallPath $InstallPath -Kind 'backup'
    Write-Info "Binary-only backup: $($backup.Path)"
    Write-Info ('The database is not part of this backup. The new version migrates the schema on its ' +
                'first start and a rollback restores binaries only, so keep a database backup from before this update.')

    try {
        Write-Step "Stopping service '$ServiceName'"
        Stop-ServiceAndVerify -Name $ServiceName

        # A stopped service does not guarantee an empty install directory: an orphaned worker (or
        # a manually started NodePilot.Api.exe) keeps its DLLs mapped as image sections, and
        # deleting a mapped DLL fails with "Access denied" halfway through the wipe.
        #
        # The SCM reports SERVICE_STOPPED while the host is still unwinding, so the process this
        # script just stopped can still be listed a second later. Wait for it, then end whatever
        # is left: everything under InstallPath is a NodePilot binary whose files are about to be
        # replaced anyway. Fail closed, naming the processes, only if something survives that too.
        $lockers = @(Wait-NodePilotProcessesUnderPath -Path $InstallPath -TimeoutSeconds 30 -Force)
        if ($lockers.Count -gt 0) {
            $names = ($lockers | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
            throw ("Processes are still running from ${InstallPath} and could not be ended: $names. " +
                   "Stop them (Stop-Process -Id <PID> -Force) or reboot, then re-run. " +
                   "Diagnose foreign DLL holds with: tasklist /m BCrypt-Net-Next.dll")
        }

        Write-Step 'Installing verified artifact'
        # The executable-adjacent configuration is an operator-supported source, not merely
        # a URL cache. Preserve all bytes (allowlists, SCOrch endpoints, profiles and future
        # fields) before the wipe, and fail here if the existing file cannot be read. The content
        # is not parsed: the Switcher accepts more than Windows PowerShell's strict parser (comments,
        # trailing commas, single backslashes in paths), and a file the Switcher alone has to judge
        # must not block a server update.
        . (Join-Path $PSScriptRoot 'SwitcherConfig.ps1')
        $switcherConfigPath = Join-Path $InstallPath 'tools\switcher\switcher.json'
        $previousSwitcherConfigBytes = $null
        if (Test-Path -LiteralPath $switcherConfigPath) {
            $previousSwitcherConfigBytes = [IO.File]::ReadAllBytes($switcherConfigPath)
        }
        $installTouched = $true
        # appsettings.Production.json last: if the wipe aborts midway (locked file, antivirus)
        # the config must still be on disk - the backup excludes it and the in-memory copy dies
        # with this process.
        Get-ChildItem -LiteralPath $InstallPath -Force |
            Sort-Object { $_.Name -eq 'appsettings.Production.json' } |
            Remove-Item -Recurse -Force -ErrorAction Stop
        Copy-NodePilotDirectoryWithProgress -Source $artifactStage -Destination $InstallPath -ProgressCallback $ProgressCallback
        Send-NodePilotProgress -Callback $ProgressCallback -Phase 'verify' -Message 'Verifying copied files and permissions'
        Assert-NodePilotExtractedFiles -RootPath $InstallPath
        # An installation whose directory was never hardened keeps its inherited ACL, which lets a
        # non-administrator replace the binaries the service account then executes. No
        # -RequireProtectedRules here: inheriting a safe ACL from Program Files is fine, only
        # effective write access by an untrusted principal is not.
        #
        # Repair once before giving up, the way the installer does: an untrusted ACE left by an
        # earlier install is a condition an update can fix, and refusing would leave the operator
        # with no route to the new binaries at all.
        Assert-NodePilotInstallRootHardenedOrRepair -Path $InstallPath -ServiceAccount $svcAccount
        # User configuration is restored after artifact verification, before starting the service.
        # Failure belongs to the transactional rollback, not to the optional URL-seeding warning.
        if ($null -ne $previousSwitcherConfigBytes) {
            [IO.File]::WriteAllBytes($switcherConfigPath, $previousSwitcherConfigBytes)
        }
        # The GUI setup's uninstaller and its deploy\ scripts are not part of the artifact. Without
        # them the Apps & Features entry points at a missing executable.
        $setupFiles = @(Restore-NodePilotSetupFiles -Backup $backup -InstallPath $InstallPath)
        if ($setupFiles.Count -gt 0) {
            Write-Info "  Kept the setup's uninstaller and deploy scripts: $($setupFiles -join ', ')"
        }
        # After the manifest check, never before it - see Remove-NodePilotSourceSnapshot.
        if (-not $keepSourceSnapshot) { [void](Remove-NodePilotSourceSnapshot -InstallPath $InstallPath) }
        Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount

        # Normalise the start type. Older installations carry start= delayed-auto, which idles
        # roughly two minutes past every boot waiting for the database - something the binaries
        # just laid down now do themselves. This is the one piece of service configuration an
        # update touches, and it changes no identity, no dependency and no recovery action.
        & sc.exe config $ServiceName start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warn "  sc.exe config (start= auto) returned $LASTEXITCODE" }

        Write-Step "Starting service '$ServiceName'"
        Send-NodePilotProgress -Callback $ProgressCallback -Phase 'service' -Message 'Starting the service and checking readiness' -Detail 'This can take up to one minute.'
        # Diagnostics before the rollback, not after: the catch block below wipes the new binaries,
        # and an operator who only gets "the service cannot be started" has nothing left to look at.
        try {
            Start-Service -Name $ServiceName -ErrorAction Stop
        } catch {
            Show-NodePilotServiceStartDiagnostics -DataPath $DataPath
            throw "Start-Service failed: $($_.Exception.Message)"
        }

        if (-not (Wait-NodePilotReady -Port $HttpsPort -CertificateThumbprint $probeThumbprint -SkipCertificateCheck:$probeUnpinned -TimeoutSeconds 60)) {
            Show-NodePilotServiceStartDiagnostics -DataPath $DataPath
            throw 'Service did not become ready after upgrade. Check the NodePilot and Windows Application logs for the startup error.'
        }
        Write-Ok '/healthz/ready returned 200 OK'

        # A successful update leaves the service running, whatever its state was before.
        # Operators commonly stop the service by hand first so an in-flight execution does not
        # abort, and restoring that state would hand back a dead service after a healthy upgrade.
        #
        # Failure is different and still restores the prior state (see the catch block): an update
        # that rolled back must not start something the operator had deliberately taken down.
        if (-not $serviceWasRunning) {
            Write-Info 'Service was stopped before the update and is now running.'
        }

        try {
            $backups = @(Get-ChildItem -Directory "$InstallPath.backup.*" -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending)
            if ($backups.Count -gt $KeepBackupCount) {
                $backups | Select-Object -Skip $KeepBackupCount | ForEach-Object {
                    Write-Info "Pruning old backup: $($_.FullName)"
                    Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
                }
            }
        }
        catch {
            Write-Host "[update] Update succeeded, but old backup pruning failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }

        # Keep the installation marker in step with the binaries. Its version is what the setup
        # wizard shows on its mode page and what an inventory reads.
        #
        # Version only: path, service name, provider and port are not changed by an update and are
        # already correct. Guarded on InstallPath because the marker is a single machine-wide key -
        # on a host running more than one instance it describes whichever was installed last, and
        # stamping this update's version onto another instance's marker is worse than leaving it
        # stale. A failure here is a warning: it costs discoverability, not a working installation.
        try {
            $markerPath = 'HKLM:\SOFTWARE\NodePilot\Server'
            $marker = Get-ItemProperty -LiteralPath $markerPath -ErrorAction Stop
            $markerInstallPath = [string]$marker.InstallPath
            if ($markerInstallPath -and
                $markerInstallPath.TrimEnd('\') -eq $InstallPath.TrimEnd('\')) {
                New-ItemProperty -LiteralPath $markerPath -Name 'Version' `
                    -Value ([string]$verifiedArtifact.Version) -PropertyType String -Force | Out-Null
                Write-Info "Installation marker updated to version $($verifiedArtifact.Version)."
            }
            else {
                Write-Info 'Installation marker describes another installation; left untouched.'
            }
        } catch {
            Write-Warn "Could not update the installation marker: $($_.Exception.Message)"
        }

        # Older artifacts carried no tools directory, so Install-NodePilot.ps1 had no path to add.
        # Same idempotent append as the installer: an update that already has the entry is a no-op.
        try {
            . (Join-Path $PSScriptRoot 'MachinePath.ps1')
            $toolsPath = Join-Path $InstallPath 'tools\np'
            if (Test-Path -LiteralPath (Join-Path $toolsPath 'np.exe')) {
                $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
                if (-not (Test-NodePilotPathContains -PathValue $machinePath -Directory $toolsPath)) {
                    [Environment]::SetEnvironmentVariable('Path',
                        (Add-NodePilotPathEntry -PathValue $machinePath -Directory $toolsPath), 'Machine')
                    Write-Info "Added $toolsPath to the machine PATH (new shells will find 'np')."
                }
                # Read back rather than trust the write: everything here is a warning at worst, so
                # an entry that never lands would otherwise leave no trace in the update output.
                if (-not (Test-NodePilotPathContains `
                        -PathValue ([Environment]::GetEnvironmentVariable('Path', 'Machine')) `
                        -Directory $toolsPath)) {
                    Write-Warn "The machine PATH still does not contain $toolsPath after the update."
                }
            } else {
                Write-Warn "np.exe not found under $toolsPath - skipping the PATH entry."
            }
        } catch {
            Write-Warn "Could not update the machine PATH: $($_.Exception.Message)"
        }

        # Restore the operator file only after signed artifact verification. An unset URL still
        # receives the existing AllowedHosts/port fallback; configured files remain byte-identical.
        try {
            if ((Test-Path -LiteralPath $switcherConfigPath) -and
                -not (Test-NodePilotSwitcherConfigParses -ConfigPath $switcherConfigPath)) {
                Write-Warn ('The Switcher configuration is not strict JSON, so its server URL was not ' +
                            "checked or set. The file was kept unchanged: $switcherConfigPath")
            }
            elseif (Test-Path -LiteralPath $switcherConfigPath) {
                $serverUrl = Get-NodePilotSwitcherServerUrl -ConfigPath $switcherConfigPath
                $hadSwitcherServerUrl = -not [string]::IsNullOrWhiteSpace($serverUrl)
                if (-not $serverUrl) {
                    $allowed = ([Text.Encoding]::UTF8.GetString($settingsBytes) | ConvertFrom-Json).AllowedHosts
                    $hostname = ($allowed -split ';' | ForEach-Object { $_.Trim() } |
                        Where-Object { $_ -and $_ -ne '*' -and $_ -ne 'localhost' } | Select-Object -First 1)
                    if ($hostname) {
                        $serverUrl = Get-NodePilotSwitcherServerUrlFor -Hostname $hostname -HttpsPort $HttpsPort
                    }
                }
                if ($hadSwitcherServerUrl) {
                    Write-Info 'Preserved the operator Switcher configuration.'
                } elseif ($serverUrl -and (Set-NodePilotSwitcherServerUrl `
                            -ConfigPath $switcherConfigPath -ServerUrl $serverUrl)) {
                    Write-Info "Switcher server URL set to $serverUrl."
                } elseif (-not $serverUrl) {
                    Write-Warn ('No Switcher server URL could be determined; set ' +
                                "nodePilot.serverUrl in $switcherConfigPath by hand.")
                }
            }
        } catch {
            Write-Warn "Could not set the Switcher server URL: $($_.Exception.Message)"
        }

        Write-Ok 'Update complete.'
    }
    catch {
        $updateError = $_
        Write-Host "[update] FAILED: $($updateError.Exception.Message)" -ForegroundColor Red
        try {
            if ($installTouched) {
                Send-NodePilotProgress -Callback $ProgressCallback -Phase 'rollback' -State 'rollback' -Message 'Restoring the previous installation'
                Write-Host "[update] Rolling back from $($backup.Path)" -ForegroundColor Yellow
                Stop-ServiceAndVerify -Name $ServiceName
                # The SCM can report Stopped before the process releases mapped DLLs. Treat the
                # rollback wipe like the forward update, or an access-denied error leaves a
                # half-restored installation behind.
                $rollbackLockers = @(Wait-NodePilotProcessesUnderPath -Path $InstallPath -TimeoutSeconds 30 -Force)
                if ($rollbackLockers.Count -gt 0) {
                    $names = ($rollbackLockers | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
                    throw "Processes still hold files under ${InstallPath} during rollback: $names"
                }
                # Refuses a backup whose DACL or files changed since this run made it, before the
                # install directory is wiped.
                Restore-NodePilotBinaryBackup -Backup $backup -InstallPath $InstallPath
                Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount
            }

            if ($serviceWasRunning -and
                (Get-Service -Name $ServiceName -ErrorAction Stop).Status -eq 'Stopped') {
                Start-Service -Name $ServiceName -ErrorAction Stop
                # A rollback swaps binaries only. If the new version already migrated the database,
                # the restored binary can start and still fail against the changed schema, so the
                # rollback is not reported as complete on the strength of a service start alone.
                if (Wait-NodePilotReady -Port $HttpsPort -CertificateThumbprint $probeThumbprint -SkipCertificateCheck:$probeUnpinned -TimeoutSeconds 60) {
                    Write-Ok 'Rolled-back service answered /healthz/ready.'
                } else {
                    Write-Warn ('The rolled-back service did not become ready within 60s. A binary rollback ' +
                                'does not roll back the database schema; if the new version had migrated ' +
                                'it, restore the database from the backup taken before this update.')
                }
            }
            if ($installTouched) {
                Write-Host "[update] Rollback complete. Restored $($backup.Path)" -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "[update] Rollback ALSO failed: $($_.Exception.Message). Manual intervention required; backup: $($backup.Path)" -ForegroundColor Red
        }
        throw $updateError
    }
}
finally {
    if ($settingsBytes) { [Array]::Clear($settingsBytes, 0, $settingsBytes.Length) }
    if ($artifactStage -and (Test-Path -LiteralPath $artifactStage)) {
        Remove-Item -LiteralPath $artifactStage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($artifactLock) { $artifactLock.Dispose() }
}
