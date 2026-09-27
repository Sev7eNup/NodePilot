# Reconstructing a failed run from start to cause

```text
02:14:03  run=EX-1042  phase=read-source   result=ok
02:14:05  run=EX-1042  phase=write-export  result=AccessDenied
02:14:05  run=EX-1042  phase=complete      result=failed
```

These three lines belong to a fictional diagnostic case. The machine names, identifier, and observations serve the explanation. They do not describe a customer incident or a measured test run.

In the example, a scheduled export starts on `srv-job01`. It reads its source successfully but fails when writing to `\\srv-file01\Exports\Finance`. An administrator later runs the script interactively and obtains a file. The initial suspicion is therefore that the failure was temporary.

## The successful comparison does not match the job

First, compare the conditions. Both invocations use the same script version and destination path. The manual attempt, however, runs under a personal administrator account, while the scheduled export uses `CONTOSO\svcExport`. The comparison has changed precisely the condition that matters to an access failure.

The error narrows the investigation but does not yet prove a particular ACL issue. Share permissions, the actual destination being resolved, and a different identity also need consideration. The process therefore needs a test under its real execution identity.

A bounded diagnostic job can report the identity immediately before the affected step:

```powershell
[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
```

Record the output in the same execution log as the destination path. With remoting, the query must run inside the relevant remote process. The identity of the calling terminal would again be the wrong observation.

## From suspicion to an established cause

Further checks produce the following picture in this fictional example:

| Observation | Meaning |
|---|---|
| Service invocation confirms `CONTOSO\svcExport` | The expected identity is in use |
| The share permits changes for this identity | The share alone does not explain the denial |
| NTFS grants only read access on the destination folder | Creating a file is not permitted |
| An approved write test under the same identity fails | The failure is reproducible under the job's conditions |

The responsible administrator then adjusts the ACL to provide the required access. The same limited write test succeeds afterwards. Comparing the outcome before and after this single change supports the cause in the example: the service account lacked the required NTFS permission on the export folder.

The test uses a specifically named file whose creation and removal are approved. Overwriting a production document would be unsuitable for diagnosis. The export function itself is checked afterwards, since successful write access does not establish that the exported content is correct.

## The finding also changes error handling

When a PowerShell cmdlet emits a non-terminating error, the script may continue. A `try/catch` block alone does not ensure that the error is caught. For a write operation whose success is required by all subsequent work, `-ErrorAction Stop` allows deliberate handling as a terminating error. [Microsoft: PowerShell error handling](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_error_handling).

In this case, the process should retain the original error alongside its target and job identifier, and report failure to its caller. An automatic retry with unchanged permissions would provide no correction. A final message such as “export finished” must not be interpreted as operational success either.

For an export started through NodePilot, execution history provides the entry point into the same investigation. The cause follows from consistent observations under the actual execution conditions. One error message and a successful administrator attempt are insufficient.
