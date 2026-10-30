# Test-ADServiceAccount returns False: diagnose the gMSA on its host

When `Test-ADServiceAccount` returns `False`, the managed account is not ready for use on the computer running the test. Start with that host, the account object and its password-retrieval authorization. Recreating the account or changing the application's permissions before locating the failure makes the diagnosis harder.

First distinguish a returned `False` from a command error. A missing ActiveDirectory module, a misspelled account and an unreachable directory are different failures. Once the account test returns `True`, task or service startup and access to files still need their own checks.

This guide accompanies the [gMSA setup article](/en/blog/gmsa-service-accounts/). Commands use example names. In a Windows Server 2025 lab with Windows PowerShell 5.1.26100.1591 on 25 October 2026, we reproduced a host without retrieval authorization, corrected that authorization, and then ran a scheduled task with both allowed and denied destination access. Other diagnostic branches below are checks to investigate, not claims that every fault was reproduced.

## 1. Capture the failure on the execution host

Open an elevated Windows PowerShell session locally on the computer that will run the job. Testing on a domain controller or your workstation answers a different question. Start with:

```powershell
$env:COMPUTERNAME
$PSVersionTable.PSVersion
Get-Module -ListAvailable ActiveDirectory
Import-Module ActiveDirectory -ErrorAction Stop
Test-ADServiceAccount -Identity 'gmsaExport' -Verbose -ErrorAction Stop
```

Record the actual output and warning/error text. If the module is unavailable, install the appropriate AD administration tools through your normal server provisioning process before continuing. If a remote administration session introduces another authentication hop, repeat the diagnostic locally before changing delegation settings.

| Observation | Next step |
|---|---|
| Module or command not found | Make the AD module available in this runtime |
| Account cannot be found | Check spelling, domain and account object in step 2 |
| Cannot contact the directory | Check DNS, DC discovery and secure channel in step 3 |
| The command returns `False` | Check retrieval authorization, KDS and local installation |
| The command returns `True`, but the job fails | Continue with job startup and resource permissions in step 6 |

