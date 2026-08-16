#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Transactional in-place upgrade of NodePilot.
.DESCRIPTION
    Verifies and pre-extracts the signed artifact before stopping the service. The current
    binaries are backed up while the service is still running; appsettings.Production.json is
    never copied to a backup and remains only in memory. Any failure after mutation starts rolls
    binaries and configuration back before the old service is restarted.

    A successful update leaves the service RUNNING, regardless of whether it was running when the
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
    Writable data directory. Retained for command-line compatibility.
.PARAMETER HttpsPort
    HTTPS port used for the health probe after restart. Defaults to the port in the installed
    appsettings.Production.json (Kestrel:Https:HttpsPort), falling back to 443 only when that
    cannot be read. Pass explicitly to override.
.PARAMETER KeepBackupCount
    Number of binary-only backups to retain. Default: 3.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$TrustedArtifactSignerThumbprint,
    [string]$ServiceName = 'NodePilot',
    [string]$InstallPath = 'C:\Program Files\NodePilot',
    [string]$DataPath = 'C:\ProgramData\NodePilot',
    [int]$HttpsPort = 443,
    [int]$KeepBackupCount = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
if (-not (Test-Path -LiteralPath $ArtifactSecurityScript -PathType Leaf)) {
    throw "Artifact security helper not found: $ArtifactSecurityScript"
}
. $ArtifactSecurityScript

# Shared with Install-NodePilot.ps1: a service the SCM calls stopped can still have a live
# process holding its own binaries, and waiting that out is this script's job, not the operator's.
$ServiceControlScript = Join-Path $PSScriptRoot 'ServiceControl.ps1'
if (-not (Test-Path -LiteralPath $ServiceControlScript -PathType Leaf)) {
    throw "Service control helper not found: $ServiceControlScript"
}
. $ServiceControlScript

function Write-Step { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Gray }
function Write-Ok   { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "[update] $Text" -ForegroundColor Yellow }

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

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [string]$ExcludedFileName
    )
    Get-ChildItem -LiteralPath $Source -Force |
        Where-Object { -not $ExcludedFileName -or $_.Name -ne $ExcludedFileName } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
        }
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

