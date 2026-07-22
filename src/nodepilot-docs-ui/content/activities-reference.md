# Activity-Referenz

Config-Keys und Output-Semantik pro Activity-Typ. Scope-Legende: **Remote** = `targetMachineId`/WinRM, **Engine-local** = im API-Prozess, **Hybrid** = beides.

Pro Step: `config.retry` (`maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`) und `config.timeoutSeconds`. Execution-Timeout zusätzlich im Execute-Body.

---

## `runScript`

**Hybrid.** Führt PowerShell aus — mit gesetzter Maschine über NodePilots managed WinRM-Session; **ohne** Maschine engine-local im API-Host, wo das Script die Remote-Verbindung via `Invoke-Command`/`New-PSSession` auch selbst managen kann (SCOrch-Stil, Trade-offs siehe [Remote-Execution](configuration/remote-execution)).

- **Config:** `script`, `engine` (`auto`/`pwsh`/`powershell`), `timeoutSeconds`, `isolated` (bool, nur lokal — eigener Prozess im Windows Job Object: Crash-/Leak-Containment, keine verwaisten Prozesse) + optionale Caps `memoryLimitMb` (lässt Allokationen fehlschlagen, terminiert nicht) / `maxProcesses`, `successExitCodes` (komma-separiert, opt-in Exit-Code-Gate)
- **Isolation-Robustheit:** Isolierte Läufe können nicht mehr in `Running` hängen bleiben. NodePilot serialisiert inheritable-handle-Spawns und begrenzt den stdout/stderr-Drain nach Prozess-Exit auf `Engine:IsolatedDrainGraceSeconds` (default 5 s), damit ein in einen Fremdprozess geleaktes Pipe-Handle den Step nicht dauerhaft blockiert.
- **Outputs:** `output`, `error`, `param.exitCode` (immer), `param.*` (auto-captured deklarierte Variablen)
- **Erfolg (fehler-basiert):** Ein Step scheitert **nur** bei einem terminierenden PowerShell-Fehler (`throw` / `Write-Error` unter `Stop`). Ein `exit N` macht den Step **nicht** rot. Wer das will, setzt `successExitCodes` (z. B. `"0"`). `successExitCodes`/`param.exitCode` greifen für Native-Command-Codes (`$LASTEXITCODE`) in allen Engines; ein script-eigenes `exit N` ist nur im Prozess/isoliert-Modus als Wert sichtbar (Runspace sieht `exit` nicht).
- **Notes:** Deklarierte Variablen werden zu `param.*` (`$hostName = …` → `{{step.param.hostName}}`). Auto-Quoting: `{{step.output}}` wird als Single-Quoted String eingesetzt — `$x = {{step.output}}` schreiben, nicht `$x = '{{step.output}}'`.

## `fileOperation`

**Remote.**

