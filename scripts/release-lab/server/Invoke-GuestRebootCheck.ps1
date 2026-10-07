<#
  Runs after the host restarted the VM. The service has to come up on its own - SQL Server starts
  delayed-automatic, so NodePilot boots before its database - become ready, and log no error entry.
  Windows PowerShell 5.1, inside the lab VM.
#>
$ErrorActionPreference = 'Stop'
$lab = Get-Content 'C:\NP-Test\lab.json' -Raw | ConvertFrom-Json
$r = [ordered]@{ id = 'reboot' }

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

$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
$deadline = (Get-Date).AddMinutes(5)
$health = 0
while ((Get-Date) -lt $deadline) {
    $health = Get-Ready
    if ($health -eq 200) { break }
    Start-Sleep 5
}
$r.healthAfterBoot = $health
$r.secondsFromBoot = [int]((Get-Date) - $boot).TotalSeconds
$r.service = (Get-Service NodePilot).Status.ToString()
$r.startMode = (Get-CimInstance Win32_Service -Filter "Name='NodePilot'").StartMode

$mark = @{}
(Get-Content 'C:\NP-Test\logmark.json' -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $mark[$_.Name] = [long]$_.Value }
$errorLines = @()
Get-ChildItem 'C:\ProgramData\NodePilot\logs\nodepilot-*.log' | Where-Object Name -notlike '*support*' | ForEach-Object {
    $start = if ($mark.ContainsKey($_.FullName)) { $mark[$_.FullName] } else { 0 }
    $fs = [IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
    try { [void]$fs.Seek($start, 'Begin'); $text = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    $errorLines += @($text -split "`n" | Where-Object { $_ -match 'type="3"' })
}
$errors = $errorLines.Count
$r.errorsSinceRestart = $errors
# The message and component of each error, so a failure can be read from the result alone.
$r.errorSamples = @($errorLines | Select-Object -First 5 | ForEach-Object {
    $m = [regex]::Match($_, '^<!\[LOG\[(.*?)\]LOG\]!>.*?component="([^"]*)"')
    "[$($m.Groups[2].Value)] " + $m.Groups[1].Value.Substring(0, [Math]::Min(400, $m.Groups[1].Value.Length))
})
$r.verdict = if ($health -eq 200 -and $r.service -eq 'Running' -and $errors -eq 0) { 'PASS' } else { 'FAIL' }
return [pscustomobject]$r
