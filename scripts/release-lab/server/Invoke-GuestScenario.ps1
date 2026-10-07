<#
  Runs one server-setup scenario inside the lab VM and returns a result object.
  The host copies this script, lab.json, scenario.json, the setups and the smoke workflow to
  C:\NP-Test first (see Invoke-ServerMatrix.ps1). Windows PowerShell 5.1.
#>
$ErrorActionPreference = 'Stop'
$work = 'C:\NP-Test'
$lab = Get-Content (Join-Path $work 'lab.json') -Raw | ConvertFrom-Json
$scenario = Get-Content (Join-Path $work 'scenario.json') -Raw | ConvertFrom-Json
$pg = $lab.postgres
$installDir = 'C:\Program Files\NodePilot'
$dataDir = 'C:\ProgramData\NodePilot'
$result = [ordered]@{ id = $scenario.id }

function Invoke-Exe([string]$File, [string]$Arguments) {
    # Start-Process -PassThru reports no exit code inside a PowerShell Direct session.
    $psi = New-Object System.Diagnostics.ProcessStartInfo $File, $Arguments
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.WaitForExit()
    return $p.ExitCode
}

function Save-SetupLog([string]$Name) {
    $src = Join-Path $env:TEMP 'nodepilot-server-setup.log'
    if (Test-Path $src) { Copy-Item $src (Join-Path $work "$($scenario.id)-$Name-adapter.log") -Force }
}

function Invoke-Psql([string]$Sql) {
    $env:PGPASSWORD = $pg.superPassword
    try {
        $conn = "host=$($lab.fqdn) port=5432 dbname=postgres user=$($pg.superUser) sslmode=verify-full sslrootcert=$($pg.rootCertificate -replace '\\', '/')"
        return (& $pg.psql -tA -v ON_ERROR_STOP=1 -c $Sql $conn)
    }
    finally { $env:PGPASSWORD = $null }
}

function Invoke-LocalSql([string]$Sql) {
    # The scenario's SQL Server: this VM, or the remote one for the computer-account scenario.
    $server = if ($scenario.sqlServer) { $scenario.sqlServer } else { 'localhost' }
    $c = New-Object System.Data.SqlClient.SqlConnection "Server=$server;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
    $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; return $cmd.ExecuteScalar() }
    finally { $c.Close() }
}

function Get-LogMark {
    $map = @{}
    Get-ChildItem "$dataDir\logs\nodepilot-*.log" -ErrorAction SilentlyContinue |
        Where-Object Name -notlike '*support*' | ForEach-Object { $map[$_.FullName] = $_.Length }
    return $map
}

