# Activity reference

This reference describes the configuration and outputs of every activity type.

## AI Agent / AI Agent Team

`aiAgent` performs a task through repeated model and tool calls. `aiAgentTeam` contains
one supervisor and sequential specialists with separate sessions. Only the outer team
is a workflow step; click a visible member to configure its role, instructions and tools.
`needs_input` from a specialist goes to the supervisor, not to a user dialogue.

- **Config:** `task`, `agent` (individual) or `members` (team), `resultFormat` (`text`/`json`),
  `resultSchema`, `maxModelCalls`, `maxToolCalls`, `maxDelegations`, `timeoutSeconds`.
  Each agent/member has a stable `id`, free `role`, `instructions`, optional `model`,
  `tools`, `skillIds` and `useServiceIdentity`. Team members also have `targetMachineId`
  and `credentialId`; individual agents use the usual node target/credential fields.
  Exactly one member has `isSupervisor: true`.
  Other members may optionally set `isReviewer: true` (designer: “Team function”).
  This review function is independent of the free role label. Every selected reviewer
  must review the current work. Open `needs_input`/`failed` responses block successful
  completion; only the same member can close them with `completed`. New specialist
  responses or supervisor tool calls invalidate previous reviews. After at most two
  additional corrective supervisor turns within existing budgets, incomplete review fails.
- **Tools:** allowlisted files, HTTP GET/HEAD with optional host restrictions, published workflows, PowerShell, CMD, installed
  Git Bash and selected tools from administrator-registered MCP servers. MCP supports
  stdio and Streamable HTTP. Settings → AI agents manages servers, immutable ZIP skill
  versions and runtime ceilings. Skills use root `SKILL.md` with YAML name/description,
  resources and optional scripts. A script needs the corresponding selected shell tool.
- **Read-only checks:** shells and skill scripts accept a checked subset of read commands.
  HTTP needs no URL whitelist, rejects bodies/redirects and retains network protection.
  MCP read approvals are administrator-managed and pinned to server/tool revisions; remote
  services and credentials must enforce read access. Child workflows and their resolved steps
  are checked at runtime, including synchronous descendants. Unsupported operations fail closed.
- **Identity:** choose a credential/machine default, or explicitly select service identity
  and enable the administrator option. Shell can use the full OS rights of that identity;
  tool lists and working directories are not shell isolation. Windows local/WinRM only.
- **Defaults:** individual 20 model calls / 40 tool calls / 20 minutes; team 100 / 500 /
  20 delegations / 30 minutes, shared. Two concurrent runs per server process. A zero
  timeout uses the default. Timeout includes queue time, tools and child workflows.
  Automatic retries are rejected. Stop/restart does not undo completed side effects.
- **Logs:** `files_search` filters on the target; `logs_collect` transfers blocks into
  temporary shared storage, at most 250 MB per run/team. `logs_search` returns bounded
  source excerpts. Raw files are removed; evidence used in tool results remains in history.
- **Context:** a maximum of 250,000 **output tokens** per model call; lower profile and
  model limits still apply. The separate input guard is configured in characters.
  At 75%, the host compacts older complete exchanges into untrusted working notes,
  preserving the original task and current exchanges. Every member can recall stored
  observations through `evidence_list`/`evidence_read` without repeating remote actions.
  `evidence_analyze` examines bounded overlapping sections and records their ranges,
  findings and open questions. Summaries consume the shared model/time budget; they
  are neither original evidence nor fresh observations for review approval. Compaction is journaled.
- **Outputs:** `output`, `error`, `param.agentRunId`, `param.outcome`, `param.outcomeReason`, `param.modelCalls`, `param.toolCalls`,
  `param.delegations`, `param.promptTokens`, `param.completionTokens`. JSON is validated
  locally with at most one tool-free repair round within budget. External schema references
  are rejected. Agent history shows durable numbered events and catches up after reconnect.

Publishing authorizes autonomous use of selected tools. The existing chat approval rules
and `llmQuery` behavior stay unchanged. Enable AI and an active LLM profile first.

| Scope | Execution location |
|---|---|
| **Remote** | A Windows target system through `targetMachineId` and WinRM |
| **Engine-local** | The NodePilot API process |
| **Hybrid** | Remote or engine-local depending on the configuration |

