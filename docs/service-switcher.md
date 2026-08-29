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

## Configuration and allowlist paths

The switcher loads the first applicable configuration source:

1. `--config <absolute-file-path>`
2. `%ProgramData%\NodePilot\ServiceSwitcher\service-switcher.json`
3. `service-switcher.json` next to the executable

The server artifact includes a template next to the executable. For a machine-wide configuration,
copy it to `%ProgramData%\NodePilot\ServiceSwitcher` and use an absolute `cliPath`. Both allowlist
paths must be absolute paths and may point either to a local file (`C:\...`) or to a UNC share
(`\\server\share\...`). Relative allowlist paths are rejected. The account that starts the elevated
switcher must have read access to the files or shares.

```json
{
  "nodePilot": {
    "workflowAllowListPath": "C:\\ProgramData\\NodePilot\\ServiceSwitcher\\nodepilot-workflows.txt",
    "cliPath": "C:\\Program Files\\NodePilot\\tools\\np\\np.exe",
    "profile": "service-switcher",
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

Before the first NodePilot switch, authenticate the configured CLI profile once under the same
Windows account that runs the switcher:

```powershell
& 'C:\Program Files\NodePilot\tools\np\np.exe' auth login `
  --profile service-switcher --server https://nodepilot.example.test
```

Example list contents:

```text
Daily Maintenance; 4f12e199-bbcf-45d5-bca1-9e4e478d1202
Monitor Incoming Incidents
```

## Logs and installation

The scrollable activity panel shows the complete history for the current app session, with the
newest action first. Its Copy action copies the complete visible history. The persistent rolling
log is stored under `%ProgramData%\NodePilot\ServiceSwitcher` and is restricted to Administrators
and SYSTEM.
The title-bar toggle switches between light and dark mode; the selected theme is stored per user.

The server installer adds **NodePilot Engine Switcher** to the Start menu. In a scripted ZIP
installation, run:

```text
C:\Program Files\NodePilot\tools\service-switcher\NodePilot.ServiceSwitcher.exe
```

Release builds also publish a self-contained `NodePilot-ServiceSwitcher-<version>-win-x64.exe`, so
the utility does not require the .NET Desktop Runtime on the target server. The standalone file
still needs a configuration supplied through `%ProgramData%` or `--config`; `np.exe` remains an
explicit configured dependency for NodePilot authentication and workflow administration.
