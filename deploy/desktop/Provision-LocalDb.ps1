#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Provisions the local NodePilot desktop runtime: a loopback-only PostgreSQL cluster + service,
    the self-signed loopback TLS certificate, the rendered Production config, both Windows
    services (Postgres + API), the ACL-restricted DB connection string, and the installer->Electron
    handoff file (desktop.json).

.DESCRIPTION
    Invoked once by the Inno Setup installer's [Run] step (elevated). Idempotent: re-running
    against an existing cluster reuses it (initdb is skipped when the data directory is already
    populated) and refreshes services/config.

    Security model (see deploy/README.md and CLAUDE.md "Desktop"):
      - API service runs as LocalSystem (zero-config; local activities therefore run as SYSTEM).
      - Postgres service runs as NetworkService, bound to 127.0.0.1 only.
      - The DB password lives ONLY in the ACL-restricted ConnectionStrings__Postgres service
        environment value - never in appsettings JSON.
      - The loopback certificate is trusted by the Electron shell via SHA-256 pinning
        (desktop.json), NOT by installing a system root CA.

.NOTES
    Reuses the restricted-file / registry-ACL patterns from deploy/Install-NodePilot.ps1.
    Not executed by Claude. Requires on-VM validation (see the Testplan in the desktop docs).
#>
[CmdletBinding()]
param(
    # Read-only binaries: <InstallPath>\app (API), <InstallPath>\pgsql (Postgres binaries).
    [Parameter(Mandatory)] [string] $InstallPath,
    # Writable data root: cluster, logs, keys, tokens, desktop.json.
    [string] $DataPath = (Join-Path $env:ProgramData 'NodePilot'),
    [string] $ApiServiceName = 'NodePilot',
    [string] $DbServiceName  = 'NodePilotDb',
    [string] $ApiServiceDisplayName = 'NodePilot',
    [string] $DbServiceDisplayName  = 'NodePilot Database',
    # Fixed, offline-safe port pools (see plan A5). Free ports are chosen and persisted.
    [int]    $HttpsPortRangeStart = 47000,
    [int]    $HttpsPortRangeEnd   = 47049,
    [int]    $PgPortRangeStart    = 47100,
    [int]    $PgPortRangeEnd      = 47149,
    [string] $DbName = 'nodepilot',
    [string] $DbRole = 'nodepilot'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# --- paths -----------------------------------------------------------------------------------
$PgBinPath   = Join-Path $InstallPath 'pgsql\bin'
$AppPath     = Join-Path $InstallPath 'app'
$PgData      = Join-Path $DataPath 'pgdata'
$SecretsDir  = Join-Path $DataPath 'secrets'
$LogsDir     = Join-Path $DataPath 'logs'
$DesktopJson = Join-Path $DataPath 'desktop.json'
$ApiExe      = Join-Path $AppPath 'NodePilot.Api.exe'
$initdb      = Join-Path $PgBinPath 'initdb.exe'
$pg_ctl      = Join-Path $PgBinPath 'pg_ctl.exe'
$psql        = Join-Path $PgBinPath 'psql.exe'
$TemplatePath = Join-Path $PSScriptRoot 'appsettings.Desktop.json.template'
$RenderedConfig = Join-Path $AppPath 'appsettings.Production.json'

function Write-Step([string] $m) { Write-Host "==> $m" -ForegroundColor Cyan }

foreach ($p in @($InstallPath, $PgBinPath, $AppPath, $ApiExe, $initdb, $pg_ctl, $psql, $TemplatePath)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required path not found: $p" }
}

New-Item -ItemType Directory -Force -Path $DataPath, $PgData, $SecretsDir, $LogsDir | Out-Null

# --- helpers ---------------------------------------------------------------------------------

function New-RandomSecret([int] $bytes = 32) {
    $buf = New-Object byte[] $bytes
    # Windows PowerShell 5.1 runs on .NET Framework, which has no static RandomNumberGenerator.Fill
    # (that is .NET 5+). Use the instance API, which exists on both Framework and modern .NET.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    # URL/shell-safe base64 without padding-sensitive characters that complicate connection strings.
    return ([Convert]::ToBase64String($buf)) -replace '[+/=]', ''
}

function Get-FreePort([int] $start, [int] $end) {
    for ($port = $start; $port -le $end; $port++) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        } catch {
            continue
        } finally {
            if ($listener) { $listener.Stop() }
        }
    }
    throw "No free port available in range $start-$end."
}

