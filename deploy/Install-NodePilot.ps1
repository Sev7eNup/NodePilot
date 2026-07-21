#requires -Version 5.1
#requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs NodePilot as a Windows Service on a domain-joined server with a gMSA,
    TLS via Kestrel + cert from LocalMachine\My, and either SQL Server 2022 (Trusted Connection)
    or PostgreSQL 16+ (user/password) as the backing database.

.DESCRIPTION
    Prerequisites (see deploy\README.md for details):
      - Windows Server 2022/2025, domain-joined
      - .NET 10 ASP.NET Core Runtime / Hosting Bundle installed
      - gMSA created in AD, installed on this host (Test-ADServiceAccount green)
      - SQL Server path: gMSA has SQL login with db_owner on the target DB
      - Postgres path: role created with CREATEDB or the DB already exists + owned by role
      - TLS cert imported into Cert:\LocalMachine\My (with private key)

    The installer is idempotent: re-running it stops/reinstalls the service cleanly.

.PARAMETER ArtifactPath
    Path to the NodePilot-*.zip produced by Build-Artifact.ps1.

.PARAMETER TrustedArtifactSignerThumbprint
    Thumbprint of the trusted enterprise Code Signing certificate. The detached CMS
    signature and signed SHA-256 manifest next to the ZIP are mandatory.

.PARAMETER ServiceAccount
    gMSA name including the '$' suffix, e.g. 'CONTOSO\svc-nodepilot$'. Mutually exclusive with
    -UseLocalSystem. Required unless -UseLocalSystem is passed.

.PARAMETER UseLocalSystem
    Run the service as LocalSystem instead of a gMSA. LocalSystem authenticates on the network
    as the computer account (DOMAIN\<host>$): SQL Server Trusted_Connection logs in as that
    account and integrated WinRM presents it to targets. Grant the computer account a SQL login
    with db_owner on the target DB. No gMSA, no 'Log on as a service' grant, no managed-account
    wiring needed - LocalSystem has all of that inherently.

.PARAMETER DbProvider
    Backing database provider. One of 'sqlserver' (default) or 'postgres'.

.PARAMETER SqlServer
    Hostname or host\instance of the SQL Server 2022 instance. Required when DbProvider=sqlserver.

.PARAMETER SqlDatabase
    Target SQL Server database name. Default: NodePilot.

.PARAMETER SqlCertificateHostName
    DNS name expected in the SQL Server certificate. Defaults to the host portion of SqlServer.

.PARAMETER PostgresHost
    Hostname of the PostgreSQL server. Required when DbProvider=postgres.

.PARAMETER PostgresPort
    PostgreSQL port. Default: 5432.

.PARAMETER PostgresDatabase
    Target PostgreSQL database name. Default: nodepilot.

.PARAMETER PostgresUser
    PostgreSQL role NodePilot connects as. Required when DbProvider=postgres.

.PARAMETER PostgresPassword
    Password for PostgresUser. Required when DbProvider=postgres. The generated JSON contains no
    database password. The installer injects the complete, correctly quoted connection string
    through the service-scoped ConnectionStrings__Postgres environment value after restricting
    the service registry key to SYSTEM and Administrators.

.PARAMETER PostgresRootCertificate
    PEM root CA used to validate the PostgreSQL server certificate. Required for Postgres;
    copied into the ACL-restricted NodePilot data directory.

.PARAMETER CertThumbprint
    40-hex thumbprint of the TLS cert in Cert:\LocalMachine\My (spaces/case ignored).

.PARAMETER PublicHostname
    Public hostname used for the admin setup URL shown at the end. Defaults to the
    machine FQDN.

.PARAMETER HttpsPort
    HTTPS listen port. Default: 443.

.PARAMETER HttpPort
    HTTP listen port (HTTP → HTTPS redirect). Default: 80. Pass 0 to disable HTTP binding.

.PARAMETER InstallPath
    Install directory (read-only for the service). Default: C:\Program Files\NodePilot.

.PARAMETER DataPath
    Writable data directory (logs, JWT key, admin-setup.token). Default: C:\ProgramData\NodePilot.

.PARAMETER ServiceName
    Windows Service name. Default: NodePilot.

.PARAMETER ServiceDisplayName
    Windows Service display name. Default: NodePilot Orchestrator.

.PARAMETER ExternalTriggerApiKey
    Pre-shared key for POST /api/trigger/{workflow}. Auto-generated (48 random bytes base64) if omitted.

.PARAMETER JwtIssuer
    JWT issuer claim. Default: nodepilot:prod:<machine-name>.

.PARAMETER JwtAudience
    JWT audience claim. Default: nodepilot:prod:<machine-name>.

.PARAMETER AllowedHosts
    ASP.NET Core AllowedHosts value. Default: the public hostname.

.PARAMETER KnownProxyIps
    Transport IP address(es) of trusted reverse proxies. NodePilot accepts X-Forwarded-For
    and X-Forwarded-Proto only from these peers (plus loopback). Pass every HAProxy node;
    for example -KnownProxyIps '10.0.1.5','10.0.1.6'. Do not pass the public VIP.

.PARAMETER SkipSqlConnectivityCheck
    Skip the Test-NetConnection probe to SQL / Postgres. Use when the installer host cannot
    reach the DB port but the service account will once the service starts.

.PARAMETER SkipGmsaCheck
    Skip Test-ADServiceAccount. Use when the ActiveDirectory module is unavailable on the
    install host (rare - usually a sign of missing RSAT).

.EXAMPLE
    # SQL Server (default)
    .\deploy\Install-NodePilot.ps1 `
        -ArtifactPath C:\Packages\NodePilot-2026.04.23.zip `
        -TrustedArtifactSignerThumbprint '0123456789ABCDEF0123456789ABCDEF01234567' `
        -ServiceAccount 'CONTOSO\svc-nodepilot$' `
        -SqlServer 'sql01.contoso.local' `
        -CertThumbprint 'A1B2C3D4E5F6...' `
        -PublicHostname 'nodepilot.contoso.local'

.EXAMPLE
    # PostgreSQL
    $pw = Read-Host -Prompt 'Postgres password' -AsSecureString
    .\deploy\Install-NodePilot.ps1 `
        -ArtifactPath C:\Packages\NodePilot-2026.04.23.zip `
        -TrustedArtifactSignerThumbprint '0123456789ABCDEF0123456789ABCDEF01234567' `
        -ServiceAccount 'CONTOSO\svc-nodepilot$' `
        -DbProvider postgres `
        -PostgresHost 'pg01.contoso.local' `
        -PostgresDatabase 'nodepilot' `
        -PostgresUser 'nodepilot' `
        -PostgresPassword $pw `
        -PostgresRootCertificate 'C:\PKI\postgres-root-ca.pem' `
        -CertThumbprint 'A1B2C3D4E5F6...' `
        -PublicHostname 'nodepilot.contoso.local'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$TrustedArtifactSignerThumbprint,
    [string]$ServiceAccount,
    [switch]$UseLocalSystem,
    [ValidateSet('sqlserver','postgres')][string]$DbProvider = 'sqlserver',
    [string]$SqlServer,
    [string]$SqlDatabase = 'NodePilot',
    [string]$SqlCertificateHostName,
    [string]$PostgresHost,
    [int]$PostgresPort = 5432,
    [string]$PostgresDatabase = 'nodepilot',
    [string]$PostgresUser,
    [System.Security.SecureString]$PostgresPassword,
    [string]$PostgresRootCertificate,
    [Parameter(Mandatory)][string]$CertThumbprint,
    [string]$PublicHostname,
    [int]$HttpsPort = 443,
    [int]$HttpPort = 80,
    [string]$InstallPath = 'C:\Program Files\NodePilot',
    [string]$DataPath = 'C:\ProgramData\NodePilot',
    [string]$ServiceName = 'NodePilot',
    [string]$ServiceDisplayName = 'NodePilot Orchestrator',
    [string]$ExternalTriggerApiKey,
    [string]$JwtIssuer,
    [string]$JwtAudience,
    [string]$AllowedHosts,
    [string[]]$KnownProxyIps = @(),
    [switch]$SkipSqlConnectivityCheck,
    [switch]$SkipGmsaCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$ArtifactSecurityScript = Join-Path $PSScriptRoot 'ArtifactSecurity.ps1'
if (-not (Test-Path -LiteralPath $ArtifactSecurityScript -PathType Leaf)) {
    throw "Artifact security helper not found: $ArtifactSecurityScript"
}
. $ArtifactSecurityScript
$artifactLock = $null
$artifactStage = $null
$installRollbackDir = $null
$previousSettingsBytes = $null
$previousService = $null
$previousAclIdentity = 'NT AUTHORITY\SYSTEM'
$postgresRootCertificateTarget = $null
$previousPostgresRootCertificateBytes = $null
$previousPostgresRootCertificateAcl = $null
$postgresRootCertificatePreviouslyExisted = $false
$postgresRootCertificateTouched = $false
$installMutationStarted = $false
try {
    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) { throw "Artifact not found: $ArtifactPath" }
    $ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
    # FileShare.Read prevents replacement, deletion or modification between verification
    # and Expand-Archive while still allowing the extractor to open a read handle.
    $artifactLock = [IO.File]::Open($ArtifactPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $verifiedArtifact = Assert-NodePilotSignedArtifact `
        -ArtifactPath $ArtifactPath `
        -TrustedSignerThumbprint $TrustedArtifactSignerThumbprint `
        -ArtifactStream $artifactLock
    Write-Host "[install] Verified signed artifact version $($verifiedArtifact.Version), signer $($verifiedArtifact.SignerThumbprint)." -ForegroundColor Green

