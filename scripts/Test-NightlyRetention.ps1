#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Load the actual retention functions and their ownership constants without running the nightly suites.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'nightly-tests.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
foreach ($variable in @('$runMarkerName', '$runNamePattern')) {
    $assignment = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq $variable }, $false)
    . ([scriptblock]::Create($assignment.Extent.Text))
}
foreach ($name in @('New-NightlyRunDirectory', 'Remove-ExpiredNightlyRun')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}

$keepRuns = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'KeepRuns' }
$range = @($keepRuns.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateRange' })
if ($range.Count -ne 1 -or $range[0].PositionalArguments[0].Value -lt 1) { throw 'KeepRuns accepts values below 1.' }

$root = Join-Path ([IO.Path]::GetTempPath()) ('np-nightly-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'out'
$outside = Join-Path $root 'outside'
New-Item -ItemType Directory -Path $output, $outside | Out-Null
try {
    $owned = @('2026-10-01_220000', '2026-10-02_220000', '2026-10-03_220000') |
        ForEach-Object { New-NightlyRunDirectory -OutputDir $output -Stamp $_ }
    # Unrelated content: a foreign folder, an unmarked and a mismatched timestamp folder, a
    # marked folder with a foreign name, and a junction that points outside the output root.
    New-Item -ItemType Directory -Path (Join-Path $output 'backup') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $output '2026-09-01_220000') | Out-Null
    $mismatched = New-Item -ItemType Directory -Path (Join-Path $output '2026-09-02_220000')
    Set-Content -LiteralPath (Join-Path $mismatched.FullName $runMarkerName) -Value '2026-09-03_220000'
    $foreign = New-Item -ItemType Directory -Path (Join-Path $output 'archive')
    Set-Content -LiteralPath (Join-Path $foreign.FullName $runMarkerName) -Value 'archive'
    Set-Content -LiteralPath (Join-Path $outside $runMarkerName) -Value '2026-08-01_220000'
    Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'keep'
    $null = & cmd.exe /c mklink /J (Join-Path $output '2026-08-01_220000') $outside
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the junction fixture.' }

    Remove-ExpiredNightlyRun -OutputDir $output -KeepRuns 1

    $remaining = @(Get-ChildItem -LiteralPath $output -Directory -Force | ForEach-Object { $_.Name } | Sort-Object)
    $expected = @('2026-08-01_220000', '2026-09-01_220000', '2026-09-02_220000', '2026-10-03_220000', 'archive', 'backup')
    if (($remaining -join ',') -ne ($expected -join ',')) { throw "Retention left '$($remaining -join ',')' instead of '$($expected -join ',')'." }
    if (-not (Test-Path -LiteralPath (Join-Path $outside 'keep.txt'))) { throw 'Retention deleted data outside the output root.' }
    if (Test-Path -LiteralPath $owned[0]) { throw 'The oldest owned run was not pruned.' }

    $rejected = $false
    try { Remove-ExpiredNightlyRun -OutputDir $output -KeepRuns 0 } catch { $rejected = $true }
    if (-not $rejected) { throw 'KeepRuns 0 was accepted and would prune every run.' }
} finally {
    $junction = Join-Path $output '2026-08-01_220000'
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    Remove-Item -LiteralPath $root -Recurse -Force
}

Write-Host 'Nightly retention prunes only marked run folders inside the output root.'
