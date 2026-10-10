#requires -Version 5.1
<# Offline behavioral checks: execute the updater's rollback statements with fake services,
   database tools and no components. No installer, database or service is touched. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'desktop\Update-Desktop.ps1'
$parseErrors = $null
$tokens = $null
$program = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Updater contains PowerShell syntax errors: $parseErrors" }
$update = @($program.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })[-1]
$rollbackBody = $update.CatchClauses[0].Body
$rollbackText = $rollbackBody.Extent.Text.Substring(1, $rollbackBody.Extent.Text.Length - 2)
# Return to this harness where production exits its process; preserve every branch/statement.
foreach ($exit in @($rollbackBody.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.ExitStatementAst]
}, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $offset = $exit.Extent.StartOffset - $rollbackBody.Extent.StartOffset - 1
    $rollbackText = $rollbackText.Remove($offset, $exit.Extent.Text.Length).Insert($offset, 'return')
}
$rollback = [scriptblock]::Create($rollbackText)

# Only import the restore helper when present. All OS-facing commands are replaced below.
$restoreFunction = $program.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Restore-DatabaseBackup'
}, $false)
if ($restoreFunction) { . ([scriptblock]::Create($restoreFunction.Extent.Text)) }

function Stop-Everything {
    $script:events.Add('stop')
    if ($script:stopFails) { throw 'Runtime could not be stopped' }
}
function Start-Services { $script:events.Add('start-db'); $script:events.Add('start-api') }
function Start-DatabaseForRestore { $script:events.Add('start-db') }
function Get-PostgresConnection {
    [pscustomobject]@{ DbHost = 'fake'; Port = '1'; Username = 'fake'; Database = 'fake'; Password = 'fixture-only' }
}
function Invoke-FakeRestore { $script:events.Add('restore'); $global:LASTEXITCODE = $script:restoreExit }
function Start-Sleep { param($Seconds, $Milliseconds) }
function Write-Step { param($Message) }
function Write-Warning { param($Message) }
function Write-Error { param($Message) $script:lastError = [string]$Message }
function Assert-True { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }

$components = @()
$swappedComponents = @()
$backupFile = 'fixture.dump'
$pgRestore = 'Invoke-FakeRestore'
$DbServiceName = 'fake-db'
$ApiServiceName = 'fake-api'
$previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD')
try {
    foreach ($exitCode in @(0, 1)) {
        $script:stopFails = $false
        $script:events = New-Object 'System.Collections.Generic.List[string]'
        $script:restoreExit = $exitCode
        $script:lastError = ''
        [Environment]::SetEnvironmentVariable('PGPASSWORD', 'prior-fixture-value')
        $_ = [pscustomobject]@{ Exception = [Exception]::new('Update health check failed') }
        & $rollback

        $restoreIndex = $script:events.IndexOf('restore')
        $apiIndex = $script:events.IndexOf('start-api')
        Assert-True ($restoreIndex -ge 0) 'Rollback must attempt the database restore.'
        if ($exitCode -eq 0) {
            Assert-True ($apiIndex -gt $restoreIndex) 'API must remain stopped until the database restore succeeds.'
            Assert-True ($script:lastError -eq 'Update rolled back to the previous version.') 'Successful rollback should be identified accurately.'
        } else {
            Assert-True ($apiIndex -eq -1) 'A failed restore must not restart the API against a partial database.'
            Assert-True ($script:lastError -match 'rollback failed') 'Failed restore must be reported as failed rollback.'
        }
        Assert-True ([Environment]::GetEnvironmentVariable('PGPASSWORD') -eq 'prior-fixture-value') 'Restore must preserve the caller environment.'
    }
    $script:events.Clear()
    $script:stopFails = $true
    & $rollback
    Assert-True (-not $script:events.Contains('restore')) 'Failed shutdown must prevent database restore.'
    Assert-True (-not $script:events.Contains('start-api')) 'Failed shutdown must not restart the API.'

    # Exercise the actual readiness loop independently; only its native calls are simulated.
    $readyFunction = $program.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-DatabaseForRestore'
    }, $false)
    . ([scriptblock]::Create($readyFunction.Extent.Text))
    function Start-Service { param($Name) $script:events.Add('start-db') }
    function Join-Path { param($Path, $ChildPath) return 'Invoke-FakeReady' }
    function Invoke-FakeReady {
        $script:readyCalls++
        $global:LASTEXITCODE = if ($script:readyCalls -ge $script:readyAfter) { 0 } else { 1 }
    }
    $pgBin = 'unused'
    $script:readyCalls = 0
    $script:readyAfter = 3
    Start-DatabaseForRestore -Connection (Get-PostgresConnection)
    Assert-True ($script:readyCalls -eq 3) 'Readiness must wait until PostgreSQL accepts connections.'
    $script:readyCalls = 0
    $script:readyAfter = 100
    $failure = $null
    try { Start-DatabaseForRestore -Connection (Get-PostgresConnection) } catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $script:readyCalls -eq 30) 'Readiness must fail within its bounded attempt count.'
} finally {
    [Environment]::SetEnvironmentVariable('PGPASSWORD', $previousPassword)
}
# The mocks above set the exit code of a native command; do not leak it to the caller.
$global:LASTEXITCODE = 0
Write-Host 'Desktop rollback checks passed (restore success/failure, failed shutdown, readiness success/timeout).'