function Set-RestrictedAcl([string] $path, [string[]] $extraReadPrincipals = @(), [switch] $NoCurrentUser) {
    # SYSTEM + Administrators FullControl, inheritance disabled; optional extra read grants.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        $acl = New-Object System.Security.AccessControl.FileSecurity
    }
    $acl.SetAccessRuleProtection($true, $false)
    $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
    $inherit = if (Test-Path -LiteralPath $path -PathType Container) {
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    } else { [System.Security.AccessControl.InheritanceFlags]::None }
    $prop = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    # SYSTEM + Administrators always. The installing user is added too (unless -NoCurrentUser),
    # because PostgreSQL's initdb/postgres, when started by an admin on Windows, re-exec under a
    # restricted token that DROPS the Administrators group -- only the user SID survives it, so PG
    # needs that grant on pgdata/pwfile. -NoCurrentUser is used for the JWT / data-protection key
    # parent (DataPath): the backend fail-closes the boot if an untrusted principal can mutate it.
    $fullSids = [System.Collections.ArrayList]@('S-1-5-18', 'S-1-5-32-544')
    if (-not $NoCurrentUser) { [void]$fullSids.Add(([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value) }
    foreach ($sid in $fullSids) {
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, $rights, $inherit, $prop, $allow)))
    }
    foreach ($principal in $extraReadPrincipals) {
        # Accept well-known SID strings (locale-invariant) or account names. Names like
        # "BUILTIN\Users" do not resolve on non-English Windows, so callers pass SIDs.
        $id = if ($principal -match '^S-\d-') {
            New-Object System.Security.Principal.SecurityIdentifier($principal)
        } else {
            New-Object System.Security.Principal.NTAccount($principal)
        }
        $read = [System.Security.AccessControl.FileSystemRights]'ReadAndExecute'
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, $read, $inherit, $prop, $allow)))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}

function Write-RestrictedText([string] $path, [string] $content) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    New-Item -ItemType File -Path $path | Out-Null
    Set-RestrictedAcl -path $path
    [System.IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-Native([string] $exe, [string[]] $arguments, [hashtable] $env = @{}) {
    $old = @{}
    foreach ($k in $env.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $env[$k]) }
    try {
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "$exe exited with code $LASTEXITCODE (args: $($arguments -join ' '))." }
    } finally {
        foreach ($k in $env.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) }
    }
}

# --- 0. idempotency: remove any prior NodePilot services -------------------------------------
# A re-run/upgrade must not collide with a running postmaster on the reused data directory, and
# must free the old binaries. Stop + delete both services up front (no-op on a clean install).
Write-Step 'Removing any prior NodePilot services'
foreach ($svc in @($ApiServiceName, $DbServiceName)) {
    if (Get-Service -Name $svc -ErrorAction SilentlyContinue) {
        & sc.exe stop $svc | Out-Null
        Start-Sleep -Seconds 2
        & sc.exe delete $svc | Out-Null
        Start-Sleep -Seconds 1
    }
}

# --- 1. ports --------------------------------------------------------------------------------
Write-Step 'Selecting free loopback ports'
$HttpsPort = Get-FreePort $HttpsPortRangeStart $HttpsPortRangeEnd
$PgPort    = Get-FreePort $PgPortRangeStart $PgPortRangeEnd
Write-Host "    HTTPS=$HttpsPort  Postgres=$PgPort"

# --- 2. postgres cluster ---------------------------------------------------------------------
$superSecretFile = Join-Path $SecretsDir 'pg-superuser.secret'
$roleSecretFile  = Join-Path $SecretsDir 'pg-nodepilot.secret'

