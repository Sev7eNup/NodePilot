# Run a PowerShell scheduled task with a gMSA

A scheduled PowerShell task can use a group Managed Service Account without anyone entering its password. Configure the principal as `DOMAIN\account$` with `-LogonType Password`, choose the executable explicitly, and test the job under that identity. The word `Password` is the logon type; it is not a password value.

This walkthrough writes a small JSON file to an SMB share and reads it back. It records the account, runtime and result locally, so a successful registration cannot be mistaken for a successful export.

The account test, task registration and successful and denied SMB writes were exercised on Windows Server 2025 with Windows PowerShell 5.1.26100.1591 on 25 October 2026. The task used `RunLevel Limited`, with no interactive user session. Names and paths below are example values. PowerShell 7 and older Windows Server releases were not part of this test.

## Prepare the account and three separate permissions

Start with an existing gMSA, an authorized domain-joined execution host and the ActiveDirectory module. The [gMSA setup guide](/en/blog/gmsa-service-accounts/) covers account creation and the KDS prerequisites. On the execution host, run an elevated Windows PowerShell session:

```powershell
Import-Module ActiveDirectory
Install-ADServiceAccount -Identity 'gmsaExport'
Test-ADServiceAccount -Identity 'gmsaExport'
```

Continue when the test returns `True`. If it fails, first check host authorization and the prerequisites in the [gMSA setup guide](/en/blog/gmsa-service-accounts/). Microsoft documents this as a test of the account on the local computer, not of the export destination: [Test-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount?view=windowsserver2025-ps).

| Permission | Identity receiving it | Purpose |
|---|---|---|
| Retrieve the managed password | Execution host's computer account, usually through a group | Use the gMSA on this host |
| Log on as a batch job | `CONTOSO\gmsaExport$` | Start the scheduled process |
| Script, log and destination access | `CONTOSO\gmsaExport$` | Perform this specific job |

Check the effective **Log on as a batch job** policy and **Deny log on as a batch job**, including group membership. Apply any required grant through the policy managing this host; do not replace the existing list of accounts. The deny right takes precedence. See Microsoft's [batch-logon policy documentation](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/log-on-as-a-batch-job).

Prepare a dedicated test share, here `\\files01.contoso.com\GmsaProbe`, containing no production data. Give the gMSA **Change** on that share and **Modify** on its backing test directory. Check both layers. The account does not need to be a local administrator. Use a DNS name and a UNC path rather than a drive mapped in your personal session.

On the execution host, create a new `C:\Ops\GmsaProbe` directory and a `Results` subdirectory. Configure their ACLs as follows; keep the script directory separate from writable output:

| Local path | SYSTEM and Administrators | gMSA |
|---|---|---|
| `C:\Ops\GmsaProbe` and the script | Full control | Read and execute |
| `C:\Ops\GmsaProbe\Results` | Full control | Modify |

Remove inherited write access for ordinary users on this dedicated directory. The gMSA must not be able to replace its own script. The local report directory must remain writable even when the network destination is unavailable.

## Save a probe that reports its own result

Save this as `C:\Ops\GmsaProbe\Probe.ps1`. Use the execution policy and signing procedure required by your environment. The example does not change the execution policy or add a bypass switch.

```powershell
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$runId = [guid]::NewGuid().ToString()
$record = [ordered]@{
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('o')
    identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    powershell = $PSVersionTable.PSVersion.ToString()
    executable = (Get-Process -Id $PID).Path
    destination = $Destination
    success = $false
    error = $null
}
$exitCode = 1
try {
    $target = Join-Path $Destination ($runId + '.json')
    $payload = [ordered]@{ runId = $runId; identity = $record.identity }
    $payload | ConvertTo-Json | Set-Content -LiteralPath $target -Encoding UTF8
    $readBack = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
    if ($readBack.runId -ne $runId) { throw 'Read-back does not match this run.' }
    $record.success = $true
    $exitCode = 0
} catch {
    $record.error = $_.Exception.Message
} finally {
    $reportPath = Join-Path $PSScriptRoot ('Results\' + $runId + '.json')
    $record | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
exit $exitCode
```

Each attempt has its own run ID. A missing local report means that execution or local logging still needs investigation; it is not an empty success. For a real export, replace the probe with acceptance checks for the actual data and define retention for the output and logs.