[Microsoft's cmdlet documentation](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount?view=windowsserver2025-ps) defines the local account test. It does not check your script or its destination share.

## 2. Inspect the account and the authorized computers

On a machine with AD administration access, inspect the intended object:

```powershell
Get-ADServiceAccount -Identity 'gmsaExport' `
    -Properties PrincipalsAllowedToRetrieveManagedPassword, KerberosEncryptionType |
    Select-Object Name, SamAccountName, ObjectClass, Enabled, `
        PrincipalsAllowedToRetrieveManagedPassword, KerberosEncryptionType
```

For this guide, `ObjectClass` should identify a group managed service account and `Enabled` should be `True`. `SamAccountName` ends in `$`. Use that full account name in a task principal; do not confuse it with the execution host's computer account.

Examine every principal listed in `PrincipalsAllowedToRetrieveManagedPassword`. It can contain computer accounts directly or groups. If it refers to `gmsaExport-Hosts`, inspect that actual group:

```powershell
Get-ADComputer -Identity 'srv-job01' |
    Select-Object Name, DistinguishedName, Enabled
Get-ADGroupMember -Identity 'gmsaExport-Hosts' -Recursive |
    Select-Object Name, ObjectClass, DistinguishedName
```

The relevant member is the execution **computer**, not the administrator who created the account. A suitable domain administrator must correct missing authorization. Avoid blindly replacing `PrincipalsAllowedToRetrieveManagedPassword` with one host: the existing list may authorize other active consumers.

If a computer was recently added to a group, a directory query can show the new membership before its existing security context reflects it. Allow for AD replication and arrange a restart of the affected host when needed. Purging system Kerberos tickets is not a harmless read-only diagnostic and can affect other workloads; it is not a default step in this guide.

## 3. Check the host's domain connection

On the member server:

```powershell
Get-CimInstance Win32_ComputerSystem | Select-Object Name, Domain, PartOfDomain
Resolve-DnsName -Name '_ldap._tcp.dc._msdcs.contoso.com' -Type SRV
nltest.exe /dsgetdc:contoso.com
Test-ComputerSecureChannel -Verbose
w32tm.exe /query /status
```

Replace `contoso.com` with the real AD DNS domain. Confirm domain membership, discoverable domain controllers and a healthy secure channel. Inspect time-source and synchronization information; a configured source alone does not prove that the clocks agree. `Test-ComputerSecureChannel` here is for a member computer, not a domain controller.

Native programs can report errors through their exit status as well as text. If a check fails, repair that specific DNS, connectivity, trust or time problem and repeat the account test. Do not run `-Repair`, rejoin the domain or relax Kerberos settings as a routine first response.

## 4. Check KDS readiness, then local installation

On a domain controller with the relevant administration rights:

```powershell
$keys = Get-KdsRootKey
foreach ($key in $keys) {
    $key | Select-Object KeyId, CreationTime, EffectiveTime
}
```

A newly created KDS root key needs time to become usable across domain controllers. Microsoft documents a waiting period of up to ten hours; its backdating example is for a single-DC test environment. It is not a general production fix. If a key already exists, do not create another one merely because one host fails. See [Create the KDS root key](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key).

After checking authorization and directory readiness, install and test on the intended execution host:

```powershell
Install-ADServiceAccount -Identity 'gmsaExport' -ErrorAction Stop
Test-ADServiceAccount -Identity 'gmsaExport' -Verbose -ErrorAction Stop
```

If installation fails, preserve its exact error. The lab produced `Access Denied` when the host lacked retrieval permission. That observed cause does not make every installation error an authorization error.

If the warning points to Kerberos encryption, compare the account settings with the domain's supported encryption policy and relevant events. Do not enable an older encryption type simply to make the warning disappear. Microsoft's [gMSA management guide](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts) describes the deployment requirements.

## 5. Change one cause and repeat the same check

The lab used a dedicated test gMSA and direct computer authorization to avoid changing any production group or service. The same member server produced:

| Configuration | `Test-ADServiceAccount` | `Install-ADServiceAccount` |
|---|---|---|
| Only another computer was authorized | `False` | Access denied |
| The tested computer was authorized instead | `True` after installation | Succeeded |

No account recreation, KDS replacement or administrator-group membership was required. This isolates one cause; it does not cover every replication, group-membership or encryption-policy case. With group-based authorization, also account for the computer's refreshed security context.

## 6. If the account test passes, follow the failing job

For a scheduled task, inspect its principal, executable and most recent completed run:

```powershell
$task = Get-ScheduledTask -TaskName 'GmsaExportProbe'
$task.Principal | Select-Object UserId, LogonType, RunLevel
$task.Actions | Select-Object Execute, Arguments, WorkingDirectory
Get-ScheduledTaskInfo -TaskName 'GmsaExportProbe' |
    Select-Object LastRunTime, LastTaskResult
```

Confirm `DOMAIN\account$`, the intended runtime and a suitable logon type. Check the effective batch-logon rights if the process never starts. For a Windows service, inspect its configured account, service-logon rights and the Service Control Manager events instead. These are separate application checks.

If the process starts, use its own output to identify the failing operation. A successful write by your administrator account says nothing about the gMSA's rights. The [scheduled-task walkthrough](/en/blog/gmsa-scheduled-task/) provides a probe that logs its execution identity, writes a unique file and reads it back.

In the second lab test, the account test stayed `True` in both cases. With destination access the task returned `0`; with NTFS access withheld from a separate test directory it returned `1` and logged the denied path. The problem was resource authorization, so changing KDS or the account's retrieval permission would have addressed the wrong layer.

## Keep a short diagnostic record

Keep the host and runtime, account identity, exact failed command, original result, one configuration change and the repeated result together. If the account test becomes healthy, finish with an actual job under the gMSA and verify its output. Sanitize domain names, paths and account details before sharing logs publicly; never include managed passwords.

When NodePilot runs the job, use its execution record alongside these Windows checks. The [remote-execution documentation](/docs/en/configuration/remote-execution/) explains the relevant configuration. A green account test is one prerequisite; successful work still needs evidence from the executing process.
