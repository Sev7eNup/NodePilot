#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$json = @'
import sys,json
sys.path.insert(0,sys.argv[1])
import spec_invasive as s
print(json.dumps([{"family":family,"id":step.id,"script":step.config["script"]}
  for family,wf in [("service",s.service_workflow()),("task",s.scheduled_task_workflow())]
  for step in wf.steps if step.id in ("janitor","teardown")]))
'@ | & python - $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not generate fixture cleanup scripts.' }
function Get-Service { [CmdletBinding()] param($Name) if ($script:serviceExists) { [pscustomobject]@{ Name = 'NPTestSvc_12345678' } } }
function Get-ScheduledTask { [CmdletBinding()] param($TaskPath, $TaskName) if ($script:taskExists) { [pscustomobject]@{ TaskName = 'NPTestTask_12345678' } } }
function sc.exe { param($action, $Name) if ($action -ne 'delete') { throw 'Unexpected service action' }; $script:serviceExists = $false }
function Unregister-ScheduledTask { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, [switch]$Confirm) process { $script:taskExists = $false } }
foreach ($case in ($json | ConvertFrom-Json)) {
    $script:serviceExists = $true; $script:taskExists = $true
    $body = $case.script
    if ($case.id -eq 'janitor') {
        # The shared filesystem janitor is outside this fixture; execute the actual appended resource cleanup.
        $marker = '$janitorSweep = ''ok'''
        $body = $body.Substring($body.IndexOf($marker) + $marker.Length)
    }
    $body = $body.Replace('{{cid.param.text}}', "'12345678-1234-1234-1234-123456789012'")
    . ([scriptblock]::Create($body))
    if ($case.family -eq 'service') {
        if ($script:serviceExists -or -not $script:taskExists) { throw "Service $($case.id) touched the concurrent task fixture or left its own orphan." }
    } elseif ($script:taskExists -or -not $script:serviceExists) { throw "Task $($case.id) touched the concurrent service fixture or left its own orphan." }
}
Write-Host 'Invasive fixture ownership: both janitors and teardowns preserve the other active workflow family.'
