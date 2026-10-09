#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Load the actual selection functions without running the reset script.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'dev-reset.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
foreach ($name in @('Test-DevProcess', 'Get-DevProcessesToStop')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}

$apiDirectory = 'C:\work\NodePilot\src\NodePilot.Api'
$uiDirectory = 'C:\work\NodePilot\src\nodepilot-ui'
$script:processes = @(
    [pscustomobject]@{ Name='NodePilot.Api.exe'; ProcessId=101; ExecutablePath="$apiDirectory\bin\Debug\net10.0-windows\NodePilot.Api.exe"; CommandLine='' },
    [pscustomobject]@{ Name='node.exe'; ProcessId=102; ExecutablePath='C:\node\node.exe'; CommandLine="node `"$uiDirectory\node_modules\.bin\..\vite\bin\vite.js`"" },
    [pscustomobject]@{ Name='node.exe'; ProcessId=103; ExecutablePath='C:\node\node.exe'; CommandLine='node C:\other\node_modules\vite\bin\vite.js' },
    [pscustomobject]@{ Name='node.exe'; ProcessId=104; ExecutablePath='C:\node\node.exe'; CommandLine='node C:\tools\worker.js' },
    [pscustomobject]@{ Name='NodePilot.Api.exe'; ProcessId=105; ExecutablePath='C:\Program Files\NodePilot\NodePilot.Api.exe'; CommandLine='' }
)
$script:listeners = @()
function Get-CimInstance { param($ClassName) return $script:processes }
function Get-NetTCPConnection { param($State, $ErrorAction) return $script:listeners }
function Assert-Selected {
    $selected = @(Get-DevProcessesToStop -Ports @(5000,5173) -ApiDirectory $apiDirectory -UiDirectory $uiDirectory)
    if (($selected.ProcessId -join ',') -ne '101,102') { throw 'Only this checkout API and Vite may be selected.' }
}
Assert-Selected
$script:listeners = @([pscustomobject]@{ LocalPort=5000; OwningProcess=101 }, [pscustomobject]@{ LocalPort=5173; OwningProcess=102 })
Assert-Selected
foreach ($foreignId in @(103,104,105,999)) {
    $script:listeners = @([pscustomobject]@{ LocalPort=5000; OwningProcess=$foreignId })
    $rejected = $false
    try { Get-DevProcessesToStop -Ports @(5000,5173) -ApiDirectory $apiDirectory -UiDirectory $uiDirectory | Out-Null }
    catch { if ($_.Exception.Message -notlike '*not a verified NodePilot development process*') { throw }; $rejected = $true }
    if (-not $rejected) { throw "Foreign listener $foreignId was not rejected." }
}
Write-Host 'Dev reset process ownership: 6 scenarios passed; no processes were stopped.'