Every step supports `config.retry` with `maxAttempts`, `backoff`, `initialDelayMs` and `maxDelayMs`. Permanent remote failures are exempt from it: a denied WinRM logon, a session blocked by the SSL policy and a credential that cannot be decrypted fail the step on the first attempt — repeating them cannot help, and repeated failed logons can lock a domain account out. `config.timeoutSeconds` bounds a single step. The execute request can additionally bound the whole execution.

---

## `runScript`

**Hybrid.** Runs PowerShell — with a machine set, through NodePilot's managed WinRM session; **without** a machine, engine-local in the API host, where the script can also manage the remote connection itself via `Invoke-Command`/`New-PSSession` (the SCOrch style; trade-offs in [Remote execution](configuration/remote-execution)).

- **Config:** `script`, `engine` (`auto`/`pwsh`/`powershell`), `timeoutSeconds`, `isolated` (bool, local only — its own process inside a Windows job object: crash/leak containment, no orphaned processes) + optional caps `memoryLimitMb` (makes allocations fail, does not terminate) / `maxProcesses`, `successExitCodes` (comma-separated, an opt-in exit-code gate)
- **Engine-local = the PowerShell Core SDK:** available are the core modules, the system's CDXML modules (ScheduledTasks, NetTCPIP, …) and the bundled `Microsoft.PowerShell.Archive` (`Compress-`/`Expand-Archive`). Pure Windows PowerShell modules (edition `Desktop` without a Core flag) are **not** silently loaded through a compatibility session — the call fails with a clear message. If you deliberately need such a module: `Import-Module -UseWindowsPowerShell` in the script (the session then belongs to the script), choose `engine: "powershell"`, or run the step with a target machine over WinRM.
- **Isolation robustness:** isolated runs can no longer hang in `Running`. NodePilot serializes inheritable-handle spawns and bounds the stdout/stderr drain after process exit to `Engine:IsolatedDrainGraceSeconds` (default 5 s), so that a pipe handle leaked into a foreign process cannot block the step indefinitely.
- **Outputs:** `output`, `error`, `param.exitCode` (always — the last native command **of this script**), `param.*` (the variables this script assigns)
- **Success (error-based):** a step fails **only** on a terminating PowerShell error (`throw` / `Write-Error` under `Stop`). An `exit N` does not automatically mark the step as failed. For exit-code-based evaluation, set `successExitCodes`, for example `"0"`. `successExitCodes`/`param.exitCode` apply to native command codes (`$LASTEXITCODE`) in every engine; a script's own `exit N` is only visible as a value in process/isolated mode (a runspace cannot observe `exit`).
- **Notes:** variables the script assigns become `param.*` (`$hostName = …` → `{{step.param.hostName}}`). An upstream parameter the script only reads stays the output of the step that produced it, and PowerShell automatic/preference variables are never published. Auto-quoting: `{{step.output}}` is substituted as a single-quoted string — write `$x = {{step.output}}`, not `$x = '{{step.output}}'`.

## `fileOperation`

**Remote.**

