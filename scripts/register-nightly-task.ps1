<#
.SYNOPSIS
  Registers (or updates) the "NodePilot Nightly Tests" Windows Task Scheduler job that runs
  scripts\nightly-tests.ps1 every evening.

.DESCRIPTION
  Runs in the current user's context with "run only when the user is logged on" (LogonType
  Interactive) so NO password needs to be stored. StartWhenAvailable makes a missed run
  (machine off/asleep at trigger time) catch up at next opportunity.

  To run even when you are NOT logged on, you must supply credentials — easiest via Task
  Scheduler GUI (set "Run whether user is logged on or not") or:
    schtasks /Change /TN "NodePilot Nightly Tests" /RU <user> /RP <password>

  Re-run this script any time to change the time (-Time) — it uses -Force to overwrite.

.PARAMETER Time
  Daily start time, HH:mm. Default 22:00.

.PARAMETER ScriptPath
  Path to the nightly runner. Defaults to nightly-tests.ps1 sitting next to this script, so
  the task registers correctly from any checkout location (not a hard-coded absolute path).
#>
[CmdletBinding()]
param(
  [string]$Time       = '22:00',
  [string]$TaskName   = 'NodePilot Nightly Tests',
  [string]$ScriptPath = (Join-Path $PSScriptRoot 'nightly-tests.ps1')
)

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""

$trigger = New-ScheduledTaskTrigger -Daily -At $Time

$settings = New-ScheduledTaskSettingsSet `
  -StartWhenAvailable `
  -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
  -MultipleInstances IgnoreNew `
  -DontStopOnIdleEnd

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
  -Settings $settings -Principal $principal `
  -Description 'Runs NodePilot backend (dotnet test), frontend unit (vitest) and E2E (Playwright) suites nightly. See scripts\nightly-tests.ps1.' `
  -Force | Out-Null

Write-Host "Registered '$TaskName' - daily at $Time (current user, when logged on)."
Get-ScheduledTask -TaskName $TaskName | Format-List TaskName, State
