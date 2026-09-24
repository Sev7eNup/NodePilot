<#
  The desktop-setup scenarios, run inside the lab VM by Invoke-DesktopMatrix.ps1 in three phases:
  'first' (T1), 'second' after the host restarted the VM (T1b reboot, T2-T8), and 'upgrade' from a
  clean checkpoint (previous release, then this one).
#>
param([Parameter(Mandatory)][ValidateSet('first', 'second', 'upgrade')][string]$Phase)
Set-ExecutionPolicy -Scope Process Bypass -Force
. C:\np-lab\DesktopLab.ps1
$exe = Get-ChildItem C:\np-lab\NodePilot-Desktop-Setup-*.exe | Select-Object -First 1 -ExpandProperty FullName
Log "setup: $(Split-Path -Leaf $exe), phase: $Phase"
Log "uptime: $([int]((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalMinutes) min"

if ($Phase -eq 'upgrade') {
    Log '=== U1 previous release, then this one over it'
    $c = Invoke-Setup 'C:\np-lab\previous-desktop-setup.exe' '' 'u1-install-previous.log'
    Check 'U1 previous release installs' ($c -eq 0) "exit=$c"
    Check 'U1 first administrator created on the previous release' ((Invoke-Login 'npadmin' 'FirstPassw0rd!' -UseSetupToken) -eq '200')
    Remove-Item (Get-Handoff) -Force -ErrorAction SilentlyContinue
    $run = Invoke-SmokeWorkflow 'npadmin' 'FirstPassw0rd!'
    Check 'U1 smoke workflow runs on the previous release' ($run -eq 'Succeeded') $run
    $mark = Get-LogMark
    $c = Invoke-Setup $exe '' 'u1-upgrade.log'
    Check 'U1 upgrade exit 0' ($c -eq 0) "exit=$c"
    Assert-Installed 'U1'
    Check 'U1 administrator from the previous release signs in' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -eq '200')
    Check 'U1 no setup handoff after the upgrade' (-not (Test-Path (Get-Handoff)))
    Check 'U1 pre-update database backup taken' (@(Get-ChildItem "$DataDir\backups" -Filter 'pre-update-*.dump' -ErrorAction SilentlyContinue).Count -ge 1)
    $run = Invoke-SmokeWorkflow 'npadmin' 'FirstPassw0rd!'
    Check 'U1 smoke workflow runs after the upgrade' ($run -eq 'Succeeded') $run
    Start-Sleep -Seconds 120
    Check-NoNewErrors 'U1' $mark
    Invoke-Uninstall '/PURGEDATA=1' 'u1-uninstall-purge.log'
    Assert-ProgramRemoved 'U1'
    Check 'U1 data directory gone' (-not (Test-Path $DataDir))
    Log ("=== SUMMARY desktop upgrade: {0} passed, {1} failed" -f $global:NpPass, $global:NpFail)
    return
}

if ($Phase -eq 'second') {
    Log '=== T1b after a restart: services come up on their own'
    $bootMark = @{}
    (Get-Content C:\np-lab\logmark.json -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $bootMark[$_.Name] = [long]$_.Value }
    Check 'T1b ready after the restart' (Wait-Ready 300)
    Assert-Installed 'T1b'
    Check 'T1b administrator signs in after the restart' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -eq '200')
    Check-NoNewErrors 'T1b' $bootMark
}

if ($Phase -eq 'first') {
    Log '=== T1 fresh install on a clean machine'
    $mark = Get-LogMark
    $c = Invoke-Setup $exe '' 't1-install.log'
    Check 'T1 setup exit 0' ($c -eq 0) "exit=$c"
    Assert-Installed 'T1'
    Check 'T1 setup handoff written' (Test-Path (Get-Handoff))
    Check 'T1 first administrator created with the setup token' ((Invoke-Login 'npadmin' 'FirstPassw0rd!' -UseSetupToken) -eq '200')
    Remove-Item (Get-Handoff) -Force -ErrorAction SilentlyContinue
    Check 'T1 administrator signs in' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -eq '200')
    Check 'T1 wrong password rejected' ((Invoke-Login 'npadmin' 'not-the-password') -eq '401')
    $run = Invoke-SmokeWorkflow 'npadmin' 'FirstPassw0rd!'
    Check 'T1 smoke workflow runs through the dispatch path' ($run -eq 'Succeeded') $run
    & "$InstallDir\tools\np\np.exe" config set server (Get-Origin) 2>&1 | Out-Null
    Check 'T1 np client config created (per-user data)' (Test-Path "$env:APPDATA\NodePilot\config.json")
    Log '  observing the service log for two minutes'
    Start-Sleep -Seconds 120
    Check-NoNewErrors 'T1' $mark
    Save-CertKeyName
    # The restart check counts only what the services log after this point.
    Get-LogMark | ConvertTo-Json | Set-Content C:\np-lab\logmark.json -Encoding utf8
    Log ("=== SUMMARY desktop first: {0} passed, {1} failed" -f $global:NpPass, $global:NpFail)
    return
}