# Provider-specific param validation. The param() block makes all DB params optional so a
# single entry point works for both SQL Server and Postgres; we enforce the actually-required
# subset here so callers get a clean error instead of a mid-install surprise.
$DbProvider = $DbProvider.ToLowerInvariant()
if ($DbProvider -eq 'sqlserver') {
    if ([string]::IsNullOrWhiteSpace($SqlServer)) {
        throw "-SqlServer is required when -DbProvider is 'sqlserver'."
    }
    if ([string]::IsNullOrWhiteSpace($SqlCertificateHostName)) {
        $SqlCertificateHostName = (($SqlServer -replace '^tcp:', '') -split '[\\,]')[0]
    }
} elseif ($DbProvider -eq 'postgres') {
    if ([string]::IsNullOrWhiteSpace($PostgresHost)) { throw "-PostgresHost is required when -DbProvider is 'postgres'." }
    if ([string]::IsNullOrWhiteSpace($PostgresUser)) { throw "-PostgresUser is required when -DbProvider is 'postgres'." }
    if ($null -eq $PostgresPassword -or $PostgresPassword.Length -eq 0) {
        throw "-PostgresPassword is required when -DbProvider is 'postgres'. Pass a SecureString (e.g. (Read-Host -AsSecureString))."
    }
    if ([string]::IsNullOrWhiteSpace($PostgresRootCertificate) -or
        -not (Test-Path -LiteralPath $PostgresRootCertificate -PathType Leaf)) {
        throw "-PostgresRootCertificate must point to the trusted PostgreSQL root CA PEM file when -DbProvider is 'postgres'."
    }
    $PostgresRootCertificate = (Resolve-Path -LiteralPath $PostgresRootCertificate).Path
}

$normalizedKnownProxyIps = @()
foreach ($proxyIp in $KnownProxyIps) {
    $parsedProxyIp = $null
    if ([string]::IsNullOrWhiteSpace($proxyIp) -or
        -not [System.Net.IPAddress]::TryParse($proxyIp, [ref]$parsedProxyIp)) {
        throw "-KnownProxyIps contains an invalid IP address: '$proxyIp'. Use transport IPs, not hostnames or CIDR ranges."
    }
    $normalizedKnownProxyIps += $parsedProxyIp.ToString()
}

# ---------------------------------------------------------------------------
# Service-identity resolution: gMSA vs LocalSystem
# ---------------------------------------------------------------------------
# Two supported runtime identities:
#   * gMSA         -ServiceAccount 'CONTOSO\svc-nodepilot$'  (AD-managed password)
#   * LocalSystem  -UseLocalSystem  (or -ServiceAccount 'LocalSystem')
# LocalSystem authenticates on the network as the COMPUTER account (DOMAIN\<host>$):
#   - SQL Server Trusted_Connection logs in as DOMAIN\<host>$ (that login needs db_owner)
#   - integrated WinRM presents the computer account to targets
# It already holds FullControl on the machine plus SeServiceLogonRight, so the per-file ACL
# grants, the managed-account flag, and the 'Log on as a service' grant the gMSA path needs
# are all no-ops here.
$LocalSystemAliases = @('localsystem', 'nt authority\system', 'system', '.\localsystem')
$isLocalSystem = $UseLocalSystem.IsPresent -or
    ($ServiceAccount -and ($LocalSystemAliases -contains $ServiceAccount.Trim().ToLowerInvariant()))

if ($isLocalSystem) {
    if ($ServiceAccount -and -not ($LocalSystemAliases -contains $ServiceAccount.Trim().ToLowerInvariant())) {
        throw "Pass either -UseLocalSystem or a gMSA via -ServiceAccount, not both with conflicting values."
    }
    $ScmStartName    = 'LocalSystem'          # SCM canonical name for Win32_Service.Create
    $AclIdentity     = 'NT AUTHORITY\SYSTEM'  # valid NTAccount string for ACL rules
    $AccountLabel    = 'LocalSystem (NT AUTHORITY\SYSTEM)'
    $ComputerAccount = "$env:USERDOMAIN\$env:COMPUTERNAME`$"
    $SqlPrincipal    = $ComputerAccount
} else {
    if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
        throw "Specify a service identity: either -UseLocalSystem (run as LocalSystem) or -ServiceAccount '<DOMAIN>\<gmsa>`$' (run as a gMSA)."
    }
    $ScmStartName    = $ServiceAccount
    $AclIdentity     = $ServiceAccount
    $AccountLabel    = $ServiceAccount
    $ComputerAccount = $null
    $SqlPrincipal    = $ServiceAccount
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step { param([string]$Text) Write-Host "[install] $Text" -ForegroundColor Cyan }
function Write-Info { param([string]$Text) Write-Host "[install] $Text" -ForegroundColor Gray }
function Write-Ok   { param([string]$Text) Write-Host "[install] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "[install] $Text" -ForegroundColor Yellow }

function ConvertTo-NormalizedThumbprint {
    param([string]$Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) { return '' }
    ($Raw -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function Get-RandomBase64 {
    param([int]$ByteCount = 48)
    # RNGCryptoServiceProvider works on both Windows PowerShell 5.1 (.NET Framework)
    # and PowerShell 7 (.NET 6+). RandomNumberGenerator.Fill() is Core-only.
    $buf = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($buf)
}

function Test-DotNet10Runtime {
    try {
        $lines = & dotnet --list-runtimes 2>$null
    } catch {
        throw ".NET Runtime not found on PATH. Install the .NET 10 ASP.NET Core Hosting Bundle from https://dotnet.microsoft.com/download."
    }
    if (-not ($lines -match '^Microsoft\.AspNetCore\.App 10\.')) {
        throw ".NET 10 ASP.NET Core Runtime not found. Install the Hosting Bundle (https://dotnet.microsoft.com/download)."
    }
}

function Set-DirectoryAclForService {
    <#
      $DataPath must be writable by the service account and readable by Administrators/SYSTEM
      only. Inheritance is disabled so nothing from Program Files or ProgramData parent ACLs
      leaks in.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$ReadOnlyForService,
        [switch]$SkipServiceRule
    )

    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)

    # Wipe inherited ACEs that SetAccessRuleProtection preserved-as-explicit.
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

    $sysAdmin = @(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
    foreach ($id in $sysAdmin) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }

    # LocalSystem is already covered by the SYSTEM FullControl ACE above - adding a second ACE
    # for the same SID is redundant, so the caller passes -SkipServiceRule in that case.
    if (-not $SkipServiceRule) {
        $svcRights = if ($ReadOnlyForService) { 'ReadAndExecute' } else { 'Modify' }
        $svcRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $ServiceAccount, $svcRights,
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($svcRule)
    }

    Set-Acl -Path $Path -AclObject $acl
}

function Set-FileAclForService {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [switch]$SkipServiceRule
    )
    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }

    foreach ($id in @(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, 'FullControl', 'None', 'None', 'Allow')))
    }
    # LocalSystem already has FullControl via the SYSTEM ACE above - skip the redundant Read ACE.
    if (-not $SkipServiceRule) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $ServiceAccount, 'Read', 'None', 'None', 'Allow')))
    }

    Set-Acl -Path $Path -AclObject $acl
}

