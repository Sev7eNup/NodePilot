#requires -Version 5.1

<#
.SYNOPSIS
    Creates the SQL Server login and database NodePilot needs, if the installing admin may.
.DESCRIPTION
    Opt-in helper behind the setup wizard's readiness page. Idempotent and existence-guarded.

    The permission gate runs before anything is mutated. Without sysadmin or CREATE ANY DATABASE
    the script changes nothing and returns the DDL for a DBA to run.

    Every server call returns an outcome rather than throwing, so a failed statement is reported
    with its own message instead of terminating the setup adapter. Same shape as
    Provision-NodePilotPostgres.ps1.

    PostgreSQL is out of scope. The installer ships no Npgsql and psql.exe exists only in the
    desktop payload, so the wizard shows the CREATE ROLE snippet from
    Get-NodePilotPostgresRemediationScript instead of a button that cannot work.
.PARAMETER Server
    SQL Server instance, as passed to Install-NodePilot.ps1 -SqlServer.
.PARAMETER Database
    Database to create. Must be a plain identifier.
.PARAMETER Principal
    Windows principal to create the login for: the gMSA, or the computer account for LocalSystem.
.PARAMETER CertificateHostName
    Name to validate the server certificate against. Derived from -Server when omitted.
.PARAMETER ConnectionFactory
    Opens a connection for a connection string. The tests replace it with a fake, which is the
    only way to reach the branches below without a SQL Server.
.OUTPUTS
    An object with Status ('Pass' | 'Skipped' | 'Fail'), Detail and Remediation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Principal,
    [string]$CertificateHostName,
    [scriptblock]$ConnectionFactory = {
        param($ConnectionString)
        New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    }
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

# DDL cannot be parameterised and these names come from wizard text boxes. Two layers guard the
# interpolation: an allowlist check on the name, and doubling of ']' inside the identifier.
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

# Errors are values here, not exceptions. A SqlException in the middle of the mutating block used
# to terminate the script, which left the adapter with no outcome object to report.
function Open-SqlConnection {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection = $null
    try {
        $connection = & $ConnectionFactory $ConnectionString
        $connection.Open()
        return [pscustomobject]@{ Succeeded = $true; Connection = $connection; Error = '' }
    }
    catch {
        if ($connection) { $connection.Dispose() }
        return [pscustomobject]@{ Succeeded = $false; Connection = $null; Error = $_.Exception.Message }
    }
}

function Invoke-Scalar {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Sql)
    try {
        $command = $Connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = 60
        return [pscustomobject]@{ Succeeded = $true; Value = $command.ExecuteScalar(); Error = '' }
    }
    catch {
        return [pscustomobject]@{ Succeeded = $false; Value = $null; Error = $_.Exception.Message }
    }
}

function Invoke-NonQuery {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Sql)
    try {
        $command = $Connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = 60
        [void]$command.ExecuteNonQuery()
        return [pscustomobject]@{ Succeeded = $true; Error = '' }
    }
    catch {
        return [pscustomobject]@{ Succeeded = $false; Error = $_.Exception.Message }
    }
}

# SQL answers NULL for a login or role name it cannot resolve, and [bool][System.DBNull]::Value is
# $true in PowerShell - which would read "no permission" as "may create databases" and open the
# gate. NULL is not a yes.
function Test-SqlTruth {
    param($Value)
    if ($null -eq $Value -or $Value -is [System.DBNull]) { return $false }
    return [bool]$Value
}