- **Config:** `operation` (copy/move/delete/exists/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Leaf` auf destruktiven Ops. `create` erzeugt eine leere Datei (truncated mit `-Force`).
- **Outputs:** `param.operation`, `param.path`, `param.destination`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName` (op-abhängig)

## `folderOperation`

**Remote.**

- **Config:** `operation` (copy/move/delete/exists/list/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Container`. `list` enumeriert direkte Children.
- **Outputs:** `param.operation`, `param.path`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName`; `list` → `param.items` + `param.count` + `param.truncated`

## `textFileEdit`

**Remote.** BOM-aware, atomarer Write (tmp + `Move-Item -Force`).

- **Config:** `operation` (append/prepend/insert/delete/replace/replaceLine), `path`, `content`, `lineNumber`, `matchPattern`, `replace`, `useRegex`, `ignoreCase`, `occurrences`, `encoding` (auto/utf8/utf8-bom/utf16le/utf16be/ascii), `lineEnding` (preserve/crlf/lf), `createIfMissing`, `dryRun`, `backupSuffix`, `appendIfMissing` (Exact), `maxFileSizeMB` (default 50)
- **Outputs:** `param.operation`, `param.path`, `param.linesBefore`/`linesAfter`/`linesChanged`, `param.encoding`, `param.lineEnding`, `param.backupPath`, `param.dryRun`

## `serviceManagement`

**Remote.**

- **Config:** `serviceName`, `action` (start/stop/restart/status/create/delete/setStartType; `create`/`setStartType` nehmen `binaryPath`/`displayName`/`description`/`startupType`; `delete` stoppt den Dienst und entfernt ihn dauerhaft via `sc.exe delete`)
- **Outputs:** `param.name`, `param.status`, `param.startType`

## `registryOperation`

**Remote.**

- **Config:** `operation` (read/write/deleteValue/deleteKey/createKey/exists/listSubKeys/listValues), `keyPath`, `valueName`, `value`, `valueType` (String/ExpandString/Binary/DWord/MultiString/QWord). `read`+`exists` agieren auf Key- oder Value-Ebene je nach `valueName`.
- **Outputs:** `param.value`+`param.type` (single-read), `param.values`+`param.count` (listValues), `param.subKeys`+`param.count` (listSubKeys), `param.exists`, `param.created`

## `wmiQuery`

**Remote.**

- **Config:** `className`, `namespace`, `filter`, `mode` (`query`/`wql`/`invokeMethod`), `captureProperties` (optionales `string[]`). Mit `captureProperties` → erste Row in `param.<Name>` + `param.count`. Ohne → Legacy-Text `output`. Property-Namen müssen CIM-konform sein (`^[A-Za-z_][A-Za-z0-9_]*$`).
- **Outputs:** `param.*`, `param.count`, `output`

## `startProgram`

**Remote.**

- **Config:** `filePath`, `arguments`, `waitForExit`, `timeoutSeconds`, `successExitCodes`
- **Outputs:** `param.exitCode`, `param.processId`, `param.stdout`, `param.stderr`, `param.waited`

## `powerManagement`

**Remote.**

- **Config:** `action` (shutdown/restart/logoff/abort/hibernate), `delaySeconds`, `force`, `message`
- **Outputs:** —

## `scheduledTask`

**Remote.** Erfordert meist Admin auf dem Ziel.

- **Config:** `action` (get/start/stop/enable/disable/unregister/register, default `get`), `taskName`, `taskPath` (default `\`). Register-only: `program`, `arguments`, `workingDirectory`, `triggerType` (once/daily/weekly/atLogon/atStartup), `startTime`, `daysOfWeek[]`, `weeksInterval`, `daysInterval`, `runAsUser` (default SYSTEM), `runLevel` (limited/highest), `description`, `force`
- **Outputs:** `param.taskName`, `param.state`, `param.lastRunTime`, `param.lastTaskResult`, `param.nextRunTime`
- **Provider-Fallback:** Aktionen auf vorhandenen Tasks verwenden zuerst die PowerShell-Cmdlets. Nur wenn der ScheduledTasks-CIM-Provider mit `0x80041318` fehlschlägt, greift die Activity innerhalb derselben lokalen/WinRM-PowerShell-Session auf die Task-Scheduler-Automation-API zurück. `register` bleibt ausschließlich Cmdlet-basiert.

## `fileHash`

**Remote.**

- **Config:** `path`, `algorithm` (MD5/SHA1/SHA256/SHA384/SHA512, default SHA256), `expected` (optional — verifiziert; Mismatch ⇒ Step fails)
- **Outputs:** `param.hash`, `param.algorithm`, `param.match`

## `zipOperation`

**Remote.** Extract führt einen Zip-Slip-Pre-Scan aus.

- **Config:** `operation` (compress/extract, default `compress`), `source` (Wildcards erlaubt für compress), `destination`, `compressionLevel` (Optimal/Fastest/NoCompression — compress only), `force`
- **Outputs:** `param.destination`, `param.sizeBytes` (extract ⇒ 0)

## `restApi`

**Engine-local.**

- **Config:** `url`, `method`, `body`, `headers`, `timeoutSeconds`, `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy`
- **Outputs:** `param.statusCode` (Response-Body in `output` als `HTTP {code}\n{body}`; Headers nicht als `param`)

## `sql`

**Engine-local.** Connection-Precedence: `connectionRef` > Builder > raw `connectionString`.

- **Config:** `provider` (sqlserver/sqlite/postgres), `query`, `timeoutSeconds`. Connection-Optionen: (a) Builder — SQL Server: `server`/`database`/`authentication`/`username`/`password`/`encrypt`/`trustServerCertificate`; Postgres: `host`/`port`/`database`/`username`/`password`/`sslMode`; SQLite: `dataSource`; (b) raw `connectionString`; (c) named `connectionRef` aus `SqlActivity:ConnectionStrings:{name}`.
- **Outputs:** SELECT → `param.rowCount` + erste-Row-Spalten als `param.<col>` + `param.row{i}_{col}` (erste 20 Rows) + `param.truncated`/`param.flatKeysTruncated`. DML/DDL → `param.rowsAffected` + `param.rowCount`

## `emailNotification`

**Engine-local.** Einzeler-Empfänger. SMTP via `Smtp:*`.

- **Config:** `to`, `subject`, `body`, `isHtml`
- **Outputs:** —

## `delay`

**Engine-local.**

- **Config:** `seconds`
- **Outputs:** —

## `junction`

**Engine-local, controlFlow.**

- **Config:** `mode` (waitAll/waitAny/waitNofM), `requiredCount` (für waitNofM)
- **Outputs:** —

## `forEach`

**Engine-local, controlFlow.** Teilt sich `ISubWorkflowGate` mit `startWorkflow`.

- **Config:** `items` (Template → JSON-Array oder Zeilenliste), `itemsFormat` (auto/json/lines), `childWorkflowNameOrId`, `itemParameterName` (default `item`), `indexParameterName` (default `index`), `parameters` (statisch, an jedes Child), `maxParallelism` (default 1, hard cap 64), `continueOnError`, `timeoutSecondsPerItem` (default 3600)
- **Outputs:** `param.total`, `param.succeeded`, `param.failed`, `param.skipped`, `param.firstError`, `param.results` (JSON-Array)

## `decision`

**Engine-local, controlFlow.** Routing via `step.param.case == "name"` Edge-Conditions.

- **Config:** `cases` (Array, each `{name, condition}`; `condition` nutzt denselben AST wie Edge `conditionExpression`, `type` hier obligatorisch), `defaultCaseName` (default `default`)
- **Outputs:** `param.case`, `param.matched`, `param.reason`

## `startWorkflow`

**Engine-local, controlFlow.**

- **Config:** `workflowNameOrId`, `parameters`, `waitForCompletion`, `timeoutSeconds`
- **Outputs:** sync → `param.*` (gespiegelt aus Child-`returnData`) + immer `param.__executionId`/`__status`/`__workflowId`/`__workflowName`. fire-and-forget → `param.workflowId`/`param.workflowName`/`param.waited`

## `returnData`

**Engine-local, controlFlow.**

- **Config:** `data` (Objekt mit `{{template}}`-Werten)
- **Outputs:** `param.*` (Keys aus `data`)

## `xmlQuery`

**Engine-local.**

- **Config:** `source`, `path`/`content`, `xpath`, `namespaces`, `resultMode`
- **Outputs:** `param.result`, `param.count`

## `jsonQuery`

**Engine-local.**

- **Config:** `source`, `path`/`content`, `jsonPath`, `resultMode`
- **Outputs:** `param.result`, `param.count`

## `log`

**Engine-local.**

- **Config:** `level` (info/warning/error), `message`
- **Outputs:** —

## `generateText`

**Engine-local.** Entropie aus `RandomNumberGenerator`, rejection-sampled (kein Modulo-Bias). Generierter Wert wird **nicht** redigiert.

- **Config:** `mode` (`alphanumeric` default /`alphabetic`/`numeric`/`hex`/`guid`/`password`/`custom`), `length` (1–1024, default 16; ignoriert für `guid`), `customCharset` (required für `mode=custom`), `excludeAmbiguous` (entfernt verwechslungsfähige Zeichen 0/O, 1/l/I …). `password` = nur Charset-Preset (keine Policy-Garantie).
- **Outputs:** `output` (generierter String), `param.text`

## `llmQuery`

**Engine-local.** Ruft einen OpenAI-kompatiblen Chat-Completions-Endpunkt auf (Prompt → Text). Nutzt per Default die globale `Llm:*`-Config; per-Node überschreibbar. **Braucht `Llm:Enabled=true`** (zentraler Kill-Switch — gilt auch bei Node-eigenem Endpunkt). Teilt Transport + SSRF-Guard mit dem KI-Assistenten (`LlmEndpointGuard` validiert jede `baseUrl`, Cloud-Metadata-Endpoints sind blockiert).

- **Config:** `prompt` (required; `{{templates}}` erlaubt), `systemPrompt` (optional; leer = Passthrough), `jsonMode` (bool → `response_format:json_object`, Antwort wird **nicht** validiert). Per-Node-Overrides (leer → global): `baseUrl` (absolute http/https), `model`, `apiKey` (Secret, auto-redigiert), `maxTokens` (>0), `temperature` (0–2, per-Node-only), `timeoutSeconds` (>0).
- **Outputs:** `output` (Antworttext), `param.model`, `param.promptTokens`, `param.completionTokens`, `param.totalTokens`, `param.finishReason` (Token-Keys immer gesetzt; `""` wenn der Server keine `usage` liefert).

## `waitForCondition`

**Hybrid.**

- **Config:** `conditionType` (`script` default / `pathExists` / `serviceRunning` / `portOpen` / `httpOk`), `intervalSeconds`, `timeoutSeconds`. Mode-spezifisch: `script` → `script` (PowerShell-Expression, **keine** `{{…}}`-Templates), `pathExists` → `path`, `serviceRunning` → `serviceName`, `portOpen` → `host`+`port`, `httpOk` → `url`. Typed Modes akzeptieren `{{upstream.param.x}}` — die Engine quotet Werte sicher. Netzwerkmodi laufen nur für Hosts, die exakt unter `RestApi:AllowedHosts` freigegeben sind; Schema, Port, Pfad und Wildcards gehören nicht in diese Liste.
- **Outputs:** `param.attempts`, `param.elapsedSeconds`, `param.lastResult`
