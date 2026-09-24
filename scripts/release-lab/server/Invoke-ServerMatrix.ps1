<#
.SYNOPSIS
  Runs the server-setup release matrix on the Hyper-V lab and writes a summary.

.DESCRIPTION
  Every scenario starts from the configured clean checkpoint, installs the release's server setup
  unattended (or the previous release first, then updates), checks health, runs a workflow through
  the dispatch path, watches the service log for error entries, and uninstalls through the setup's
  uninstaller. See ..\README.md for the matrix, the lab prerequisites and the pass criteria.

.EXAMPLE
  .\Invoke-ServerMatrix.ps1 -ConfigPath C:\lab-cred\release-lab.json -ArtifactDir ..\..\..\out -Version 1.4.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$ArtifactDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$ResultsDir = (Join-Path $env:TEMP "nodepilot-release-lab\$Version\server"),
    [string[]]$Only = @(),
    [int]$SoakSeconds = 120
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$srv = $config.server
$cred = Import-Clixml $config.credentialPath
$vm = $srv.vm
$setup = Join-Path $ArtifactDir "NodePilot-Server-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Server setup not found: $setup" }
# The runtimes the build fetched; the previous release may need them installed beforehand.
$payload = Join-Path (Split-Path $here -Parent | Split-Path -Parent | Split-Path -Parent) 'deploy\server\stage\payload'
New-Item -ItemType Directory -Force $ResultsDir | Out-Null

# id, identity, database, path, uninstall mode, extras
$scenarios = @(
    @{ id = 'F1'; identity = 'gmsa';        database = 'sqlserver'; upgrade = $false; uninstall = 'keep';  reinstallAfter = $true },
    @{ id = 'F2'; identity = 'localSystem'; database = 'sqlserver'; upgrade = $false; uninstall = 'purge' },
    @{ id = 'F3'; identity = 'gmsa';        database = 'postgres';  upgrade = $false; uninstall = 'keep' },
    @{ id = 'F4'; identity = 'localSystem'; database = 'postgres';  upgrade = $false; uninstall = 'purge'; omitPort = $true },
    @{ id = 'U1'; identity = 'gmsa';        database = 'sqlserver'; upgrade = $true;  uninstall = 'purge' },
    @{ id = 'U2'; identity = 'localSystem'; database = 'sqlserver'; upgrade = $true;  uninstall = 'keep' },
    @{ id = 'U3'; identity = 'gmsa';        database = 'postgres';  upgrade = $true;  uninstall = 'purge' },
    @{ id = 'U4'; identity = 'localSystem'; database = 'postgres';  upgrade = $true;  uninstall = 'keep' },
    @{ id = 'I1'; identity = 'localSystem'; database = 'sqlserver'; upgrade = $false; secondIdentity = 'gmsa' },
    @{ id = 'S1'; identity = 'localSystem'; database = 'sqlserver'; upgrade = $false; remoteSql = $true; uninstall = 'purge' },
    @{ id = 'R1'; identity = 'gmsa';        database = 'sqlserver'; upgrade = $false; check = 'reboot' },
    @{ id = 'A';  identity = 'gmsa';        database = 'sqlserver'; upgrade = $false; check = 'dbOutage' },
    @{ id = 'B';  identity = 'gmsa';        database = 'postgres';  upgrade = $false; check = 'crlMissing' }
)
# -Only arrives as one comma-separated string when the script is started with -File.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Only) { $scenarios = @($scenarios | Where-Object { $Only -contains $_.id }) }