- **Config:** `operation` (copy/move/delete/exists/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Leaf` on destructive operations. `create` produces an empty file (truncated with `-Force`).
- **Outputs:** `param.operation`, `param.path`, `param.destination`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName` (depending on the operation)

## `folderOperation`

**Remote.**

- **Config:** `operation` (copy/move/delete/exists/list/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Container`. `list` enumerates direct children.
- **Outputs:** `param.operation`, `param.path`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName`; `list` → `param.items` + `param.count` + `param.truncated`

## `textFileEdit`

**Remote.** BOM-aware, with an atomic write (tmp + `Move-Item -Force`).

- **Config:** `operation` (append/prepend/insert/delete/replace/replaceLine), `path`, `content`, `lineNumber`, `matchPattern`, `replace`, `useRegex`, `ignoreCase`, `occurrences`, `encoding` (auto/utf8/utf8-bom/utf16le/utf16be/ascii), `lineEnding` (preserve/crlf/lf), `createIfMissing`, `dryRun`, `backupSuffix`, `appendIfMissing` (exact), `maxFileSizeMB` (default 50)
- **Outputs:** `param.operation`, `param.path`, `param.linesBefore`/`linesAfter`/`linesChanged`, `param.encoding`, `param.lineEnding`, `param.backupPath`, `param.dryRun`

## `serviceManagement`

**Remote.**

- **Config:** `serviceName`, `action` (start/stop/restart/status/create/delete/setStartType; `create`/`setStartType` take `binaryPath`/`displayName`/`description`/`startupType`; `delete` stops the service and removes it permanently via `sc.exe delete`). `sc.exe` reports its failures on stdout and leaves the error stream empty, so the exit code of the `sc.exe` calls behind `delete` and behind an `AutomaticDelayedStart` startup type is checked and a non-zero exit fails the step — a refused delete or startup-type change is never reported as success.
- **Outputs:** `param.name`, `param.status`, `param.startType`

## `registryOperation`

**Remote.**

- **Config:** `operation` (read/write/deleteValue/deleteKey/createKey/exists/listSubKeys/listValues), `keyPath`, `valueName`, `value`, `valueType` (String/ExpandString/Binary/DWord/MultiString/QWord). `read` and `exists` act at key or value level depending on `valueName`.
- **Outputs:** `param.value`+`param.type` (single read), `param.values`+`param.count` (listValues), `param.subKeys`+`param.count` (listSubKeys), `param.exists`, `param.created`

## `wmiQuery`

**Remote.**

- **Config:** `className`, `namespace`, `filter`, `mode` (`query`/`wql`/`invokeMethod`), `captureProperties` (an optional `string[]`). With `captureProperties` → the first row in `param.<name>` + `param.count`. Without it → legacy text in `output`. Property names have to be CIM-conformant (`^[A-Za-z_][A-Za-z0-9_]*$`).
- **Outputs:** `param.*`, `param.count`, `output`

## `startProgram`

**Remote.**

- **Config:** `filePath`, `arguments`, `workingDirectory`, `useShellExecute`, `waitForExit`, `timeoutSeconds`, `successExitCodes`
- **Outputs:** `param.exitCode`, `param.processId`, `param.stdout`, `param.stderr`, `param.stdoutTruncated`, `param.stderrTruncated`, `param.waited`

The stored `filePath` must be an absolute local executable path on the target machine. The engine rejects UNC paths and bare names and never searches `PATH`. In the designer, `cmd`, `powershell`, `cscript` and `wscript` (with or without `.exe`) are completed to absolute paths when you leave the field. API/MCP clients and generated definitions must supply absolute paths themselves. `useShellExecute: true` is rejected when `StartProgram:DisallowShellExecute` is enabled.

With `waitForExit: false`, the activity returns the process ID while the program continues running; stdout and stderr are not captured. Use `waitForExit: true` to collect the exit code, and keep `useShellExecute: false` to capture output.

The timeout bounds the wait on the running process. Once it has exited and a stream still does not end — because a surviving grandchild inherited the handle, as with `cmd /c start …` — reading continues for another `Engine:IsolatedDrainGraceSeconds` (default 5 s); after that the step returns the buffered output with a note on its meta line instead of failing on the timeout. Once a stream reaches its capture limit, further output is discarded while the process continues; `stdoutTruncated` or `stderrTruncated` reports this.

## `powerManagement`

**Remote.**

- **Config:** `action` (shutdown/restart/logoff/abort/hibernate), `delaySeconds`, `force`, `message`
- **Outputs:** —

## `scheduledTask`

**Remote.** Usually requires admin rights on the target.

- **Config:** `action` (get/start/stop/enable/disable/unregister/register, default `get`), `taskName`, `taskPath` (default `\`). Register only: `program`, `arguments`, `workingDirectory`, `triggerType` (once/daily/weekly/atLogon/atStartup), `startTime`, `daysOfWeek[]`, `weeksInterval`, `daysInterval`, `runAsUser` (default SYSTEM), `runLevel` (limited/highest), `description`, `force`
- **Outputs:** `param.taskName`, `param.state`, `param.lastRunTime`, `param.lastTaskResult`, `param.nextRunTime`
- **Provider fallback:** actions on existing tasks use the PowerShell cmdlets first. Only if the ScheduledTasks CIM provider fails with `0x80041318` does the activity fall back to the Task Scheduler automation API within the same local/WinRM PowerShell session. `register` stays cmdlet-based exclusively.

## `fileHash`

**Remote.**

- **Config:** `path`, `algorithm` (MD5/SHA1/SHA256/SHA384/SHA512, default SHA256), `expected` (optional — verified; a mismatch makes the step fail)
- **Outputs:** `param.hash`, `param.algorithm`, `param.match`

## `zipOperation`

**Remote.** Compress builds an explicitly validated file manifest and writes it directly with
`ZipArchive`; wildcards are only allowed in the last source segment and square brackets are treated
literally. Extract validates and writes every entry individually. Zip slip, existing
junctions/symlinks in the source or destination, and following an existing output link are all
rejected; the target ACL remains the boundary against concurrent parent renames.

- **Config:** `operation` (compress/extract, default `compress`), `source` (wildcards allowed for compress), `destination`, `compressionLevel` (Optimal/Fastest/NoCompression — compress only), `force`
- **Outputs:** `param.destination`, `param.sizeBytes` (extract ⇒ 0)

## `restApi`

**Engine-local.**

- **Config:** `url`, `method`, `body`, `headers` (on a request that carries a body, a `Content-Type` set here moves onto the entity — no second, conflicting field on the wire; an unparsable value is left alone and the body still goes out as `application/json`. A request without a body, and every GET/HEAD, has no entity, so a `Content-Type` set there is dropped), `timeoutSeconds`, `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`
- **Outputs:** `param.statusCode` (the response body in `output` as `HTTP {code}\n{body}`; headers are not exposed as `param`)

## `sql`

**Engine-local.** Connection precedence: `connectionRef` > builder > raw `connectionString`.

- **Config:** `provider` (sqlserver/sqlite/postgres), `query`, `timeoutSeconds`. Connection options: (a) the builder — SQL Server: `server`/`database`/`authentication`/`username`/`password`/`encrypt`/`trustServerCertificate`; Postgres: `host`/`port`/`database`/`username`/`password`/`sslMode` (`VerifyFull` + `Trust Server Certificate=false` by default; weaker modes only for literal loopback hosts); SQLite: `dataSource`; (b) a raw `connectionString`; (c) a named `connectionRef` from `SqlActivity:ConnectionStrings:{name}`. The Postgres TLS policy applies to raw and ref as well.
- **Outputs:** SELECT → `param.rowCount` + the first row's columns as `param.<col>` + `param.row{i}_{col}` (the first 20 rows) + `param.truncated`/`param.flatKeysTruncated`. DML/DDL → `param.rowsAffected` + `param.rowCount`
- The shape follows the statement, not the number of hits: a `SELECT` that matches nothing still
  publishes the SELECT shape (`[]`, `param.rowCount` of 0) and no `rowsAffected`. Scalar columns
  are published invariantly — `1.5`, never `1,5` — so a comparison means the same thing on every
  host locale.

## `emailNotification`

**Engine-local.** A single recipient. SMTP through `Smtp:*`.

- **Config:** `to`, `subject`, `body`, `isHtml`
- **Outputs:** —

## `delay`

**Engine-local.**

- **Config:** `seconds`
- **Outputs:** —

## `junction`

**Engine-local, controlFlow.**

- **Config:** `mode` (waitAll/waitAny/waitNofM), `requiredCount` (for waitNofM)
- **Outputs:** —

## `forEach`

**Engine-local, controlFlow.** Shares the `ISubWorkflowGate` with `startWorkflow`.

- **Config:** `items` (a template → a JSON array or a list of lines), `itemsFormat` (auto/json/lines), `childWorkflowNameOrId`, `itemParameterName` (default `item`), `indexParameterName` (default `index`), `parameters` (static, passed to every child), `maxParallelism` (default 1, hard cap 64), `continueOnError`, `timeoutSecondsPerItem` (default 3600)
- **Outputs:** `param.total`, `param.succeeded`, `param.failed`, `param.skipped`, `param.firstError`, `param.results` (a JSON array)

## `decision`

**Engine-local, controlFlow.** Routing through `step.param.case == "name"` edge conditions.

- **Config:** `cases` (an array, each `{name, condition}`; `condition` uses the same AST as an edge's `conditionExpression`, with `type` mandatory here), `defaultCaseName` (default `default`)
- **Outputs:** `param.case`, `param.matched`, `param.reason`

## `startWorkflow`

**Engine-local, controlFlow.**

- **Config:** `workflowNameOrId`, `parameters`, `waitForCompletion`, `timeoutSeconds`
- **Outputs:** synchronous → `param.*` (mirrored from the child's `returnData`) plus always `param.__executionId`/`__status`/`__workflowId`/`__workflowName`. Fire and forget → `param.workflowId`/`param.workflowName`/`param.waited`

## `returnData`

**Engine-local, controlFlow.**

- **Config:** `data` (an object with `{{template}}` values)
- **Outputs:** `param.*` (the keys from `data`)

## `xmlQuery`

**Engine-local.**

- **Config:** `source`, `path`/`content`, `xpath`, `namespaces`, `resultMode`
- **Outputs:** `param.result`, `param.count`
- `resultMode` switches cardinality only. Both modes publish the element text; `all` returns it
  as a JSON array. Numeric XPath results use an invariant decimal point, so a comparison means
  the same thing on every host locale.

## `jsonQuery`

**Engine-local.**

- **Config:** `source`, `path`/`content`, `jsonPath`, `resultMode`
- **Outputs:** `param.result`, `param.count`
- Scalars are published invariantly: `9.99`, and `true`/`false` in lower case. An ISO-8601
  timestamp is passed through as the original text — it is never re-parsed into a
  host-formatted date.

## `log`

**Engine-local.**

- **Config:** `level` (info/warning/error), `message`
- **Outputs:** —

## `generateText`

**Engine-local.** Entropy from `RandomNumberGenerator`, rejection-sampled (no modulo bias). The generated value is **not** redacted.

- **Config:** `mode` (`alphanumeric` default /`alphabetic`/`numeric`/`hex`/`guid`/`password`/`custom`), `length` (1–1024, default 16; ignored for `guid`), `customCharset` (required for `mode=custom`), `excludeAmbiguous` (removes easily confused characters 0/O, 1/l/I …). `password` is only a charset preset (it guarantees no policy).
- **Outputs:** `output` (the generated string), `param.text`

## `llmQuery`

**Engine-local.** Calls an OpenAI-compatible endpoint (prompt → text) — chat completions or the Responses API, depending on the path of the base URL. By default it uses the active LLM profile (`Llm:ActiveProfileId` → `Llm:Profiles:<id>`); this can be overridden per node. **It requires `Llm:Enabled=true` and a resolvable active profile** (the central kill switch — which applies even with a node-specific endpoint). It shares the transport and the SSRF guard with the AI assistant (`LlmEndpointGuard` validates every `baseUrl`, and cloud-metadata endpoints are blocked).

- **Config:** `prompt` (required; `{{templates}}` allowed), `systemPrompt` (optional; empty = passthrough), `jsonMode` (bool → `response_format:json_object`; the answer is **not** validated). Per-node overrides (empty → global): `baseUrl` (an absolute http/https URL), `model`, `apiKey` (a secret, auto-redacted), `maxTokens` (>0), `temperature` (0–2, per node only), `timeoutSeconds` (>0).
- **Outputs:** `output` (the answer text), `param.model`, `param.promptTokens`, `param.completionTokens`, `param.totalTokens`, `param.finishReason` (the token keys are always set; `""` when the server returns no `usage`).

## `waitForCondition`

**Hybrid.**

- **Config:** `conditionType` (`script` default / `pathExists` / `serviceRunning` / `portOpen` / `httpOk`), `intervalSeconds`, `timeoutSeconds`. Mode-specific: `script` → `script` (a PowerShell expression, **no** `{{…}}` templates), `pathExists` → `path`, `serviceRunning` → `serviceName`, `portOpen` → `host`+`port`, `httpOk` → `url`. The typed modes accept `{{upstream.param.x}}` — the engine quotes values safely. The network modes only run for hosts permitted exactly under `WaitForCondition:AllowedHosts` (default `["localhost"]`); scheme, port, path and wildcards do not belong in that list. This is **not** the same list as `RestApi:AllowedHosts` — the latter concerns only `restApi`. For **both** network modes it is the sole authority: `RestApi:BlockPrivateNetworks`/`RestApi:AllowedHosts` are not consulted, so a loopback probe needs no restApi exception. Link-local/cloud-metadata (169.254/16) stays blocked always — no allow-list can open it.
- **Outputs:** `param.attempts`, `param.elapsedSeconds`, `param.lastResult`
