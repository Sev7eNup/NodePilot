# NodePilot Engine Switcher

The NodePilot Engine Switcher is a small, local Windows utility for machines that host both
NodePilot and Microsoft System Center Orchestrator. It ensures that only one orchestration stack
is configured to run after a restart and that the selected stack uses an exact workload allowlist.

## Managed services

- NodePilot: resolved from the server installation marker, the desktop handoff, or the default
  service name `NodePilot`. The resolved service must run `NodePilot.Api.exe`.
- System Center Orchestrator: the exact local service names `omanagement`, `oremoting`, `omonitor`,
  and `orunbook`, when installed.

`NodePilotDb`, SQL Server, PostgreSQL, IIS, and remote services are never controlled. The utility
requires elevation and does not expose a network API.

## Switching behavior

Before a switch, the app asks for confirmation because running workflows can be interrupted. It
sets all managed services to manual start, stops the old side, and only then configures and starts
the target side. NodePilot is restored as delayed automatic; installed System Center services are
restored as automatic.

Each service gets 30 seconds for a graceful stop, except `oremoting` and `omonitor`: Microsoft
documents that these SCOrch services do not exit cleanly, so the switcher force-stops them
immediately. In both the immediate and fallback force-stop paths, the utility re-queries the Service
Control Manager and terminates only the process id currently owned by that allowlisted service. It
never matches arbitrary processes by name.

The switch completes as soon as the live service states, persisted start modes, and exact workload
allowlist have been verified. There is no fixed post-switch delay. If stopping, starting, or final
verification fails, the app attempts to stop every managed service and leaves their start modes set
to manual. It never starts the target before the source is fully stopped and it never automatically
rolls back to the old orchestrator.

Before any start mode or service is changed, the switcher reads and validates the target's
allowlist. An unavailable, unreadable, empty, ambiguous, or incomplete list aborts the switch
without changing a service. Entries may be separated by commas, semicolons, or line breaks.
Whitespace and case-insensitive duplicates are removed. Each entry is either a GUID or an exact,
unique name; use the GUID when names are duplicated.

Switching to NodePilot is also rejected before any service mutation while the interactive SCOrch
Runbook Designer is open. The designer reconnects by restarting `omanagement`; terminating it
automatically could discard unsaved runbook edits, so the operator must save and close it first.

When switching to NodePilot, the switcher also stops and verifies every pending, queued, or running
SCOrch job before it stops the SCOrch services. This prevents a job that SCOrch still records as
active from resuming the next time its runbook server starts.

After the target services are running:

- NodePilot permanently enables every listed workflow, disables every unlisted workflow, cancels
  running or pending executions of unlisted workflows, and then verifies that the enabled set
  exactly matches the allowlist. A single live operations snapshot identifies which unlisted
  workflows actually have active executions; inactive workflows no longer cause one redundant
  `cancel-all` process each. The switcher uses `np.exe` and its DPAPI-protected named session;
  the profile therefore needs an Admin or Operator login with access to every workflow.
- SCOrch stops every pending or running job whose runbook is not listed, starts one job for each
  listed runbook that has no active job, and verifies the resulting active job set. The calls use
  the current elevated Windows identity against the .NET Web API. Listed runbooks must be published
  and must not require input parameters, because the allowlist only supplies identities.

Any reconciliation or verification failure occurs after service mutation has begun and therefore
uses the same fail-closed cleanup as a service failure.

Before the source engine's services are touched, the switcher first stops the source workload's
own jobs. That step begins with a query, and a failure up to the first stopped job changes nothing
and leaves every service as it was; from the first stopped job onwards it is a mutation like any
other and arms the fail-closed cleanup. A SCOrch response that is not a readable collection — a
truncated body under HTTP 200, an error page, an unsupported `$filter` — is reported with the
request URL and the beginning of the body, because the parser message alone does not say which
call broke.

## Configuration and allowlist paths

The switcher loads the first applicable configuration source:

1. `--config <absolute-file-path>`
2. `%ProgramData%\NodePilot\EngineSwitcher\engine-switcher.json`
3. `engine-switcher.json` next to the executable

When no configuration exists, the error names every location that was checked.

Both the server artifact and the standalone zip include a template next to the executable, so the
utility is configurable wherever it is unpacked. For a machine-wide configuration, copy the
template to `%ProgramData%\NodePilot\EngineSwitcher`.

`cliPath` is optional. Left at `null`, the switcher locates `np.exe` through the installation
marker `HKLM\SOFTWARE\NodePilot\Server` (`InstallPath` plus `tools\np`) and then through the machine
PATH; a configured value wins and stays relative to the configuration file. The template ships
without one because a relative path only points at the installation while the configuration sits
inside it.

Both allowlist paths must be absolute paths and may point either to a local file (`C:\...`) or to a
UNC share (`\\server\share\...`). Relative allowlist paths are rejected. The account that starts the
elevated switcher must have read access to the files or shares.

