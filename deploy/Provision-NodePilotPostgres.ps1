#requires -Version 5.1

<#
.SYNOPSIS
    Creates the PostgreSQL role and database NodePilot needs, if the given credentials may.
.DESCRIPTION
    Sibling of Provision-NodePilotDatabase.ps1 and follows the same rule: the permission gate runs
    before anything is mutated. Without CREATEROLE and CREATEDB (or superuser) the script changes
    nothing and returns the DDL for a DBA to run.

    Two things are left alone, because both would change a server that already works:

      * An existing role's password is never reset. Rewriting it would hide a typo in the answer
        file and lock out anything else that uses the role.
      * An existing database's owner is never changed. A database owned by somebody else is
        reported, not corrected.

    PostgreSQL has no equivalent of Trusted_Connection, where the installing admin's own Windows
    identity carries the permission, so this script needs a second set of credentials that the
    SQL Server path never asks for.

    Connects the way the runtime will: sslmode=verify-full against the same root certificate, so
    a success here cannot come from a laxer TLS path that the service could not repeat.
.PARAMETER PsqlPath
    psql.exe from the installer payload. The client ships with the setup; PostgreSQL does not have
    to be installed on the NodePilot host.
.OUTPUTS
    An object with Status ('Pass' | 'Skipped' | 'Fail'), Detail and Remediation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PsqlPath,
    [Parameter(Mandatory)][string]$HostName,
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][System.Security.SecureString]$Password,
    [Parameter(Mandatory)][string]$SuperUser,
    [Parameter(Mandatory)][System.Security.SecureString]$SuperPassword,
    [Parameter(Mandatory)][string]$RootCertificate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDirectory 'Preflight.ps1')

$remediation = Get-NodePilotPostgresRemediationScript -User $User -Database $Database

function New-Outcome {
    param([string]$Status, [string]$Detail, [string]$Remediation = '')
    [pscustomobject]@{ Status = $Status; Detail = $Detail; Remediation = $Remediation }
}

if (-not (Test-Path -LiteralPath $PsqlPath -PathType Leaf)) {
    return New-Outcome -Status 'Skipped' -Remediation $remediation -Detail (
        "No PostgreSQL client at '$PsqlPath'. This build of the setup cannot create the role and " +
        'database; run the statements below on the server instead.')
}
if (-not (Test-Path -LiteralPath $RootCertificate -PathType Leaf)) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "The PostgreSQL root certificate '$RootCertificate' does not exist. The runtime connects " +
        'with sslmode=verify-full and so does this, so there is nothing to verify the server against.')
}

# DDL cannot parameterise identifiers, and these come from wizard text boxes. Two layers, as on
# the SQL Server side: an allowlist before interpolation, and quoting after it.
foreach ($pair in @(@{ Name = 'Database'; Value = $Database }, @{ Name = 'Role'; Value = $User })) {
    if ($pair.Value -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "$($pair.Name) name '$($pair.Value)' is not a plain identifier. The wizard will not build " +
            'DDL from it; create it by hand.')
    }
}
$quotedDatabase = '"' + $Database.Replace('"', '""') + '"'
$quotedUser = '"' + $User.Replace('"', '""') + '"'