$clusterInitialized = Test-Path -LiteralPath (Join-Path $PgData 'PG_VERSION')
if (-not $clusterInitialized) {
    Write-Step 'Initializing PostgreSQL cluster (initdb)'
    # Grant the installing user (+ SYSTEM/Admins) FullControl on the still-empty data dir BEFORE
    # initdb runs. PostgreSQL re-execs under a restricted token that drops Administrators, so it
    # writes the cluster as the bare user SID and must own this directory. (NetworkService, needed
    # by the runtime service, is added after init below.)
    Set-RestrictedAcl -path $PgData
    $superPw = New-RandomSecret
    $pwFile = Join-Path $SecretsDir 'initdb.pw'
    try {
        Write-RestrictedText -path $pwFile -content $superPw
        Invoke-Native $initdb @(
            '-D', $PgData, '-U', 'postgres', '-E', 'UTF8',
            '--auth-host=scram-sha-256', '--auth-local=scram-sha-256',
            "--pwfile=$pwFile"
        )
    } finally {
        if (Test-Path -LiteralPath $pwFile) { Remove-Item -LiteralPath $pwFile -Force }
    }
    Write-RestrictedText -path $superSecretFile -content $superPw

    # Loopback-only, own port. Append to postgresql.conf (initdb defaults are otherwise fine).
    $confAppend = @"

# --- NodePilot desktop overrides ---
listen_addresses = '127.0.0.1'
port = $PgPort
ssl = off
"@
    Add-Content -LiteralPath (Join-Path $PgData 'postgresql.conf') -Value $confAppend -Encoding utf8

    # Minimal pg_hba: scram over loopback only (Windows ignores "local" lines).
    $hba = @"
# NodePilot desktop - loopback only, scram-sha-256
host    all             all             127.0.0.1/32            scram-sha-256
host    all             all             ::1/128                 scram-sha-256
"@
    [System.IO.File]::WriteAllText((Join-Path $PgData 'pg_hba.conf'), $hba, (New-Object System.Text.UTF8Encoding($false)))
} else {
    Write-Step 'Reusing existing PostgreSQL cluster'
    if (-not (Test-Path -LiteralPath $superSecretFile)) {
        throw "Cluster exists at $PgData but $superSecretFile is missing - cannot manage roles. Re-install with a clean DataPath."
    }
    $superPw = [System.IO.File]::ReadAllText($superSecretFile).Trim()
    # Ensure the port override reflects the (possibly re-selected) port.
    $confPath = Join-Path $PgData 'postgresql.conf'
    $conf = Get-Content -LiteralPath $confPath -Raw
    if ($conf -notmatch "(?m)^\s*port\s*=\s*$PgPort\b") {
        Add-Content -LiteralPath $confPath -Value "`n# NodePilot desktop re-provision`nport = $PgPort`nlisten_addresses = '127.0.0.1'`nssl = off" -Encoding utf8
    }
}

# NetworkService needs Modify on the data dir to run the cluster.
Set-RestrictedAcl -path $PgData -extraReadPrincipals @()
$pgAcl = Get-Acl -LiteralPath $PgData
$svcRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-20')),
    [System.Security.AccessControl.FileSystemRights]::Modify,
    [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow)
$pgAcl.AddAccessRule($svcRule)
Set-Acl -LiteralPath $PgData -AclObject $pgAcl

# --- 3. role + database (transient start) ----------------------------------------------------
Write-Step 'Ensuring role and database'
$dbSecret = if (Test-Path -LiteralPath $roleSecretFile) {
    [System.IO.File]::ReadAllText($roleSecretFile).Trim()
} else {
    $s = New-RandomSecret; Write-RestrictedText -path $roleSecretFile -content $s; $s
}

Invoke-Native $pg_ctl @('-D', $PgData, '-o', "-p $PgPort -c listen_addresses=127.0.0.1", '-w', 'start')
# PGPASSWORD for the whole block so EVERY psql call authenticates non-interactively; every psql
# also gets -w (--no-password) so it fails fast instead of ever prompting on a console.
$prevPgPassword = $env:PGPASSWORD
$env:PGPASSWORD = $superPw
try {
    $roleExists = (& $psql '-w' '-h' '127.0.0.1' '-p' "$PgPort" '-U' 'postgres' '-d' 'postgres' '-tAc' "SELECT 1 FROM pg_roles WHERE rolname='$DbRole'" 2>$null)
    if ("$roleExists".Trim() -ne '1') {
        Invoke-Native $psql @('-w','-h','127.0.0.1','-p',"$PgPort",'-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c',"CREATE ROLE $DbRole LOGIN PASSWORD '$dbSecret'")
    } else {
        Invoke-Native $psql @('-w','-h','127.0.0.1','-p',"$PgPort",'-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c',"ALTER ROLE $DbRole LOGIN PASSWORD '$dbSecret'")
    }
    $dbExists = (& $psql '-w' '-h' '127.0.0.1' '-p' "$PgPort" '-U' 'postgres' '-d' 'postgres' '-tAc' "SELECT 1 FROM pg_database WHERE datname='$DbName'" 2>$null)
    if ("$dbExists".Trim() -ne '1') {
        Invoke-Native $psql @('-w','-h','127.0.0.1','-p',"$PgPort",'-U','postgres','-d','postgres','-v','ON_ERROR_STOP=1','-c',"CREATE DATABASE $DbName OWNER $DbRole")
    }
} finally {
    $env:PGPASSWORD = $prevPgPassword
    Invoke-Native $pg_ctl @('-D', $PgData, '-w', 'stop')
}

