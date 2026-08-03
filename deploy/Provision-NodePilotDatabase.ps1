#requires -Version 5.1

<#
.SYNOPSIS
    Creates the SQL Server login and database NodePilot needs, when the installing admin may.
.DESCRIPTION
    Opt-in helper behind the setup wizard's readiness page. Idempotent and existence-guarded.

    The permission gate runs FIRST and nothing is touched when it fails: without sysadmin or
    CREATE ANY DATABASE the script returns the DDL for a DBA to run instead of half-applying it.
    That is the whole design - degrade before mutating, not during.

    PostgreSQL is deliberately out of scope. The installer ships no Npgsql (pulling it in would
    bloat the bootstrap, which is also why Test-NodePilotPostgresReachable is a TCP probe) and
    psql.exe exists only in the desktop payload. The wizard therefore shows the CREATE ROLE
    snippet from Get-NodePilotPostgresRemediationScript rather than offering a button that
    cannot work.
.PARAMETER Server
    SQL Server instance, as passed to Install-NodePilot.ps1 -SqlServer.
.PARAMETER Database
    Database to create. Must be a plain identifier.
.PARAMETER Principal
    Windows principal to create the login for: the gMSA, or the computer account for LocalSystem.
.PARAMETER CertificateHostName
    Name to validate the server certificate against. Derived from -Server when omitted.
.OUTPUTS
    An object with Status ('Pass' | 'Skipped' | 'Fail'), Detail and Remediation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Principal,
    [string]$CertificateHostName
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDirectory 'Preflight.ps1')

if ([string]::IsNullOrWhiteSpace($CertificateHostName)) {
    $CertificateHostName = (($Server -replace '^tcp:', '') -split '[\\,]')[0]
}

$remediation = Get-NodePilotSqlRemediationScript -Principal $Principal -Database $Database

function New-Outcome {
    param([string]$Status, [string]$Detail, [string]$Remediation = '')
    [pscustomobject]@{ Status = $Status; Detail = $Detail; Remediation = $Remediation }
}

# DDL cannot be parameterised and these names come out of wizard text boxes. Two layers, not one:
# an allowlist before interpolation, and the house-style ']' doubling from
# Enable-SqlReadCommittedSnapshot. Either alone is a single point of failure.
if ($Database -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,127}$') {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Database name '$Database' is not a plain identifier. The wizard will not build DDL from it; " +
        'create the database by hand.')
}
if ($Principal -notmatch '^[A-Za-z0-9._-]+\\[A-Za-z0-9._$-]+$') {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Principal '$Principal' is not a DOMAIN\\account name. Create the login by hand.")
}
$escapedDatabase = $Database.Replace(']', ']]')
$escapedPrincipal = $Principal.Replace(']', ']]')

function Invoke-Scalar {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Sql)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 60
    return $command.ExecuteScalar()
}

function Invoke-NonQuery {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Sql)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 60
    [void]$command.ExecuteNonQuery()
}

# Same connection shape the runtime and the pre-flight use, so a success here cannot be achieved
# over a TLS path the service would later reject.
$masterConnectionString = Resolve-NodePilotSqlProbeConnectionString `
    -Server $Server -Database 'master' -CertificateHostName $CertificateHostName

$created = New-Object System.Collections.Generic.List[string]
$connection = New-Object System.Data.SqlClient.SqlConnection $masterConnectionString
try {
    try { $connection.Open() }
    catch {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "Cannot connect to $Server as the current admin: $($_.Exception.Message)")
    }

    # --- permission gate: everything below this point mutates ---
    $isSysadmin = [bool](Invoke-Scalar -Connection $connection -Sql "SELECT IS_SRVROLEMEMBER('sysadmin')")
    $mayCreateDatabase = [bool](Invoke-Scalar -Connection $connection `
        -Sql "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')")
    if (-not $isSysadmin -and -not $mayCreateDatabase) {
        return New-Outcome -Status 'Skipped' -Remediation $remediation -Detail (
            "The installing account has neither sysadmin nor CREATE ANY DATABASE on $Server. " +
            'Nothing was changed - hand the statements below to a DBA.')
    }

    $loginExists = [bool](Invoke-Scalar -Connection $connection -Sql (
        "SELECT COUNT(*) FROM sys.server_principals WHERE name = N'$($Principal.Replace("'", "''"))'"))
    if (-not $loginExists) {
        Invoke-NonQuery -Connection $connection -Sql "CREATE LOGIN [$escapedPrincipal] FROM WINDOWS"
        $created.Add('login')
    }

    $databaseExists = [bool](Invoke-Scalar -Connection $connection -Sql (
        "SELECT CASE WHEN DB_ID(N'$($Database.Replace("'", "''"))') IS NULL THEN 0 ELSE 1 END"))
    if (-not $databaseExists) {
        Invoke-NonQuery -Connection $connection -Sql "CREATE DATABASE [$escapedDatabase]"
        $created.Add('database')
    }
}
finally { $connection.Dispose() }

# The user and role membership live in the application database, so a second connection is needed.
$databaseConnectionString = Resolve-NodePilotSqlProbeConnectionString `
    -Server $Server -Database $Database -CertificateHostName $CertificateHostName
$connection = New-Object System.Data.SqlClient.SqlConnection $databaseConnectionString
try {
    $connection.Open()
    $userExists = [bool](Invoke-Scalar -Connection $connection -Sql (
        "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'$($Principal.Replace("'", "''"))'"))
    if (-not $userExists) {
        Invoke-NonQuery -Connection $connection `
            -Sql "CREATE USER [$escapedPrincipal] FOR LOGIN [$escapedPrincipal]"
        $created.Add('user')
    }
    # ALTER ROLE is idempotent for an existing member, so it runs unconditionally.
    Invoke-NonQuery -Connection $connection `
        -Sql "ALTER ROLE db_owner ADD MEMBER [$escapedPrincipal]"
}
catch {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Login and database are in place, but granting db_owner failed: $($_.Exception.Message)")
}
finally { $connection.Dispose() }

$detail = if ($created.Count -eq 0) {
    "Login and database already existed on $Server; db_owner reasserted for $Principal."
}
else {
    "Created $($created -join ', ') on $Server and granted db_owner to $Principal."
}
return New-Outcome -Status 'Pass' -Detail $detail