function Write-Json([string]$Path, $Object) {
    [IO.File]::WriteAllText($Path, ($Object | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
}

function New-Answers([hashtable]$S, [bool]$ForPrevious, [string]$Identity = $S.identity) {
    $identityBlock = if ($Identity -eq 'gmsa') { [ordered]@{ type = 'gmsa'; account = $srv.gmsaAccount } } else { [ordered]@{ type = 'localSystem' } }
    $provisioning = [ordered]@{ createDatabaseAndLogin = $true }
    if ($S.database -eq 'sqlserver') {
        $sqlHost = if ($S.remoteSql) { $config.remoteSql.fqdn } else { $srv.fqdn }
        $sqlDb = if ($S.remoteSql) { $config.remoteSql.database } else { $srv.sqlDatabase }
        $db = [ordered]@{ provider = 'sqlserver'; sqlServer = $sqlHost; sqlDatabase = $sqlDb; sqlCertificateHostName = $sqlHost }
    }
    else {
        $p = $srv.postgres
        $db = [ordered]@{ provider = 'postgres'; postgresHost = $srv.fqdn; postgresDatabase = $p.database
                          postgresUser = $p.role; postgresPassword = $p.rolePassword; postgresRootCertificate = $p.rootCertificate }
        if (-not ($S.omitPort -and -not $ForPrevious)) { $db['postgresPort'] = 5432 }
        if ($ForPrevious -and -not $config.previousRelease.canInstallPostgres) { $provisioning = [ordered]@{} }
        else { $provisioning['postgresSuperUser'] = $p.superUser; $provisioning['postgresSuperPassword'] = $p.superPassword }
    }
    if (-not $ForPrevious) { $provisioning['installDotnetRuntime'] = $true }
    $answers = [ordered]@{
        schemaVersion = 1; mode = 'install'
        installPath = 'C:\Program Files\NodePilot'; dataPath = 'C:\ProgramData\NodePilot'
        serviceName = 'NodePilot'; includeSourceSnapshot = $false
        identity = $identityBlock; database = $db
        network = [ordered]@{ publicHostname = $srv.fqdn; httpsPort = [int]$srv.httpsPort; httpPort = 0; allowedHosts = "$($srv.fqdn);$($srv.fqdn.Split('.')[0]);localhost" }
        certificate = [ordered]@{ thumbprint = $srv.certificateThumbprint; source = 'existing' }
        bootstrap = [ordered]@{ adminUsername = 'npadmin' }
    }
    if ($provisioning.Count -gt 0) { $answers['provisioning'] = $provisioning }
    return $answers
}

function Restore-CleanVm {
    Stop-VM -Name $vm -TurnOff -Force
    Restore-VMCheckpoint -VMSnapshot (Get-VMCheckpoint -VMName $vm -Name $srv.checkpoint) -Confirm:$false
    Start-VM -Name $vm
    $deadline = (Get-Date).AddMinutes(6)
    do {
        Start-Sleep 10
        $up = try { Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop } catch { $null }
    } until ($up -or (Get-Date) -gt $deadline)
    if (-not $up) { throw "$vm did not come back." }
    # SQL Server starts delayed-automatic; the first connection before that fails with error 40.
    Invoke-Command -VMName $vm -Credential $cred -ArgumentList $srv.postgres.service -ScriptBlock {
        param($pgService)
        $deadline = (Get-Date).AddMinutes(5)
        while (((Get-Service MSSQLSERVER).Status -ne 'Running' -or (Get-Service $pgService).Status -ne 'Running') -and (Get-Date) -lt $deadline) { Start-Sleep 5 }
        if ((Get-Service MSSQLSERVER).Status -ne 'Running') { throw 'SQL Server did not start.' }
        Start-Sleep 15
    }
}

$results = @()
foreach ($s in $scenarios) {
    $started = Get-Date
    Write-Host "=== $($s.id) $($s.identity) + $($s.database)$(if ($s.upgrade) { ' (update)' }) $(Get-Date -Format HH:mm:ss)"
    if ($s.upgrade -and $s.database -eq 'postgres' -and -not $config.previousRelease.canInstallPostgres) {
        $r = [pscustomobject]@{ id = $s.id; verdict = 'N/A'; note = 'the previous release cannot complete a PostgreSQL installation' }
        $results += $r; continue
    }
    if ($s.remoteSql -and -not $config.PSObject.Properties['remoteSql']) {
        $r = [pscustomobject]@{ id = $s.id; verdict = 'N/A'; note = 'no remoteSql configured' }
        $results += $r; continue
    }
    $remoteLoginExisted = $false
    try {
        if ($s.remoteSql) {
            # The remote SQL Server is not restored by a checkpoint: refuse to touch an existing
            # database, and remember whether the computer account's login was already there.
            $pre = Invoke-Command -VMName $config.remoteSql.vm -Credential $cred -ArgumentList $config.remoteSql.database, $srv.computerAccount -ScriptBlock {
                param($db, $login)
                $c = New-Object System.Data.SqlClient.SqlConnection 'Server=localhost;Integrated Security=true;Encrypt=false'; $c.Open()
                $cmd = $c.CreateCommand()
                $cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = '$db'"; $dbCount = [int]$cmd.ExecuteScalar()
                $cmd.CommandText = "SELECT COUNT(*) FROM sys.server_principals WHERE name = '$login'"; $loginCount = [int]$cmd.ExecuteScalar()
                $c.Close()
                [pscustomobject]@{ databaseExists = $dbCount -gt 0; loginExists = $loginCount -gt 0 }
            }
            if ($pre.databaseExists) { throw "Database '$($config.remoteSql.database)' already exists on $($config.remoteSql.fqdn); not touching it." }
            $remoteLoginExisted = $pre.loginExists
        }
        Restore-CleanVm
        $stage = Join-Path $ResultsDir "stage-$($s.id)"
        New-Item -ItemType Directory -Force $stage | Out-Null
        $lab = [ordered]@{
            fqdn = $srv.fqdn; httpsPort = [int]$srv.httpsPort; sqlDatabase = $srv.sqlDatabase
            postgres = $srv.postgres
            previous = [ordered]@{ preinstallRuntimes = [bool]$config.previousRelease.preinstallRuntimes
                                   preGrantSystemForLocalSql = [bool]$config.previousRelease.preGrantSystemForLocalSql
                                   canProvisionPostgres = [bool]$config.previousRelease.canInstallPostgres }
        }
        Write-Json (Join-Path $stage 'lab.json') $lab
        $soak = if ($s.check) { 0 } else { $SoakSeconds }
        Write-Json (Join-Path $stage 'scenario.json') ([ordered]@{ id = $s.id; identity = $s.identity; database = $s.database; upgrade = $s.upgrade
            uninstall = if ($s.uninstall) { $s.uninstall } else { '' }; reinstallAfter = [bool]$s.reinstallAfter; soakSeconds = $soak
            secondIdentity = if ($s.secondIdentity) { $srv.gmsaAccount } else { '' }
            sqlServer = if ($s.remoteSql) { $config.remoteSql.fqdn } else { '' }
            sqlDatabase = if ($s.remoteSql) { $config.remoteSql.database } else { '' } })
        Write-Json (Join-Path $stage 'answers.json') (New-Answers $s $false)
        if ($s.secondIdentity) { Write-Json (Join-Path $stage 'answers-second.json') (New-Answers $s $false $s.secondIdentity) }
        $files = @((Join-Path $here 'Invoke-GuestScenario.ps1'), (Join-Path $here 'Invoke-GuestDbOutageCheck.ps1'),
                   (Join-Path $here 'Invoke-GuestRebootCheck.ps1'), (Join-Path (Split-Path $here) 'smoke-workflow.json')) +
                 @(Get-ChildItem $stage -File | ForEach-Object FullName)
        $copies = @{ $setup = 'server-setup.exe' }
        if ($s.upgrade) {
            Write-Json (Join-Path $stage 'answers-previous.json') (New-Answers $s $true)
            Write-Json (Join-Path $stage 'update.json') ([ordered]@{ schemaVersion = 1; mode = 'update'; installPath = 'C:\Program Files\NodePilot'; serviceName = 'NodePilot' })
            $files += @((Join-Path $stage 'answers-previous.json'), (Join-Path $stage 'update.json'))
            $copies[$config.previousRelease.serverSetup] = 'previous-server-setup.exe'
            if ($config.previousRelease.preinstallRuntimes) { Get-ChildItem $payload -Filter '*-runtime-*-win-x64.exe' | ForEach-Object { $files += $_.FullName } }
        }
        $session = New-PSSession -VMName $vm -Credential $cred
        try {
            Invoke-Command -Session $session -ScriptBlock { New-Item -ItemType Directory -Force C:\NP-Test | Out-Null }
            foreach ($f in ($files | Select-Object -Unique)) { Copy-Item -LiteralPath $f -Destination ('C:\NP-Test\' + (Split-Path $f -Leaf)) -ToSession $session -Force }
            foreach ($k in $copies.Keys) { Copy-Item -LiteralPath $k -Destination ('C:\NP-Test\' + $copies[$k]) -ToSession $session -Force }
            if ($s.remoteSql -and $config.remoteSql.PSObject.Properties['trustCertificate']) {
                # The lab's remote SQL Server has a self-signed certificate; trust it on this clean VM.
                Copy-Item -LiteralPath $config.remoteSql.trustCertificate -Destination 'C:\NP-Test\remote-sql.cer' -ToSession $session -Force
                Invoke-Command -Session $session -ScriptBlock { Import-Certificate -FilePath C:\NP-Test\remote-sql.cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null }
            }
            if ($s.check -eq 'crlMissing') {
                # Without the lab CA's CRL the certificate's revocation status cannot be checked.
                Invoke-Command -Session $session -ArgumentList $srv.postgres.caName -ScriptBlock { param($ca) certutil -delstore CA $ca | Out-Null }
            }
            $r = Invoke-Command -Session $session -ScriptBlock {
                try { & C:\NP-Test\Invoke-GuestScenario.ps1 }
                catch { [pscustomobject]@{ verdict = 'GUEST-ERROR'; error = "$($_.Exception.Message) @ $($_.InvocationInfo.PositionMessage)" } }
            }
            $r = $r | Select-Object * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName
            if ($s.check -eq 'dbOutage' -and $r.setupExit -eq 0) {
                $outage = Invoke-Command -Session $session -ScriptBlock { & C:\NP-Test\Invoke-GuestDbOutageCheck.ps1 } |
                    Select-Object * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName
                $r | Add-Member -NotePropertyName dbOutage -NotePropertyValue $outage
                $r.verdict = if ($r.verdict -eq 'PASS') { $outage.verdict } else { 'FAIL' }
            }
            if ($s.check -eq 'crlMissing') {
                $inno = Invoke-Command -Session $session -ScriptBlock { Get-Content 'C:\NP-Test\B-inno.log' -Raw -ErrorAction SilentlyContinue }
                $refused = $r.setupExit -ne 0 -and $inno -match 'certificate revocation could not be checked'
                $r | Add-Member -NotePropertyName preflightRefused -NotePropertyValue $refused -Force
                $r.verdict = if ($refused) { 'PASS' } else { 'FAIL' }
            }
            $out = Join-Path $ResultsDir "out-$($s.id)"
            New-Item -ItemType Directory -Force $out | Out-Null
            $names = Invoke-Command -Session $session -ArgumentList $s.id -ScriptBlock { param($id) Get-ChildItem C:\NP-Test -File -Filter "$id-*" | ForEach-Object FullName }
            foreach ($n in $names) { Copy-Item -FromSession $session -LiteralPath $n -Destination $out -Force }
        }
        finally { Remove-PSSession $session }

        if ($s.check -eq 'reboot' -and $r.verdict -eq 'PASS') {
            $before = Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
            Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { Restart-Computer -Force }
            $deadline = (Get-Date).AddMinutes(8)
            do {
                Start-Sleep 15
                $bootNow = try { Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime } -ErrorAction Stop } catch { $null }
            } until (($bootNow -and $bootNow -gt $before) -or (Get-Date) -gt $deadline)
            if (-not ($bootNow -and $bootNow -gt $before)) { throw "$vm did not come back after the restart." }
            $reboot = Invoke-Command -VMName $vm -Credential $cred -ScriptBlock { & C:\NP-Test\Invoke-GuestRebootCheck.ps1 } |
                Select-Object * -ExcludeProperty PSComputerName, RunspaceId, PSShowComputerName
            $r | Add-Member -NotePropertyName reboot -NotePropertyValue $reboot
            $r.verdict = $reboot.verdict
        }
    }
    catch { $r = [pscustomobject]@{ id = $s.id; verdict = 'HARNESS-ERROR'; error = $_.Exception.Message } }
    finally {
        if ($s.remoteSql -and $config.PSObject.Properties['remoteSql']) {
            # Leave the remote SQL Server as it was: drop the scenario's database, and the login only
            # if this scenario created it.
            Invoke-Command -VMName $config.remoteSql.vm -Credential $cred -ArgumentList $config.remoteSql.database, $srv.computerAccount, $remoteLoginExisted -ScriptBlock {
                param($db, $login, $keepLogin)
                $c = New-Object System.Data.SqlClient.SqlConnection 'Server=localhost;Integrated Security=true;Encrypt=false'; $c.Open()
                $cmd = $c.CreateCommand()
                $cmd.CommandText = "IF DB_ID('$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END"
                [void]$cmd.ExecuteNonQuery()
                if (-not $keepLogin) {
                    $cmd.CommandText = "IF SUSER_ID('$login') IS NOT NULL DROP LOGIN [$login]"
                    [void]$cmd.ExecuteNonQuery()
                }
                $c.Close()
            }
        }
    }
    $r | Add-Member -NotePropertyName minutes -NotePropertyValue ([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) -Force
    $r | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir "$($s.id).json") -Encoding utf8
    Write-Host "    -> $($r.verdict)"
    $results += $r
}

Stop-VM -Name $vm -TurnOff -Force
Restore-VMCheckpoint -VMSnapshot (Get-VMCheckpoint -VMName $vm -Name $srv.checkpoint) -Confirm:$false

$summary = @("# Server setup $Version - release lab", '', '| Scenario | Verdict | Details |', '|---|---|---|')
foreach ($r in $results) {
    $detail = @($r.PSObject.Properties | Where-Object { $_.Name -in 'setupExit', 'healthz', 'execution', 'errors', 'uninstallFailed', 'reinstallLogin', 'note', 'error' -and "$($_.Value)" -ne '' } |
        ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
    $summary += "| $($r.id) | $($r.verdict) | $detail |"
}
$summary | Set-Content (Join-Path $ResultsDir 'summary.md') -Encoding utf8
$summary | Write-Host
if (@($results | Where-Object { $_.verdict -notin 'PASS', 'N/A' }).Count) { exit 1 }
