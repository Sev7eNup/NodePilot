# Antivirus exclusions

NodePilot executes PowerShell and starts processes. Both collide with the standard heuristics of endpoint-security products. This page lists the exclusions required for that, each with its rationale and residual risk, and is intended as a basis for handing over to a security team.

It concerns the [Windows Server](./production) and [Desktop app](./desktop) operating modes. No NodePilot software is installed on the target machines orchestrated over WinRM; they are not the subject of this page.

**This is not about SmartScreen.** The blue dialog when a downloaded installer is started comes from a separate reputation service that ignores exclusion lists — no entry on this page affects it. See [First launch: the blue SmartScreen window](./production#first-launch-the-blue-smartscreen-window).

## Triggers

| Behaviour | Operating mode | Typical scanner reaction |
|---|---|---|
| A service running as LocalSystem writes `nodepilot_<hex>.ps1` to `%TEMP%`, hardens the ACL and executes the file with `-ExecutionPolicy Bypass` | both | The script file is blocked, the step runs into its timeout |
| Process isolation starts the PowerShell host through `CreateProcessW` with an attribute list, inheritable pipes and a job object | both | Behavioural detection as a launcher pattern |
| The installer unpacks a signature-verified artifact into `%TEMP%` and swaps the program directory | Server | A file handle prevents the move, the update aborts |
| `postgres.exe` writes continuously into `pgdata\base` and `pgdata\pg_wal` | Desktop | Throughput loss, in extreme cases a write error |
| Electron starts child processes with the same binary name and ships Chromium native DLLs | Desktop | A generic heuristic hit, the interface does not start |

## Principles

- Order of preference for exclusion types: **signature/publisher → process → path**. The server artifact is signed and verified against a pinned signer thumbprint before it is unpacked; a publisher rule is therefore the narrowest route.
- Exclusions concern real-time inspection, not EDR telemetry. Where the product separates the two, behavioural collection stays active.
- Exclusions apply per role. A domain-wide policy would be an unnecessary broadening.
- All paths are defaults. A different `-InstallPath`/`-DataPath`, or a configured `Logging:File:Path`/`Retention:*:ArchivePath`, shifts the entries accordingly.

## Windows Server

One service: `NodePilot` (display name `NodePilot Orchestrator`), running as LocalSystem or a gMSA. The database is on another host.

### Folders

| Path | Contents | Priority | Residual risk |
|---|---|---|---|
| `C:\Program Files\NodePilot` | The program directory, replaced wholesale during an update | Required | Only SYSTEM and Administrators have write access. Prefer a publisher rule over a path |
| `C:\ProgramData\NodePilot` | Logs, archives, key material, runtime configuration | Required | Contains keys and tokens; protecting them is the ACL's job, not the scanner's |
| `C:\Program Files\NodePilot.rollback.*`, `…NodePilot.backup.*` | Timestamped previous states (three are retained) | Recommended | Can be limited to the maintenance window |
| `%TEMP%\nodepilot-artifact-*` (service: `C:\Windows\Temp\…`) | Staging of the signed artifact — ~2,900 files, ~2,650 of them under 64 KB. The **most expensive point of an update**: it is not the 114 MB that costs but the file count, and a real-time scan inspects every creation individually | Recommended | A restrictive DACL at creation; the contents are then verified file by file against the signed manifest. Can be limited to the maintenance window |

### Processes

| Process | Priority | Residual risk |
|---|---|---|
| `C:\Program Files\NodePilot\NodePilot.Api.exe` | Required | Executes workflow PowerShell by design. Compensated by NodePilot's own roles, folder RBAC and audit log |
| `pwsh.exe` (`C:\Program Files\PowerShell\7`) | Required | Broad. Restrict it to the parent process `NodePilot.Api.exe` where possible |
| `powershell.exe` (`System32\WindowsPowerShell\v1.0`) | Required if PowerShell 7 is not guaranteed to be present | As above |
| `where.exe` | Recommended | A one-off path resolution at engine start, with no write access |

### Behavioural rules

| Mechanism | Conflict | Recommendation |
|---|---|---|
| Rules of the form "a service starts a script host" (including the ASR rules on process creation and obfuscated scripts) | NodePilot's core function | An exception for the parent process; do not disable the rule globally |
| Controlled folder access | Blocks writing to `C:\ProgramData\NodePilot`, so the service fails to start | Register `NodePilot.Api.exe` as trusted |
| Delete-quarantine in the program directory | If the scanner removes `powershell.config.json`, the PowerShell SDK starts an additional `powershell.exe -Version 5.1 -s` per runspace which is never terminated — the service then consumes gigabytes of memory | Report findings instead of removing them |
| Script scanning / AMSI | None | Leave enabled |

Ports (informational, not an exclusion): inbound 443 and optionally 80; outbound 5985/5986 for WinRM and 1433 or 5432 for the database. The installer creates the firewall rules `NodePilot NodePilot HTTPS` and `NodePilot NodePilot HTTP-Redirect`.

## Desktop app

Two services: `NodePilot` (the API, LocalSystem) and `NodePilotDb` (PostgreSQL through `pg_ctl.exe`, NetworkService). All ports are restricted to loopback, and no firewall rule is created.

The API sits one level deeper here than on the server — `…\NodePilot\app\NodePilot.Api.exe`. A path exclusion written for the server variant does not apply.

### Folders

| Path | Contents | Priority | Residual risk |
|---|---|---|---|
| `C:\Program Files\NodePilot\app` | The API service | Required | As for the server program directory |
| `C:\Program Files\NodePilot\desktop` | The Electron shell including Chromium native DLLs (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `libEGL.dll`, `libGLESv2.dll`) | Required | Static program code; a single quarantine finding makes the interface unable to start |
| `C:\Program Files\NodePilot\pgsql` | The bundled PostgreSQL | Recommended | Alternatively exclude only the six processes used instead of the whole folder |
| `C:\ProgramData\NodePilot` | `pgdata`, `secrets`, `logs`, `backups`, `rollback`, `archive`, `desktop.json` | Required | Contains database passwords; protecting them is the ACL's job |
| `%APPDATA%\NodePilot` | The shell's Chromium profile and caches | Recommended | Cache data only, no executable code |
| `%LOCALAPPDATA%\NodePilot` | `admin-setup.handoff`, the one-time token for the first sign-in | Recommended | A short-lived text file |

### Processes

| Process (under `C:\Program Files\NodePilot`) | Priority | Residual risk |
|---|---|---|
| `app\NodePilot.Api.exe` | Required | As on the server |
| `desktop\NodePilot.exe` | Required | Starts child processes with the same name, and through the tray menu a UAC-elevated `powershell.exe` to restart the service. Restrict the exclusion to the path, not the file name |
| `pgsql\bin\postgres.exe`, `pgsql\bin\pg_ctl.exe` | Required | Bound to `127.0.0.1` only |
| `pgsql\bin\initdb.exe`, `psql.exe`, `pg_dump.exe`, `pg_restore.exe` | Recommended | Run only during installation and update |
| `powershell.exe` / `pwsh.exe` | Required | As on the server |

The remaining 37 programs in `pgsql\bin` are never invoked and need no exclusion.

Ports (informational): loopback 47000–47049 for the API, 47100–47149 for PostgreSQL; one free port from each range is chosen at installation time and pinned.

## Common: temporary script files

| Pattern | When it appears | Priority | Residual risk |
|---|---|---|---|
| `%TEMP%\nodepilot_*.ps1` — under LocalSystem `C:\Windows\Temp\nodepilot_*.ps1` | On every isolated or process-based `runScript` step; deleted afterwards | Required | The broadest entry. Exclude the name pattern only, never the entire temp directory, and restrict it to the parent process where possible |
| `%TEMP%\NodePilot-Transcript-*.log` | Only with transcription enabled; cleans itself up after 24 hours | Recommended | A plain text file |

An exclusion covering "everything under the two NodePilot directories" looks complete but leaves precisely this script file unprotected — it lives outside every NodePilot-named path.

The **default execution mode writes no temporary file and starts no child process**: scripts run in an in-process runspace pool inside `NodePilot.Api.exe`. If isolation and explicit PowerShell hosts are not used in the environment, the entries of this section and the script-host processes do not apply.

## Only during installation and update

| Path | Operating mode | Priority |
|---|---|---|
| `%TEMP%\nodepilot-artifact-*` (the installer **and** the updater) | Server | Recommended, for installation and update |
| `%TEMP%\nodepilot-provision.log` | Desktop | Recommended |
| `NodePilot-Desktop-Setup-*.exe`, `unins000.exe` | Desktop | Recommended |
| `C:\ProgramData\NodePilot\backups\pre-update-*.dump`, `…\rollback` | Desktop | Recommended |

These entries can be limited to a maintenance window.

## Do not exclude

| Do not exclude | Reason |
|---|---|
| `C:\Windows\Temp` as a whole | Writable by many processes; exclude only the pattern `nodepilot_*.ps1` |
| The target paths of workflows | The file, folder, text-file, ZIP and registry activities write to freely configurable targets. Those writes are exactly what should keep being inspected |
| The whole `pgsql\bin` folder | 37 of the 43 programs are never invoked |
| User profiles | `%APPDATA%\NodePilot` and `%LOCALAPPDATA%\NodePilot` are sufficient |
| `pwsh.exe`/`powershell.exe` system-wide without a parent-process restriction | The narrower rule is preferable where the product supports it |
| Disabling script scanning / AMSI | NodePilot is not hindered by it |
| The file type `*.npbackup` | The destination of the backup export is chosen freely by the administrator |

## Symptoms of missing exclusions

| Symptom | Probably missing exclusion |
|---|---|
| A step stays in `Running` and runs into its timeout | `%TEMP%\nodepilot_*.ps1` or the script-host process |
| The service does not start after an update, program directory incomplete | The program directory |
| The service starts but no log file appears; the first sign-in is impossible | `C:\ProgramData\NodePilot`, or controlled folder access |
| The service's memory grows over hours, with many `powershell.exe` processes | `powershell.config.json` quarantined |
| Desktop: the interface does not start or stays blank | `C:\Program Files\NodePilot\desktop` |
| Desktop: `NodePilotDb` does not start | `C:\ProgramData\NodePilot\pgdata`, or `postgres.exe`/`pg_ctl.exe` |
| Saving in the admin settings has no effect | `C:\ProgramData\NodePilot` (the atomic replace operation) |

The detailed version with all paths and evidence is in the repository under `docs/av-exclusions.md`.