function Set-ServiceRegistryAclForSecrets {
    <#
      Service Environment values live under HKLM\SYSTEM\CurrentControlSet\Services\<name>.
      Windows grants BUILTIN\Users ReadKey there by default, so placing a password in the
      Environment MULTI_SZ without first protecting the service key simply moves the plaintext
      leak from JSON to the registry. SCM runs as SYSTEM and remains fully authorised.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    @($acl.Access) | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
    foreach ($identity in @(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.RegistryAccessRule(
            $identity,
            [Security.AccessControl.RegistryRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Grant-CertPrivateKeyAccess {
    <#
      The service account needs READ access to the private key backing the TLS cert.
      Works for both RSA (CAPI/CNG via X509Certificate2.GetRSAPrivateKey) and ECDSA.
      We locate the on-disk key container and apply an icacls grant.
    #>
    param(
        [Parameter(Mandatory)][string]$Thumbprint,
        [Parameter(Mandatory)][string]$ServiceAccount
    )

    $cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $Thumbprint }
    if (-not $cert) { throw "Certificate $Thumbprint not found in LocalMachine\My." }
    if (-not $cert.HasPrivateKey) { throw "Certificate $Thumbprint has no private key." }

    # Try CNG first (modern), fall back to RSA CSP (legacy .PFX imports).
    $keyName = $null
    try {
        $cng = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        if ($cng -is [System.Security.Cryptography.RSACng]) {
            $keyName = $cng.Key.UniqueName
        } elseif ($cng -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $keyName = $cng.CspKeyContainerInfo.UniqueKeyContainerName
        }
    } catch { }
    if (-not $keyName) {
        # Last resort: check the ECDSA path.
        try {
            $ecd = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert)
            if ($ecd -is [System.Security.Cryptography.ECDsaCng]) {
                $keyName = $ecd.Key.UniqueName
            }
        } catch { }
    }
    if (-not $keyName) {
        throw "Could not locate private-key file for cert $Thumbprint. Re-import the PFX with -KeyStorageFlags MachineKeySet|PersistKeySet and retry."
    }

    $candidatePaths = @(
        "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\$keyName",
        "$env:ProgramData\Microsoft\Crypto\Keys\$keyName",
        "$env:ProgramData\Microsoft\Crypto\SystemKeys\$keyName"
    )
    $keyFile = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $keyFile) {
        throw "Private-key file '$keyName' not found under $env:ProgramData\Microsoft\Crypto. Verify the cert was imported to the machine store."
    }

    Write-Info "Granting $ServiceAccount READ on key file: $keyFile"
    & icacls.exe $keyFile /grant ("{0}:(R)" -f $ServiceAccount) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls grant failed on $keyFile (exit $LASTEXITCODE)."
    }
}

function Test-SqlReachable {
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$CertificateHostName
    )
    # Connection using the installer's current Windows identity. Actual service runtime
    # will use the gMSA - we're just verifying the SQL instance is reachable and that the
    # database exists. If the installing admin is not authorised, the script still surfaces
    # a clear error with a T-SQL remediation snippet.
    # Windows PowerShell 5.1's legacy System.Data.SqlClient cannot express
    # HostNameInCertificate. Connect through the certificate hostname while preserving an
    # explicit instance/port suffix so this preflight validates the same identity the .NET 10
    # runtime pins via HostNameInCertificate.
    $serverWithoutPrefix = $Server -replace '^tcp:', ''
    $suffixIndex = $serverWithoutPrefix.IndexOfAny([char[]]@('\', ','))
    $serverSuffix = if ($suffixIndex -ge 0) { $serverWithoutPrefix.Substring($suffixIndex) } else { '' }
    $tlsValidationServer = "tcp:$CertificateHostName$serverSuffix"

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Server'] = $tlsValidationServer
    $builder['Database'] = $Database
    $builder['Integrated Security'] = $true
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $false
    $builder['Connect Timeout'] = 10
    $builder['Application Name'] = 'NodePilot-Installer'

    $conn = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = 'SELECT 1'
        [void]$cmd.ExecuteScalar()
    } finally {
        $conn.Dispose()
    }
}

function Enable-SqlReadCommittedSnapshot {
    <#
      Enable READ_COMMITTED_SNAPSHOT on the target database. This is the SQL-Server-side
      analogue to Postgres MVCC: without it, long-running readers (the WorkflowStatsRefresher's
      GROUP BY sweeps, retention scans) block every concurrent INSERT into WorkflowExecutions
      / StepExecutions under the default 2PL locking model. RCSI flips reads to row-versioning;
      writers no longer block readers and vice versa. It's the single biggest production
      foot-gun for the SQL Server provider.

      Idempotent: checks `sys.databases.is_read_committed_snapshot_on` first. WITH ROLLBACK
      IMMEDIATE drops any open sessions on the target DB so the ALTER can grab the brief
      exclusive lock it needs — safe at install time when the service isn't running yet.
      Failure here is a warning, not a hard fail: RCSI is performance, not correctness.
    #>
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$CertificateHostName
    )
    $serverWithoutPrefix = $Server -replace '^tcp:', ''
    $suffixIndex = $serverWithoutPrefix.IndexOfAny([char[]]@('\', ','))
    $serverSuffix = if ($suffixIndex -ge 0) { $serverWithoutPrefix.Substring($suffixIndex) } else { '' }
    $tlsValidationServer = "tcp:$CertificateHostName$serverSuffix"

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Server'] = $tlsValidationServer
    # Connect to master so ALTER DATABASE ... WITH ROLLBACK IMMEDIATE doesn't kick out our
    # own session.
    $builder['Database'] = 'master'
    $builder['Integrated Security'] = $true
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = $false
    $builder['Connect Timeout'] = 10
    $builder['Application Name'] = 'NodePilot-Installer'

    $conn = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
    try {
        $conn.Open()

        $check = $conn.CreateCommand()
        $check.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @db"
        [void]$check.Parameters.AddWithValue('@db', $Database)
        $current = $check.ExecuteScalar()
        if ($null -eq $current) {
            Write-Warn "  Database '$Database' not found while checking RCSI - skipped."
            return
        }
        if ([bool]$current) {
            Write-Info "  RCSI already enabled on $Database."
            return
        }

        $alter = $conn.CreateCommand()
        # QUOTENAME the database name to defend against unusual identifiers; sp_executesql
        # would also work but adds noise. Database names cannot contain ']' so escape is the
        # standard Replace pattern.
        $escaped = $Database.Replace(']', ']]')
        $alter.CommandText = "ALTER DATABASE [$escaped] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE"
        $alter.CommandTimeout = 60
        [void]$alter.ExecuteNonQuery()
        Write-Ok "  RCSI enabled on $Database (READ_COMMITTED_SNAPSHOT ON)."
    } finally {
        $conn.Dispose()
    }
}

function Test-PostgresReachable {
    <#
      TCP port probe against the Postgres endpoint. We don't attempt a full auth + query
      because Npgsql isn't shipped with the installer and pulling it in would bloat the
      bootstrap. A 503 "can't even connect" is the common failure mode we want to catch
      before starting the service; role/password errors surface in the health-probe step.

      Param is named HostName (not Host) because $Host is a reserved PowerShell automatic
      variable (PSAvoidAssignmentToAutomaticVariable).
    #>
    param(
        [Parameter(Mandatory)][string]$HostName,
        [Parameter(Mandatory)][int]$Port
    )
    $tnc = Test-NetConnection -ComputerName $HostName -Port $Port -WarningAction SilentlyContinue
    if (-not $tnc.TcpTestSucceeded) {
        throw "TCP probe failed to $HostName`:$Port. Check DNS, firewall, and pg_hba.conf."
    }
}

function Invoke-HealthProbe {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSeconds = 60
    )
    # Trust any cert on localhost - the installer may run before a public hostname resolves,
    # and the only caller is the installer itself.
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            try {
                $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -SkipCertificateCheck -TimeoutSec 5
                if ($r.StatusCode -eq 200) { return $true }
            } catch { Start-Sleep -Seconds 2 }
        }
        return $false
    } else {
        if (-not ('TrustAllCerts' -as [type])) {
            Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllCerts : ICertificatePolicy {
  public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; }
}
"@
        }
        $previousCertificatePolicy = [System.Net.ServicePointManager]::CertificatePolicy
        $previousSecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol
        try {
            [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCerts
            [System.Net.ServicePointManager]::SecurityProtocol = 'Tls12, Tls13'
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline) {
                try {
                    $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
                    if ($r.StatusCode -eq 200) { return $true }
                } catch { Start-Sleep -Seconds 2 }
            }
            return $false
        }
        finally {
            [System.Net.ServicePointManager]::CertificatePolicy = $previousCertificatePolicy
            [System.Net.ServicePointManager]::SecurityProtocol = $previousSecurityProtocol
        }
    }
}

