<#
  Helpers for the desktop-setup scenarios, dot-sourced inside the lab VM. Windows PowerShell 5.1.
#>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Continue'

$global:NpPass = 0
$global:NpFail = 0
$InstallDir = 'C:\Program Files\NodePilot'
$DataDir = 'C:\ProgramData\NodePilot'

function Log([string] $m) { Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $m) }

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $global:NpPass++; Log "  PASS $name $detail" } else { $global:NpFail++; Log "  FAIL $name $detail" }
}

function Get-Origin {
    try { return (Get-Content -LiteralPath "$DataDir\desktop.json" -Raw -ErrorAction Stop | ConvertFrom-Json).origin } catch { return $null }
}

function Get-ReadyCode {
    $origin = Get-Origin
    if (-not $origin) { return 'no-origin' }
    return "$(& curl.exe -sk --http1.1 -m 5 -o NUL -w '%{http_code}' "$origin/healthz/ready" 2>$null)"
}

function Get-ServiceState([string] $name) {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
    if ($svc) { return $svc.State } else { return 'absent' }
}

function Invoke-Setup([string] $exe, [string] $extraArgs, [string] $logName) {
    $log = "C:\np-lab\$logName"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`" $extraArgs" -PassThru
    if (-not $p.WaitForExit(1500000)) { Log "  setup still running after 25 min - killing"; Stop-Process -Id $p.Id -Force }
    $code = $p.ExitCode
    Log ("  setup {0}: exit={1} in {2:N0}s" -f $logName, $code, $sw.Elapsed.TotalSeconds)
    $prov = Join-Path $env:TEMP 'nodepilot-provision.log'
    if (Test-Path $prov) {
        $starts = @(Select-String -LiteralPath $prov -Pattern 'stopped while starting').Count
        Log "  provision: API restarts during start-up = $starts"
    }
    return $code
}

function Invoke-Uninstall([string] $extraArgs, [string] $logName) {
    $unins = "$InstallDir\unins000.exe"
    if (-not (Test-Path $unins)) { Log '  uninstaller missing'; return }
    $log = "C:\np-lab\$logName"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath $unins -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`" $extraArgs" -Wait
    Start-Sleep -Seconds 2
    $deadline = (Get-Date).AddMinutes(15)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins0*' }).Count -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    Log ("  uninstall {0}: done in {1:N0}s" -f $logName, $sw.Elapsed.TotalSeconds)
    $ul = Join-Path $env:TEMP 'nodepilot-uninstall.log'
    if (Test-Path $ul) { Log '  uninstall log present (problems reported):'; Get-Content $ul -Tail 15 | ForEach-Object { Log "    $_" } }
}

function Save-CertKeyName {
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq 'NodePilot Desktop Local' | Select-Object -First 1
    if (-not $cert) { return }
    try {
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        $unique = if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.UniqueName } else { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
        Set-Content -Path C:\np-lab\certkey.txt -Value $unique
    } catch { Log "  could not read cert key name: $($_.Exception.Message)" }
}

function Test-CertKeyGone {
    if (-not (Test-Path C:\np-lab\certkey.txt)) { return $true }
    $unique = (Get-Content C:\np-lab\certkey.txt -Raw).Trim()
    $hits = @(Get-ChildItem 'C:\ProgramData\Microsoft\Crypto' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object Name -eq $unique)
    return $hits.Count -eq 0
}

function Get-PerUserNodePilotDirs {
    $found = @()
    foreach ($u in Get-ChildItem C:\Users -Directory -Force -ErrorAction SilentlyContinue) {
        foreach ($rel in 'AppData\Roaming\NodePilot', 'AppData\Local\NodePilot') {
            $x = Join-Path $u.FullName $rel
            if (Test-Path -LiteralPath $x) { $found += $x }
        }
    }
    return $found
}

function Get-Handoff { return (Join-Path $env:LOCALAPPDATA 'NodePilot\admin-setup.handoff') }

function Invoke-Login([string] $user, [string] $password, [switch] $UseSetupToken) {
    $origin = Get-Origin
    $body = Join-Path $env:TEMP 'np-login.json'
    [IO.File]::WriteAllText($body, (@{ username = $user; password = $password } | ConvertTo-Json -Compress))
    $hdr = @('-H', 'Content-Type: application/json')
    if ($UseSetupToken) {
        $token = ([IO.File]::ReadAllText((Get-Handoff))).Trim()
        $hdr += @('-H', "X-Setup-Token: $token")
    }
    return "$(& curl.exe -sk --http1.1 -m 30 -o NUL -w '%{http_code}' @hdr --data-binary "@$body" "$origin/api/auth/login" 2>$null)"
}

