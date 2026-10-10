#requires -Version 5.1
<# Execute only the server builder's supplied-runtime staging branch against temporary files. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'server\Build-ServerInstaller.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Server builder does not parse.' }
$branch = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -eq '[string]::IsNullOrWhiteSpace($RuntimePayloadDirectory)' })[0]
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-runtime-staging-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    $RuntimePayloadDirectory = Join-Path $fixtureRoot 'runtime [offline]'
    $stage = Join-Path $fixtureRoot 'stage'
    foreach ($path in @($RuntimePayloadDirectory, (Join-Path $stage 'payload'))) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    foreach ($name in @('dotnet-runtime-10.0.12-win-x64.exe', 'aspnetcore-runtime-10.0.12-win-x64.exe', 'unrelated.exe')) {
        Set-Content -LiteralPath (Join-Path $RuntimePayloadDirectory $name) -Value 'offline fixture, never executed'
    }
    . ([scriptblock]::Create($branch.Extent.Text))
    $copied = @(Get-ChildItem -LiteralPath (Join-Path $stage 'payload') -File)
    if ($copied.Count -ne 2 -or $copied.Name -contains 'unrelated.exe') { throw 'Offline staging must copy exactly the two supplied runtime installers.' }
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixtureRoot) -notlike 'nodepilot-runtime-staging-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
Write-Host 'Server runtime staging checks passed (offline inputs, literal parent path, selective copy).'