function Invoke-Psql {
    <#
      -w on every call so psql fails instead of prompting: there is no console behind a hidden
      Exec, and a prompt would hang the installation until the wizard times out. ON_ERROR_STOP
      turns a failing statement into a non-zero exit code. Same shape as
      deploy\desktop\Provision-LocalDb.ps1.

      Process plumbing and connection environment come from Preflight.ps1, so this cannot connect
      differently from the check that decided the fix was needed.
    #>
    param(
        [Parameter(Mandatory)][string]$ConnectAs,
        [Parameter(Mandatory)][string]$Secret,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Sql,
        [switch]$Tuples
    )

    $arguments = @(
        '-w'
        '-h', $HostName
        '-p', "$Port"
        '-U', $ConnectAs
        '-d', $Database
        '-v', 'ON_ERROR_STOP=1'
    )
    if ($Tuples) { $arguments += '-tA' }

    # The statement goes over stdin rather than -c: CREATE ROLE carries the new role's password,
    # and command-line arguments are readable in the process list by every user on the machine.
    return Invoke-NodePilotPsql -PsqlPath $PsqlPath -Arguments $arguments -Sql "$Sql;" `
        -Environment (Get-NodePilotPsqlEnvironment -Secret $Secret -RootCertificate $RootCertificate)
}

$superSecret = ConvertFrom-NodePilotSecureString -Value $SuperPassword
$roleSecret = ConvertFrom-NodePilotSecureString -Value $Password

# --- permission gate: everything below this point mutates ---------------------------------------
# Connects to 'postgres', which every cluster has. The target database may still have to be
# created, so it cannot authenticate this connection.
$gate = Invoke-Psql -ConnectAs $SuperUser -Secret $superSecret -Database 'postgres' -Tuples `
    -Sql "SELECT rolsuper OR (rolcreaterole AND rolcreatedb) FROM pg_roles WHERE rolname = current_user"
if (-not $gate.Succeeded) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Cannot connect to $HostName`:$Port as '$SuperUser': $($gate.Error)")
}
if ($gate.Output -ne 't') {
    return New-Outcome -Status 'Skipped' -Remediation $remediation -Detail (
        "'$SuperUser' is neither a superuser nor holds CREATEROLE and CREATEDB on $HostName. " +
        'Nothing was changed - hand the statements below to a DBA.')
}

$created = New-Object System.Collections.Generic.List[string]

$roleExists = Invoke-Psql -ConnectAs $SuperUser -Secret $superSecret -Database 'postgres' -Tuples `
    -Sql "SELECT 1 FROM pg_roles WHERE rolname = '$($User.Replace("'", "''"))'"
if (-not $roleExists.Succeeded) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Could not read pg_roles on $HostName`: $($roleExists.Error)")
}
if ($roleExists.Output -ne '1') {
    # The password is a literal because CREATE ROLE takes no parameters. It comes from the answer
    # file or a wizard field, never from the database, and quotes are doubled as for the
    # identifiers above.
    $create = Invoke-Psql -ConnectAs $SuperUser -Secret $superSecret -Database 'postgres' `
        -Sql "CREATE ROLE $quotedUser WITH LOGIN PASSWORD '$($roleSecret.Replace("'", "''"))'"
    if (-not $create.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "CREATE ROLE $User failed: $($create.Error)")
    }
    $created.Add('role')
}

$databaseExists = Invoke-Psql -ConnectAs $SuperUser -Secret $superSecret -Database 'postgres' -Tuples `
    -Sql "SELECT 1 FROM pg_database WHERE datname = '$($Database.Replace("'", "''"))'"
if (-not $databaseExists.Succeeded) {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "Could not read pg_database on $HostName`: $($databaseExists.Error)")
}
if ($databaseExists.Output -ne '1') {
    $create = Invoke-Psql -ConnectAs $SuperUser -Secret $superSecret -Database 'postgres' `
        -Sql "CREATE DATABASE $quotedDatabase OWNER $quotedUser"
    if (-not $create.Succeeded) {
        return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
            "CREATE DATABASE $Database failed: $($create.Error)")
    }
    $created.Add('database')
}

# What matters is whether the service can log in and create objects, so the check connects as the
# role itself rather than as the superuser. The readiness page asks the same question.
$verify = Invoke-Psql -ConnectAs $User -Secret $roleSecret -Database $Database -Tuples `
    -Sql 'SELECT has_database_privilege(current_user, current_database(), ''CREATE'')'
if (-not $verify.Succeeded) {
    $detail = if ($created.Count -eq 0) {
        "Role and database already existed, but '$User' cannot log in to [$Database]: $($verify.Error)" +
        ' Nothing was changed - an existing role keeps the password the server already has.'
    }
    else {
        "Created $($created -join ', '), but '$User' still cannot log in to [$Database]: $($verify.Error)"
    }
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail $detail
}
if ($verify.Output -ne 't') {
    return New-Outcome -Status 'Fail' -Remediation $remediation -Detail (
        "'$User' can log in to [$Database] but may not create objects in it, so the EF migrations " +
        'will fail at first start. The database exists and belongs to someone else; its owner was ' +
        'deliberately left alone.')
}

$detail = if ($created.Count -eq 0) {
    "Role and database already existed on $HostName; '$User' can log in to [$Database]."
}
else {
    "Created $($created -join ', ') on $HostName; '$User' can log in to [$Database]."
}
return New-Outcome -Status 'Pass' -Detail $detail