$artifactLock = $null
$artifactStage = $null
$settingsBytes = $null
$backupDir = $null
$installTouched = $false
$previousCertificatePolicy = $null
$previousSecurityProtocol = $null
$certificatePolicyChanged = $false

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

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$ServiceName' not found. Nothing to update." }
    $serviceWasRunning = $svc.Status -ne 'Stopped'
    $svcAccount = Resolve-ServiceAclIdentity -Name $ServiceName
    Set-RestrictedSettingsAcl -Path $settingsPath -ServiceAccount $svcAccount
    $settingsBytes = [IO.File]::ReadAllBytes($settingsPath)

    # Health-probe port: the installed configuration is authoritative. Silently probing the
    # 443 parameter default against an installation that listens on 8443 (any host where IIS
    # owns 443 — SCCM, WSUS) fails the post-restart probe and rolls back a perfectly healthy
    # upgrade (lab 2026-08-01). Explicit -HttpsPort still wins.
    if (-not $PSBoundParameters.ContainsKey('HttpsPort')) {
        try {
            $installedSettings = [Text.Encoding]::UTF8.GetString($settingsBytes) | ConvertFrom-Json
            $httpsSection = $null
            if ($installedSettings.PSObject.Properties.Name -contains 'Kestrel') {
                $kestrelSection = $installedSettings.Kestrel
                if ($kestrelSection -and $kestrelSection.PSObject.Properties.Name -contains 'Https') {
                    $httpsSection = $kestrelSection.Https
                }
            }
            if ($httpsSection -and $httpsSection.PSObject.Properties.Name -contains 'HttpsPort') {
                $configuredPort = [int]$httpsSection.HttpsPort
                if ($configuredPort -gt 0 -and $configuredPort -ne $HttpsPort) {
                    Write-Info "Health probe follows the installed configuration: port $configuredPort."
                    $HttpsPort = $configuredPort
                }
            }
        } catch {
            Write-Warn ("Could not read Kestrel:Https:HttpsPort from $settingsPath " +
                        "($($_.Exception.Message)); probing the default $HttpsPort. " +
                        'Pass -HttpsPort explicitly if the service listens elsewhere.')
        }
    }

    # Reject a malformed signed ZIP before stopping the service or touching the installation.
    #
    # Announced, because this is the longest stretch of the whole update and used to run without a
    # single progress line: the first phase below is the backup, so the wizard sat on its start
    # caption while ~2900 files were expanded to staging and then hashed one by one against the
    # signed manifest. Measured 2.7 s of hashing on a warm NVMe, minutes on a machine whose
    # real-time scanner inspects every file created under %TEMP% - see docs/av-exclusions.md.
    # An operator watching a motionless dialog reasonably concludes it has hung.
    Write-Step 'Extracting artifact'
    $artifactStage = Expand-NodePilotArtifactToStaging -ArtifactPath $ArtifactPath
    Write-Info "Verified restricted staging: $artifactStage"

    # Installed binaries are immutable. Backing them up while the service still runs ensures a
    # disk/ACL failure here cannot leave an otherwise healthy service stopped.
    Write-Step 'Backing up current install'
    $backupDir = "$InstallPath.backup.$(Get-Date -Format 'yyyyMMdd-HHmmssfff')-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $backupDir -ErrorAction Stop | Out-Null
    Copy-DirectoryContents `
        -Source $InstallPath `
        -Destination $backupDir `
        -ExcludedFileName 'appsettings.Production.json'
    Write-Info "Binary-only backup: $backupDir"

    try {
        Write-Step "Stopping service '$ServiceName'"
        Stop-ServiceAndVerify -Name $ServiceName

        # A stopped service does not guarantee an empty install dir: an orphaned worker (or a
        # manually started NodePilot.Api.exe) keeps its DLLs mapped as image sections, and
        # deleting a mapped DLL fails with plain "Access denied" (lab 2026-08-01, mid-wipe -
        # which also destroys appsettings.Production.json before the abort).
        #
        # It does not follow that the operator should be the one to clean up. The SCM reports
        # SERVICE_STOPPED while the host is still unwinding, so the process this script just
        # stopped was routinely still listed a second later - and the run aborted telling the
        # operator to kill it by hand (lab 2026-08-03). Wait for it, then end whatever is left:
        # everything under InstallPath is a NodePilot binary whose files are about to be
        # replaced anyway. Fail CLOSED, with names, only if something survives that too.
        $lockers = @(Wait-NodePilotProcessesUnderPath -Path $InstallPath -TimeoutSeconds 30 -Force)
        if ($lockers.Count -gt 0) {
            $names = ($lockers | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
            throw ("Processes are still running from ${InstallPath} and could not be ended: $names. " +
                   "Stop them (Stop-Process -Id <PID> -Force) or reboot, then re-run. " +
                   "Diagnose foreign DLL holds with: tasklist /m BCrypt-Net-Next.dll")
        }

        Write-Step 'Installing verified artifact'
        $installTouched = $true
        # appsettings.Production.json last: if the wipe aborts midway (locked file, AV), the
        # config must still be on disk — the backup deliberately excludes it and the in-memory
        # copy dies with this process.
        Get-ChildItem -LiteralPath $InstallPath -Force |
            Sort-Object { $_.Name -eq 'appsettings.Production.json' } |
            Remove-Item -Recurse -Force -ErrorAction Stop
        Copy-DirectoryContents -Source $artifactStage -Destination $InstallPath
        Assert-NodePilotExtractedFiles -RootPath $InstallPath
        # H-18: an installation made before the installer hardened this directory keeps its
        # inherited ACL forever - replacing the binaries on every upgrade would never notice that
        # a non-administrator can replace them right back, with the service account executing the
        # result. No -RequireProtectedRules here: inheriting a safe ACL from Program Files is fine,
        # only effective write access by an untrusted principal is not.
        Assert-NodePilotInstallRootHardened -Path $InstallPath
        Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount

        # Normalise the start type. Installations made before the API waited for the database
        # carry start= delayed-auto, which idles roughly two minutes past every boot for a wait
        # the binaries we just laid down now do themselves. Without this the fix would only ever
        # reach fresh installations, and an upgraded host would keep looking dead after a reboot.
        # This is the one piece of service configuration an update touches, and it changes no
        # identity, no dependency and no recovery action.
        & sc.exe config $ServiceName start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warn "  sc.exe config (start= auto) returned $LASTEXITCODE" }

        Write-Step "Starting service '$ServiceName'"
        Start-Service -Name $ServiceName -ErrorAction Stop

        # The localhost probe is the only operation that bypasses certificate validation. Restore
        # the process-global Windows PowerShell 5.1 policy when the script exits.
        if ($PSVersionTable.PSVersion.Major -lt 6) {
            if (-not ('TrustAllCertsUpdate' -as [type])) {
                Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsUpdate : ICertificatePolicy {
  public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; }
}
"@
            }
            $previousCertificatePolicy = [System.Net.ServicePointManager]::CertificatePolicy
            $previousSecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol
            [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsUpdate
            [System.Net.ServicePointManager]::SecurityProtocol = 'Tls12, Tls13'
            $certificatePolicyChanged = $true
        }

        $probeUrl = "https://localhost:$HttpsPort/healthz/ready"
        Write-Info "Probing $probeUrl (up to 60s)..."
        $deadline = (Get-Date).AddSeconds(60)
        $healthy = $false
        while ((Get-Date) -lt $deadline) {
            try {
                $response = if ($PSVersionTable.PSVersion.Major -ge 6) {
                    Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -SkipCertificateCheck -TimeoutSec 5
                } else {
                    Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 5
                }
                if ($response.StatusCode -eq 200) { $healthy = $true; break }
            }
            catch { Start-Sleep -Seconds 2 }
        }
        if (-not $healthy) { throw 'Service did not become ready after upgrade.' }
        Write-Ok '/healthz/ready returned 200 OK'

        # A successful update leaves the service RUNNING, whatever its state was before.
        #
        # This used to restore the pre-update state, and that combination bit in the lab: the
        # 30-second timeout in Stop-ServiceAndVerify pushes operators to stop the service by hand
        # first (an in-flight execution otherwise aborts the run), so "stopped" becomes the
        # recorded prior state — and the update then started the service, proved it healthy on
        # /healthz/ready, and stopped it again. The operator was left with a dead service after a
        # run that printed "Update complete."
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

        # Only Install-NodePilot.ps1 used to write this, so the marker kept the version of the last
        # INSTALL and every script-driven update was invisible in it. That value is what the setup
        # wizard puts on its mode page ("NodePilot <version> is already installed in <path>") and
        # the obvious thing for an inventory to read - measured in the lab: binaries updated from
        # 1.2.6-rc1 to the 1.2.5 artifact, marker still claiming 1.2.6-rc1.
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

        # Updating FROM a pre-1.2.8 installation is the case that needs this: the old artifact
        # carried no tools directory, so Install-NodePilot.ps1 never had a path to add. Same
        # idempotent append as the installer, so an update that already has the entry is a no-op.
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
            }
        } catch {
            Write-Warn "Could not update the machine PATH: $($_.Exception.Message)"
        }

        Write-Ok 'Update complete.'
    }
    catch {
        $updateError = $_
        Write-Host "[update] FAILED: $($updateError.Exception.Message)" -ForegroundColor Red
        try {
            if ($installTouched) {
                Write-Host "[update] Rolling back from $backupDir" -ForegroundColor Yellow
                Stop-ServiceAndVerify -Name $ServiceName
                if (Test-Path -LiteralPath $InstallPath) {
                    Get-ChildItem -LiteralPath $InstallPath -Force |
                        Remove-Item -Recurse -Force -ErrorAction Stop
                }
                Copy-DirectoryContents -Source $backupDir -Destination $InstallPath
                Write-RestrictedSettings -Path $settingsPath -Content $settingsBytes -ServiceAccount $svcAccount
            }

            if ($serviceWasRunning -and
                (Get-Service -Name $ServiceName -ErrorAction Stop).Status -eq 'Stopped') {
                Start-Service -Name $ServiceName -ErrorAction Stop
            }
            if ($installTouched) {
                Write-Host "[update] Rollback complete. Restored $backupDir" -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "[update] Rollback ALSO failed: $($_.Exception.Message). Manual intervention required; backup: $backupDir" -ForegroundColor Red
        }
        throw $updateError
    }
}
finally {
    if ($certificatePolicyChanged) {
        [System.Net.ServicePointManager]::CertificatePolicy = $previousCertificatePolicy
        [System.Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
    }
    if ($settingsBytes) { [Array]::Clear($settingsBytes, 0, $settingsBytes.Length) }
    if ($artifactStage -and (Test-Path -LiteralPath $artifactStage)) {
        Remove-Item -LiteralPath $artifactStage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($artifactLock) { $artifactLock.Dispose() }
}
