# PowerShell 5.1 and 7: the runtime is part of the job

Installing PowerShell 7 does not automatically change existing script invocations. Windows PowerShell 5.1 starts through `powershell.exe`, while PowerShell 7 uses `pwsh.exe`. Both can coexist while a scheduled task continues to use its previous runtime. [Microsoft: Migrating to PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/whats-new/migrating-from-windows-powershell-51-to-powershell-7).

## Identify the runtime where the job executes

The following query belongs inside the actual job, for example as temporary diagnostic output from a scheduled task. It also records the executing account, whose permissions can affect the result.

```powershell
[pscustomobject]@{
    Version = $PSVersionTable.PSVersion.ToString()
    Edition = $PSVersionTable.PSEdition
    Process = (Get-Process -Id $PID).Path
    Account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    Is64Bit = [Environment]::Is64BitProcess
}
```

Under 5.1, `Edition` reports `Desktop`. Under 7, it reports `Core`. The process path and architecture help identify differences between invocations. A check in an administrator's own terminal confirms only that environment.

## Compare the same script result under 5.1 and 7

A small inventory script provides a starting point. Saved as `Inventory.ps1`, it reads the local computer name and the operating system's caption and version. It changes no system settings and returns the selected fields as JSON.

```powershell
$ErrorActionPreference = 'Stop'
Get-CimInstance -ClassName Win32_OperatingSystem |
    Select-Object CSName, Caption, Version |
    ConvertTo-Json -Compress
```

Run the following on the same Windows computer from the directory containing the file. Both PowerShell versions must be available through `PATH`. `-NoProfile` excludes personal profile customizations, while `-NonInteractive` prevents interactive prompts.

```powershell
$ErrorActionPreference = 'Stop'
$ps51 = & powershell.exe -NoProfile -NonInteractive -File .\Inventory.ps1
if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell failed.' }

$ps7 = & pwsh.exe -NoProfile -NonInteractive -File .\Inventory.ps1
if ($LASTEXITCODE -ne 0) { throw 'PowerShell 7 failed.' }

Compare-Object ($ps51 | ConvertFrom-Json) ($ps7 | ConvertFrom-Json) `
    -Property CSName, Caption, Version -CaseSensitive
```

No output means the three compared values match. Differences appear with `<=` for the 5.1 side and `=>` for the 7 side. This checks the selected data after JSON conversion, not the original .NET object types. [Microsoft: Compare-Object](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/compare-object).

For an existing script, testing also needs to cover module versions, missing access rights and unreachable targets. File output additionally requires a comparison of format and encoding. A successful run alone does not cover these cases.

Modules loaded through `Import-Module -UseWindowsPowerShell` need particular attention. They run in a local Windows PowerShell 5.1 background session. Serialization can strip methods from returned objects even when reading their properties still works. A successful import therefore does not establish script compatibility. [Microsoft: Windows PowerShell Compatibility](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility).

## Select the remote endpoint explicitly

With remoting over WinRM, the endpoint determines the remote runtime. The following example requires an already registered `PowerShell.7` endpoint, reachable WinRM and the necessary access rights. Replace `srv-app01` with the intended test host.

```powershell
Invoke-Command -ComputerName 'srv-app01' `
    -ConfigurationName 'PowerShell.7' -ErrorAction Stop `
    -ScriptBlock { $PSVersionTable.PSVersion.ToString() }
```

The expected result is a version number in the `7.x` series. Starting `pwsh.exe` locally is insufficient. Setup takes place on the target through `Enable-PSRemoting`, run from the intended PowerShell version with administrator rights. This also configures WinRM and firewall rules. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

In NodePilot, the engine setting applies only to local script steps:

| Execution | Runtime used |
|---|---|
| Local, `engine: auto` | Windows PowerShell 5.1 by default |
| Local, `engine: powershell` | Windows PowerShell 5.1 explicitly |
| Local, `engine: pwsh` | Separate PowerShell 7 process, installation required |
| Local, `engine: runspace` | Embedded PowerShell 7 with restrictions on module use |
| Remote through NodePilot | Currently Windows PowerShell 5.1 through `Microsoft.PowerShell` |

The remoting example therefore shows an independent PowerShell invocation. Installing PowerShell 7 on the target does not change remote execution in NodePilot. The [Activity reference](https://www.nodepilot.run/docs/en/activities-reference/) provides the details.
