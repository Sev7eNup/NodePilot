<#
  Starts an installed NodePilot while its local SQL Server is stopped, then brings the database
  back. The service has to report running at once, name the connection error while it waits, and
  become ready on its own. Windows PowerShell 5.1, run inside the lab VM after a scenario install.
#>
$ErrorActionPreference = 'Stop'
$lab = Get-Content 'C:\NP-Test\lab.json' -Raw | ConvertFrom-Json
$r = [ordered]@{ id = 'dbOutage' }

function Get-Ready {
    try {
        $tcp = New-Object Net.Sockets.TcpClient('localhost', [int]$lab.httpsPort)
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { $true })
        $ssl.AuthenticateAsClient('localhost', $null, [Security.Authentication.SslProtocols]'Tls12', $false)
        $req = [Text.Encoding]::ASCII.GetBytes("GET /healthz/ready HTTP/1.1`r`nHost: localhost`r`nConnection: close`r`n`r`n")
        $ssl.Write($req); $buf = New-Object byte[] 256; $n = $ssl.Read($buf, 0, 256); $tcp.Close()
        return [int](([Text.Encoding]::ASCII.GetString($buf, 0, $n) -split ' ')[1])
    }
    catch { return 0 }
}

Stop-Service NodePilot -Force
Stop-Service MSSQLSERVER -Force
$logFile = (Get-ChildItem 'C:\ProgramData\NodePilot\logs\nodepilot-2*.log' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
$offset = (Get-Item $logFile).Length

$t0 = Get-Date
try { Start-Service NodePilot -ErrorAction Stop; $r.startService = 'ok' } catch { $r.startService = "FAILED: $($_.Exception.Message)" }
$r.startSeconds = [int]((Get-Date) - $t0).TotalSeconds
$r.stateWhileDbDown = (Get-Service NodePilot).Status.ToString()
Start-Sleep 45
$r.stateAfter45s = (Get-Service NodePilot).Status.ToString()

function Read-Since {
    $fs = [IO.File]::Open($logFile, 'Open', 'Read', 'ReadWrite'); [void]$fs.Seek($offset, 'Begin')
    try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
}
$wait = @((Read-Since) -split "`n" | Where-Object { $_ -match 'Waiting for the database' } | Select-Object -Last 1)
$r.waitLineNamesReason = [bool]($wait -and ($wait[0] -match 'Waiting for the database to accept connections \([^)]*\): \S'))

Start-Service MSSQLSERVER
$deadline = (Get-Date).AddSeconds(120); $health = 0
while ((Get-Date) -lt $deadline) { $health = Get-Ready; if ($health -eq 200) { break }; Start-Sleep 3 }
$r.healthAfterDbBack = $health
$r.stateFinal = (Get-Service NodePilot).Status.ToString()
$r.errorsLogged = @((Read-Since) -split "`n" | Where-Object { $_ -match 'type="3"' }).Count

$ok = $r.startService -eq 'ok' -and $r.startSeconds -lt 30 -and $r.stateAfter45s -eq 'Running' -and
      $r.waitLineNamesReason -and $r.healthAfterDbBack -eq 200 -and $r.errorsLogged -eq 0
$r.verdict = if ($ok) { 'PASS' } else { 'FAIL' }
return [pscustomobject]$r
