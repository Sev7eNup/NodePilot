#requires -Version 5.1
# Executes the consumers' real list statements against the API's capped list / full names contract.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:rows = @(1..501 | ForEach-Object { [pscustomobject]@{ id = "id-$_"; name = "workflow-$_" } })
function Invoke-NodePilotJson {
  param($Method, $Path)
  if ($Path -eq '/api/workflows/names') { return $script:rows }
  if ($Path -eq '/api/workflows') { return $script:rows[0..499] }
  if ($Path -eq '/api/workflows/id-501') { return [pscustomobject]@{ id = 'id-501'; name = 'workflow-501'; isEnabled = $false; checkedOutByUserId = 'owner'; checkedOutByUserName = 'Operator' } }
  throw "Unexpected route: $Path"
}
function Invoke-RestMethod { param($Uri, $Headers) Invoke-NodePilotJson -Method GET -Path ($Uri.Replace('http://fixture', '')) }
$BaseUrl = 'http://fixture'; $h = @{}
foreach ($file in @('Install-TestSuite.ps1', 'Verify-TestSuite.ps1')) {
  $tokens = $null; $errors = $null
  $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors)
  if ($errors.Count) { throw "$file does not parse." }
  if ($file -eq 'Install-TestSuite.ps1') {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-WorkflowList' }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
    $actual = @(Get-WorkflowList)
  } else {
    $assignment = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$allWorkflows' }, $true)
    . ([scriptblock]::Create($assignment.Extent.Text))
    $actual = @($allWorkflows)
  }
  if ($actual.Count -ne 501 -or $actual[-1].id -ne 'id-501') { throw "$file misses the existing workflow beyond the API list cap." }
  if ($file -eq 'Install-TestSuite.ps1') {
    $current = $actual[-1]
    $detail = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$current' -and $node.Right.Extent.Text -like '*-Method GET*/api/workflows/*' }, $true)
    . ([scriptblock]::Create($detail.Extent.Text))
    if ($current.checkedOutByUserId -ne 'owner' -or $current.checkedOutByUserName -ne 'Operator') { throw 'Existing lock details were lost.' }
  } else {
    $wf = $actual[-1]
    $detail = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$wf' -and $node.Right.Extent.Text -like '*Invoke-RestMethod*' }, $true)
    . ([scriptblock]::Create($detail.Extent.Text))
    if ($wf.isEnabled) { throw 'Disabled workflow was treated as enabled.' }
  }
}
Write-Host 'TestSuite catalogue: both consumers retain existing workflows beyond the 500-item list cap.'