function Grant-LogOnAsServiceRight {
    <#
      Grants SeServiceLogonRight to the given account. Required because Win32_Service.Create
      does NOT auto-grant this right (unlike New-Service / sc.exe). Without it the SCM rejects
      the service start with a generic "Cannot start service" error.

      We export the current LSA policy with secedit, append the account's SID to
      SeServiceLogonRight (idempotent), and re-import. Pure built-in tooling, no external
      module dependency, no P/Invoke.
    #>
    param([Parameter(Mandatory)][string]$Account)

    # Resolve account -> SID. The INF file expects "*S-1-5-21-..." format.
    $ntAccount = New-Object System.Security.Principal.NTAccount($Account)
    $sid       = $ntAccount.Translate([System.Security.Principal.SecurityIdentifier]).Value
    $sidEntry  = "*$sid"

    $tempBase = [System.IO.Path]::GetTempPath()
    $exportInf = Join-Path $tempBase ('np-rights-export-{0}.inf' -f ([Guid]::NewGuid().ToString('N')))
    $importInf = Join-Path $tempBase ('np-rights-import-{0}.inf' -f ([Guid]::NewGuid().ToString('N')))
    $tempSdb   = Join-Path $tempBase ('np-rights-{0}.sdb'        -f ([Guid]::NewGuid().ToString('N')))

    try {
        & secedit.exe /export /cfg $exportInf /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exportInf)) {
            throw "secedit.exe /export failed (exit $LASTEXITCODE)."
        }

        $line = Select-String -Path $exportInf -Pattern '^\s*SeServiceLogonRight\s*=\s*(.*)$' |
                Select-Object -First 1
        $existing = if ($line) { $line.Matches[0].Groups[1].Value.Trim() } else { '' }

        $alreadyGranted = $false
        if ($existing) {
            $alreadyGranted = ($existing -split ',') |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -ieq $sidEntry } |
                Select-Object -First 1
        }
        if ($alreadyGranted) {
            Write-Info "  $Account already has 'Log on as a service' (SID $sid)."
            return
        }

        $newValue = if ($existing) { "$existing,$sidEntry" } else { $sidEntry }

        $infContent = @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeServiceLogonRight = $newValue
"@
        Set-Content -Path $importInf -Value $infContent -Encoding Unicode

        & secedit.exe /configure /db $tempSdb /cfg $importInf /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "secedit.exe /configure failed (exit $LASTEXITCODE)."
        }
        Write-Info "  Granted 'Log on as a service' to $Account (SID $sid)."
    }
    finally {
        Remove-Item $exportInf, $importInf, $tempSdb -ErrorAction SilentlyContinue
    }
}

function Remove-ExistingService {
    param([Parameter(Mandatory)][string]$Name)
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    Write-Info "Existing service '$Name' found - stopping and removing."
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force -ErrorAction Stop
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
            if (-not $s -or $s.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }
        $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($s -and $s.Status -ne 'Stopped') {
            throw "Service '$Name' did not stop within 30s; refusing to delete the service or replace its files."
        }
    }
    & sc.exe delete $Name | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Service '$Name' could not be removed within 20s."
}

function Get-ServiceRollbackSnapshot {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return $null }
    $escapedName = $Name.Replace("'", "''")
    try {
        $service = Get-CimInstance `
            -ClassName Win32_Service `
            -Filter "Name='$escapedName'" `
            -ErrorAction Stop
    }
    catch {
        throw "Could not snapshot existing service '$Name'; refusing to mutate it: $($_.Exception.Message)"
    }
    if (-not $service) {
        throw "Service '$Name' exists but its configuration could not be read; refusing to mutate it."
    }
    $normalizedStartName = $service.StartName.Trim().ToLowerInvariant()
    $isRestorableSystemAccount = $normalizedStartName -in @(
        'localsystem', '.\localsystem', 'system', 'nt authority\system')
    if (-not $isRestorableSystemAccount -and -not $service.StartName.TrimEnd().EndsWith('$')) {
        throw "Existing service '$Name' uses '$($service.StartName)'. Only LocalSystem and gMSA services can be transactionally restored because other account passwords are not recoverable."
    }

    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $environment = @()
    $delayedAutoStart = 0
    if (-not (Test-Path -LiteralPath $registryPath)) {
        throw "Service '$Name' has no readable registry configuration; refusing to mutate it."
    }
    try { $registryValues = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop }
    catch {
        throw "Could not snapshot registry configuration for service '$Name'; refusing to mutate it: $($_.Exception.Message)"
    }
    if ($registryValues.PSObject.Properties.Name -contains 'Environment') {
        $environment = @($registryValues.Environment | Where-Object { $null -ne $_ })
    }
    if ($registryValues.PSObject.Properties.Name -contains 'DelayedAutoStart') {
        $delayedAutoStart = [int]$registryValues.DelayedAutoStart
    }
    return [pscustomobject]@{
        Name = $Name
        DisplayName = [string]$service.DisplayName
        PathName = [string]$service.PathName
        StartName = [string]$service.StartName
        StartMode = [string]$service.StartMode
        WasRunning = [string]$service.State -eq 'Running'
        Environment = $environment
        DelayedAutoStart = $delayedAutoStart
    }
}

function Restore-ServiceRollbackSnapshot {
    param([Parameter(Mandatory)]$Snapshot)

    $isSystem = $Snapshot.StartName.Trim().ToLowerInvariant() -in @(
        'localsystem', '.\localsystem', 'system', 'nt authority\system')
    if (-not $isSystem -and -not $Snapshot.StartName.TrimEnd().EndsWith('$')) {
        throw "Cannot automatically restore service account '$($Snapshot.StartName)' because its password is not recoverable."
    }

    $createArgs = @{
        Name            = $Snapshot.Name
        DisplayName     = $Snapshot.DisplayName
        PathName        = $Snapshot.PathName
        ServiceType     = [byte]16
        StartMode       = $(if ($Snapshot.StartMode -eq 'Disabled') { 'Disabled' } elseif ($Snapshot.StartMode -eq 'Manual') { 'Manual' } else { 'Automatic' })
        ErrorControl    = [byte]1
        StartName       = $(if ($isSystem) { 'LocalSystem' } else { $Snapshot.StartName })
        StartPassword   = $(if ($isSystem) { $null } else { '' })
        DesktopInteract = $false
    }
    $createResult = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments $createArgs
    if ($createResult.ReturnValue -ne 0) {
        throw "Rollback could not recreate service '$($Snapshot.Name)' (Win32_Service.Create=$($createResult.ReturnValue))."
    }

    if (-not $isSystem) {
        & sc.exe managedaccount $Snapshot.Name true | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Rollback could not restore gMSA managed-account flag." }
    }
    if ($Snapshot.StartMode -eq 'Auto' -and $Snapshot.DelayedAutoStart -eq 1) {
        & sc.exe config $Snapshot.Name start= delayed-auto | Out-Null
    }

    if ($Snapshot.Environment.Count -gt 0) {
        $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$($Snapshot.Name)"
        if (@($Snapshot.Environment | Where-Object { $_ -like 'ConnectionStrings__Postgres=*' }).Count -gt 0) {
            Set-ServiceRegistryAclForSecrets -Path $registryPath
        }
        New-ItemProperty `
            -Path $registryPath `
            -Name Environment `
            -PropertyType MultiString `
            -Value @($Snapshot.Environment) `
            -Force | Out-Null
    }

    if ($Snapshot.WasRunning) {
        Start-Service -Name $Snapshot.Name -ErrorAction Stop
    }
}

# ---------------------------------------------------------------------------
# Defaults + derived values
# ---------------------------------------------------------------------------

if (-not $PublicHostname) {
    $PublicHostname = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
}
if (-not $JwtIssuer)   { $JwtIssuer   = "nodepilot:prod:$env:COMPUTERNAME" }
if (-not $JwtAudience) { $JwtAudience = "nodepilot:prod:$env:COMPUTERNAME" }
if (-not $AllowedHosts) { $AllowedHosts = $PublicHostname }
if (-not $ExternalTriggerApiKey) { $ExternalTriggerApiKey = Get-RandomBase64 -ByteCount 48 }

