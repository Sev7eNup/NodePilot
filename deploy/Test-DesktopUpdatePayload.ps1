#requires -Version 5.1
<# Offline checks using the updater's real validation/swap/rollback AST statements.
   Only fresh temporary directories are mutated; no services, processes or database are used. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'desktop\Update-Desktop.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Desktop updater does not parse.' }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$update = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })[-1]
$swap = @($update.Body.Statements | Where-Object { $_ -is [System.Management.Automation.Language.ForEachStatementAst] })[0]
$restore = $update.CatchClauses[0].Body.Find({ param($node) $node -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)
$componentAssignment = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$components' })[0]
$validationStart = @($ast.EndBlock.Statements | Where-Object { $_.Extent.Text -like 'if (-not $NewArtifactPath)*' })[0]
$backupAssignment = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$backupFile' })[0]
$validation = @($ast.EndBlock.Statements | Where-Object { $_.Extent.StartOffset -ge $validationStart.Extent.StartOffset -and $_.Extent.EndOffset -le $backupAssignment.Extent.StartOffset })
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-desktop-payload-' + [guid]::NewGuid().ToString('N'))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    $InstallPath = Join-Path $fixtureRoot 'installed'
    $NewArtifactPath = Join-Path $fixtureRoot 'payload'
    $rollbackRoot = Join-Path $fixtureRoot 'rollback'
    foreach ($path in @($InstallPath, $NewArtifactPath, $rollbackRoot)) {
        Assert-True ([IO.Path]::GetFullPath($path).StartsWith($fixtureRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture path escaped temporary workspace.'
        New-Item -ItemType Directory -Path $path | Out-Null
    }
    foreach ($name in @('app', 'desktop', 'pgsql', 'deploy', 'tools')) {
        $new = Join-Path $NewArtifactPath $name
        New-Item -ItemType Directory -Path $new | Out-Null
        Set-Content -LiteralPath (Join-Path $new 'version.txt') -Value 'new'
        # Older installations may not contain the tools directory yet.
        if ($name -ne 'tools') {
            $old = Join-Path $InstallPath $name
            New-Item -ItemType Directory -Path $old | Out-Null
            Set-Content -LiteralPath (Join-Path $old 'version.txt') -Value 'old'
        }
    }
    . ([scriptblock]::Create($componentAssignment.Extent.Text))
    $swappedComponents = New-Object 'System.Collections.Generic.List[string]'
    . ([scriptblock]::Create(($validation.Extent.Text -join "`n")))
    . ([scriptblock]::Create($swap.Extent.Text))
    foreach ($name in @('app', 'desktop', 'pgsql', 'deploy', 'tools')) {
        $versionPath = Join-Path (Join-Path $InstallPath $name) 'version.txt'
        Assert-True ((Test-Path -LiteralPath $versionPath) -and (Get-Content -LiteralPath $versionPath) -eq 'new') "Full update did not replace '$name'."
    }
    # Simulate a failure after the swap, and execute the real binary rollback loop.
    . ([scriptblock]::Create($restore.Extent.Text))
    foreach ($name in @('app', 'desktop', 'pgsql', 'deploy')) {
        Assert-True ((Get-Content -LiteralPath (Join-Path (Join-Path $InstallPath $name) 'version.txt')) -eq 'old') "Rollback did not restore '$name'."
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstallPath 'tools'))) 'Rollback retained a newly introduced tools component.'
    # Every missing component must fail validation before the first backup/stop/mutation.
    foreach ($name in @('app', 'desktop', 'pgsql', 'deploy', 'tools')) {
        $path = Join-Path $NewArtifactPath $name
        $heldPath = Join-Path $NewArtifactPath ($name + '-held')
        Move-Item -LiteralPath $path -Destination $heldPath
        try {
            $failure = $null
            try { . ([scriptblock]::Create(($validation.Extent.Text -join "`n"))) } catch { $failure = $_ }
            Assert-True ($null -ne $failure) "Incomplete '$name' payload was accepted."
        } finally { Move-Item -LiteralPath $heldPath -Destination $path }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'nodepilot-desktop-payload-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'Desktop payload checks passed: all five components, complete rollback, missing-component rejection.'