# --- 4. postgres windows service (NetworkService, boot-start) --------------------------------
Write-Step "Registering Postgres service '$DbServiceName'"
if (Get-Service -Name $DbServiceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $DbServiceName | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $DbServiceName | Out-Null
    Start-Sleep -Seconds 2
}
Invoke-Native $pg_ctl @('register', '-N', $DbServiceName, '-D', $PgData,
    '-U', 'NT AUTHORITY\NetworkService', '-S', 'auto', '-o', "-p $PgPort")
& sc.exe config $DbServiceName DisplayName= "$DbServiceDisplayName" | Out-Null
& sc.exe failure $DbServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

# --- 5. loopback certificate (self-signed, pinned by Electron - no root CA install) ----------
Write-Step 'Creating self-signed loopback certificate'
$existing = Get-ChildItem -Path Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -eq 'NodePilot Desktop Local' -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1
$cert = if ($existing) { $existing } else {
    New-SelfSignedCertificate -DnsName 'localhost', '127.0.0.1' `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -FriendlyName 'NodePilot Desktop Local' `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -NotAfter (Get-Date).AddYears(10)
}
$certThumbprint = $cert.Thumbprint
# SHA-256 over the DER cert, uppercase hex without separators (matches Node's fingerprint256 after
# stripping colons, which the Electron shell pins against). Computed manually so we do not depend on
# the GetCertHashString(HashAlgorithmName) overload that only exists on .NET Framework 4.8+.
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $certSha256 = (($sha.ComputeHash($cert.RawData)) | ForEach-Object { $_.ToString('X2') }) -join '' }
finally { $sha.Dispose() }
Write-Host "    Thumbprint=$certThumbprint"

# --- 6. render Production config -------------------------------------------------------------
Write-Step 'Rendering appsettings.Production.json'
$dataEscaped = $DataPath -replace '\\', '\\'
$template = Get-Content -LiteralPath $TemplatePath -Raw
$rendered = $template `
    -replace '\{\{DATA_PATH_ESCAPED\}\}', $dataEscaped `
    -replace '\{\{HTTPS_PORT\}\}', "$HttpsPort" `
    -replace '\{\{CERT_THUMBPRINT\}\}', $certThumbprint
# Strip the leading "//" documentation key so the file is strict JSON.
$rendered = ($rendered -split "`n" | Where-Object { $_ -notmatch '^\s*"//"\s*:' }) -join "`n"
Write-RestrictedText -path $RenderedConfig -content $rendered
# API service (LocalSystem) must read it.
$cfgAcl = Get-Acl -LiteralPath $RenderedConfig
$cfgAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')),
    [System.Security.AccessControl.FileSystemRights]::Read,
    [System.Security.AccessControl.AccessControlType]::Allow)))
Set-Acl -LiteralPath $RenderedConfig -AclObject $cfgAcl

# --- 7. API windows service (LocalSystem, boot-start, depends on DB) --------------------------
Write-Step "Registering API service '$ApiServiceName'"
if (Get-Service -Name $ApiServiceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $ApiServiceName | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $ApiServiceName | Out-Null
    Start-Sleep -Seconds 2
}
$binPath = "`"$ApiExe`" --contentRoot `"$AppPath`""
& sc.exe create $ApiServiceName binPath= $binPath start= delayed-auto obj= 'LocalSystem' DisplayName= "$ApiServiceDisplayName" depend= $DbServiceName | Out-Null
& sc.exe description $ApiServiceName "NodePilot workflow orchestrator (desktop, loopback)." | Out-Null
& sc.exe failure $ApiServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

