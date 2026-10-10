#requires -Version 5.1
<# Offline regression: execute the actual preflight and finally blocks with Invoke-Command
   replaced. No remote session, SQL connection or VM action is performed. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-ServerMatrix.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Server matrix does not parse.' }
$loop = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.ForEachStatementAst] -and $_.Variable.Extent.Text -eq '$s' })[0]
$attempt = @($loop.Body.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })[0]
$preflight = @($attempt.Body.Statements)[0]
$initializers = @($loop.Body.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -like '$remote*' })
$cleanup = $attempt.Finally.Extent.Text
$config = [pscustomobject]@{ remoteSql = [pscustomobject]@{ vm = 'fixture-only'; database = 'existing-customer-db'; fqdn = 'fixture-only' } }
$srv = [pscustomobject]@{ computerAccount = 'fixture-only' }
$s = @{ remoteSql = $true }
$cred = $null
function Invoke-Command {
    [CmdletBinding()]
    param($VMName, $Credential, $ArgumentList, $ScriptBlock)
    if ($ScriptBlock.ToString().Contains('DROP DATABASE')) {
        $script:cleanupCalls++
        $script:keepLogin = $ArgumentList[2]
    } else {
        if ($script:probeFails) { throw 'Offline preflight failure' }
        if ($script:probeMode -eq 'empty') { return }
        if ($script:probeMode -eq 'error') { Write-Error 'Offline nonterminating remoting failure'; return }
        if ($script:probeMode -eq 'invalid') { return [pscustomobject]@{ databaseExists = 'false'; loginExists = $false } }
        return [pscustomobject]@{ databaseExists = $script:databaseExists; loginExists = $script:loginExists }
    }
}
foreach ($case in @(
    @{ Name = 'existing database'; Database = $true; Login = $true; Fails = $false; Cleanup = 0 },
    @{ Name = 'failed probe'; Database = $false; Login = $false; Fails = $true; Cleanup = 0 },
    @{ Name = 'empty probe'; Database = $false; Login = $false; Fails = $false; Mode = 'empty'; Cleanup = 0 },
    @{ Name = 'nonterminating probe failure'; Database = $false; Login = $false; Fails = $false; Mode = 'error'; Cleanup = 0 },
    @{ Name = 'invalid probe'; Database = $false; Login = $false; Fails = $false; Mode = 'invalid'; Cleanup = 0 },
    @{ Name = 'new database, existing login'; Database = $false; Login = $true; Fails = $false; Cleanup = 1 },
    @{ Name = 'new database and login'; Database = $false; Login = $false; Fails = $false; Cleanup = 1 }
)) {
    $script:databaseExists = $case.Database; $script:loginExists = $case.Login; $script:probeFails = $case.Fails
    $script:probeMode = $case['Mode']
    $script:cleanupCalls = 0; $script:keepLogin = $null
    . ([scriptblock]::Create(($initializers.Extent.Text -join "`n")))
    try {
        . ([scriptblock]::Create($preflight.Extent.Text))
        throw 'Simulated later scenario failure'
    } catch { } finally { . ([scriptblock]::Create($cleanup.Substring(1, $cleanup.Length - 2))) }
    if ($script:cleanupCalls -ne $case.Cleanup) { throw "Unsafe cleanup for $($case.Name): $script:cleanupCalls remote deletion call(s)." }
    if ($case.Cleanup -and $script:keepLogin -ne $case.Login) { throw 'Cleanup did not preserve the captured login baseline.' }
}
Write-Host 'Remote SQL cleanup checks passed: existing database and failed probe untouched; scenario-owned cleanup preserves login baseline.'
