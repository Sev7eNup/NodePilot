# Logs & diagnostics

NodePilot writes two application logs; the installers keep a transcript each. This page names every file with its location and retention, maps the common failure modes onto the right source, and lists the artifacts that belong in a support ticket. Which formats are available and how they are set is covered under [Logging](../configuration/logging).

## Which files exist

| Artifact | Pattern | Location | Contents | Retention |
|---|---|---|---|---|
| Application log | `nodepilot-YYYYMMDD.log`, plus `_001`, `_002` … on a size rollover | `C:\ProgramData\NodePilot\logs\` (server and desktop); in a development instance `src\NodePilot.Api\logs\` | Full diagnostics: boot, configuration, HTTP, engine, database, stack traces | **7 files**, with an additional rollover at 100 MB |
| Support log | `nodepilot-support-YYYYMMDD.log` (+ `_001` …) | The same folder | A curated extract without stack traces: the boot banner, allow-listed audit events, applied migrations, failed steps, output of the `log` activity | 90 files, with an additional rollover at 10 MiB |
| `SupportEvents` | A database table | The database, read through the `/support-log` page | The same events, structured, with filtering, cursor and export | 90 days via `Retention:SupportEvents` |
| Server setup transcript | `nodepilot-server-setup.log` | `%TEMP%` | A complete transcript of an installation or update run | Appended, not capped |
| Desktop provisioning log | `nodepilot-provision.log` | `%TEMP%` | A transcript of the provisioning; it ends at the failing step | Overwritten on each run |
| Installation report | `install-report.txt` | `C:\ProgramData\NodePilot\` | The result of the last server installation, without secrets | Overwritten on each installation |
| Windows Event Viewer | The *Application* log, source = the service name | The operating system | Service start, stop and crash events written by the SCM | The operating system's own policy |

Not to be confused with logs are the retention **archives** under `C:\ProgramData\NodePilot\archive\`: `executions\executions-YYYYMMDD.ndjson` and `audit\audit-*.ndjson.gz` with its `.sha256` sidecar are produced when history is trimmed, not when something is logged. Details under [Retention services](../configuration/retention).

## What NodePilot does not write

These places are deliberately empty and do not need to be searched during an incident:

- **The Electron shell keeps no log of its own.** When the desktop window reports an error, the cause is in the application log or in the provisioning log.
- **`np` and `nodepilot-mcp` do not log to a file.** `%APPDATA%\NodePilot\` holds only `config.json` and `session-<profile>.dat` as state. The MCP server writes its diagnostics to stderr, because stdout belongs to the protocol.
- **The bundled desktop PostgreSQL runs without `logging_collector`** and therefore creates no log file of its own. A `NodePilotDb` outage becomes visible through the application log and `/healthz/database`.
- **The service redirects no standard output.** The console sink goes nowhere in a service context; the files are what counts.
- **NodePilot writes no entries of its own to the Event Viewer.** What appears there under the service name comes from the Service Control Manager.

## Which log, when

| Situation | Source | What to watch for |
|---|---|---|
| A server installation or update aborts | `%TEMP%\nodepilot-server-setup.log`, then `install-report.txt` | The transcript is appended to: lines from an earlier run sit in the same file, so check the timestamps |
| The desktop reports "setup completed" but the app does not start | `%TEMP%\nodepilot-provision.log` | The file ends at the failing step; the table of causes is in [desktop troubleshooting](https://github.com/Sev7eNup/NodePilot/blob/main/docs/desktop-troubleshooting.md) |
| The service starts and stops immediately | Event Viewer → *Application*, source = the service name; then the application log | Typically a configuration or ACL problem occurring before the first write to the log |
| `/healthz/ready` stays 503 | First `/healthz/database` — it always answers HTTP 200 with `status` and `reason` — then the application log | `RejectedByServer` means wrong credentials, database selection or TLS configuration, and waiting alone does not clear it |
| A single workflow step is red | The execution detail in the interface | Step output comes from the database, not from the log files; the support log carries only the redacted short form |
| "What happened on this system?" | The `/support-log` page (admin), toggle between table and plain text | The table view filters and exports to CSV or NDJSON; the plain-text view shows the file |
| "Who changed what?" | The `/audit` page, see [Audit log](../security/audit-log) | Configuration and data changes are recorded there, not in the application log |
| Analysis across several hosts | `Logging:Format=ecs-json`, see [SIEM logging](../enterprise/siem-logging) | A collector reads the same files; NodePilot ships nothing out by itself |

## Access and pitfalls

- **`C:\ProgramData\NodePilot` is readable by administrators only.** Opening it in Explorer as a standard user gives "access denied" — that is the intended ACL, not damage. Use an elevated console.
- **`%TEMP%` belongs to the account that elevated the installer.** If a different administrator account was entered at the UAC prompt, the transcript is in *that* account's temp directory.
- **The application log keeps only seven files.** On a talkative system — or after several size rollovers in one day — it therefore reaches back correspondingly few days. For an incident further in the past the files have to be secured beforehand; the support log and `SupportEvents` reach considerably further with 90 units.
- **What ships is `Logging:Format=cmtrace`,** not the code default `text`. The files stay readable in any editor; CMTrace.exe additionally renders them with columns and colours.
- **The `Logging` section is not hot-reloadable.** A format change takes effect only after a service restart.

A daily support-log file can be fetched without file access on the server — signed in as an administrator, against the installation itself:

```powershell
Invoke-WebRequest 'https://nodepilot.contoso.local/api/diagnostics/support-log/download?date=2026-08-29' -OutFile support.log
```

The expected result is the complete plain-text file for the requested day; if it does not exist, the endpoint answers HTTP 404. Without a date, `GET /api/diagnostics/support-log` returns the last lines of today's file (200 by default, 1000 at most). Both endpoints are restricted to administrators, and the download is recorded in the audit log.

## What belongs in a ticket

For a server installation:

1. The product version and the operating mode (server or desktop).
2. The section of the application log around the time of the incident.
3. The support-log file for the day of the incident.
4. The output of `Get-Service NodePilot` and the response from `/healthz/database`.
5. For a failed installation, additionally `%TEMP%\nodepilot-server-setup.log`.

The same list applies to the desktop app, with `%TEMP%\nodepilot-provision.log` in place of the setup transcript and `Get-Service NodePilot, NodePilotDb`.

**Review logs before sending them.** Output redaction masks recognised secrets, but script output, host names and paths remain in clear text.
