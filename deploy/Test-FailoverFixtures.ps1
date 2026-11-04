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
# The healthy 200 body has no reason property (ClusterSetup.ComputeLeaderHealth).
function Invoke-WebRequest {
  param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
  [pscustomobject]@{ StatusCode = 200; Content = '{"status":"leader","nodeId":"A","leaseExpiresAt":"2026-11-04T10:00:30Z","leaseEpoch":3,"lastRenewAt":"2026-11-04T10:00:00Z"}' }
}
$result = Get-LeaderHealth 'https://fixture'
if (-not $result.IsLeader -or $result.StatusCode -ne 200 -or $result.NodeId -ne 'A' -or $result.LeaseEpoch -ne 3 -or $null -ne $result.Reason) {
  throw "Healthy leader response was not recognised: status=$($result.StatusCode) leader=$($result.IsLeader) reason=$($result.Reason)"
}
function Invoke-WebRequest { param($Uri, [switch]$UseBasicParsing, $TimeoutSec) throw [Net.WebException]::new('Unable to connect to the remote server', [Net.WebExceptionStatus]::ConnectFailure) }
$result = Get-LeaderHealth 'https://fixture'
if ($result.IsLeader -or $result.StatusCode -ne 0 -or $result.Reason -notlike 'request-error: Unable to connect*') { throw 'A transport error was not reported as one.' }
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
Write-Host 'Failover fixture checks passed: healthy leader, follower body and transport error recognised; start/stop remotely routed without live services.'