## Register the task without supplying a password

Run these commands as an administrator on the execution host after adapting the domain and destination:

```powershell
$action = New-ScheduledTaskAction `
    -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument '-NoProfile -NonInteractive -File "C:\Ops\GmsaProbe\Probe.ps1" -Destination "\\files01.contoso.com\GmsaProbe"' `
    -WorkingDirectory 'C:\Ops\GmsaProbe'
$principal = New-ScheduledTaskPrincipal `
    -UserId 'CONTOSO\gmsaExport$' -LogonType Password -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'GmsaExportProbe' `
    -Action $action -Principal $principal -Settings $settings
```

There is deliberately no time trigger yet: first verify an on-demand run. There is also no `-Force` to silently replace an existing task. The two-minute limit belongs to this small probe, not to every production export.

`ServiceAccount` is not the appropriate logon type merely because gMSA contains “service account” in its name: Microsoft's enum describes that value for LocalSystem, LocalService and NetworkService. `S4U` also has different network-access semantics. See [Principal.LogonType](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype) and [New-ScheduledTaskPrincipal](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtaskprincipal?view=windowsserver2025-ps). The gMSA/`Password` combination above was verified in the lab without a password argument.

This task explicitly starts Windows PowerShell 5.1. For a PowerShell 7 job, select the installed `pwsh.exe` path and repeat the acceptance tests with its modules. Installing PowerShell 7 does not change an existing task's executable. The [runtime comparison](/en/blog/powershell-51-and-7/) explains what to verify.

## Verify a new run rather than an old result

Record the previous run time, start the task and wait for that new run to finish:

```powershell
$previousRun = (Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe').LastRunTime
Start-ScheduledTask -TaskName 'GmsaExportProbe'
$deadline = (Get-Date).AddSeconds(45)
do {
    Start-Sleep -Milliseconds 400
    $info = Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe'
    $task = Get-ScheduledTask -TaskName 'GmsaExportProbe'
} while (($info.LastRunTime -le $previousRun -or $task.State -eq 'Running') `
    -and (Get-Date) -lt $deadline)
if ($info.LastRunTime -le $previousRun -or $task.State -eq 'Running') {
    throw 'No completed new run within 45 seconds; inspect the task.'
}
$info | Select-Object LastRunTime, LastTaskResult
```

The 45-second timeout only stops this observation; it does not cancel the task. In the resulting local JSON, check `identity`, `executable`, `success` and `error`. Match its `runId` to the file on the share, inspect that file's content and check its owner on the file server. A result of `0` is meaningful here because the probe returns it only after writing and reading back its own file.

The lab produced these contrasting results:

| Case | Account test | Task result | Evidence |
|---|---|---|---|
| gMSA has destination access | `True` | `0` | Matching file, gMSA owner, local success report |
| gMSA lacks NTFS access to the test destination | `True` | `1` | Local report records access denied |

Both runs used the gMSA without administrator membership. Changing the destination permission changed the job result, not the account test. Use a separate test directory for a negative test; do not revoke access from a production job to reproduce it.

Only after acceptance, add a schedule, for example:

```powershell
$trigger = New-ScheduledTaskTrigger -Daily -At '02:00'
Set-ScheduledTask -TaskName 'GmsaExportProbe' -Trigger $trigger
```

Confirm the host's local time zone and the intended handling of missed starts. This example does not enable catch-up execution.

## Remove the probe and keep the useful evidence

When the probe is no longer needed, disable it, check whether it is still running and stop only this test task if necessary. Then unregister it:

```powershell
Disable-ScheduledTask -TaskName 'GmsaExportProbe'
Get-ScheduledTask -TaskName 'GmsaExportProbe' | Select-Object TaskName, State
# Only if this test task is still running:
# Stop-ScheduledTask -TaskName 'GmsaExportProbe'
Unregister-ScheduledTask -TaskName 'GmsaExportProbe' -Confirm:$true
```

Archive the test reports and remove the dedicated files, share and permissions when no longer needed. Uninstall or remove the gMSA only after checking its other consumers.

For one independent job, Task Scheduler may be sufficient. When exports acquire dependencies, retries and a shared operational history, [PowerShell workflows in NodePilot](/en/powershell-automation/) become a relevant next step. Verify execution identity and destination permissions again in that actual runtime.
