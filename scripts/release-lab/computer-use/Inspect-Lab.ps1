<# Read-only host/guest inspection. Native input readiness is attested separately by the agent. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigPath, [Parameter(Mandatory)][string]$RunDir)
. "$PSScriptRoot\Lab.ps1"
$cfg = Read-CuConfig $ConfigPath
$run = Get-Content -LiteralPath (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
$credential = Import-Clixml -LiteralPath $cfg.credentialPath
$checks = [ordered]@{ signatures = $false; isolation = $true; desktop = $false; server = $false }
$details = [ordered]@{}
try { Assert-CuSignature $cfg; $checks.signatures = $true } catch { $details.signatures = 'Signature/publisher check failed' }
foreach ($kind in @('desktop', 'server')) {
    $target = $cfg.targets.$kind
    try {
        if ((Get-VM -Name $target.vm).State -ne 'Running') { throw 'VM is not running' }
        $result = Invoke-Command -VMName $target.vm -Credential $credential -ArgumentList $kind, $cfg.release.version, $run.id, $target.baselineId, $target.origin -ScriptBlock {
            param($kind, $version, $runId, $baseline, $origin)
            $ErrorActionPreference = 'Stop'
            $receipt = Get-Content C:\np-computer-use\installation.json -Raw | ConvertFrom-Json
            $exe = if ($kind -eq 'desktop') { 'C:\Program Files\NodePilot\desktop\NodePilot.exe' } else { 'C:\Program Files\NodePilot\NodePilot.Api.exe' }
            $installed = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
            $service = Get-Service NodePilot
            $uri = [uri]$origin
            $pin = if ($kind -eq 'desktop') { (Get-Content C:\ProgramData\NodePilot\desktop.json -Raw | ConvertFrom-Json).certificateSha256 } else { $null }
            $client = New-Object Net.Sockets.TcpClient
            try {
                if (-not $client.ConnectAsync($uri.Host, $uri.Port).Wait(5000)) { throw 'Connect timed out' }
                $callback = [Net.Security.RemoteCertificateValidationCallback]{ param($sender, $cert, $chain, $errors)
                    if ($pin) {
                        $sha = [Security.Cryptography.SHA256]::Create()
                        try { return ([BitConverter]::ToString($sha.ComputeHash($cert.GetRawCertData())).Replace('-', '') -eq $pin) }
                        finally { $sha.Dispose() }
                    }
                    return $errors -eq [Net.Security.SslPolicyErrors]::None
                }
                $ssl = New-Object Net.Security.SslStream($client.GetStream(), $false, $callback)
                $ssl.ReadTimeout = 5000; $ssl.WriteTimeout = 5000; $ssl.AuthenticateAsClient($uri.Host)
                $bytes = [Text.Encoding]::ASCII.GetBytes("GET /healthz/ready HTTP/1.1`r`nHost: $($uri.Authority)`r`nConnection: close`r`n`r`n")
                $ssl.Write($bytes, 0, $bytes.Length); $reader = New-Object IO.StreamReader($ssl)
                $ready = $reader.ReadLine() -match '^HTTP/1\.[01] 200 '
            } finally { $client.Dispose() }
            [pscustomobject]@{
                version = $installed; service = "$($service.Status)"; ready = $ready
                receiptMatches = ($receipt.runId -eq $runId -and $receipt.baselineId -eq $baseline)
                installerSha256 = $receipt.installerSha256
                interactiveSessions = @(Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty SessionId -Unique)
            }
        }
        $expectedHash = $run.binding.artifacts."$($kind)Installer".sha256
        $checks[$kind] = $result.ready -and $result.service -eq 'Running' -and
            ($result.version -split '\+')[0] -eq $cfg.release.version -and $result.installerSha256 -eq $expectedHash
        if (-not $result.receiptMatches) { $checks.isolation = $false }
        $details[$kind] = $result
    } catch { $checks[$kind] = $false; $checks.isolation = $false; $details[$kind] = 'Guest identity/readiness inspection failed' }
}
$jobs = @()
try {
    foreach ($name in @('winrm', 'sql', 'smtp', 'http', 'proxy', 'llm', 'ldap', 'windows-sso', 'oidc', 'scim', 'eventlog')) {
        $checks[$name] = $false
        $property = $cfg.integrations.PSObject.Properties[$name]
        if (-not $property) { $details[$name] = 'Not configured'; continue }
        $jobs += Start-Job -ArgumentList "$PSScriptRoot\Lab.ps1", $name, $property.Value, $ConfigPath, $run.id -ScriptBlock {
            param($library, $name, $spec, $configPath, $runId)
            . $library
            Invoke-CuIntegrationProbe $name $spec $configPath $runId
        }
    }
    $jobs | Wait-Job -Timeout 90 | Out-Null
    foreach ($job in $jobs) {
        if ($job.State -eq 'Completed') {
            $result = Receive-Job $job
            if ($result -and $checks.Contains($result.name)) { $checks[$result.name] = $result.ready -eq $true; $details[$result.name] = $result }
        }
    }
} finally { $jobs | Stop-Job -ErrorAction SilentlyContinue; $jobs | Remove-Job -Force -ErrorAction SilentlyContinue }
$evidencePath = Join-Path $RunDir 'evidence\environment-inspection.json'
Write-CuJson $evidencePath @{ checkedAt = (Get-Date).ToUniversalTime().ToString('o'); details = $details; checks = $checks }
Write-CuJson (Join-Path $RunDir 'preflight.json') @{ binding = $run.binding; checks = $checks; evidence = @('evidence/environment-inspection.json') }
& python "$PSScriptRoot\gate.py" preflight --run $RunDir --input (Join-Path $RunDir 'preflight.json')
if ($LASTEXITCODE -ne 0 -or $checks.Values -contains $false) { exit 1 }
