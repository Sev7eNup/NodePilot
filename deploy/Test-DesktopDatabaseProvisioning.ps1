#requires -Version 5.1
<# Offline regression: extracts only the database function; never provisions or stops services. #>
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$provisionPath = Join-Path $PSScriptRoot 'desktop\Provision-LocalDb.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($provisionPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Desktop provisioner failed to parse.' }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-DesktopDatabase'
}, $true)
if ($null -eq $definition) { throw 'Desktop database entry point is missing.' }
. ([scriptblock]::Create($definition.Extent.Text))

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$script:Calls = New-Object 'System.Collections.Generic.List[object]'
$script:Existing = $false
$script:FailRoleWrite = $false
function Invoke-NodePilotPsql {
    param([string]$PsqlPath, [string[]]$Arguments, [string]$Sql, [hashtable]$Environment)
    $script:Calls.Add([pscustomobject]@{ Path = $PsqlPath; Arguments = $Arguments; Sql = $Sql; Environment = $Environment.Clone() })
    if ($script:FailRoleWrite -and $Sql -match '^(CREATE|ALTER) ROLE') {
        return [pscustomobject]@{ Succeeded = $false; Output = $Sql; Error = "ERROR with statement: $Sql" }
    }
    $output = if ($Sql -like 'SELECT*' -and $script:Existing) { '1' } else { '' }
    return [pscustomobject]@{ Succeeded = $true; Output = $output; Error = '' }
}

$superSecret = 'super-secret-marker'
$roleSecret = "role'password-marker"
$originalParentSecret = [Environment]::GetEnvironmentVariable('PGPASSWORD')
$invoke = @{
    PsqlPath = 'C:\fake\psql.exe'; Port = 47100; SuperSecret = $superSecret
    Role = 'role"name'; RoleSecret = $roleSecret; Database = 'database"name'
}

Initialize-DesktopDatabase @invoke
Assert-Condition ($script:Calls.Count -eq 4) 'Fresh database must look up and create both role and database.'
Assert-Condition ($script:Calls[1].Sql -eq "CREATE ROLE `"role`"`"name`" LOGIN PASSWORD 'role''password-marker';") 'Role and password must be SQL-quoted on stdin.'
Assert-Condition ($script:Calls[3].Sql -eq 'CREATE DATABASE "database""name" OWNER "role""name";') 'Database identifiers must be SQL-quoted.'
foreach ($call in $script:Calls) {
    Assert-Condition (($call.Arguments -notcontains '-c') -and ($call.Arguments -notcontains '-tAc')) 'SQL must not enter process arguments.'
    Assert-Condition ($call.Arguments -contains '-X') 'Provisioning must ignore a user psqlrc that could echo statements.'
    $argv = $call.Arguments -join ' '
    Assert-Condition (-not $argv.Contains($superSecret) -and -not $argv.Contains($roleSecret)) 'A process argument exposed a secret.'
    Assert-Condition ($call.Environment.PGPASSWORD -eq $superSecret) 'Authentication must be passed to the child.'
    Assert-Condition ($call.Environment.PGSSLMODE -eq 'disable') 'Desktop loopback transport semantics changed.'
}

$script:Calls.Clear()
$script:Existing = $true
Initialize-DesktopDatabase @invoke
Assert-Condition ($script:Calls.Count -eq 3) 'Existing database should only refresh its role password.'
Assert-Condition ($script:Calls[1].Sql -like 'ALTER ROLE *') 'Existing role password refresh must remain supported.'

$script:FailRoleWrite = $true
$failure = $null
try { Initialize-DesktopDatabase @invoke } catch { $failure = $_.ToString() }
Assert-Condition ($null -ne $failure) 'A failing role update must fail provisioning.'
Assert-Condition (-not $failure.Contains('password-marker') -and -not $failure.Contains($superSecret)) 'Failing SQL leaked credentials into the error/transcript.'
Assert-Condition ([Environment]::GetEnvironmentVariable('PGPASSWORD') -eq $originalParentSecret) 'Parent process environment changed.'

$parentWrites = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$env:PGPASSWORD'
}, $true))
Assert-Condition ($parentWrites.Count -eq 0) 'Provisioner must not set PGPASSWORD on itself.'
$rawPsqlCalls = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    (($node.GetCommandName() -eq 'Invoke-Native' -and $node.Extent.Text -match '\$psql\b') -or
     ($node.CommandElements[0].Extent.Text -eq '$psql'))
}, $true))
Assert-Condition ($rawPsqlCalls.Count -eq 0) 'Provisioner bypasses shared secret-safe psql plumbing.'
$build = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'desktop\Build-DesktopInstaller.ps1') -Raw
Assert-Condition ($build -match "'Preflight.ps1'\) -Destination \`$deployStage") 'Desktop package must contain shared psql plumbing.'

# Execute the actual registry publication block against an in-memory ACL. No registry provider
# is accessed: the write stub inspects permissions at the instant the secret would be published.
& {
    $statements = @($ast.EndBlock.Statements)
    $first = @($statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$svcRegPath' })[0]
    $last = @($statements | Where-Object { $_.Extent.Text -match '^Set-Acl -Path \$svcRegPath' -or $_.Extent.Text -match '^New-ItemProperty -Path \$svcRegPath' } | Sort-Object { $_.Extent.EndOffset })[-1]
    $block = $statements | Where-Object { $_.Extent.StartOffset -ge $first.Extent.StartOffset -and $_.Extent.EndOffset -le $last.Extent.EndOffset }
    $ApiServiceName = 'OfflineFixture'
    $connString = 'fixture-only-secret'
    $script:RegistryAcl = New-Object System.Security.AccessControl.RegistrySecurity
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $script:RegistryAcl.AddAccessRule((New-Object System.Security.AccessControl.RegistryAccessRule($users, 'ReadKey', 'Allow')))
    $script:RegistryWriteCount = 0
    function Get-Acl { param($Path) return $script:RegistryAcl }
    function Set-Acl { param($Path, $AclObject) $script:RegistryAcl = $AclObject }
    function New-ItemProperty {
        param($Path, $Name, $PropertyType, $Value, [switch]$Force)
        Assert-Condition $script:RegistryAcl.AreAccessRulesProtected 'DB secret was published before registry inheritance was disabled.'
        $rules = @($script:RegistryAcl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        Assert-Condition ($rules.Count -eq 2) 'Registry publication retained an untrusted explicit ACE.'
        foreach ($rule in $rules) {
            Assert-Condition ($rule.IdentityReference.Value -in @('S-1-5-18', 'S-1-5-32-544')) 'DB secret was readable by an untrusted principal at publication.'
        }
        Assert-Condition ($Value -contains "ConnectionStrings__Postgres=$connString") 'Connection string publication was lost.'
        $script:RegistryWriteCount++
    }
    . ([scriptblock]::Create(($block.Extent.Text -join "`n")))
    Assert-Condition ($script:RegistryWriteCount -eq 1) 'Expected exactly one environment publication.'
}

Write-Host 'Desktop database provisioning checks passed: create, repeat, error redaction, process boundary, packaging, ACL-before-publication.'