$NormalizedThumbprint = ConvertTo-NormalizedThumbprint -Raw $CertThumbprint
if ($NormalizedThumbprint.Length -ne 40) {
    $rawLen = if ($null -eq $CertThumbprint) { 0 } else { $CertThumbprint.Length }
    Write-Host ""
    Write-Host "Certificates currently in Cert:\LocalMachine\My on this machine:" -ForegroundColor Yellow
    $certsHere = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Select-Object Thumbprint, Subject, @{n='HasKey';e={$_.HasPrivateKey}}, NotAfter
    if ($certsHere) {
        $certsHere | Format-Table -AutoSize | Out-String | Write-Host
    } else {
        Write-Host "  (none)" -ForegroundColor Gray
    }
    throw ("Cert thumbprint must be 40 hex chars after normalization. " +
           "Raw -CertThumbprint input was '$CertThumbprint' (length $rawLen); " +
           "after stripping non-hex got '$NormalizedThumbprint' (length $($NormalizedThumbprint.Length)). " +
           "Check the list above for the correct thumbprint, then re-run with the full 40-char value.")
}
$BindHttp = $HttpPort -gt 0
$BindHttpJson = if ($BindHttp) { 'true' } else { 'false' }

Write-Step "NodePilot installer"
Write-Info "  Artifact      : $ArtifactPath"
Write-Info "  Service       : $ServiceName ($ServiceDisplayName)"
Write-Info "  Service acct  : $AccountLabel"
Write-Info "  Install path  : $InstallPath"
Write-Info "  Data path     : $DataPath"
Write-Info "  DB provider   : $DbProvider"
if ($DbProvider -eq 'sqlserver') {
    Write-Info "  SQL Server    : $SqlServer / $SqlDatabase"
} else {
    Write-Info "  Postgres      : $($PostgresUser)@$($PostgresHost):$PostgresPort / $PostgresDatabase"
}
Write-Info "  HTTPS port    : $HttpsPort"
Write-Info "  HTTP  port    : $(if ($BindHttp) { "$HttpPort (redirect)" } else { 'disabled' })"
Write-Info "  Cert thumb    : $NormalizedThumbprint"
Write-Info "  Public host   : $PublicHostname"

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------

Write-Step "Pre-flight checks"

if (-not (Test-Path $ArtifactPath)) { throw "Artifact not found: $ArtifactPath" }
Test-DotNet10Runtime
Write-Info "  .NET 10 ASP.NET Core runtime found."

# Cert present + private key
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $NormalizedThumbprint }
if (-not $cert) {
    throw "Cert $NormalizedThumbprint not found in Cert:\LocalMachine\My. Import the PFX (MachineKeySet|PersistKeySet) and retry."
}
if (-not $cert.HasPrivateKey) {
    throw "Cert $NormalizedThumbprint has no private key. Re-import with -KeyStorageFlags MachineKeySet|PersistKeySet|Exportable."
}
Write-Info "  Cert found: $($cert.Subject)"

# gMSA check (best-effort; ActiveDirectory module may be absent). Skipped for LocalSystem,
# whose network identity is the computer account - there is no gMSA to verify.
if ($isLocalSystem) {
    Write-Info "  Service identity: LocalSystem - network identity is the computer account $ComputerAccount."
} elseif (-not $SkipGmsaCheck) {
    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        # Test-ADServiceAccount takes the short SAM name (without domain, without $).
        $sam = $ServiceAccount
        if ($sam -like '*\*') { $sam = $sam.Split('\')[-1] }
        $sam = $sam.TrimEnd('$')
        if (Test-ADServiceAccount -Identity $sam) {
            Write-Info "  gMSA '$sam' is installed on this host."
        } else {
            throw "Test-ADServiceAccount returned false for '$sam'. Run Install-ADServiceAccount -Identity $sam as Domain Admin."
        }
    } catch {
        Write-Warn "  gMSA check skipped: $($_.Exception.Message)"
        Write-Warn "  Install the RSAT-AD-PowerShell feature, or re-run with -SkipGmsaCheck once verified manually."
    }
}

# DB reachability - dispatch per provider
if (-not $SkipSqlConnectivityCheck) {
    if ($DbProvider -eq 'sqlserver') {
        try {
            Test-SqlReachable `
                -Server $SqlServer `
                -Database $SqlDatabase `
                -CertificateHostName $SqlCertificateHostName
            Write-Info "  SQL reachable: $SqlServer/$SqlDatabase"
            if ($isLocalSystem) {
                # The probe used the INSTALLING ADMIN's identity, not the service identity. A
                # green check here does NOT prove the computer account can log in. Spell out the
                # exact login the running service will use so a 503 on /healthz/ready isn't a surprise.
                Write-Warn "  NOTE: reachability was tested with your admin identity. At runtime the"
                Write-Warn "  service connects as the computer account $ComputerAccount."
                Write-Warn "  Ensure that login exists with db_owner on [$SqlDatabase]:"
                Write-Warn "    CREATE LOGIN [$ComputerAccount] FROM WINDOWS;"
                Write-Warn "    USE [$SqlDatabase]; CREATE USER [$ComputerAccount] FOR LOGIN [$ComputerAccount];"
                Write-Warn "    ALTER ROLE db_owner ADD MEMBER [$ComputerAccount];"
            }
            # RCSI is best applied at install time, before the service starts and holds
            # connections - the brief exclusive lock is uncontended then. Failures degrade
            # to a warning so a missing ALTER DATABASE permission doesn't block install.
            try {
                Enable-SqlReadCommittedSnapshot `
                    -Server $SqlServer `
                    -Database $SqlDatabase `
                    -CertificateHostName $SqlCertificateHostName
            } catch {
                Write-Warn "  RCSI setup failed: $($_.Exception.Message)"
                Write-Warn "  NodePilot will run without snapshot isolation. Long-running"
                Write-Warn "  reads (stats refresh, retention) will block concurrent writes."
                Write-Warn "  Have a DBA run, in SSMS:"
                Write-Warn "    ALTER DATABASE [$SqlDatabase] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;"
            }
        } catch {
            Write-Warn "  SQL reachability FAILED: $($_.Exception.Message)"
            Write-Host ""
            Write-Host "  The installer could not open a connection to the target DB using the current" -ForegroundColor Yellow
            Write-Host "  admin's Windows identity. Have the DBA run, on the SQL Server:" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "    CREATE LOGIN [$SqlPrincipal] FROM WINDOWS;" -ForegroundColor Gray
            Write-Host "    CREATE DATABASE [$SqlDatabase];" -ForegroundColor Gray
            Write-Host "    USE [$SqlDatabase];" -ForegroundColor Gray
            Write-Host "    CREATE USER [$SqlPrincipal] FOR LOGIN [$SqlPrincipal];" -ForegroundColor Gray
            Write-Host "    ALTER ROLE db_owner ADD MEMBER [$SqlPrincipal];" -ForegroundColor Gray
            Write-Host ""
            Write-Host "  After the DB is ready, re-run the installer (optionally with -SkipSqlConnectivityCheck)." -ForegroundColor Yellow
            throw "Aborted: SQL pre-flight failed."
        }
    } else {
        try {
            Test-PostgresReachable -HostName $PostgresHost -Port $PostgresPort
            Write-Info "  Postgres TCP reachable: $PostgresHost`:$PostgresPort"
        } catch {
            Write-Warn "  Postgres reachability FAILED: $($_.Exception.Message)"
            Write-Host ""
            Write-Host "  Cannot reach $PostgresHost`:$PostgresPort from this host. Verify DNS, firewall," -ForegroundColor Yellow
            Write-Host "  and that Postgres is listening on the external interface. Role setup on the DB server:" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "    CREATE ROLE $PostgresUser WITH LOGIN PASSWORD '<same-as--PostgresPassword>';" -ForegroundColor Gray
            Write-Host "    CREATE DATABASE $PostgresDatabase OWNER $PostgresUser;" -ForegroundColor Gray
            Write-Host ""
            Write-Host "  After Postgres is reachable, re-run the installer (optionally with -SkipSqlConnectivityCheck)." -ForegroundColor Yellow
            throw "Aborted: Postgres pre-flight failed."
        }
    }
}

# ---------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------

$artifactStage = Expand-NodePilotArtifactToStaging -ArtifactPath $ArtifactPath
Write-Info "  Artifact extracted and verified in restricted staging: $artifactStage"

