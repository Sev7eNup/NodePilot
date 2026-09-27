# PowerShell 5.1 and 7: the runtime is part of the job

```powershell
[pscustomobject]@{
    Version = $PSVersionTable.PSVersion.ToString()
    Edition = $PSVersionTable.PSEdition
    Process = (Get-Process -Id $PID).Path
    Is64Bit = [Environment]::Is64BitProcess
}
```

Run this query inside the process that will actually execute the script. Its output on an administrator's workstation does not establish which runtime a scheduled task, service, or remote session uses.

Windows PowerShell 5.1 and PowerShell 7 can be installed side by side. `powershell.exe` starts Windows PowerShell, while PowerShell 7 uses `pwsh.exe`. Installing another runtime does not automatically migrate existing jobs. [Microsoft: Migrating from Windows PowerShell to PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/whats-new/migrating-from-windows-powershell-51-to-powershell-7).

## A small comparison with explicit boundaries

An existing inventory script can be assessed without immediately changing the whole operating environment. Run a copy against the same approved test targets under both runtimes. Record module versions and the execution identity with each result. Otherwise, a permission difference could be mistaken for a runtime problem.

| Check | What to compare |
|---|---|
| Module import | Can the intended module version load in the actual process? |
| Output | Do properties, data types, and result counts agree? |
| Failure behavior | Does the orchestration layer detect missing permissions and unreachable targets? |
| File exchange | Do encoding and format meet the recipient's expectations? |
| Unattended start | Does execution work without the personal PowerShell profile? |

This table supports acceptance testing. It does not claim that every script fails at these points. It directs attention to properties that a downstream process actually consumes. Identical console output, for example, can conceal different object types.

## An imported module may work in another process

On Windows, PowerShell 7 provides a compatibility feature for Windows PowerShell modules. `Import-Module -UseWindowsPowerShell` can load a module through a background session running Windows PowerShell 5.1. This uses implicit remoting. Returned objects may be deserialized and therefore lose their original methods. [Microsoft: Windows PowerShell Compatibility](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility).

That may be sufficient for a script which only reads selected properties. If it calls object methods or expects a particular live .NET type, however, that exact operation needs testing. A successful import does not establish full compatibility.

The test record should therefore state whether a module loads natively or works through the compatibility session. This distinction also explains why two apparently equivalent PowerShell 7 jobs may have different prerequisites.

## Migration ends with the final caller

After successful testing, change the executing process explicitly. For a scheduled task, this concerns the program path and arguments. For remoting, the selected endpoint determines the remote runtime. Starting `pwsh.exe` locally does not force the target server to use a PowerShell 7 session.

`Enable-PSRemoting` configures endpoints for the installation in which the command runs. Registration and permissions for the intended endpoint must therefore be part of deployment. [Microsoft: Enable-PSRemoting](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/enable-psremoting).

During a bounded transition period, tested jobs can run under PowerShell 7 while documented dependencies remain on 5.1. Each exception needs an owner and an identifiable reason. It should not survive merely because nobody can account for the old invocation.

For a script integrated into NodePilot, acceptance testing likewise includes the runtime actually used. The executing process or remote endpoint determines that runtime, rather than whichever PowerShell version was installed most recently on the server.
