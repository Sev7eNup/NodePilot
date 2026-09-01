Three defects found by running a switch to System Center Orchestrator against a real installation with an allowlist of ordinary runbooks.

## The crash

The runbooks started, the activity log went quiet, and the app disappeared:

```
Description: The process was terminated due to an unhandled exception.
Exception Info: System.Threading.Tasks.TaskCanceledException: A task was canceled.
   at ScorchRunbookReconciler.ReconcileAsync(...)
   at SwitchCoordinator.SwitchAsync(...)
   at MainWindowViewModel.SwitchAsync(...)
   at AsyncCommand.Execute(Object parameter)
```

Reconciliation runs under its own deadline linked to the caller's token. When it expired, the cancellation passed through `SwitchCoordinator` â€” whose catch filter was `when (exception is not OperationCanceledException)` â€” and then out of `AsyncCommand.Execute`, which is `async void` with `try/finally` and no `catch`. Result: no dialog, **no fail-closed cleanup**, and the target services left running, so the next start reported the other engine as active.

Note the filter was guarding against something that cannot happen: the coordinator is invoked with `CancellationToken.None`, so the only source of `OperationCanceledException` was the internal deadline.

- The coordinator now passes on only the caller's own cancellation (`when (!cancellationToken.IsCancellationRequested)`).
- The reconciler converts its deadline into a `TimeoutException` naming the runbooks or jobs that did not settle.
- `AsyncCommand` takes an error callback, so nothing escaping a command can take the process down.

## Why it reached the deadline

The verification loop required every listed runbook to be `Running`/`InProgress` **at the same moment**. Only a long-lived monitor runbook satisfies that. An ordinary runbook finished within seconds, left the Pending/Running set, and stayed in `missing` forever.

A runbook now settles once its job is running **or** once the job this switch started has finished. Unlisted jobs are still rejected exactly as before.

## The switch back to NodePilot

`No server URL configured. Run 'np config set server <URL>' or pass --server.`

`serverUrl` shipped empty, so `np` fell back to its own configuration â€” which is per-user and DPAPI-protected. The setup account is not the account that runs the switcher, so seeding the CLI at install time would land in the wrong profile. The installer writes `serverUrl` into the shipped configuration instead, from the hostname and HTTPS port it just configured, and the switcher then passes `--server` on every call. Only the copy next to the executable is touched; a machine-wide configuration under `%ProgramData%\NodePilot\EngineSwitcher` wins at load time and is left alone.

Written in place rather than round-tripped through `ConvertTo-Json`, which would escape `&` and quotes in `activeJobsPath` and reflow the file the docs tell operators to hand-edit. Verified for both the `:443` and non-default-port cases, and idempotent.

## `np` was never on the machine PATH after a GUI install

`np.exe` sits in `<install>\tools\np`, `np` in cmd says "not found", and the installation reported success.

Setup runs `Install-NodePilot.ps1` from a payload staged by `deploy/server/Build-ServerInstaller.ps1`. That script list did not contain `MachinePath.ps1` — the helper the PATH block dot-sources:

```powershell
try {
    . (Join-Path $PSScriptRoot 'MachinePath.ps1')   # throws: not in the payload
    ...
} catch {
    Write-Warn "  Could not update the machine PATH: ..."   # and that is all it ever said
}
```

Installations driven by the deployment-scripts zip carry the helper in their own list and were never affected — which is why the lab machine (script-installed, 1.2.21) has `C:\Program Files\NodePilot\tools\np` on the machine PATH and `where np` resolves it, while a double-clicked setup does not. The same omission also left `{app}\deploy` without the helper, so a later manual update or uninstall from that folder could not touch the PATH either.

The payload carries it now. Rather than maintain a third hand-written list, `Test-DeploymentTemplates.ps1` extracts the `$PSScriptRoot` references from the three entry points and asserts both staging lists cover them — verified by removing the entry again, which fails the run with `the server setup payload ships MachinePath.ps1`.

Both scripts additionally read the machine PATH back after writing it, and the installer states the outcome in its closing summary, including the reminder that an already-open console keeps the environment it started with.

## Tests

`dotnet test tests/NodePilot.EngineSwitcher.Tests` â€” 82/82 (was 75).

New: reconciliation settles when a started runbook finishes immediately; a runbook stuck in `Pending` fails with a timeout naming it; the coordinator reports a deadline and runs fail-closed; caller cancellation still propagates; `AsyncCommand` reports instead of escaping.

`deploy/Test-DeploymentTemplates.ps1` passes, with four new contracts on the installer â€” including the "name the path on ONE line" rule that exists because a line-break slip once made the PATH entry fail silently in 1.2.8 and 1.2.9.