JSON escapes every backslash, so the doubled form below is the correct one and stays recommended.
Because the file is edited by hand on the machine, `workflowAllowListPath`, `runbookAllowListPath`
and `cliPath` also accept paths written with single backslashes — `"D:\Scripts\runbooks.txt"` and
`"\\fileserver\automation\runbooks.txt"` load as written. The tolerance is limited to those three
properties; a stray backslash anywhere else, for instance in `profile`, remains a load error.

The configuration is loaded at every system check, not only when a switch starts. An unusable file
is reported in the activity history with its path and disables both switch buttons; correcting the
file releases them without restarting the switcher.

```json
{
  "nodePilot": {
    "workflowAllowListPath": "C:\\ProgramData\\NodePilot\\EngineSwitcher\\nodepilot-workflows.txt",
    "cliPath": null,
    "profile": "engine-switcher",
    "serverUrl": null,
    "commandTimeoutSeconds": 30
  },
  "systemCenterOrchestrator": {
    "runbookAllowListPath": "\\\\fileserver\\automation\\scorch-runbooks.txt",
    "apiBaseUrl": "http://localhost:81",
    "runbooksPath": "api/runbooks",
    "runbookServersPath": "api/runbookServers",
    "jobsPath": "api/jobs",
    "activeJobsPath": "api/jobs?$filter=Status in ('Pending','Running')",
    "stopJobPathTemplate": "api/jobs/{id}",
    "stopJobMethod": "PATCH",
    "requestTimeoutSeconds": 30,
    "reconciliationTimeoutSeconds": 60
  }
}
```

SCOrch's Web API uses Windows Authentication. Plain HTTP is accepted only for a loopback API URL;
a remote URL must use HTTPS. The endpoint fields are configurable because supported SCOrch web
component builds publish their exact contract in the OpenAPI file shipped with the Web API.

When starting a runbook, the switcher reads the available runbook servers from
`runbookServersPath` and sends their names with the job request. A job counts as started only in
`Running` or `InProgress`; a stale `Pending`/`Queued` job is stopped and restarted on an available
runbook server.

The configured NodePilot CLI profile can be authenticated once under the same Windows account that
runs the switcher:

```powershell
& 'C:\Program Files\NodePilot\tools\np\np.exe' auth login `
  --profile engine-switcher --server https://nodepilot.example.test
```

If that session later reaches its absolute server lifetime, the switcher opens an in-app NodePilot
sign-in dialog and automatically retries the interrupted reconciliation after a successful login.
It invokes `np auth login --username <name> --password-stdin`; the password is never placed in the
process arguments, configuration, activity history, or persistent switcher log, and is not stored by
the switcher.

Example list contents:

```text
Daily Maintenance; 4f12e199-bbcf-45d5-bca1-9e4e478d1202
Monitor Incoming Incidents
```

## Logs and installation

The scrollable activity panel shows the complete history for the current app session, with the
newest action first. Its Copy action copies the complete visible history. The persistent rolling
log is stored under `%ProgramData%\NodePilot\EngineSwitcher` and is restricted to Administrators
and SYSTEM.
The title-bar toggle switches between light and dark mode; the selected theme is stored per user.
Engine activation confirmations use the same themed in-app dialog design as NodePilot reauthentication
and show the services that will be stopped and activated before the switch begins.

The server installer adds **NodePilot Engine Switcher** to the Start menu. In a scripted ZIP
installation, run:

```text
C:\Program Files\NodePilot\tools\engine-switcher\NodePilot.EngineSwitcher.exe
```

Release builds also publish `NodePilot-EngineSwitcher-<version>-win-x64.zip`, containing the
self-contained executable and the configuration template, so the utility needs neither the .NET
Desktop Runtime nor a NodePilot installation to take a template from. Unpack it and fill in the two
allowlist paths. `np.exe` stays a dependency for NodePilot authentication and workflow
administration; it is located automatically while `cliPath` is unset.

## Migrating a machine installed before the rename

The utility was called *Service Switcher* until it was renamed to *Engine Switcher*, which moved
every persisted name. The installer places the new files, but nothing carries the old state over —
on a machine that already ran the previous version, do this once after updating:

```powershell
$old = "$env:ProgramData\NodePilot\ServiceSwitcher"
$new = "$env:ProgramData\NodePilot\EngineSwitcher"
if ((Test-Path $old) -and -not (Test-Path $new)) { Rename-Item -LiteralPath $old -NewName 'EngineSwitcher' }
$config = Join-Path $new 'service-switcher.json'
if (Test-Path $config) { Rename-Item -LiteralPath $config -NewName 'engine-switcher.json' }
```

A configuration that sits next to the executable instead needs the same rename in
`{app}\tools\engine-switcher`. The old `{app}\tools\service-switcher` directory and the previous
Start-menu entry are left behind by the update and can be deleted.

The CLI profile in the shipped template is now `engine-switcher`. An existing installation may keep
its old profile name — `cliPath` and `profile` are read verbatim — or authenticate the new one:

```powershell
& 'C:\Program Files\NodePilot\tools\np\np.exe' auth login `
  --profile engine-switcher --server https://nodepilot.example.test
```

The theme preference moves to `HKCU\Software\NodePilot\EngineSwitcher`; the old key is ignored and
the switcher starts in light mode once until the toggle is used again.
