# PowerShell execution policy: diagnose a script that will not start

For “running scripts is disabled on this system”, first run `Get-ExecutionPolicy -List` and `Get-ExecutionPolicy` in the affected PowerShell process. The list shows the scopes; the second command shows the effective policy. Those observations determine whether to investigate a local setting, Group Policy or the file's origin.

A common case is a script that works in your console but fails as a scheduled task. Before changing the whole machine, compare runtime, identity, policy and the same script file. This article covers Windows PowerShell and PowerShell running on Windows.

## 1. Inspect the correct process

```powershell
$PSVersionTable.PSVersion
$PSVersionTable.PSEdition
[Security.Principal.WindowsIdentity]::GetCurrent().Name
Get-ExecutionPolicy -List
Get-ExecutionPolicy
```

These commands read the current context. Output from an administrator's console does not establish the settings of a scheduled task account. Collect the comparison under the actual identity and runtime. Windows PowerShell 5.1 and PowerShell 7 are managed separately; `powershell.exe` and `pwsh.exe` are not interchangeable. [Microsoft: Get-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-executionpolicy) and [Set-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-executionpolicy).

Precedence is:

| Priority | Scope | Applies to |
| ---: | --- | --- |
| 1 | MachinePolicy | Computer Group Policy |
| 2 | UserPolicy | User Group Policy |
| 3 | Process | Current PowerShell session |
| 4 | CurrentUser | Current user |
| 5 | LocalMachine | Local computer setting |

`Undefined` means no policy is set at that scope. It does not mean unrestricted script execution. The first defined scope in this order determines the policy; platform defaults apply when none is set. The most restrictive setting does not automatically win. [Microsoft: execution policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

## 2. Classify the actual failure

| Observation | Next check |
| --- | --- |
| Script execution disabled; effective policy is Restricted | Is script execution intended for this machine and task? |
| File is not digitally signed; effective policy is RemoteSigned | Inspect the origin marker and signature of this specific file |
| File is not digitally signed; effective policy is AllSigned | Use an approved artifact with an appropriate signature |
| Local change has no effect | Check MachinePolicy, UserPolicy and the process actually started |
| Script starts but cannot access a share | Investigate file permissions and execution identity; execution policy grants no SMB access |

`Restricted` prevents script files from running. `RemoteSigned` permits unsigned local scripts but generally requires a trusted signature for scripts marked as Internet downloads, unless their marker has been removed after review. `AllSigned` requires signatures for locally created scripts too. A valid signature does not replace reviewing the intended behavior. [Microsoft: script signing](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_signing).

## 3. Inspect a downloaded file individually

`C:\Ops\Test-JobInput.ps1` is an example path; replace it with the affected file before running these commands.

```powershell
$scriptPath = 'C:\Ops\Test-JobInput.ps1'
Get-Item -LiteralPath $scriptPath -Stream *
Get-AuthenticodeSignature -LiteralPath $scriptPath |
    Select-Object Status, StatusMessage
```

On a filesystem supporting alternate data streams, `Zone.Identifier` can record download origin. Not every transfer method preserves it, so its absence does not establish trust.

After reviewing the source, contents and operational approval, you can remove the marker from an individual file. The first line previews the action; the second changes the file:

```powershell
Unblock-File -LiteralPath $scriptPath -WhatIf
Unblock-File -LiteralPath $scriptPath
```

`Unblock-File` neither changes execution policy nor creates a signature. It therefore does not resolve a missing signature under `AllSigned`. Do not indiscriminately unblock an unreviewed download collection. [Microsoft: Unblock-File](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file).

## 4. Choose a local setting deliberately

If no higher policy applies and `RemoteSigned` is the approved policy for the user, the targeted setting is:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
Get-ExecutionPolicy -List
Get-ExecutionPolicy
```

The first command persistently changes that user's setting. Record the previous value, name the intended scope explicitly and inspect the effective result afterwards. Without `-Scope`, `Set-ExecutionPolicy` defaults to `LocalMachine`. A local change cannot override Group Policy; a required adjustment belongs in the managed policy in that case. [Microsoft: Set-ExecutionPolicy](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/set-executionpolicy).

`Bypass` disables the corresponding checks. It neither explains the failure nor replaces appropriate deployment. Execution policy itself is not a security boundary or a substitute for access permissions and application control. [Microsoft: execution policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

## 5. Retest the original task

After correcting a scheduled task, use the same executable path, arguments, script file and account. First establish that the script starts. It then needs its own error handling and result checks: [PowerShell try/catch](https://www.nodepilot.run/en/blog/powershell-try-catch/) and the [gMSA scheduled task guide](https://www.nodepilot.run/en/blog/gmsa-scheduled-task/) cover these next steps. For runtime changes, use the [PowerShell 5.1 and 7 comparison](https://www.nodepilot.run/en/blog/powershell-51-and-7/).

Read-only checks, the download marker and targeted unblocking were tested under Windows PowerShell 5.1 using a temporary file created for the test. No persistent execution policy was changed. Group Policy scenarios and PowerShell 7 are documented from Microsoft sources, not presented as locally executed tests.