# Uses the same connection shape as the runtime and the pre-flight, so success here cannot come
# from a TLS path the service would later reject.
$masterConnectionString = Resolve-NodePilotSqlProbeConnectionString `
    -Server $Server -Database 'master' -CertificateHostName $CertificateHostName

$created = New-Object System.Collections.Generic.List[string]

$master = Open-SqlConnection -ConnectionString $masterConnectionString
if (-not $master.Succeeded) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Cannot connect to $Server as the current admin: $($master.Error)")
}

try {
    # --- permission gate: everything below this point mutates ---
    $sysadmin = Invoke-Scalar -Connection $master.Connection -Sql "SELECT IS_SRVROLEMEMBER('sysadmin')"
    if (-not $sysadmin.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "Could not read the server role membership on $Server`: $($sysadmin.Error)")
    }
    $createAny = Invoke-Scalar -Connection $master.Connection `
        -Sql "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')"
    if (-not $createAny.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "Could not read the CREATE ANY DATABASE permission on $Server`: $($createAny.Error)")
    }
    if (-not (Test-SqlTruth $sysadmin.Value) -and -not (Test-SqlTruth $createAny.Value)) {
        return New-Outcome -Status 'Skipped' -Remediation $remediation -Detail (
            "The installing account has neither sysadmin nor CREATE ANY DATABASE on $Server. " +
            'Nothing was changed - hand the statements below to a DBA.')
    }

    $loginLookup = Invoke-Scalar -Connection $master.Connection -Sql (
        "SELECT COUNT(*) FROM sys.server_principals WHERE name = N'$($Principal.Replace("'", "''"))'")
    if (-not $loginLookup.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "Could not read sys.server_principals on $Server`: $($loginLookup.Error)")
    }
    if (-not (Test-SqlTruth $loginLookup.Value)) {
        $createLogin = Invoke-NonQuery -Connection $master.Connection `
            -Sql "CREATE LOGIN [$escapedPrincipal] FROM WINDOWS"
        if (-not $createLogin.Succeeded) {
            return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
                "CREATE LOGIN $Principal failed: $($createLogin.Error)")
        }
        $created.Add('login')
    }

    $databaseLookup = Invoke-Scalar -Connection $master.Connection -Sql (
        "SELECT CASE WHEN DB_ID(N'$($Database.Replace("'", "''"))') IS NULL THEN 0 ELSE 1 END")
    if (-not $databaseLookup.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "Could not check whether [$Database] exists on $Server`: $($databaseLookup.Error)")
    }
    if (-not (Test-SqlTruth $databaseLookup.Value)) {
        $createDatabase = Invoke-NonQuery -Connection $master.Connection `
            -Sql "CREATE DATABASE [$escapedDatabase]"
        if (-not $createDatabase.Succeeded) {
            return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
                "CREATE DATABASE $Database failed: $($createDatabase.Error)")
        }
        $created.Add('database')
    }
}
finally { $master.Connection.Dispose() }

# The user and role membership live in the application database, so a second connection is needed.
$databaseConnectionString = Resolve-NodePilotSqlProbeConnectionString `
    -Server $Server -Database $Database -CertificateHostName $CertificateHostName

$soFar = if ($created.Count -eq 0) { 'Login and database were already in place' }
         else { "Created $($created -join ', ') on $Server" }

# Opening [$Database] is its own step: folding it into the grant below reported a connection that
# never came up as a failed db_owner grant.
$application = Open-SqlConnection -ConnectionString $databaseConnectionString
if (-not $application.Succeeded) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "$soFar, but [$Database] could not be opened as the current admin: $($application.Error)")
}

try {
    $userLookup = Invoke-Scalar -Connection $application.Connection -Sql (
        "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'$($Principal.Replace("'", "''"))'")
    if (-not $userLookup.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "$soFar, but sys.database_principals in [$Database] could not be read: $($userLookup.Error)")
    }
    if (-not (Test-SqlTruth $userLookup.Value)) {
        $createUser = Invoke-NonQuery -Connection $application.Connection `
            -Sql "CREATE USER [$escapedPrincipal] FOR LOGIN [$escapedPrincipal]"
        if (-not $createUser.Succeeded) {
            return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
                "$soFar, but CREATE USER $Principal in [$Database] failed: $($createUser.Error)")
        }
        $created.Add('user')
    }

    # ALTER ROLE is idempotent for an existing member, so it runs unconditionally.
    $grant = Invoke-NonQuery -Connection $application.Connection `
        -Sql "ALTER ROLE db_owner ADD MEMBER [$escapedPrincipal]"
    if (-not $grant.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "$soFar, but granting db_owner on [$Database] failed: $($grant.Error)")
    }
}
finally { $application.Connection.Dispose() }

$detail = if ($created.Count -eq 0) {
    "Login and database already existed on $Server; db_owner reasserted for $Principal."
}
else {
    "Created $($created -join ', ') on $Server and granted db_owner to $Principal."
}
return New-Outcome -Status 'Pass' -Detail $detail