# Service environment: ASPNETCORE_ENVIRONMENT + the ACL-protected DB connection string.
$connString = "Host=127.0.0.1;Port=$PgPort;Database=$DbName;Username=$DbRole;Password=$dbSecret;SSL Mode=Disable;Maximum Pool Size=50;Minimum Pool Size=2;Connection Idle Lifetime=60;Application Name=NodePilot"
$svcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ApiServiceName"
$envValue = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    'DOTNET_PRINT_TELEMETRY_MESSAGE=false',
    "ConnectionStrings__Postgres=$connString"
)
New-ItemProperty -Path $svcRegPath -Name 'Environment' -PropertyType MultiString -Value $envValue -Force | Out-Null

# Lock the service registry key so only SYSTEM + Administrators can read the connection string.
$regAcl = Get-Acl -Path $svcRegPath
$regAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
    $regAcl.AddAccessRule((New-Object System.Security.AccessControl.RegistryAccessRule(
        $id, [System.Security.AccessControl.RegistryRights]::FullControl,
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit',
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)))
}
Set-Acl -Path $svcRegPath -AclObject $regAcl

# --- 8. installer -> Electron handoff (non-secret) -------------------------------------------
Write-Step 'Writing desktop.json handoff'
$handoff = [ordered]@{
    schemaVersion     = 1
    origin            = "https://localhost:$HttpsPort"
    certificateSha256 = $certSha256
    serviceName       = $ApiServiceName
}
[System.IO.File]::WriteAllText($DesktopJson, ($handoff | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
Set-RestrictedAcl -path $DesktopJson -extraReadPrincipals @('S-1-5-32-545')

# Lock DataPath (the JWT / data-protection key parent) BEFORE the API starts: SYSTEM + Admins only,
# plus Users read/traverse so the Electron shell can reach desktop.json. NO installing-user
# mutation -- otherwise the backend's key-directory security check fail-closes the boot.
Set-RestrictedAcl -path $SecretsDir
Set-RestrictedAcl -path $DataPath -extraReadPrincipals @('S-1-5-32-545') -NoCurrentUser

# --- 9. start services + health poll ---------------------------------------------------------
Write-Step 'Starting services'
& sc.exe start $DbServiceName | Out-Null
Start-Sleep -Seconds 3
& sc.exe start $ApiServiceName | Out-Null

Write-Step 'Waiting for readiness'
$ready = $false
$origin = "https://localhost:$HttpsPort"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { param($s,$certArg,$chain,$errs) $certArg.Thumbprint -eq $certThumbprint }
for ($i = 0; $i -lt 90; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "$origin/healthz/ready" -UseBasicParsing -TimeoutSec 3
        if ($resp.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 2 }
}
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null

if (-not $ready) {
    Write-Warning "API did not report /healthz/ready within the timeout. Check $LogsDir and the '$ApiServiceName' service."
    exit 1
}

# --- 10. admin bootstrap handoff (first run only) --------------------------------------------
# The API wrote a SYSTEM-owned one-shot setup token under DataPath during first boot. Copy it
# into the installing user's profile so the user-context Electron shell (started next) can read
# it and drive first-run admin creation. ACL-restricted to the installing user + SYSTEM. No-op
# on re-install (token absent once users exist).
Write-Step 'Writing admin setup handoff'
$tokenPath = Join-Path $DataPath 'admin-setup.token'
if (Test-Path -LiteralPath $tokenPath) {
    $handoffDir = Join-Path $env:LOCALAPPDATA 'NodePilot'
    New-Item -ItemType Directory -Force -Path $handoffDir | Out-Null
    $handoffPath = Join-Path $handoffDir 'admin-setup.handoff'
    $tokenValue = [System.IO.File]::ReadAllText($tokenPath)
    $userSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User
    if (Test-Path -LiteralPath $handoffPath) { Remove-Item -LiteralPath $handoffPath -Force }
    New-Item -ItemType File -Path $handoffPath | Out-Null
    $hacl = New-Object System.Security.AccessControl.FileSecurity
    $hacl.SetAccessRuleProtection($true, $false)
    $hacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $userSid, [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow)))
    $hacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')),
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow)))
    Set-Acl -LiteralPath $handoffPath -AclObject $hacl
    [System.IO.File]::WriteAllText($handoffPath, $tokenValue, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    First-run handoff written to $handoffPath"
}

Write-Host "NodePilot desktop runtime provisioned. Origin: $origin" -ForegroundColor Green
exit 0