Log '=== T2 install the same version over the running installation (shell open)'
$shell = Start-Process "$InstallDir\desktop\NodePilot.exe" -PassThru
Start-Sleep -Seconds 10
Log "  shell running before over-install: $(-not $shell.HasExited)"
$mark = Get-LogMark
$c = Invoke-Setup $exe '' 't2-over-install.log'
Check 'T2 setup exit 0' ($c -eq 0) "exit=$c"
Assert-Installed 'T2'
Check 'T2 existing administrator still signs in (database kept)' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -eq '200')
Check 'T2 no setup handoff on an installation with users' (-not (Test-Path (Get-Handoff)))
Check 'T2 pre-update database backup taken' (@(Get-ChildItem "$DataDir\backups" -Filter 'pre-update-*.dump' -ErrorAction SilentlyContinue).Count -ge 1)
$setupLog = Get-Content C:\np-lab\t2-over-install.log -Raw
Check 'T2 no file-in-use errors in the setup log' ($setupLog -notmatch 'DeleteFile failed|MoveFileEx|code 5\)|being used by another process')
Start-Sleep -Seconds 60
Check-NoNewErrors 'T2' $mark
Save-CertKeyName

Log '=== T3 uninstall, keep data (the silent default)'
Invoke-Uninstall '' 't3-uninstall-keep.log'
Assert-ProgramRemoved 'T3'
Check 'T3 database kept' (Test-Path "$DataDir\pgdata\PG_VERSION")

Log '=== T4 install again, continuing with the kept data'
$mark = Get-LogMark
$c = Invoke-Setup $exe '' 't4-reinstall-kept.log'
Check 'T4 setup exit 0' ($c -eq 0) "exit=$c"
Assert-Installed 'T4'
Check 'T4 no setup handoff (administrator exists)' (-not (Test-Path (Get-Handoff)))
Check 'T4 existing administrator signs in with the old password' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -eq '200')
$run = Invoke-SmokeWorkflow 'npadmin' 'FirstPassw0rd!'
Check 'T4 smoke workflow runs after the reinstall' ($run -eq 'Succeeded') $run
Start-Sleep -Seconds 60
Check-NoNewErrors 'T4' $mark
Save-CertKeyName

Log '=== T5 uninstall, delete everything'
Invoke-Uninstall '/PURGEDATA=1' 't5-uninstall-purge.log'
Assert-ProgramRemoved 'T5'
Check 'T5 data directory gone' (-not (Test-Path $DataDir))
$userDirs = @(Get-PerUserNodePilotDirs)
Check 'T5 per-user NodePilot folders gone' ($userDirs.Count -eq 0) ($userDirs -join '; ')
Check 'T5 no start-up log under System32' (-not (Test-Path 'C:\Windows\System32\logs\nodepilot-*.log'))
Check 'T5 provisioning log gone' (-not (Test-Path (Join-Path $env:TEMP 'nodepilot-provision.log')))

Log '=== T6 fresh install after the full removal'
$mark = Get-LogMark
$c = Invoke-Setup $exe '' 't6-install-after-purge.log'
Check 'T6 setup exit 0' ($c -eq 0) "exit=$c"
Assert-Installed 'T6'
Check 'T6 setup handoff written again' (Test-Path (Get-Handoff))
Check 'T6 old administrator is gone' ((Invoke-Login 'npadmin' 'FirstPassw0rd!') -ne '200')
Check 'T6 new administrator created with the new setup token' ((Invoke-Login 'second' 'SecondPassw0rd!' -UseSetupToken) -eq '200')
Remove-Item (Get-Handoff) -Force -ErrorAction SilentlyContinue
Check-NoNewErrors 'T6' $mark
Save-CertKeyName

Log '=== T7 uninstall keeping data, then install with /DISCARDDATA=1'
Invoke-Uninstall '' 't7-uninstall-keep.log'
Assert-ProgramRemoved 'T7'
Check 'T7 database kept' (Test-Path "$DataDir\pgdata\PG_VERSION")
$mark = Get-LogMark
$c = Invoke-Setup $exe '/DISCARDDATA=1' 't7-install-discard.log'
Check 'T7 setup exit 0' ($c -eq 0) "exit=$c"
Assert-Installed 'T7'
Check 'T7 setup handoff written (fresh database)' (Test-Path (Get-Handoff))
Check 'T7 previous administrator is gone' ((Invoke-Login 'second' 'SecondPassw0rd!') -ne '200')
Check 'T7 new administrator created' ((Invoke-Login 'third' 'ThirdPassw0rd!' -UseSetupToken) -eq '200')
Check-NoNewErrors 'T7' $mark

Log '=== T8 final uninstall, delete everything'
Invoke-Uninstall '/PURGEDATA=1' 't8-uninstall-purge.log'
Assert-ProgramRemoved 'T8'
Check 'T8 data directory gone' (-not (Test-Path $DataDir))

Log ("=== SUMMARY desktop second: {0} passed, {1} failed" -f $global:NpPass, $global:NpFail)