$previousService = Get-ServiceRollbackSnapshot -Name $ServiceName
$previousAclIdentity = if ($previousService -and
    $previousService.StartName.Trim().ToLowerInvariant() -notin @('localsystem', '.\localsystem', 'system', 'nt authority\system')) {
    $previousService.StartName
} else {
    'NT AUTHORITY\SYSTEM'
}
$previousSettingsPath = Join-Path $InstallPath 'appsettings.Production.json'
if (Test-Path -LiteralPath $previousSettingsPath -PathType Leaf) {
    Set-NodePilotRestrictedFileAcl `
        -Path $previousSettingsPath `
        -ServiceAccount $previousAclIdentity `
        -SkipServiceRule:($previousAclIdentity -eq 'NT AUTHORITY\SYSTEM')
    $previousSettingsBytes = [IO.File]::ReadAllBytes($previousSettingsPath)
}

if ((Test-Path -LiteralPath $InstallPath -PathType Container) -and
    @(Get-ChildItem -LiteralPath $InstallPath -Force).Count -gt 0) {
    $installRollbackDir = "$InstallPath.rollback.$(Get-Date -Format 'yyyyMMdd-HHmmssfff')-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $installRollbackDir -ErrorAction Stop | Out-Null
    Get-ChildItem -LiteralPath $InstallPath -Force |
        Where-Object { $_.Name -ne 'appsettings.Production.json' } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $installRollbackDir -Recurse -Force -ErrorAction Stop
        }
    Write-Info "  Existing binary-only install backed up to $installRollbackDir"
}

# Reinstalling Postgres may replace the trust anchor used by the currently installed service.
# Preserve both bytes and the exact ACL before any mutation so a failed health check cannot
# restore old binaries/configuration with a new, incompatible CA.
if ($DbProvider -eq 'postgres') {
    $postgresRootCertificateTarget = Join-Path $DataPath 'postgres-root-ca.pem'
    if (Test-Path -LiteralPath $postgresRootCertificateTarget) {
        if (-not (Test-Path -LiteralPath $postgresRootCertificateTarget -PathType Leaf)) {
            throw "PostgreSQL root certificate target is not a file: $postgresRootCertificateTarget"
        }
        $previousPostgresRootCertificateBytes = [IO.File]::ReadAllBytes($postgresRootCertificateTarget)
        $previousPostgresRootCertificateAcl = Get-Acl -LiteralPath $postgresRootCertificateTarget
        $postgresRootCertificatePreviouslyExisted = $true
    }
}

try {
$installMutationStarted = $true
Remove-ExistingService -Name $ServiceName

Write-Step "Preparing directories"
if (Test-Path $InstallPath) {
    # Empty install path but do NOT touch DataPath.
    Get-ChildItem $InstallPath -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}
New-Item -ItemType Directory -Path $DataPath -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataPath 'logs') -Force | Out-Null
Set-DirectoryAclForService -Path $DataPath -ServiceAccount $AclIdentity -SkipServiceRule:$isLocalSystem

$installedPostgresRootCertificate = $null
if ($DbProvider -eq 'postgres') {
    $installedPostgresRootCertificate = $postgresRootCertificateTarget
    $newPostgresRootCertificateBytes = [IO.File]::ReadAllBytes($PostgresRootCertificate)
    try {
        $postgresRootCertificateTouched = $true
        Write-NodePilotRestrictedFile `
            -Path $installedPostgresRootCertificate `
            -Content $newPostgresRootCertificateBytes `
            -ServiceAccount $AclIdentity `
            -SkipServiceRule:$isLocalSystem
    }
    finally {
        [Array]::Clear(
            $newPostgresRootCertificateBytes,
            0,
            $newPostgresRootCertificateBytes.Length)
    }
}

Write-Step "Extracting artifact"
Get-ChildItem -LiteralPath $artifactStage -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallPath -Recurse -Force
}
Assert-NodePilotExtractedFiles -RootPath $InstallPath

$ApiExe = Join-Path $InstallPath 'NodePilot.Api.exe'
if (-not (Test-Path $ApiExe)) { throw "Artifact did not contain NodePilot.Api.exe at $ApiExe" }
$SpaIndex = Join-Path (Join-Path $InstallPath 'wwwroot') 'index.html'
if (-not (Test-Path $SpaIndex)) { throw "Artifact did not contain wwwroot\index.html. SPA fallback will 404." }
$TemplateArtifact = Join-Path $InstallPath 'appsettings.Production.json.template'
$TemplateScript   = Join-Path $PSScriptRoot 'templates\appsettings.Production.json.template'
$Template = if (Test-Path $TemplateArtifact) { $TemplateArtifact } else { $TemplateScript }
if (-not (Test-Path $Template)) { throw "No appsettings.Production.json.template found (neither in artifact nor deploy/templates)." }

Write-Step "Generating appsettings.Production.json"
$tpl = Get-Content $Template -Raw
$dataEscaped = $DataPath -replace '\\', '\\'

# Build provider-specific connection strings. The template carries both DefaultConnection
# (SQL Server) and Postgres keys; the unused one is rendered empty so Program.cs's GetConnectionString
# returns null for it - harmless, since Program.cs only reads the key for the active provider.
$sqlServerConnStr = ''
$postgresServiceConnStr = ''
if ($DbProvider -eq 'sqlserver') {
    # MARS (MultipleActiveResultSets) is intentionally omitted. EF Core 10's BatchExecutor
    # combined with MARS can retry a batch after a transient network blip, re-sending INSERTs
    # that already committed on the first attempt — resulting in PRIMARY KEY violations on
    # StepExecutions when multiple parallel steps flush their SaveChanges at the same time.
    # EF Core works correctly without MARS; the feature is only needed for legacy ADO.NET
    # patterns with multiple open DataReaders on one connection simultaneously.
    #
    # Pool sizing (Min=40 / Max=800) matches the Postgres branch and gives MaxConcurrentSteps=600
    # in appsettings.json enough headroom — without it SqlClient defaults to Max=100 and
    # parallel-step bursts queue at the connection level instead of the engine's step gate.
    $sqlBuilder = New-Object System.Data.Common.DbConnectionStringBuilder
    $sqlBuilder['Server'] = $SqlServer
    $sqlBuilder['Database'] = $SqlDatabase
    $sqlBuilder['Trusted_Connection'] = $true
    $sqlBuilder['TrustServerCertificate'] = $false
    $sqlBuilder['Encrypt'] = 'Strict'
    $sqlBuilder['HostNameInCertificate'] = $SqlCertificateHostName
    $sqlBuilder['Application Name'] = 'NodePilot'
    $sqlBuilder['Min Pool Size'] = 40
    $sqlBuilder['Max Pool Size'] = 800
    $sqlServerConnStr = $sqlBuilder.ConnectionString
} else {
    # Convert the SecureString at the last possible moment. The resulting connection string is
    # never rendered into JSON; it is placed in the ACL-restricted service environment below.
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($PostgresPassword)
    try {
        $pgPwPlain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    # Pool sizing matches the dev appsettings.json default and gives MaxConcurrentSteps=600
    # enough connection headroom. Connection Idle Lifetime=60 keeps the pool from holding
    # connections open longer than necessary on a slow workload.
    $postgresBuilder = New-Object System.Data.Common.DbConnectionStringBuilder
    $postgresBuilder['Host'] = $PostgresHost
    $postgresBuilder['Port'] = $PostgresPort
    $postgresBuilder['Database'] = $PostgresDatabase
    $postgresBuilder['Username'] = $PostgresUser
    $postgresBuilder['Password'] = $pgPwPlain
    $postgresBuilder['SSL Mode'] = 'VerifyFull'
    $postgresBuilder['Root Certificate'] = $installedPostgresRootCertificate
    $postgresBuilder['Check Certificate Revocation'] = $true
    $postgresBuilder['Maximum Pool Size'] = 800
    $postgresBuilder['Minimum Pool Size'] = 40
    $postgresBuilder['Connection Idle Lifetime'] = 60
    $postgresBuilder['Application Name'] = 'NodePilot'
    $postgresServiceConnStr = $postgresBuilder.ConnectionString
}

# JSON-escape helper: feed the value through ConvertTo-Json (which handles \, ", control chars,
# and unicode correctly) and strip the surrounding quotes so the result drops into a JSON-string
# literal in the template. Empty input stays empty.
function ConvertTo-JsonInnerLocal {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '' }
    $json = $Value | ConvertTo-Json -Compress
    return $json.Substring(1, $json.Length - 2)
}

# Substitute placeholders with literal String.Replace (NOT -replace, which is regex-based and
# would interpret $1 in API keys as backreferences and Regex.Escape would clobber dots/spaces).
$rendered = $tpl
$rendered = $rendered.Replace('{{DB_PROVIDER}}',                $DbProvider)
$rendered = $rendered.Replace('{{SQLSERVER_CONNECTION_STRING}}',(ConvertTo-JsonInnerLocal $sqlServerConnStr))
$rendered = $rendered.Replace('{{POSTGRES_CONNECTION_STRING}}', '')
$rendered = $rendered.Replace('{{JWT_ISSUER}}',                 (ConvertTo-JsonInnerLocal $JwtIssuer))
$rendered = $rendered.Replace('{{JWT_AUDIENCE}}',               (ConvertTo-JsonInnerLocal $JwtAudience))
$rendered = $rendered.Replace('{{CERT_THUMBPRINT}}',            $NormalizedThumbprint)
$rendered = $rendered.Replace('{{HTTPS_PORT}}',                 $HttpsPort.ToString())
$rendered = $rendered.Replace('{{HTTP_PORT}}',                  $HttpPort.ToString())
$rendered = $rendered.Replace('{{BIND_HTTP_JSON}}',             $BindHttpJson)
$rendered = $rendered.Replace('{{DATA_PATH_ESCAPED}}',          $dataEscaped)
$rendered = $rendered.Replace('{{EXTERNAL_TRIGGER_API_KEY}}',   (ConvertTo-JsonInnerLocal $ExternalTriggerApiKey))
$rendered = $rendered.Replace('{{ALLOWED_HOSTS}}',              (ConvertTo-JsonInnerLocal $AllowedHosts))
$rendered = $rendered.Replace('{{KNOWN_PROXIES_JSON}}',         (ConvertTo-Json -InputObject @($normalizedKnownProxyIps) -Compress))

$settingsPath = Join-Path $InstallPath 'appsettings.Production.json'
# Validate before a secret-bearing file exists on disk. Write-NodePilotRestrictedFile passes
# the final DACL into CreateFile, so there is no inherited-ACL placeholder handle to race.
try { [void]($rendered | ConvertFrom-Json) }
catch { throw "Generated appsettings.Production.json is not valid JSON: $($_.Exception.Message)" }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$settingsContentBytes = $utf8NoBom.GetBytes($rendered)
try {
    Write-NodePilotRestrictedFile `
        -Path $settingsPath `
        -Content $settingsContentBytes `
        -ServiceAccount $AclIdentity `
        -SkipServiceRule:$isLocalSystem
}
finally {
    [Array]::Clear($settingsContentBytes, 0, $settingsContentBytes.Length)
}

Write-Step "Applying ACLs"
Set-FileAclForService      -Path $settingsPath  -ServiceAccount $AclIdentity -SkipServiceRule:$isLocalSystem
if ($isLocalSystem) {
    # MachineKeys private-key files grant SYSTEM read access on import by default, so LocalSystem
    # already has what it needs - no explicit icacls grant required.
    Write-Info "  Cert private-key: LocalSystem (SYSTEM) already has read access - skipping grant."
} else {
    Grant-CertPrivateKeyAccess -Thumbprint $NormalizedThumbprint -ServiceAccount $AclIdentity
}

Write-Step "Firewall rules"
$fwRule = "NodePilot $ServiceName HTTPS"
Get-NetFirewallRule -DisplayName $fwRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $fwRule -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Action Allow -Profile Domain | Out-Null
if ($BindHttp) {
    $fwRuleHttp = "NodePilot $ServiceName HTTP-Redirect"
    Get-NetFirewallRule -DisplayName $fwRuleHttp -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $fwRuleHttp -Direction Inbound -Protocol TCP -LocalPort $HttpPort -Action Allow -Profile Domain | Out-Null
}

Write-Step "Registering Windows Service"
# Win32_Service.Create over CIM is the native PS way to register a service and - unlike
# New-Service, whose -Credential parameter rejects empty SecureStrings - it accepts a blank
# StartPassword, which is exactly what a gMSA needs (AD manages the password). This avoids
# the PS-5.1 sc.exe-quoting trap where `password= ''` got stripped from the argv.
$pathName = '"{0}" --contentRoot "{1}"' -f $ApiExe, $InstallPath
$cimArgs = @{
    Name            = $ServiceName
    DisplayName     = $ServiceDisplayName
    PathName        = $pathName
    ServiceType     = [byte]16     # SERVICE_WIN32_OWN_PROCESS (UInt8)
    StartMode       = 'Automatic'
    ErrorControl    = [byte]1      # Normal: log error, continue startup (UInt8)
    StartName       = $ScmStartName
    StartPassword   = $(if ($isLocalSystem) { $null } else { '' })
    DesktopInteract = $false
}
$cimResult = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments $cimArgs
if ($cimResult.ReturnValue -ne 0) {
    $msg = switch ($cimResult.ReturnValue) {
        2  { 'Access denied (run as Administrator)' }
        4  { 'Service marked for deletion - wait a moment and retry' }
        5  { 'Database locked' }
        22 { 'Invalid service account or password (gMSA not installed on this host?)' }
        23 { 'Service already exists' }
        24 { 'Service already paused' }
        default { "Win32_Service.Create returned $($cimResult.ReturnValue)" }
    }
    throw "Service registration failed: $msg"
}
Write-Info "  Service '$ServiceName' registered (running as $AccountLabel)."

# CRITICAL for gMSAs: mark the service as a Managed Account. This sets
# SERVICE_CONFIG_MANAGED_ACCOUNT in the SCM database so Windows fetches the gMSA password
# from the DC at start time instead of trying a regular logon with an empty password (which
# fails with "Cannot start service" - generic SCM error code 1057 / "the account name is
# invalid or does not exist"). Win32_Service.Create does NOT expose this flag.
if (-not $isLocalSystem -and $ScmStartName.TrimEnd().EndsWith('$')) {
    & sc.exe managedaccount $ServiceName true | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # Rollback: a registered-but-unconfigured service in this state would just keep
        # failing on every start attempt.
        & sc.exe delete $ServiceName | Out-Null
        throw "sc.exe managedaccount failed (exit $LASTEXITCODE). Service rolled back."
    }
    Write-Info "  Marked service as Managed Account (SCM will retrieve gMSA password from DC)."
}

# delayed-auto and recovery-actions aren't exposed via Win32_Service, so we still use sc.exe
# for those - but those calls don't involve empty-string args, so the plain & operator works.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warn "  sc.exe config (delayed-auto) returned $LASTEXITCODE" }

& sc.exe description $ServiceName 'NodePilot workflow orchestrator. See https://localhost/swagger for API docs.' | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warn "  sc.exe description returned $LASTEXITCODE" }

& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warn "  sc.exe failure returned $LASTEXITCODE" }

# Environment variables: Windows services read MULTI_SZ from HKLM\...\<svc>\Environment.
$envRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envValues = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    'DOTNET_PRINT_TELEMETRY_MESSAGE=false'
)
if ($DbProvider -eq 'postgres') {
    # The active Postgres connection string is intentionally absent from JSON. Restrict the
    # service registry key before placing the process-scoped secret in its Environment value.
    Set-ServiceRegistryAclForSecrets -Path $envRegPath
    $envValues += "ConnectionStrings__Postgres=$postgresServiceConnStr"
}
New-ItemProperty -Path $envRegPath -Name 'Environment' -PropertyType MultiString -Value $envValues -Force | Out-Null
if ($DbProvider -eq 'postgres') {
    $pgPwPlain = $null
    $postgresServiceConnStr = $null
}

if ($isLocalSystem) {
    Write-Info "Skipping 'Log on as a service' grant - LocalSystem holds SeServiceLogonRight inherently."
} else {
    Write-Step "Granting 'Log on as a service' to $AccountLabel"
    # Win32_Service.Create does NOT auto-grant SeServiceLogonRight (sc.exe and New-Service do).
    # Without this the SCM rejects the start with "Cannot start service" - generic error, real
    # cause only visible in Event Viewer as event 7000 / "Logon failure: the user has not been
    # granted the requested logon type at this computer".
    Grant-LogOnAsServiceRight -Account $ServiceAccount
}

function Show-ServiceStartDiagnostics {
    <#
      Prints recent Application/System errors and the tail of the NodePilot log file when
      Start-Service or the health probe fails. Saves the user from running diagnostic
      one-liners manually.
    #>
    param([string]$ServiceName, [string]$DataPath)
    Write-Host ""
    Write-Warn "  Service-Start-Diagnose:"
    Write-Host ""
    try {
        $appEvents = Get-WinEvent -LogName Application -MaxEvents 25 -ErrorAction Stop |
            Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-3) -and
                           $_.LevelDisplayName -in @('Error','Critical') }
        if ($appEvents) {
            Write-Host "  --- Application-Log (letzte 3 Min, Errors) ---" -ForegroundColor Yellow
            foreach ($e in $appEvents | Select-Object -First 6) {
                Write-Host ("  [{0:HH:mm:ss}] {1} (id {2})" -f $e.TimeCreated, $e.ProviderName, $e.Id) -ForegroundColor Yellow
                $msg = $e.Message
                if ($msg.Length -gt 600) { $msg = $msg.Substring(0, 600) + ' [..gekürzt]' }
                $msg -split "`n" | Select-Object -First 12 | ForEach-Object { Write-Host "    $_" }
                Write-Host ""
            }
        }
    } catch { Write-Host "  (Application-Log nicht lesbar: $($_.Exception.Message))" }

    try {
        $sysEvents = Get-WinEvent -LogName System -MaxEvents 15 -ErrorAction Stop |
            Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-3) -and
                           $_.ProviderName -eq 'Service Control Manager' -and
                           $_.LevelDisplayName -in @('Error','Critical','Warning') }
        if ($sysEvents) {
            Write-Host "  --- System-Log / SCM (letzte 3 Min) ---" -ForegroundColor Yellow
            foreach ($e in $sysEvents | Select-Object -First 4) {
                Write-Host ("  [{0:HH:mm:ss}] SCM event {1}: {2}" -f $e.TimeCreated, $e.Id,
                    ($e.Message -split "`n" | Select-Object -First 1)) -ForegroundColor Yellow
            }
            Write-Host ""
        }
    } catch { }

    $logDir = Join-Path $DataPath 'logs'
    if (Test-Path $logDir) {
        $latestLog = Get-ChildItem (Join-Path $logDir 'nodepilot-*.log') -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latestLog) {
            Write-Host "  --- $($latestLog.Name) (Tail) ---" -ForegroundColor Yellow
            Get-Content $latestLog.FullName -Tail 25 | ForEach-Object { Write-Host "  $_" }
        } else {
            Write-Host "  (Keine nodepilot-*.log Datei in $logDir - der Service crashte vor dem Serilog-Init.)" -ForegroundColor Yellow
        }
    }
    Write-Host ""
}

