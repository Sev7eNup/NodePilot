#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Failover.ps1'), [ref]$tokens, [ref]$errors)
$health = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-LeaderHealth' }, $true)
. ([scriptblock]::Create($health.Extent.Text))
Add-Type @'
using System.IO;
using System.Net;
using System.Text;
public sealed class OfflineFollowerResponse : WebResponse {
  public HttpStatusCode StatusCode { get { return HttpStatusCode.ServiceUnavailable; } }
  public override Stream GetResponseStream() { return new MemoryStream(Encoding.UTF8.GetBytes("{\"reason\":\"not_leader\",\"nodeId\":\"B\",\"leaseEpoch\":2}")); }
}
'@
function Invoke-WebRequest { param($Uri, [switch]$UseBasicParsing, $TimeoutSec) throw [Net.WebException]::new('503', $null, [Net.WebExceptionStatus]::ProtocolError, [OfflineFollowerResponse]::new()) }
$result = Get-LeaderHealth 'https://fixture'
if ($result.IsLeader -or $result.StatusCode -ne 503 -or $result.Reason -ne 'not_leader' -or $result.NodeId -ne 'B') { throw 'Follower HTTP response body was lost.' }
Add-Type -AssemblyName System.Net.Http
function Invoke-WebRequest {
  param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
  $response = New-Object Net.Http.HttpResponseMessage([Net.HttpStatusCode]::ServiceUnavailable)
  $response.Content = New-Object Net.Http.StringContent('{"reason":"not_leader","nodeId":"B","leaseEpoch":2}')
  $failure = New-Object System.Exception('503')
  $failure | Add-Member -NotePropertyName Response -NotePropertyValue $response
  throw $failure
}
$result = Get-LeaderHealth 'https://fixture'
if ($result.IsLeader -or $result.StatusCode -ne 503 -or $result.Reason -ne 'not_leader') { throw 'PowerShell 7 HTTP response shape was lost.' }
$script:actions = @()
function Stop-Service { [CmdletBinding()] param($Name, [switch]$Force) $script:actions += "stop:$Name" }
function Start-Service { [CmdletBinding()] param($Name) $script:actions += "start:$Name" }
function Invoke-Command { [CmdletBinding()] param($ComputerName, [object[]]$ArgumentList, $ScriptBlock) if ($ComputerName -ne 'fixture') { throw 'Wrong remote host' }; & $ScriptBlock @ArgumentList }
$leaderHost = 'fixture'; $ServiceName = 'NodePilot-Fixture'
$commands = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and (($n.GetCommandName() -in @('Stop-Service','Start-Service')) -or ($n.GetCommandName() -eq 'Invoke-Command' -and $n.Extent.Text -match '(Stop|Start)-Service')) }, $true)
foreach ($command in $commands) {
  if ($command.GetCommandName() -ne 'Invoke-Command' -and $command.Extent.Text -notmatch 'ComputerName') { continue }
  . ([scriptblock]::Create($command.Extent.Text))
}
if (($script:actions -join ',') -ne 'stop:NodePilot-Fixture,start:NodePilot-Fixture') { throw "Service actions were not routed correctly: $($script:actions -join ',')" }
Write-Host 'Failover fixture checks passed: follower body retained and start/stop remotely routed without live services.'
