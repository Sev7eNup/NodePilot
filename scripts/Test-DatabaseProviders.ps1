<#
.SYNOPSIS
Runs database integration tests against isolated PostgreSQL 16 and SQL Server 2022 databases.
.DESCRIPTION
Uses NODEPILOT_TEST_POSTGRES and NODEPILOT_TEST_SQLSERVER when both are configured.
Otherwise starts two disposable Docker containers bound to loopback. Each test fixture creates
its own nodepilot_test_<guid> database. No application database is used or reset.
#>
param(
    [switch]$NoBuild,
    [string]$ArtifactsPath,
    [string]$Filter = 'Category=DatabaseIntegration'
)

$ErrorActionPreference = 'Stop'
$testRepo = Split-Path -Parent $PSScriptRoot
$testContainerIds = New-Object 'System.Collections.Generic.List[string]'
$testOldPostgres = $env:NODEPILOT_TEST_POSTGRES
$testOldSqlServer = $env:NODEPILOT_TEST_SQLSERVER
$testOldPgPassword = $env:POSTGRES_PASSWORD
$testOldSaPassword = $env:MSSQL_SA_PASSWORD
$testOldSqlcmdPassword = $env:SQLCMDPASSWORD

function Wait-TestDatabase([string]$Container, [bool]$Postgres) {
    $testDeadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        try {
            if ($Postgres) {
                & docker exec $Container pg_isready -U postgres *> $null
            } else {
                & docker exec $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -l 2 -Q 'SELECT 1' *> $null
            }
            if ($LASTEXITCODE -eq 0) { return }
        } catch { # Windows PowerShell surfaces native stderr while a server is starting.
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $testDeadline)
    throw 'A database test container did not become ready within three minutes.'
}

try {
    if ([string]::IsNullOrWhiteSpace($testOldPostgres) -xor [string]::IsNullOrWhiteSpace($testOldSqlServer)) {
        throw 'Configure both NODEPILOT_TEST_POSTGRES and NODEPILOT_TEST_SQLSERVER, or neither.'
    }
    if ([string]::IsNullOrWhiteSpace($testOldPostgres)) {
        & docker info --format '{{.OSType}}' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Start Docker with Linux containers before running this script.' }

        $testSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $testPassword = 'Np9!' + [Guid]::NewGuid().ToString('N')
        $env:POSTGRES_PASSWORD = $testPassword
        $env:MSSQL_SA_PASSWORD = $testPassword
        $testPgId = & docker run --detach --name "nodepilot-db-tests-$testSuffix-pg" --label nodepilot.database-tests=true -e POSTGRES_PASSWORD -p '127.0.0.1::5432' postgres:16-alpine
        if ($LASTEXITCODE -ne 0) { throw 'Could not start PostgreSQL test container.' }
        $testPgId = $testPgId.Trim()
        $testContainerIds.Add($testPgId)
        # SQLCMDPASSWORD stays inside this disposable container and is not placed on a command line.
        $env:SQLCMDPASSWORD = $testPassword
        $testSqlId = & docker run --detach --name "nodepilot-db-tests-$testSuffix-sql" --label nodepilot.database-tests=true -e 'ACCEPT_EULA=Y' -e 'MSSQL_PID=Developer' -e MSSQL_SA_PASSWORD -e SQLCMDPASSWORD -p '127.0.0.1::1433' mcr.microsoft.com/mssql/server:2022-latest
        if ($LASTEXITCODE -ne 0) { throw 'Could not start SQL Server test container.' }
        $testSqlId = $testSqlId.Trim()
        $testContainerIds.Add($testSqlId)
        Wait-TestDatabase $testPgId $true
        Wait-TestDatabase $testSqlId $false
        $testPgPort = (& docker port $testPgId 5432/tcp).Split(':')[-1]
        $testSqlPort = (& docker port $testSqlId 1433/tcp).Split(':')[-1]
        $env:NODEPILOT_TEST_POSTGRES = "Host=127.0.0.1;Port=$testPgPort;Database=postgres;Username=postgres;Password=$testPassword;SSL Mode=Disable"
        $env:NODEPILOT_TEST_SQLSERVER = "Server=127.0.0.1,$testSqlPort;Database=master;User ID=sa;Password=$testPassword;Encrypt=True;TrustServerCertificate=True"
    }

    Push-Location $testRepo
    try {
        foreach ($testProject in @('NodePilot.Data.Tests', 'NodePilot.Engine.Tests', 'NodePilot.Api.Tests')) {
            $testProjectPath = Join-Path $testRepo "tests/$testProject/$testProject.csproj"
            $testArguments = @('test', $testProjectPath, '--filter', $Filter, '--logger', 'console;verbosity=normal')
            if ($NoBuild) { $testArguments += '--no-build' }
            if ($ArtifactsPath) { $testArguments += @('--artifacts-path', $ArtifactsPath) }
            & dotnet @testArguments
            if ($LASTEXITCODE -ne 0) { throw "Database integration tests failed in $testProject." }
        }
    } finally { Pop-Location }
} finally {
    foreach ($testContainerId in $testContainerIds) {
        $testLabelsJson = & docker inspect --format '{{json .Config.Labels}}' $testContainerId
        if ($LASTEXITCODE -eq 0 -and ($testLabelsJson | ConvertFrom-Json).'nodepilot.database-tests' -eq 'true') {
            & docker rm --force --volumes $testContainerId | Out-Null
        }
    }
    $env:NODEPILOT_TEST_POSTGRES = $testOldPostgres
    $env:NODEPILOT_TEST_SQLSERVER = $testOldSqlServer
    $env:POSTGRES_PASSWORD = $testOldPgPassword
    $env:MSSQL_SA_PASSWORD = $testOldSaPassword
    $env:SQLCMDPASSWORD = $testOldSqlcmdPassword
}
