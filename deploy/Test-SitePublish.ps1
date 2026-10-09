#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Exercise the upload loop without loading credentials, building or making network requests.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Publish-Site.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
$loop = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.ForEachStatementAst] -and
    $node.Variable.VariablePath.UserPath -eq 'item' -and $node.Condition.Extent.Text -eq '$relative'
}, $true)
if (-not $loop) { throw 'Upload loop not found.' }
$upload = [scriptblock]::Create($loop.Extent.Text)
$relative = @('assets/app-new.js', 'assets/app-new.css', 'index.html', 'docs/index.html')
function Send-File {
    param([string]$item)
    $script:sent.Add($item)
    return $item -ne $script:failure
}
foreach ($scenario in @(
    @{ failure=''; expected=4 },
    @{ failure='assets/app-new.js'; expected=1 },
    @{ failure='assets/app-new.css'; expected=2 }
)) {
    $script:failure = $scenario.failure
    $script:sent = New-Object 'System.Collections.Generic.List[string]'
    $uploaded = 0
    $rejected = $false
    try { . $upload } catch {
        if ($_.Exception.Message -notlike 'Upload failed:*') { throw }
        $rejected = $true
    }
    if ($script:sent.Count -ne $scenario.expected) { throw 'HTML must not be published after an asset upload fails.' }
    if ($rejected -ne [bool]$script:failure) { throw 'A failed upload must fail the publication.' }
}
Write-Host 'Site publication: 3 scenarios passed; no network requests were made.'