Write-Step "Starting service"
try {
    Start-Service -Name $ServiceName -ErrorAction Stop
} catch {
    Show-ServiceStartDiagnostics -ServiceName $ServiceName -DataPath $DataPath
    throw "Start-Service failed: $($_.Exception.Message)"
}
# Probe-Timeout 180s instead of 60s: if a previous failed install left the service in
# a restart-recovery loop (failure-action restart/5000ms), each crash + SCM-recovery cycle
# burns ~30s; the new config can take 2-3 cycles to take effect. 180s covers that without
# spamming "false-positive failed" reports for installs that ARE actually succeeding.
$probeUrl = "https://localhost:$HttpsPort/healthz/ready"
Write-Info "  Probing $probeUrl (up to 180s)…"
if (-not (Invoke-HealthProbe -Url $probeUrl -TimeoutSeconds 180)) {
    Show-ServiceStartDiagnostics -ServiceName $ServiceName -DataPath $DataPath
    throw "Service did not report /healthz/ready within 180s. See diagnostics above."
}
Write-Ok "  /healthz/ready → 200 OK"

# Admin bootstrap token surfaces for first login.
$tokenPath = Join-Path $DataPath 'admin-setup.token'
$tokenContent = $null
if (Test-Path $tokenPath) {
    $tokenContent = (Get-Content $tokenPath -Raw).Trim()
}