function Get-LogLinesSince([hashtable]$Mark) {
    $lines = @()
    Get-ChildItem "$dataDir\logs\nodepilot-*.log" -ErrorAction SilentlyContinue |
        Where-Object Name -notlike '*support*' | ForEach-Object {
            $start = if ($Mark.ContainsKey($_.FullName)) { $Mark[$_.FullName] } else { 0 }
            $fs = [IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
            try { [void]$fs.Seek($start, 'Begin'); $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() }
            finally { $fs.Dispose() }
            $lines += @($text -split "`r?`n" | Where-Object { $_ })
        }
    return $lines
}

# curl (Schannel) and Invoke-WebRequest fail against Kestrel on some lab VMs while a raw SslStream
# works, so requests are written by hand: HTTP/1.1, Connection: close, chunked bodies decoded.
$script:cookies = @{}
function Invoke-Http([string]$Method, [string]$Path, [string]$Body) {
    $tcp = New-Object Net.Sockets.TcpClient('localhost', [int]$lab.httpsPort)
    try {
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { $true })
        $ssl.AuthenticateAsClient('localhost', $null, [Security.Authentication.SslProtocols]'Tls12', $false)
        $bytes = if ($Body) { [Text.Encoding]::UTF8.GetBytes($Body) } else { [byte[]]@() }
        $head = "$Method $Path HTTP/1.1`r`nHost: localhost`r`nConnection: close`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`n"
        if ($script:cookies.Count) { $head += 'Cookie: ' + (($script:cookies.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ') + "`r`n" }
        if ($Method -ne 'GET' -and $script:cookies['np_csrf']) { $head += "X-CSRF-Token: $([Uri]::UnescapeDataString($script:cookies['np_csrf']))`r`n" }
        $h = [Text.Encoding]::ASCII.GetBytes("$head`r`n")
        $ssl.Write($h); if ($bytes.Length) { $ssl.Write($bytes) }; $ssl.Flush()
        $ms = New-Object IO.MemoryStream; $ssl.CopyTo($ms)
        $raw = [Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    finally { $tcp.Close() }
    $split = $raw.IndexOf("`r`n`r`n")
    $headers = $raw.Substring(0, $split) -split "`r`n"
    $content = $raw.Substring($split + 4)
    foreach ($line in $headers) {
        if ($line -match '^Set-Cookie:\s*([^=]+)=([^;]*)') { $script:cookies[$Matches[1]] = $Matches[2] }
    }
    if ($headers -match '^Transfer-Encoding:\s*chunked') {
        $sb = New-Object Text.StringBuilder; $pos = 0
        while ($true) {
            $eol = $content.IndexOf("`r`n", $pos)
            $size = [Convert]::ToInt32($content.Substring($pos, $eol - $pos).Split(';')[0], 16)
            if ($size -eq 0) { break }
            [void]$sb.Append($content.Substring($eol + 2, $size)); $pos = $eol + 2 + $size + 2
        }
        $content = $sb.ToString()
    }
    return [pscustomobject]@{ Status = [int]($headers[0] -split ' ')[1]; Body = $content }
}

function Invoke-Api([string]$Method, [string]$Path, [string]$Body) {
    $r = Invoke-Http $Method $Path $Body
    if ($r.Body) { try { return ($r.Body | ConvertFrom-Json) } catch { return $r } }
}

function Test-DatabaseExists {
    if ($scenario.database -eq 'sqlserver') {
        $name = if ($scenario.sqlDatabase) { $scenario.sqlDatabase } else { $lab.sqlDatabase }
        return [int](Invoke-LocalSql "SELECT COUNT(*) FROM sys.databases WHERE name = '$name'") -eq 1
    }
    return ([string](Invoke-Psql "SELECT count(*) FROM pg_database WHERE datname = '$($pg.database)'")).Trim() -eq '1'
}

# --- previous release first (update scenarios) ------------------------------------------------
if ($scenario.upgrade) {
    if ($lab.previous.preinstallRuntimes) {
        foreach ($rt in Get-ChildItem $work -Filter '*-runtime-*-win-x64.exe' | Sort-Object Name -Descending) {
            $code = Invoke-Exe $rt.FullName '/install /quiet /norestart'
            if ($code -ne 0 -and $code -ne 3010) { throw "$($rt.Name) exit $code" }
        }
    }
    if ($scenario.database -eq 'postgres' -and -not $lab.previous.canProvisionPostgres) {
        Invoke-Psql "CREATE ROLE $($pg.role) WITH LOGIN PASSWORD '$($pg.rolePassword)'" | Out-Null
        Invoke-Psql "CREATE DATABASE $($pg.database) OWNER $($pg.role)" | Out-Null
    }
    if ($scenario.database -eq 'sqlserver' -and $scenario.identity -eq 'localSystem' -and $lab.previous.preGrantSystemForLocalSql) {
        # Releases before 1.4.1 granted the computer account, which a local SQL Server never sees.
        Invoke-LocalSql "CREATE DATABASE [$($lab.sqlDatabase)]" | Out-Null
        Invoke-LocalSql "USE [$($lab.sqlDatabase)]; CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM]; ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\SYSTEM];" | Out-Null
    }
    $result.previousExit = Invoke-Exe (Join-Path $work 'previous-server-setup.exe') `
        "/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=$work\answers-previous.json /LOG=$work\$($scenario.id)-inno-previous.log"
    Save-SetupLog 'previous'
    if ($result.previousExit -ne 0) { $result.verdict = 'FAIL (previous release did not install)'; return [pscustomobject]$result }
    $result.previousVersion = (Get-Item "$installDir\NodePilot.Api.exe").VersionInfo.FileVersion
    Start-Sleep 20
}

# --- the release under test -------------------------------------------------------------------
$mark = Get-LogMark
$answer = if ($scenario.upgrade) { 'update.json' } else { 'answers.json' }
$t0 = Get-Date
$result.setupExit = Invoke-Exe (Join-Path $work 'server-setup.exe') "/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=$work\$answer /LOG=$work\$($scenario.id)-inno.log"
$result.setupSeconds = [int]((Get-Date) - $t0).TotalSeconds
$tDone = Get-Date
Save-SetupLog 'setup'
$svc = Get-CimInstance Win32_Service -Filter "Name='NodePilot'"
$result.service = if ($svc) { "$($svc.State) as $($svc.StartName)" } else { 'missing' }
$result.version = (Get-Item "$installDir\NodePilot.Api.exe" -ErrorAction SilentlyContinue).VersionInfo.FileVersion
if ($result.setupExit -ne 0) { $result.verdict = 'FAIL (setup exit)'; return [pscustomobject]$result }

# --- health and one execution through the dispatch path ---------------------------------------
$workflowFile = 'smoke-workflow.json'
if ($scenario.check -eq 'allSigned') {
    # A machine policy as a GPO sets it. Every local PowerShell path still has to run.
    $policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\PowerShell'
    New-Item -Path $policyKey -Force | Out-Null
    Set-ItemProperty -Path $policyKey -Name EnableScripts -Value 1 -Type DWord
    Set-ItemProperty -Path $policyKey -Name ExecutionPolicy -Value 'AllSigned' -Type String
    # Read in a fresh process: this one cached the policy when it started.
    $result.machinePolicy = "$(& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command 'Get-ExecutionPolicy -Scope MachinePolicy')".Trim()
    $workflowFile = 'allsigned-workflow.json'
}
$result.healthz = (Invoke-Http GET '/healthz/ready' $null).Status
$credFile = "$dataDir\bootstrap-admin.json"
$admin = $null
if (Test-Path $credFile) {
    $admin = Get-Content $credFile -Raw | ConvertFrom-Json
    $result.login = (Invoke-Http POST '/api/auth/login' (@{ username = $admin.username; password = $admin.password } | ConvertTo-Json -Compress)).Status
    $import = Invoke-Api POST '/api/workflows/import' (Get-Content (Join-Path $work $workflowFile) -Raw -Encoding UTF8)
    $wfId = @($import.workflows)[0].id
    $result.enable = (Invoke-Http POST "/api/workflows/$wfId/enable" $null).Status
    $exec = Invoke-Api POST "/api/workflows/$wfId/execute" '{"parameters":{}}'
    $execId = if ($exec.executionId) { $exec.executionId } else { $exec.id }
    $status = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep 2
        $status = (Invoke-Api GET "/api/executions/$execId" $null).status
        if ($status -in 'Succeeded', 'Failed', 'Cancelled') { break }
    }
    $result.execution = $status
    if ($status -ne 'Succeeded') {
        $result.failedSteps = @(@(Invoke-Api GET "/api/executions/$execId/steps" $null) | Where-Object { $_.status -ne 'Succeeded' } |
            ForEach-Object { "$($_.stepId)=$($_.status): $("$($_.errorOutput)".Substring(0, [Math]::Min(300, "$($_.errorOutput)".Length)))" })
    }
}
else { $result.login = 'no bootstrap-admin.json' }

# --- observation window: no error entry in the service log ------------------------------------
$remaining = [int]$scenario.soakSeconds - [int]((Get-Date) - $tDone).TotalSeconds
if ($remaining -gt 0) { Start-Sleep $remaining }
$lines = @(Get-LogLinesSince $mark)
if ($scenario.upgrade) {
    # The previous release logs until the update stops it; count from the new service's start line.
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'NodePilot\.Api started') { $start = $i } }
    if ($start -gt 0) {
        $result.errorsBeforeNewStart = @($lines[0..($start - 1)] | Where-Object { $_ -match 'type="3"' }).Count
        $lines = $lines[$start..($lines.Count - 1)]
    }
}
$errors = @($lines | Where-Object { $_ -match 'type="3"' })
$result.errors = $errors.Count
$result.warnings = @($lines | Where-Object { $_ -match 'type="2"' }).Count
$errors | Set-Content (Join-Path $work "$($scenario.id)-errors.log") -Encoding utf8

$ok = $result.healthz -eq 200 -and $result.execution -eq 'Succeeded' -and $result.errors -eq 0
if ($scenario.check -eq 'allSigned' -and $result.machinePolicy -ne 'AllSigned') { $ok = $false }

# --- a second installation over the first one with another identity ---------------------------
if ($scenario.secondIdentity) {
    $mark2 = Get-LogMark
    $t2 = Get-Date
    $result.secondExit = Invoke-Exe (Join-Path $work 'server-setup.exe') `
        "/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=$work\answers-second.json /LOG=$work\$($scenario.id)-inno-second.log"
    Save-SetupLog 'second'
    $svc2 = Get-CimInstance Win32_Service -Filter "Name='NodePilot'"
    $result.secondService = if ($svc2) { "$($svc2.State) as $($svc2.StartName)" } else { 'missing' }
    $script:cookies = @{}
    $result.secondHealthz = (Invoke-Http GET '/healthz/ready' $null).Status
    if ($admin) {
        $result.secondLogin = (Invoke-Http POST '/api/auth/login' (@{ username = $admin.username; password = $admin.password } | ConvertTo-Json -Compress)).Status
    }
    # The signing key moves to the new identity; the old owner would lock the new service out.
    $result.secondKeyOwner = (Get-Acl "$dataDir\jwt-secret.key").Owner
    $wait = [int]$scenario.soakSeconds - [int]((Get-Date) - $t2).TotalSeconds
    if ($wait -gt 0) { Start-Sleep $wait }
    $result.secondErrors = @(Get-LogLinesSince $mark2 | Where-Object { $_ -match 'type="3"' }).Count
    $expected = $scenario.secondIdentity
    if ($result.secondExit -ne 0 -or $result.secondHealthz -ne 200 -or $result.secondLogin -ne 200 -or $result.secondErrors -ne 0 -or
        $svc2.StartName -ne $expected -or $result.secondKeyOwner -ne $expected) { $ok = $false }
}

# The reboot check reads this to count only what the service logs after the restart.
Get-LogMark | ConvertTo-Json | Set-Content (Join-Path $work 'logmark.json') -Encoding utf8

# --- uninstall through the setup's uninstaller, optionally install again ----------------------
if ($scenario.uninstall) {
    $checks = [ordered]@{}
    $unins = "$installDir\unins000.exe"
    $checks.uninstallerPresent = Test-Path $unins
    if ($checks.uninstallerPresent) {
        $uninsArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=$work\$($scenario.id)-uninstall.log"
        if ($scenario.uninstall -eq 'purge') { $uninsArgs += ' /PURGEDATA=1' }
        Start-Process -FilePath $unins -ArgumentList $uninsArgs -Wait
        # unins000 relaunches itself from %TEMP% and returns at once.
        $deadline = (Get-Date).AddMinutes(10)
        while ((Get-Date) -lt $deadline -and @(Get-Process | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins0*' }).Count -gt 0) { Start-Sleep 2 }
    }
    $checks.serviceGone = -not (Get-Service NodePilot -ErrorAction SilentlyContinue)
    $checks.programDirGone = -not (Test-Path $installDir)
    $checks.markerGone = -not (Test-Path 'HKLM:\SOFTWARE\NodePilot\Server')
    $checks.appsEntryGone = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'NodePilot*' }).Count -eq 0
    $checks.firewallRulesGone = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like '*NodePilot*' -and $_.DisplayName -notlike '*NodePilot Lab*' }).Count -eq 0
    $checks.dataDir = if ($scenario.uninstall -eq 'purge') { -not (Test-Path $dataDir) } else { Test-Path "$dataDir\jwt-secret.key" }
    $checks.databaseKept = Test-DatabaseExists
    $result.uninstall = $scenario.uninstall
    $result.uninstallFailed = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key) -join ','
    if ($result.uninstallFailed) { $ok = $false }

    if ($scenario.reinstallAfter -and $admin) {
        # The same answer file over the kept data and database: no new admin, the old one signs in.
        $result.reinstallExit = Invoke-Exe (Join-Path $work 'server-setup.exe') `
            "/VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=$work\answers.json /LOG=$work\$($scenario.id)-inno-reinstall.log"
        Save-SetupLog 'reinstall'
        $script:cookies = @{}
        $result.reinstallHealthz = (Invoke-Http GET '/healthz/ready' $null).Status
        $result.reinstallLogin = (Invoke-Http POST '/api/auth/login' (@{ username = $admin.username; password = $admin.password } | ConvertTo-Json -Compress)).Status
        $list = Invoke-Api GET '/api/workflows' $null
        $items = if ($list.PSObject.Properties['items']) { @($list.items) } else { @($list) }
        $result.reinstallWorkflowKept = [bool]($items | Where-Object { $_.name -eq 'Install Smoke' })
        if ($result.reinstallExit -ne 0 -or $result.reinstallHealthz -ne 200 -or $result.reinstallLogin -ne 200 -or -not $result.reinstallWorkflowKept) { $ok = $false }
    }
}

$result.verdict = if ($ok) { 'PASS' } else { 'FAIL' }
return [pscustomobject]$result