function Wait-Ready([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-ReadyCode) -eq '200') { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Assert-Installed([string] $label) {
    Log "--- verify installed: $label"
    Check 'API service running' ((Get-ServiceState 'NodePilot') -eq 'Running') (Get-ServiceState 'NodePilot')
    Check 'DB service running' ((Get-ServiceState 'NodePilotDb') -eq 'Running') (Get-ServiceState 'NodePilotDb')
    Check 'healthz/ready 200' ((Get-ReadyCode) -eq '200') (Get-ReadyCode)
    Check 'certificate present' (@(Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq 'NodePilot Desktop Local').Count -eq 1)
    Check 'no start-up log under System32' (-not (Test-Path 'C:\Windows\System32\logs\nodepilot-*.log'))
}

function Get-LogMark {
    $map = @{}
    Get-ChildItem "$DataDir\logs\nodepilot-*.log" -ErrorAction SilentlyContinue |
        Where-Object Name -notlike '*support*' | ForEach-Object { $map[$_.FullName] = $_.Length }
    return $map
}

# Error entries (CMTrace type="3") written since the mark was taken.
function Get-NewErrorLines([hashtable] $mark) {
    $lines = @()
    Get-ChildItem "$DataDir\logs\nodepilot-*.log" -ErrorAction SilentlyContinue |
        Where-Object Name -notlike '*support*' | ForEach-Object {
            $start = if ($mark.ContainsKey($_.FullName)) { $mark[$_.FullName] } else { 0 }
            $fs = [IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
            try { [void]$fs.Seek($start, 'Begin'); $text = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
            $lines += @($text -split "`r?`n" | Where-Object { $_ -match 'type="3"' })
        }
    return $lines
}

function Check-NoNewErrors([string] $label, [hashtable] $mark) {
    $errors = @(Get-NewErrorLines $mark)
    $first = if ($errors.Count) { ($errors[0] -replace '\]LOG\]!>.*', '' -replace '^<!\[LOG\[', '').Substring(0, [Math]::Min(300, ($errors[0] -replace '\]LOG\]!>.*', '' -replace '^<!\[LOG\[', '').Length)) } else { '' }
    Check "$label no error entries in the service log" ($errors.Count -eq 0) "count=$($errors.Count) $first"
}

# Signs in, imports the smoke workflow, enables and runs it; returns the final status.
function Invoke-SmokeWorkflow([string] $user, [string] $password) {
    $origin = Get-Origin
    $jar = Join-Path $env:TEMP 'np-cookies.txt'
    if (Test-Path $jar) { Remove-Item $jar -Force }
    $body = Join-Path $env:TEMP 'np-login.json'
    [IO.File]::WriteAllText($body, (@{ username = $user; password = $password } | ConvertTo-Json -Compress))
    & curl.exe -sk --http1.1 -m 30 -c $jar -b $jar -o NUL -H 'Content-Type: application/json' --data-binary "@$body" "$origin/api/auth/login" 2>$null | Out-Null
    $csrf = [Uri]::UnescapeDataString(((Get-Content $jar | Where-Object { $_ -match "`tnp_csrf`t" } | Select-Object -Last 1) -split "`t")[-1])
    $h = @('-H', 'Content-Type: application/json', '-H', "X-CSRF-Token: $csrf")
    $import = (& curl.exe -sk --http1.1 -m 30 -c $jar -b $jar @h --data-binary '@C:\np-lab\smoke-workflow.json' "$origin/api/workflows/import" 2>$null) | Out-String | ConvertFrom-Json
    $wfId = @($import.workflows)[0].id
    & curl.exe -sk --http1.1 -m 30 -c $jar -b $jar @h -X POST "$origin/api/workflows/$wfId/enable" 2>$null | Out-Null
    [IO.File]::WriteAllText($body, '{"parameters":{}}')
    $exec = (& curl.exe -sk --http1.1 -m 30 -c $jar -b $jar @h --data-binary "@$body" "$origin/api/workflows/$wfId/execute" 2>$null) | Out-String | ConvertFrom-Json
    # StrictMode: probe the property rather than read a missing one.
    $execId = if ($exec.PSObject.Properties['executionId']) { $exec.executionId } else { $exec.id }
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $status = ((& curl.exe -sk --http1.1 -m 30 -c $jar -b $jar "$origin/api/executions/$execId" 2>$null) | Out-String | ConvertFrom-Json).status
        if ($status -in 'Succeeded', 'Failed', 'Cancelled') { return $status }
    }
    return "timeout ($status)"
}

function Assert-ProgramRemoved([string] $label) {
    Log "--- verify program removed: $label"
    Check 'API service gone' ((Get-ServiceState 'NodePilot') -eq 'absent')
    Check 'DB service gone' ((Get-ServiceState 'NodePilotDb') -eq 'absent')
    Check 'certificate gone' (@(Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq 'NodePilot Desktop Local').Count -eq 0)
    Check 'certificate private key gone' (Test-CertKeyGone)
    $left = if (Test-Path $InstallDir) { (@(Get-ChildItem $InstallDir -Recurse -Force) | ForEach-Object FullName) -join '; ' } else { '' }
    Check 'Program Files\NodePilot gone' (-not (Test-Path $InstallDir)) $left
    $arp = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'NodePilot*' })
    Check 'Apps & Features entry gone' ($arp.Count -eq 0)
    $procs = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "$InstallDir\*" })
    Check 'no NodePilot process left' ($procs.Count -eq 0) (($procs | ForEach-Object Name) -join ',')
    Check 'no setup handoff left' (-not (Test-Path (Get-Handoff)))
}
