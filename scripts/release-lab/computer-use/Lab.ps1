# Shared by the host-side preparation and inspection commands. PowerShell 5.1.
Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

function Read-CuConfig([string]$Path) {
    $cfg = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($cfg.schemaVersion -ne 1) { throw 'Unsupported computer-use environment schema.' }
    foreach ($name in @('desktop', 'server')) {
        $target = $cfg.targets.$name
        if ($target.disposable -ne $true -or -not $target.vm -or -not $target.checkpoint -or
            $target.checkpoint -like 'REPLACE*') { throw "Configure a disposable $name VM and its exact checkpoint." }
    }
    if ($cfg.targets.desktop.vm -eq $cfg.targets.server.vm) { throw 'Desktop and server must use separate VMs.' }
    return $cfg
}

function Write-CuJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding $false))
}

function Assert-CuSignature($Config) {
    $publisher = Get-PfxCertificate -FilePath $Config.release.publisherCertificate
    if ($publisher.Subject -ne 'CN=NodePilot Release Signing') { throw 'Unexpected release publisher.' }
    foreach ($path in @($Config.release.desktopInstaller, $Config.release.serverInstaller)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -notin @('Valid', 'UnknownError') -or -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $publisher.Thumbprint) {
            throw "Release installer signature/publisher mismatch: $(Split-Path -Leaf $path)"
        }
    }
}

function Wait-CuGuest([string]$Vm, [pscredential]$Credential) {
    $deadline = (Get-Date).AddMinutes(6)
    do {
        try {
            $name = Invoke-Command -VMName $Vm -Credential $Credential -ScriptBlock { $env:COMPUTERNAME }
            if ($name) { return }
        } catch { }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    throw "PowerShell Direct unavailable for $Vm."
}

function Invoke-CuIntegrationProbe([string]$Name, $Spec, [string]$ConfigPath, [string]$RunId) {
    $started = Get-Date
    try {
        switch ($Spec.kind) {
            'http' { $r = Invoke-WebRequest -UseBasicParsing -Uri $Spec.url -TimeoutSec 12; if ($r.StatusCode -ne 200) { throw 'HTTP not ready' } }
            'proxy' { $r = Invoke-WebRequest -UseBasicParsing -Uri $Spec.url -Proxy $Spec.proxy -TimeoutSec 12; if ($r.StatusCode -ne 200) { throw 'Proxy not ready' } }
            'oidc' {
                $r = Invoke-RestMethod -Uri $Spec.url -TimeoutSec 12
                if (-not $r.issuer -or -not $r.authorization_endpoint -or -not $r.token_endpoint -or -not $r.jwks_uri) { throw 'Incomplete OIDC discovery' }
            }
            'scim' {
                $credential = Import-Clixml -LiteralPath $Spec.credentialPath
                $r = Invoke-RestMethod -Uri $Spec.url -Headers @{ Authorization = "Bearer $($credential.GetNetworkCredential().Password)" } -TimeoutSec 12
                if (-not $r.schemas) { throw 'SCIM ServiceProviderConfig unavailable' }
            }
            'llm' {
                $credential = Import-Clixml -LiteralPath $Spec.credentialPath
                $body = @{ model = $Spec.model; messages = @(@{ role = 'user'; content = 'Reply with the word READY.' }); max_tokens = 12; stream = $false } | ConvertTo-Json -Depth 5
                $r = Invoke-RestMethod -Uri "$($Spec.url.TrimEnd('/'))/chat/completions" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 60 -Headers @{ Authorization = "Bearer $($credential.GetNetworkCredential().Password)" }
                if (-not $r.choices -or -not $r.choices[0].message.content) { throw 'Model did not produce a completion' }
            }
            'winrm' {
                $credential = Import-Clixml -LiteralPath $Spec.credentialPath
                $options = New-PSSessionOption -OpenTimeout 12000 -OperationTimeout 12000
                $r = Invoke-Command -ComputerName $Spec.host -Credential $credential -SessionOption $options -ScriptBlock { $env:COMPUTERNAME }
                if (-not $r) { throw 'WinRM returned no host identity' }
            }
            'sql' {
                $connection = New-Object System.Data.SqlClient.SqlConnection (Get-Content -LiteralPath $Spec.connectionStringPath -Raw)
                try {
                    $connection.Open(); $command = $connection.CreateCommand(); $command.CommandTimeout = 12
                    $command.CommandText = 'SELECT 42'; if ([int]$command.ExecuteScalar() -ne 42) { throw 'SQL probe failed' }
                } finally { $connection.Dispose() }
            }
            'ldap' {
                Add-Type -AssemblyName System.DirectoryServices.Protocols
                $credential = Import-Clixml -LiteralPath $Spec.credentialPath
                $identifier = New-Object System.DirectoryServices.Protocols.LdapDirectoryIdentifier($Spec.host, 636)
                $connection = New-Object System.DirectoryServices.Protocols.LdapConnection($identifier)
                try {
                    $connection.Timeout = [TimeSpan]::FromSeconds(12)
                    $connection.SessionOptions.SecureSocketLayer = $true
                    $connection.Credential = $credential.GetNetworkCredential(); $connection.Bind()
                } finally { $connection.Dispose() }
            }
            'smtp' {
                $client = New-Object Net.Sockets.TcpClient
                try {
                    $connect = $client.ConnectAsync($Spec.host, [int]$Spec.port)
                    if (-not $connect.Wait(12000)) { throw 'SMTP connection timeout' }
                    $stream = $client.GetStream(); $stream.ReadTimeout = 12000; $stream.WriteTimeout = 12000
                    $reader = New-Object IO.StreamReader($stream)
                    $writer = New-Object IO.StreamWriter($stream); $writer.NewLine = "`r`n"; $writer.AutoFlush = $true
                    if ($reader.ReadLine() -notlike '220*') { throw 'Missing SMTP greeting' }
                    $writer.WriteLine('EHLO nodepilot-release-lab')
                    do { $line = $reader.ReadLine(); if ($line -notlike '250*') { throw 'EHLO failed' } } while ($line -like '250-*')
                    $writer.WriteLine('QUIT')
                } finally { $client.Dispose() }
            }
            'script' {
                if (-not (Test-Path -LiteralPath $Spec.probeScript -PathType Leaf)) { throw 'Missing fixture probe' }
                $r = & $Spec.probeScript -ConfigPath $ConfigPath -RunId $RunId
                if ($r.ready -ne $true -or -not $r.observation) { throw 'Fixture probe did not attest readiness' }
            }
            default { throw 'Unknown integration kind' }
        }
        return [pscustomobject]@{ name = $Name; ready = $true; seconds = ((Get-Date) - $started).TotalSeconds }
    } catch {
        # Connection strings, tokens and exception payloads stay out of the release report.
        return [pscustomobject]@{ name = $Name; ready = $false; error = $_.Exception.GetType().Name; seconds = ((Get-Date) - $started).TotalSeconds }
    }
}
