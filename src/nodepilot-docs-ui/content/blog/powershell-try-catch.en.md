# PowerShell try/catch: detect errors and fail unattended jobs reliably

PowerShell `try/catch` catches terminating errors. Many cmdlets initially report non-terminating errors: a red message appears while the script continues. Use `-ErrorAction Stop` to route such an error from a particular call into `catch`. The handler then needs to decide whether the job can recover or must fail.

This matters for scheduled tasks. An input file is missing, the next command still prints “Done”, and someone has to reconcile those statements the next morning. The examples below reproduce the behavior without changing services, accounts or system settings.

## Why catch does not run despite an error message

This example chooses a random filename in the temporary directory without creating the file. `Continue` is explicit so the behavior remains reproducible even when the session has a different preference.

```powershell
$missingPath = Join-Path ([IO.Path]::GetTempPath()) ('np-missing-' + [guid]::NewGuid() + '.json')
try {
    Get-Item -LiteralPath $missingPath -ErrorAction Continue
    'AFTER'
}
catch {
    'CAUGHT'
}
```

Alongside the error message, the output contains `AFTER`. It does not contain `CAUGHT`: the non-terminating error did not leave the `try` block. The color of console output is therefore an unsuitable test of script success.

## Catch the error with ErrorAction Stop

```powershell
$missingPath = Join-Path ([IO.Path]::GetTempPath()) ('np-missing-' + [guid]::NewGuid() + '.json')
try {
    Get-Item -LiteralPath $missingPath -ErrorAction Stop
    'AFTER'
}
catch {
    'CAUGHT'
    $_.Exception.Message
}
finally {
    'FINALLY'
}
```

The output now contains `CAUGHT`, the error message and `FINALLY`; `AFTER` is skipped. Inside `catch`, `$_` is the current error record. Besides `Exception.Message`, useful diagnostic fields include `FullyQualifiedErrorId`, `CategoryInfo` and `ScriptStackTrace`.

Use `finally` for cleanup, such as closing a stream you opened. It runs when error handling exits normally, including after an error. A forcibly terminated process cannot guarantee this cleanup. The markers demonstrate control flow; extra text lines would interfere with a script intended to return JSON. [Microsoft: try, catch and finally](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_try_catch_finally).

For one critical call, `-ErrorAction Stop` makes the behavior visible at the call site. `$ErrorActionPreference = 'Stop'` sets it for subsequent commands in the applicable scope unless they override it. The common parameter handles non-terminating PowerShell errors; it does not establish a general success rule for arbitrary program exit codes. [Microsoft: ErrorAction](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_commonparameters#erroraction).

## A complete entry script for a scheduled process

The following script reads a small JSON job input. It accepts only `state: ready`, writes a JSON result on success and ends the process with `0`. A missing file, invalid JSON or a different state produces `1`. The state rule is a deliberately simple example of a business precondition.

Save it as `Test-JobInput.ps1`:

```powershell
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath
)

$ErrorActionPreference = 'Stop'
try {
    $job = Get-Content -LiteralPath $InputPath -Raw -ErrorAction Stop |
        ConvertFrom-Json -ErrorAction Stop
    if ($job.state -ne 'ready') {
        throw 'Input state must be ready.'
    }

    [pscustomobject]@{
        inputPath = $InputPath
        state = 'accepted'
    } | ConvertTo-Json -Compress
    exit 0
}
catch {
    [Console]::Error.WriteLine(('Input check failed: ' + $_.Exception.Message))
    exit 1
}
```

The failure path writes to standard error and sets an explicit process exit code. This is an entry script for a separate process, not a library function to dot-source into an existing session. Specify the file path, runtime and identity in the task configuration. The [gMSA scheduled task guide](https://www.nodepilot.run/en/blog/gmsa-scheduled-task/) covers one such identity setup.

Test four separate inputs:

| Input | Expected observation |
| --- | --- |
| Existing file containing `{"state":"ready"}` | JSON with `state: accepted`, process code 0 |
| Missing file | Error message, process code 1, no success output |
| Existing file containing invalid JSON | Parser error, process code 1 |
| Existing file containing `{"state":"blocked"}` | Precondition error, process code 1 |

These cases were executed using the article code under Windows PowerShell 5.1. This does not establish a PowerShell 7 run or execution under your service account. Use the [PowerShell 5.1 and 7 comparison](https://www.nodepilot.run/en/blog/powershell-51-and-7/) to check the intended runtime explicitly.

## Avoid swallowing the error in catch

A `catch` that only writes text has handled the error. The script may then continue normally. That is useful when an intended fallback really succeeded. If the required input is still missing, a success message would be incorrect.

For reusable script components, a bare `throw` inside `catch` usually provides the appropriate propagation: the caller decides how to terminate the job. The process entry script above deliberately translates the failure into `exit 1`. Callers do not all evaluate these signals in the same way.

NodePilot's `runScript` Activity has an optional exit-code check. Without `successExitCodes`, error behavior determines success; a bare `exit 1` is therefore not a reliable substitute for `throw`. In an Activity, rethrow an unresolved error or explicitly configure accepted exit codes and verify the selected execution path. The [Activity reference](https://www.nodepilot.run/docs/en/activities-reference/) describes these settings. A successful step also establishes only the conditions the script actually checked.

## Native programs need their own success rule

This Windows example starts a new `cmd.exe` process that deliberately returns `7`. It makes no system changes.

```powershell
$ErrorActionPreference = 'Stop'
try {
    & $env:ComSpec /d /c 'exit 7'
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        throw "Native command returned $nativeExitCode."
    }
}
catch {
    'CAUGHT: ' + $_.Exception.Message
}
```

Capture `$LASTEXITCODE` immediately after calling the program. A later native command can replace it. The program defines which codes indicate success; “only 0” is part of this example. Newer PowerShell versions can additionally handle native errors through `$PSNativeCommandUseErrorActionPreference`. Behavior then depends on the version and setting; the explicit exit-code check shown here was tested under Windows PowerShell 5.1. [Microsoft: LASTEXITCODE](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_automatic_variables#lastexitcode).

Before unattended operation, run a successful case, a missing prerequisite and invalid input through the same script. Inspect output, errors and the caller's observed status for each case. Together, they establish whether error handling matches the actual job.
