# Antivirus exclusions for NodePilot

A hand-off document for the antivirus / endpoint-security team. It lists which folders, processes and
file patterns NodePilot touches in operation, why an exclusion is necessary, and what residual risk it
creates.

The document is deliberately **product-neutral** — it contains no `Add-MpPreference` lines or scripts.
The concrete implementation (GPO, Intune, the vendor's own console) stays with the security team.

**Scope note: this is not about SmartScreen.** This document addresses the **virus scanner** only
(real-time inspection, heuristics, ASR, controlled folder access). The blue "Windows protected your PC"
dialog that appears when a **downloaded** installer is started comes from Microsoft Defender
SmartScreen — a separate reputation service that **ignores** exclusion lists. No entry in this document
affects it. Explanation and procedure:
[deployment-guide.md → First run: the SmartScreen prompt](deployment-guide.md#first-run-the-smartscreen-prompt).

**Applicability**

| Role | Covered |
|---|---|
| Production server (Windows service, `deploy/` installer) | yes → [Part A](#part-a--production-server) |
| Desktop app (offline installer, `deploy/desktop/`) | yes → [Part B](#part-b--desktop-app) |
| Both roles together (PowerShell execution) | yes → [Part C](#part-c--powershell-execution-both-roles) |
| Target machines orchestrated over WinRM | no — no NodePilot software runs there; see [note](#out-of-scope-target-machines) |
| Developer workstations | no |

---

## Why exclusions are necessary

NodePilot is a workflow orchestrator: the core of the application consists of executing PowerShell and
starting processes. Five behaviours that follow from that regularly collide with standard heuristics:

1. **A service running as `LocalSystem` writes a script to `%TEMP%` and executes it.**
   For the "isolated process" and "explicit PowerShell host" execution modes, NodePilot writes the
   workflow script as `nodepilot_<32-hex>.ps1` into the temp directory, hardens its ACL to
   owner-full-control (all inherited rights are removed) and starts it with
   `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File`. File creation, ACL hardening and
   immediate execution in the temp directory is the strongest heuristic signature in the entire
   product.

2. **Process isolation uses low-level Windows APIs.**
   With `config.isolated: true`, NodePilot starts the PowerShell host not through the standard .NET
   paths but through `CreateProcessW` with
   `PROC_THREAD_ATTRIBUTE_JOB_LIST`/`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, inheritable anonymous pipes
   and a job object with `KILL_ON_JOB_CLOSE`. The purpose is crash and leak containment (the job
   reliably cleans up orphaned child processes). Behavioural analysis reads the same API combination
   as an injector or launcher pattern.

3. **The installer unpacks a signed artifact and swaps the program directory.**
   Installation and update unpack into `%TEMP%\nodepilot-artifact-<GUID>\`, move the old program
   directory to `…NodePilot.rollback.<timestamp>` or `…NodePilot.backup.<timestamp>`, and put the new
   one in the same place. A real-time scanner holding a file handle during that makes move or delete
   operations fail — the installer then aborts mid-swap and rolls back.

4. **PostgreSQL produces continuous, high-frequency file I/O** (desktop role only).
   `postgres.exe` writes continuously into `pgdata\base\` and `pgdata\pg_wal\`. Real-time scanning of
   those directories costs noticeable throughput; in the worse case a scanner handle blocks a WAL write
   and the database responds with a write error.

5. **The desktop interface is an Electron application** (desktop role only).
   `NodePilot.exe` starts child processes with **the same binary name** (`--type=renderer`,
   `--type=gpu-process`, `--type=utility`) and ships native libraries that frequently produce heuristic
   hits (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `libEGL.dll`, `libGLESv2.dll`).

---

## Principles for the implementation

- **Order of preference for exclusion types: signature/publisher → process → path.**
  The server artifact is signed, and the installer verifies the signature against a pinned signer
  thumbprint before it unpacks anything. Where the AV product supports publisher-based rules, that is
  the narrowest and hardest-to-abuse route. Path exclusions are the last resort.
- **An exclusion should relieve real-time inspection, not switch off telemetry.**
  Where the product distinguishes between scan exclusion and EDR visibility, behavioural/telemetry
  collection should stay active. All entries below are meant as scan/block exceptions.
- **Exclusions apply per role, not across the board.**
  Part A belongs on the orchestrator servers, part B on the desktop installations. A shared policy for
  every Windows system in the domain would be an unnecessary broadening.
- **The "Priority" column**
  *Required* = without this exclusion, expect malfunction or an aborted operation.
  *Recommended* = without it NodePilot works, but with measurable throughput loss or recurring false
  positives.
- **Paths are defaults.** If `-InstallPath`/`-DataPath` differ at installation time, adjust the entries
  accordingly — see [below](#when-this-list-has-to-be-reviewed-again).

---

## Part A — Production server

Role: a Windows server running the **`NodePilot`** service (display name `NodePilot Orchestrator`),
executing as `LocalSystem` or as a group managed service account (gMSA, `DOMAIN\svc-nodepilot$`). The
database sits on a **different** host — the server installer brings no local PostgreSQL with it.

### A.1 Folders

| Path | Contents | Why it is needed | Priority | Residual risk |
|---|---|---|---|---|
| `C:\Program Files\NodePilot\` | Program directory: `NodePilot.Api.exe`, several hundred managed DLLs, `wwwroot\` (the SPA), `PSModules\`, `knowledge\` | Replaced wholesale during an update; a held scanner handle makes moving/deleting fail. Also contains `powershell.config.json` — see the warning under [A.4](#a4-behavioural-rules-asr-controlled-folder-access) | Required | Only SYSTEM/Administrators have write access; an attacker with those rights already owns the system. Residual risk: a foreign DLL placed there would no longer be scanned — compensable through a publisher rule instead of a path, and through integrity monitoring of the directory |
| `C:\ProgramData\NodePilot\` | Runtime data: `logs\`, `archive\`, `jwt-secret.key`, `data-protection-keys\`, `admin-setup.token`, `appsettings.runtime.json`, `install-report.txt`, `postgres-root-ca.pem` | Continuous writing (rolling logs, atomic configuration writes via `.tmp` + `File.Replace`, gzip archives). Scanner handles on the target file make the atomic replace step fail | Required | The directory is restricted by ACL to the service account + Administrators. It holds **key material and tokens** — the exclusion does not prevent their theft (file access control is responsible for that), it only reduces detection of a malicious file placed there |
| `C:\Program Files\NodePilot.rollback.*`<br>`C:\Program Files\NodePilot.backup.*` | Timestamped copies of the previous program directory (three are retained) | Only created during installation/update; contain the same binaries as above | Recommended | As for the program directory. Can be limited to a maintenance window |
| `%TEMP%\nodepilot-artifact-*`<br>(service context: `C:\Windows\Temp\nodepilot-artifact-*`) | Staging of the signed artifact: the installer **and** the updater unpack the zip here first — roughly **2,900 files**, about 2,650 of them smaller than 64 KB — and then verify each one against the signed manifest | This is the **most expensive point in the entire update** and the reason an upgrade appears to sit in one place for minutes: it is not the data volume (114 MB) that costs, it is the file count. A real-time scan inspects every creation individually and multiplies the runtime; a held handle additionally makes cleaning up the staging folder fail | Recommended | The folder already carries a restrictive DACL (SYSTEM + Administrators + the calling user, set atomically at creation), and its contents are verified **against the signed manifest** immediately after unpacking — file by file, comparing length and SHA-256. The exclusion therefore lowers detection exactly where cryptographic verification already happens. Can be limited to a maintenance window |

Explicitly **not** included: folders that workflows write to. See
[What explicitly should not be excluded](#what-explicitly-should-not-be-excluded).

### A.2 Processes

| Process | Path | Role | Why it is needed | Priority | Residual risk |
|---|---|---|---|---|---|
| `NodePilot.Api.exe` | `C:\Program Files\NodePilot\NodePilot.Api.exe` | The service itself. Contains the workflow engine, the in-process PowerShell runspace pool and the WinRM client | Starts child processes, opens inheritable pipes, creates job objects, reads and writes continuously under `ProgramData` | Required | By design the process executes arbitrary PowerShell code chosen by the workflow author. A process exclusion makes its file accesses invisible. **Compensating control:** NodePilot has its own role/folder permissions and a complete audit log for every workflow change and execution |
| `pwsh.exe` | `C:\Program Files\PowerShell\7\pwsh.exe` | PowerShell 7, the preferred host for isolated and explicitly process-based steps | Started with `-ExecutionPolicy Bypass -File <temp script>` | Required | A process exclusion for a generic script host is the broadest rule in this list. **Narrow it where possible:** only when the parent process is `NodePilot.Api.exe`, or only in combination with the file pattern from [C.1](#c1-temporary-script-and-transcript-files) |
| `powershell.exe` | `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` | Fallback host when PowerShell 7 is not installed | as above | Required unless PowerShell 7 is guaranteed to be present | as above |
| `where.exe` | `C:\Windows\System32\where.exe` | Called **once** at engine start to locate the PowerShell host | A call by a service can be classified as discovery behaviour | Recommended | Very low — pure path resolution with no write access |
| `np.exe` | `C:\Program Files\NodePilot\tools\np\np.exe` | The operations CLI. Shipped with the installer since 1.2.8 and placed on the machine `PATH`; a pure HTTPS client against NodePilot's own REST API | Invoked interactively by administrators, creates a DPAPI-protected session file under `%APPDATA%` | Recommended | Low — no service context, no child processes. **Note:** the bundled client binaries are themselves **not** Authenticode-signed (only the installer is), so a publisher rule does not apply here — use a process or path rule |
| `nodepilot-mcp.exe` | `C:\Program Files\NodePilot\tools\mcp\nodepilot-mcp.exe` | MCP server for AI agents, shipped since 1.2.8. Started over stdio by an agent, speaks HTTPS against the REST API only | A process started by an editor/agent that opens a network connection can be flagged as unusual | Optional — only needed if the MCP server is used on this host | As for `np.exe`: unsigned, no service context. Runs only while an agent keeps it open |

Depending on the workflow, generated PowerShell additionally invokes built-in Windows tools: `sc.exe`
(service management), `shutdown.exe` and `cmd.exe /c shutdown /a` (power management), the WMI/CIM
infrastructure (`WmiPrvSE.exe`) and the task scheduler. These normally run on the **target machine**,
not on the orchestrator, and need no NodePilot-specific exclusion there. They are listed here only so
that an alert on `NodePilot.Api.exe → powershell.exe → sc.exe` can be classified as expected.

### A.3 Temporary file patterns

See [Part C](#part-c--powershell-execution-both-roles) — the patterns are identical for both roles. For
a service running as `LocalSystem`, `%TEMP%` resolves to `C:\Windows\Temp\`.

### A.4 Behavioural rules, ASR, controlled folder access

| Rule/mechanism | Conflict | Recommendation |
|---|---|---|
| Behavioural rules of the form **"a service or Office process starts a script host"** (in Microsoft Defender: the ASR rules on process creation from PSExec/WMI and on obfuscated scripts) | That is precisely NodePilot's core function: `NodePilot.Api.exe` (a service, `LocalSystem`) starts `pwsh.exe`/`powershell.exe` | An exception for `NodePilot.Api.exe` as the parent process. Do **not** disable the rule globally |
| **Controlled folder access / folder protection** | Blocks writing into `C:\ProgramData\NodePilot\` and makes the service start fail, because neither the log nor the key file can be created | Register `NodePilot.Api.exe` as a trusted application |
| **Quarantining individual files in the program directory** | If `powershell.config.json` (which sits next to `System.Management.Automation.dll`) is removed, a deliberately configured compatibility block disappears. The PowerShell SDK then starts an additional `powershell.exe -Version 5.1 -s` **per runspace in the pool**, which is never terminated — in one real case this made the service consume several gigabytes of memory | Prevent delete-quarantine on `C:\Program Files\NodePilot\`; report findings instead of removing them |
| **Script scanning / AMSI** | Unproblematic and explicitly desirable | Leave enabled |

### A.5 Network (informational, not an exclusion)

No exclusion needed — listed only for context, in case the AV/firewall side uses the same console.

| Direction | Port | Purpose |
|---|---|---|
| inbound | TCP 443 | Web interface + REST API + SignalR (`/hubs/execution`, no separate port), bound on all addresses |
| inbound | TCP 80 | Redirect to HTTPS; can be disabled by configuration |
| outbound | TCP 5985 / 5986 | WinRM to the target machines (HTTP/HTTPS, configured per machine) |
| outbound | TCP 1433 or 5432 | SQL Server or PostgreSQL |

The installer creates two firewall rules: `NodePilot NodePilot HTTPS` and — if HTTP is bound —
`NodePilot NodePilot HTTP-Redirect` (both in the *Domain* profile).

---

## Part B — Desktop app

Role: a single-machine installation from the offline installer. **Two** Windows services, a bundled
PostgreSQL and an Electron interface. All network services are restricted to loopback; **no** firewall
rule is created.

> **An important difference from part A:** both roles use `C:\Program Files\NodePilot`, but in the
> desktop installation the API sits one level deeper — `…\NodePilot\app\NodePilot.Api.exe` instead of
> `…\NodePilot\NodePilot.Api.exe`. A path exclusion written for the server variant does not apply here.

### B.1 Services

| Service name | Display name | Program | Account |
|---|---|---|---|
| `NodePilot` | `NodePilot` | `C:\Program Files\NodePilot\app\NodePilot.Api.exe` | `LocalSystem` |
| `NodePilotDb` | `NodePilot Database` | `C:\Program Files\NodePilot\pgsql\bin\pg_ctl.exe` (starts `postgres.exe`) | `NT AUTHORITY\NetworkService` |

### B.2 Folders

| Path | Contents | Why it is needed | Priority | Residual risk |
|---|---|---|---|---|
| `C:\Program Files\NodePilot\app\` | The API service and its dependencies | As in part A: an update swaps the directory; contains `powershell.config.json` | Required | As in part A |
| `C:\Program Files\NodePilot\desktop\` | The Electron interface: `NodePilot.exe` plus native DLLs (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `dxil.dll`, `libEGL.dll`, `libGLESv2.dll`, `vulkan-1.dll`), `resources\app.asar`, `*.pak`, `icudtl.dat`, `snapshot_blob.bin` | Chromium native libraries regularly produce generic heuristic hits; a single quarantine finding makes the interface unable to start | Required | Static program code, changed only by the installer/update. The residual risk equals that of any excluded program directory |
| `C:\Program Files\NodePilot\pgsql\` | The bundled PostgreSQL (binaries, `lib`, `share`) | Read in full by `pg_ctl.exe`/`postgres.exe` at startup | Recommended | Contains 43 programs, of which NodePilot uses only six. To keep the surface small, exclude only the six processes from [B.3](#b3-processes) instead of the folder |
| `C:\ProgramData\NodePilot\` | `pgdata\` (the database cluster), `secrets\` (database passwords plus `appsettings.runtime.json` and its rollback copies), `logs\`, `backups\`, `rollback\`, `archive\`, `desktop.json`, `jwt-secret.key`, `data-protection-keys\` | Continuous database and WAL I/O plus all runtime writes by the API | Required | As in part A. The desktop installer protects `secrets\`, including the runtime overrides, with an ACL for SYSTEM and Administrators only; the exclusion changes nothing about that access protection |
| `%APPDATA%\NodePilot\` (per user) | The interface's Chromium profile: `Cache`, `GPUCache`, `Code Cache`, cookies | High-frequency cache I/O while the interface is in use | Recommended | Browser cache data only; no executable code |
| `%LOCALAPPDATA%\NodePilot\` (per user) | `admin-setup.handoff` — the one-time token for the first sign-in | Read and deleted on first start | Recommended | A single short-lived text file |

### B.3 Processes

| Process | Path | Why it is needed | Priority | Residual risk |
|---|---|---|---|---|
| `NodePilot.Api.exe` | `C:\Program Files\NodePilot\app\` | As in part A | Required | As in part A |
| `NodePilot.exe` | `C:\Program Files\NodePilot\desktop\` | The Electron interface. Starts child processes with **the same name** using `--type=renderer/gpu-process/utility`; through the tray menu additionally `powershell.exe` with UAC elevation, to restart the API service | Required | The self-invocation chain and the elevation from a GUI are conspicuous in their own right. The exclusion should be narrowed to the path in the program directory, not to the bare file name `NodePilot.exe` |
| `postgres.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | The database server process; continuous I/O in `pgdata\` | Required | Binds to `127.0.0.1` only, TLS is off, port from the range 47100–47149 |
| `pg_ctl.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Service host, starting/stopping the cluster | Required | Low |
| `initdb.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Creates the cluster — runs **only** during the first installation | Recommended | Low |
| `psql.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Creates the role and database — only during installation | Recommended | Low |
| `pg_dump.exe` / `pg_restore.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Backup before each update, and rollback | Recommended | Low; they run only in the maintenance window |
| `powershell.exe` / `pwsh.exe` | System paths | Workflow execution — identical to part A | Required | See [A.2](#a2-processes) |
| `np.exe` / `nodepilot-mcp.exe` | `C:\Program Files\NodePilot\tools\np\`<br>`C:\Program Files\NodePilot\tools\mcp\` | The operations CLI and the MCP server, shipped since 1.2.8. Published **self-contained** in the desktop package, so each brings its own .NET runtime (~80 MB per directory) | Optional — only needed if they are used on this device | Pure HTTPS clients against the local API, no service context. Both are **not** Authenticode-signed (only the installer is) — a publisher rule does not apply, use a process/path rule. Unlike on the server, they are **not** on the `PATH` here |

The remaining 37 programs in `pgsql\bin` (`pgbench.exe`, `pg_upgrade.exe`, `stackbuilder.exe`, …) are
**never** invoked by NodePilot and need no exclusion.

### B.4 Network (informational)

| Direction | Port | Purpose |
|---|---|---|
| Loopback | TCP 47000–47049 (a free port is chosen at installation time and pinned) | Web interface + API, `localhost` only |
| Loopback | TCP 47100–47149 (likewise) | PostgreSQL, bound to `127.0.0.1` |

No inbound connections from outside, no firewall rule.

---

## Part C — PowerShell execution (both roles)

This part concerns **the executing host**, i.e. the orchestrator server or the desktop installation.

### C.1 Temporary script and transcript files

| Pattern | When it appears | Why it is needed | Priority | Residual risk |
|---|---|---|---|---|
| `%TEMP%\nodepilot_*.ps1`<br>under `LocalSystem`: `C:\Windows\Temp\nodepilot_*.ps1` | On every workflow step that runs isolated or with an explicitly selected PowerShell host. Deleted again after the run | The file is created, ACL-hardened and immediately executed with `-ExecutionPolicy Bypass` — the most common blocking pattern | Required | **The broadest entry in this list.** `C:\Windows\Temp` is writable by many processes; an attacker who places a file there matching this naming scheme would evade the scanner. **Therefore: exclude the name pattern only, never the entire temp directory**, and where possible additionally restrict it to the parent process `NodePilot.Api.exe` |
| `%TEMP%\NodePilot-Transcript-*.log` | Only for steps with transcription enabled (`transcript`); cleans itself up after 24 h | Written during the run and read back afterwards | Recommended | A plain text file with no execution path |

> **A common mistake:** an exclusion covering "everything under `C:\Program Files\NodePilot` and
> `C:\ProgramData\NodePilot`" looks complete but leaves precisely this script file unprotected — it
> lives outside every NodePilot-named path.

### C.2 The default case needs none of this

The default execution mode writes **no** temporary file and starts **no** child process: scripts run in
an in-process runspace pool inside `NodePilot.Api.exe`. Temp files and PowerShell child processes only
arise when a step explicitly requests isolation or a specific PowerShell host. If those execution modes
are not used in the environment, the entries from
[C.1](#c1-temporary-script-and-transcript-files) and the script-host processes from
[A.2](#a2-processes) do not apply.

### C.3 Folder monitoring by NodePilot itself

The file-watcher trigger monitors a directory chosen by operations for changes. If AV software moves,
renames or quarantines files there, that produces genuine trigger events and starts workflows. This is
not a case for an exclusion, but it is a known interaction point during troubleshooting.

---

## Only during installation and update

These entries can be limited to a maintenance window.

| Path | Role | Purpose | Priority | Residual risk |
|---|---|---|---|---|
| `%TEMP%\nodepilot-artifact-*\` | Server | The unpacked, signature-verified installation artifact — the installer **and** the updater use the same path (details and rationale in A.1) | Recommended, for installation and update | The contents were verified against a pinned signer thumbprint before unpacking. Time-limiting recommended |
| `%TEMP%\nodepilot-provision.log` | Desktop | A transcript of the provisioning, for troubleshooting | Recommended | A plain text file |
| `NodePilot-Desktop-Setup-*.exe` | Desktop | The offline installer | Recommended | A signed setup; prefer a publisher rule |
| `unins000.exe` in `C:\Program Files\NodePilot\` | Desktop | The uninstall routine | Recommended | Low |
| `C:\ProgramData\NodePilot\backups\pre-update-*.dump`<br>`C:\ProgramData\NodePilot\rollback\` | Desktop | Database backup and the binary rollback state before each update | Recommended | Contains database content; ACL-protected |

---

## Symptoms of missing exclusions

For quick attribution if the exclusions were set incompletely.

| Symptom | Probably missing exclusion |
|---|---|
| A workflow step stays in *Running* indefinitely and runs into its timeout | `%TEMP%\nodepilot_*.ps1` or the script-host process |
| A step fails immediately with a file access error on a `.ps1` under `C:\Windows\Temp` | `%TEMP%\nodepilot_*.ps1` |
| The service no longer starts after an update, program directory incomplete | The program directory (a handle held during the directory swap) |
| The service starts but no log file appears; first sign-in is impossible | `C:\ProgramData\NodePilot\`, or folder protection / controlled folder access |
| The service's memory consumption grows into the gigabytes over hours, with many `powershell.exe` processes | `powershell.config.json` was removed from the program directory (quarantined) |
| Desktop: the interface does not start or shows an empty window | `C:\Program Files\NodePilot\desktop\` (a native DLL quarantined) |
| Desktop: the `NodePilotDb` service does not start or runs into a timeout | `C:\ProgramData\NodePilot\pgdata\`, or `postgres.exe`/`pg_ctl.exe` |
| Saving in the admin settings fails or does not take effect | Server: `C:\ProgramData\NodePilot\appsettings.runtime.json`; desktop: `C:\ProgramData\NodePilot\secrets\appsettings.runtime.json` (each an atomic replace including a temporary file) |
| Sporadic errors when archiving old executions or audit entries | `C:\ProgramData\NodePilot\archive\` |

---

## What explicitly should **not** be excluded

| Do not exclude | Reason |
|---|---|
| `C:\Windows\Temp\` as a whole | Writable by many processes. Exclude only the pattern `nodepilot_*.ps1` |
| Paths that workflows write to | The file, folder, text-file, ZIP and registry activities write to freely configurable targets — potentially anywhere. Those writes are exactly what should keep being inspected. NodePilot covers this area through its own roles, folder permissions, path checks and the audit log |
| The whole `pgsql\bin` folder | 37 of the 43 programs are never invoked. The six from [B.3](#b3-processes) are enough |
| User profiles (`C:\Users\…`) | NodePilot only writes `%APPDATA%\NodePilot` and `%LOCALAPPDATA%\NodePilot` there — those two subfolders suffice |
| `pwsh.exe`/`powershell.exe` **system-wide without a parent-process restriction** | Where the AV product supports restricting to the parent process `NodePilot.Api.exe`, that is considerably narrower and should be used |
| Disabling script scanning / AMSI | NodePilot is not hindered by it and it should stay enabled |
| `*.npbackup` files | The destination of the backup export is chosen freely by the administrator — a blanket file-type exclusion would be an unnecessarily broad rule |

---

## Out of scope: target machines

**No NodePilot software is installed** on the Windows hosts orchestrated over WinRM — the
orchestration is agentless. There, the built-in Windows WinRM service executes the steps in
`wsmprovhost.exe`; WMI queries run through `WmiPrvSE.exe`. Whether adjustments are needed for that
depends on the existing policy for administrative remote execution and is deliberately not part of this
document.

Conversely: the orchestrator itself starts **no** child process for WinRM. The remote connection runs
entirely inside `NodePilot.Api.exe`.

---

## When this list has to be reviewed again

- The installation deviates from `-InstallPath` = `C:\Program Files\NodePilot` or `-DataPath` = `C:\ProgramData\NodePilot`.
- `Logging:File:Path`, `Logging:SupportLog:Path` or `Retention:*:ArchivePath` were pointed at directories outside the data directory.
- The service name was changed at installation time via `-ServiceName` (this also affects the names of the firewall rules).
- Desktop: the loopback ports chosen at installation time have shifted because of a reinstallation.
- A new activity or a custom activity starts a process not listed so far.
- A NodePilot update changes the directory layout — the release notes will say so explicitly.

---

## Related documentation

- [deployment-guide.md](deployment-guide.md) — artifact verification, building from source, and deployment troubleshooting
- [desktop-troubleshooting.md](desktop-troubleshooting.md) — desktop app troubleshooting
- [claude-reference.md](claude-reference.md) — deployment architecture, gMSA, Kestrel HTTPS, configuration keys
- `deploy/README.md` — the server installer in detail
- `deploy/desktop/README.md` — the desktop installer in detail