# Persist an install report (no secrets) alongside the data dir for post-mortem.
$reportPath = Join-Path $DataPath 'install-report.txt'
$dbLine = if ($DbProvider -eq 'sqlserver') {
    "Database       : SQL Server $SqlServer / $SqlDatabase"
} else {
    "Database       : Postgres $($PostgresUser)@$($PostgresHost):$PostgresPort / $PostgresDatabase"
}
@"
NodePilot installation report
Generated : $((Get-Date).ToString('o'))
Machine   : $env:COMPUTERNAME
ServiceName    : $ServiceName
ServiceAccount : $AccountLabel
InstallPath    : $InstallPath
DataPath       : $DataPath
ArtifactPath   : $ArtifactPath
$dbLine
HttpsPort      : $HttpsPort
HttpPort       : $(if ($BindHttp) { $HttpPort } else { 'disabled' })
CertThumbprint : $NormalizedThumbprint
PublicHostname : $PublicHostname
JwtIssuer      : $JwtIssuer
JwtAudience    : $JwtAudience
AllowedHosts   : $AllowedHosts
"@ | Out-File -FilePath $reportPath -Encoding ascii -Force

Write-Host ""
Write-Ok "Installation complete."
Write-Host ""
Write-Host "  URL         : https://$PublicHostname/" -ForegroundColor Green
Write-Host "  API docs    : https://$PublicHostname/swagger (disabled when Swagger:DisableInNonDevelopment=true)" -ForegroundColor Gray
Write-Host "  Health      : https://$PublicHostname/healthz/ready"   -ForegroundColor Gray
Write-Host "  Logs        : $DataPath\logs"                          -ForegroundColor Gray
Write-Host "  Install log : $reportPath"                             -ForegroundColor Gray
Write-Host ""
Write-Host "  External-Trigger API key (store it now; it won't be shown again):" -ForegroundColor Yellow
Write-Host "    $ExternalTriggerApiKey" -ForegroundColor Yellow
Write-Host ""
if ($tokenContent) {
    Write-Host "  FIRST-LOGIN ADMIN BOOTSTRAP" -ForegroundColor Yellow
    Write-Host "  ---------------------------"
    Write-Host "  Go to:   https://$PublicHostname/"
    Write-Host "  Present the following value as the 'X-Setup-Token' header on your first POST /api/auth/login"
    Write-Host "  (the web UI prompts for it on the setup screen). After successful admin creation the"
    Write-Host "  token file is deleted and this bootstrap window closes."
    Write-Host ""
    Write-Host "    $tokenContent" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Token path: $tokenPath"
} else {
    Write-Info "  No admin-setup.token on disk (users already exist or bootstrap ran silently)."
}

if ($installRollbackDir -and (Test-Path -LiteralPath $installRollbackDir)) {
    Remove-Item -LiteralPath $installRollbackDir -Recurse -Force -ErrorAction SilentlyContinue
    $installRollbackDir = $null
}
}
catch {
    $installError = $_
    Write-Host "[install] FAILED: $($installError.Exception.Message)" -ForegroundColor Red
    if ($installMutationStarted) {
        try {
            Write-Host "[install] Restoring the previous installation..." -ForegroundColor Yellow
            Remove-ExistingService -Name $ServiceName
            if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
                New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
            } else {
                Get-ChildItem -LiteralPath $InstallPath -Force |
                    Remove-Item -Recurse -Force -ErrorAction Stop
            }
            if ($installRollbackDir -and (Test-Path -LiteralPath $installRollbackDir)) {
                Get-ChildItem -LiteralPath $installRollbackDir -Force | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination $InstallPath -Recurse -Force -ErrorAction Stop
                }
            }
            if ($previousSettingsBytes) {
                Write-NodePilotRestrictedFile `
                    -Path $previousSettingsPath `
                    -Content $previousSettingsBytes `
                    -ServiceAccount $previousAclIdentity `
                    -SkipServiceRule:($previousAclIdentity -eq 'NT AUTHORITY\SYSTEM')
            }
            if ($postgresRootCertificateTouched) {
                if ($postgresRootCertificatePreviouslyExisted) {
                    Write-NodePilotRestrictedFile `
                        -Path $postgresRootCertificateTarget `
                        -Content $previousPostgresRootCertificateBytes `
                        -ServiceAccount $previousAclIdentity `
                        -SkipServiceRule:($previousAclIdentity -eq 'NT AUTHORITY\SYSTEM')
                    Set-Acl `
                        -LiteralPath $postgresRootCertificateTarget `
                        -AclObject $previousPostgresRootCertificateAcl
                }
                elseif (Test-Path -LiteralPath $postgresRootCertificateTarget) {
                    Remove-Item -LiteralPath $postgresRootCertificateTarget -Force -ErrorAction Stop
                }
            }
            if ($previousService) {
                Restore-ServiceRollbackSnapshot -Snapshot $previousService
            }
            Write-Host "[install] Previous installation restored." -ForegroundColor Yellow
        }
        catch {
            Write-Host "[install] ROLLBACK ALSO FAILED: $($_.Exception.Message). Binary backup: $installRollbackDir" -ForegroundColor Red
        }
    }
    throw $installError
}
}
finally {
    if ($previousSettingsBytes) {
        [Array]::Clear($previousSettingsBytes, 0, $previousSettingsBytes.Length)
    }
    if ($previousPostgresRootCertificateBytes) {
        [Array]::Clear(
            $previousPostgresRootCertificateBytes,
            0,
            $previousPostgresRootCertificateBytes.Length)
    }
    if ($artifactStage -and (Test-Path -LiteralPath $artifactStage)) {
        Remove-Item -LiteralPath $artifactStage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($artifactLock) { $artifactLock.Dispose() }
}
