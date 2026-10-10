#requires -Version 5.1
<# Offline credential-boundary test: a fake Preflight helper records calls without starting psql. #>
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NodePilot-PostgresProvisioning-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Provision-NodePilotPostgres.ps1') -Destination $testRoot
    @'
function Get-NodePilotPostgresRemediationScript { param($User, $Database) return 'Ask the DBA.' }
function ConvertFrom-NodePilotSecureString { param($Value) return [Net.NetworkCredential]::new('', $Value).Password }
function Get-NodePilotPsqlEnvironment { param($Secret, $RootCertificate) return @{ PGPASSWORD = $Secret; PGSSLMODE = 'verify-full' } }
function Invoke-NodePilotPsql {
    param($PsqlPath, $Arguments, $Sql, $Environment)
    $global:NodePilotProvisionTestCalls.Add([pscustomobject]@{ Arguments = $Arguments; Sql = $Sql; Environment = $Environment })
    if ($Sql -like 'CREATE ROLE*') {
        return [pscustomobject]@{ Succeeded = $false; Output = $Sql; Error = "ERROR: $Sql" }
    }
    $output = if ($Sql -like '*rolcreaterole*') { 't' } else { '' }
    return [pscustomobject]@{ Succeeded = $true; Output = $output; Error = '' }
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Preflight.ps1') -Encoding UTF8
    $fakePath = Join-Path $testRoot 'fixture.txt'
    Set-Content -LiteralPath $fakePath -Value 'Offline fixture only.'
    $global:NodePilotProvisionTestCalls = New-Object 'System.Collections.Generic.List[object]'
    $result = & (Join-Path $testRoot 'Provision-NodePilotPostgres.ps1') -PsqlPath $fakePath `
        -HostName 'db.example.test' -Port 5432 -Database 'nodepilot' -User 'nodepilot' `
        -Password (ConvertTo-SecureString "role'password-marker" -AsPlainText -Force) `
        -SuperUser 'postgres' -SuperPassword (ConvertTo-SecureString 'super-password-marker' -AsPlainText -Force) `
        -RootCertificate $fakePath
    if ($result.Status -ne 'Fail') { throw 'A failing CREATE ROLE must fail provisioning.' }
    if (($result | ConvertTo-Json) -match 'password-marker') { throw 'Server provisioning echoed credential-bearing SQL into its result.' }
    if ($global:NodePilotProvisionTestCalls.Count -ne 3) { throw 'Provisioning must stop after CREATE ROLE fails.' }
    $write = $global:NodePilotProvisionTestCalls[2]
    if ($write.Sql -notmatch "PASSWORD 'role''password-marker'") { throw 'Credential-bearing SQL must still reach stdin.' }
    if (($write.Arguments -join ' ') -match 'password-marker|CREATE ROLE') { throw 'Credentials entered process arguments.' }
    if ($write.Arguments -notcontains '-X') { throw 'Provisioning must ignore psqlrc output/echo settings.' }
    if ($write.Environment.PGPASSWORD -ne 'super-password-marker') { throw 'Child authentication was lost.' }
    Write-Host 'Server PostgreSQL provisioning checks passed: error redaction, stdin, child environment.'
} finally {
    Remove-Variable -Name NodePilotProvisionTestCalls -Scope Global -ErrorAction SilentlyContinue
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedRoot) -notlike 'NodePilot-PostgresProvisioning-*') {
        throw 'Refusing to remove an unexpected test directory.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
