# Claude Reference — Detail-Doku ausgelagert aus CLAUDE.md

Diese Datei enthält die detaillierten Referenzen, die für die tägliche Code-Arbeit nicht
in den Kontext-Window geladen werden müssen, aber bei Bedarf nachschlagbar sind.

---

## Activity-Typen — Config-Keys & Output-Semantik

| Type | Scope | Config-Keys | Outputs |
|---|---|---|---|
| `runScript` | Hybrid | `script`, `engine` (`auto`/`pwsh`/`powershell`), `timeoutSeconds`, `isolated` (bool, local-only — separate process in a Windows Job Object) + caps `memoryLimitMb`/`maxProcesses`, `successExitCodes` (comma-sep, opt-in exit-code gate) | `output`, `error`, `param.exitCode` (always), `param.*` (auto-captured vars) |
| `fileOperation` | Remote | `operation` (copy/move/delete/exists/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Leaf` bei destruktiven Ops. `create` legt leere Datei an (truncated mit `-Force`). | `param.operation`, `param.path`, `param.destination`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName` (op-abhängig) |
| `folderOperation` | Remote | `operation` (copy/move/delete/exists/list/create/rename), `path`, `destination`, `newName`. Asserts `-PathType Container`. `list` enumeriert direkte Children. | `param.operation`, `param.path`, `param.exists`, `param.fullName`, `param.creationTime`, `param.newPath`/`param.newName`; `list` → `param.items`+`param.count`+`param.truncated` |
| `serviceManagement` | Remote | `serviceName`, `action` (start/stop/restart/status/create/setStartType; create/setStartType nehmen `binaryPath`/`displayName`/`description`/`startupType`) | `param.name`, `param.status`, `param.startType` |
| `registryOperation` | Remote | `operation` (read/write/deleteValue/deleteKey/createKey/exists/listSubKeys/listValues), `keyPath`, `valueName`, `value`, `valueType` (String/ExpandString/Binary/DWord/MultiString/QWord). `read`+`exists` arbeiten je nach `valueName` auf Key- oder Value-Ebene. | `param.value`+`param.type` (single-read), `param.values`+`param.count` (listValues), `param.subKeys`+`param.count` (listSubKeys), `param.exists`, `param.created` |
| `wmiQuery` | Remote | `className`, `namespace`, `filter`, `mode` (`query`/`wql`/`invokeMethod`), `captureProperties` (optional `string[]`). Mit `captureProperties` → erste Zeile in `param.<Name>` + `param.count`. Ohne → Legacy-Text-`output`. Property-Namen müssen CIM-konform (`^[A-Za-z_][A-Za-z0-9_]*$`). | `param.*`, `param.count`, `output` |
| `startProgram` | Remote | `filePath`, `arguments`, `waitForExit`, `timeoutSeconds`, `successExitCodes` | `param.exitCode`, `param.processId`, `param.stdout`, `param.stderr`, `param.waited` |
| `powerManagement` | Remote | `action` (shutdown/restart/logoff/abort/hibernate), `delaySeconds`, `force`, `message` | — (kein OutputParameter) |
| `textFileEdit` | Remote | `operation` (append/prepend/insert/delete/replace/replaceLine), `path`, `content`, `lineNumber`, `matchPattern`, `replace`, `useRegex`, `ignoreCase`, `occurrences`, `encoding` (auto/utf8/utf8-bom/utf16le/utf16be/ascii), `lineEnding` (preserve/crlf/lf), `createIfMissing`, `dryRun`, `backupSuffix`, `appendIfMissing`(Exact), `maxFileSizeMB` (default 50). BOM-aware, atomarer Write (tmp + `Move-Item -Force`). | `param.operation`, `param.path`, `param.linesBefore`/`linesAfter`/`linesChanged`, `param.encoding`, `param.lineEnding`, `param.backupPath`, `param.dryRun` |
| `scheduledTask` | Remote | `action` (get/start/stop/enable/disable/unregister/register, default `get`), `taskName`, `taskPath` (default `\`). Register-only: `program`, `arguments`, `workingDirectory`, `triggerType` (once/daily/weekly/atLogon/atStartup), `startTime`, `daysOfWeek[]`, `weeksInterval`, `daysInterval`, `runAsUser` (default SYSTEM), `runLevel` (limited/highest), `description`, `force`. Braucht i.d.R. Admin auf dem Target. Existing-task actions sind Cmdlet-first; ausschließlich bei CIM-Fehler `0x80041318` folgt ein lokaler Task-Scheduler-Automation-Fallback innerhalb derselben PowerShell-/WinRM-Session. `register` bleibt Cmdlet-only. | `param.taskName`, `param.state`, `param.lastRunTime`, `param.lastTaskResult`, `param.nextRunTime` |
| `fileHash` | Remote | `path`, `algorithm` (MD5/SHA1/SHA256/SHA384/SHA512, default SHA256), `expected` (optional — verifiziert; Mismatch ⇒ Step schlägt fehl). | `param.hash`, `param.algorithm`, `param.match` |
| `zipOperation` | Remote | `operation` (compress/extract, default `compress`), `source` (Wildcards bei compress erlaubt), `destination`, `compressionLevel` (Optimal/Fastest/NoCompression — nur compress), `force`. Extract macht einen Zip-Slip-Pre-Scan. | `param.destination`, `param.sizeBytes` (extract ⇒ 0) |
| `restApi` | Engine-local | `url`, `method`, `body`, `headers`, `timeoutSeconds`, `proxyMode` (`default`/`direct`/`custom`), `proxyAddress`, `noProxy` | `param.statusCode` (Response-Body steht im `output`-Stdout als `HTTP {code}\n{body}`; Header werden nicht als `param` exponiert) |
| `sql` | Engine-local | `provider` (sqlserver/sqlite/postgres), `query`, `timeoutSeconds`. Verbindung: (a) Builder-Felder (SQL Server: `server`/`database`/`authentication`/`username`/`password`/`encrypt`/`trustServerCertificate`; Postgres: `host`/`port`/`database`/`username`/`password`/`sslMode` (default `Require`; explizit `Disable` zum Abschalten); SQLite: `dataSource`), (b) raw `connectionString`, (c) named `connectionRef` aus `SqlActivity:ConnectionStrings:{name}`. Reihenfolge: `connectionRef` > Builder > raw. | SELECT: `param.rowCount` + erste-Zeile-Spalten als `param.<col>` + `param.row{i}_{col}` (erste 20 Zeilen) + `param.truncated`/`param.flatKeysTruncated`. DML/DDL: `param.rowsAffected` + `param.rowCount` |
| `emailNotification` | Engine-local | `to`, `subject`, `body`, `isHtml`. Single-Recipient. SMTP via `Smtp:*` Config. | — |
| `delay` | Engine-local | `seconds` | — |
| `junction` | Engine-local | `mode` (waitAll/waitAny/waitNofM), `requiredCount` (bei waitNofM) | — |
| `forEach` | Engine-local (controlFlow) | `items` (Template → JSON-Array oder Zeilenliste), `itemsFormat` (auto/json/lines), `childWorkflowNameOrId`, `itemParameterName` (default `item`), `indexParameterName` (default `index`), `parameters` (statisch, an jedes Child), `maxParallelism` (default 1, Hard-Cap 64), `continueOnError`, `timeoutSecondsPerItem` (default 3600). Teilt `ISubWorkflowGate` mit `startWorkflow`. | `param.total`, `param.succeeded`, `param.failed`, `param.skipped`, `param.firstError`, `param.results` (JSON-Array) |
| `decision` | Engine-local (controlFlow) | `cases` (Array, je `{name, condition}`; `condition` nutzt dieselbe AST wie Edge-`conditionExpression`, `type` ist hier Pflicht), `defaultCaseName` (default `default`). Routing dann per `step.param.case == "name"`-Edge-Conditions. | `param.case`, `param.matched`, `param.reason` |
| `startWorkflow` | Engine-local | `workflowNameOrId`, `parameters`, `waitForCompletion`, `timeoutSeconds` | sync: `param.*` (gespiegelt aus Child-`returnData`) + immer `param.__executionId`/`__status`/`__workflowId`/`__workflowName`. fire-and-forget: `param.workflowId`/`param.workflowName`/`param.waited` |
| `returnData` | Engine-local | `data` (obj mit `{{template}}`-Werten) | `param.*` (Keys aus `data`) |
| `xmlQuery` | Engine-local | `source`, `path`/`content`, `xpath`, `namespaces`, `resultMode` | `param.result`, `param.count` |
| `jsonQuery` | Engine-local | `source`, `path`/`content`, `jsonPath`, `resultMode` | `param.result`, `param.count` |
| `log` | Engine-local | `level` (info/warning/error), `message` | — |
| `generateText` | Engine-local | `mode` (`alphanumeric` default/`alphabetic`/`numeric`/`hex`/`guid`/`password`/`custom`), `length` (1–1024, default 16; bei `guid` ignoriert), `customCharset` (Pflicht bei `mode=custom`), `excludeAmbiguous` (entfernt verwechselbare Zeichen 0/O, 1/l/I …). Entropie aus `RandomNumberGenerator`, rejection-sampled (keine Modulo-Bias). `password` = nur Zeichensatz-Preset (keine Policy-Garantie). Generierter Wert wird **nicht** redigiert. | `output` (generierter String), `param.text` |
| `llmQuery` | Engine-local | `prompt` (Pflicht; `{{templates}}` erlaubt), `systemPrompt` (optional; leer = Passthrough, kein synthetischer Default), `jsonMode` (bool → `response_format:json_object`; Antwort wird **nicht** validiert). **Per-Node-Overrides** (leer → globale `Llm:*`-Config): `baseUrl` (absolute http/https; via `LlmEndpointGuard` validiert, Cloud-Metadata blockiert), `model`, `apiKey` (Secret, auto-redigiert), `maxTokens` (>0), `temperature` (0..2, per-Node-only — kein globaler Knopf; leer = Provider-Default), `timeoutSeconds` (>0). **`Llm:Enabled=true` Pflicht** (sonst sauberer Step-Fehler). `output` = Antworttext (roh, füttert Downstream-Databus; `OutputRedactor` scrubbt nur Persistenz/SignalR). Token-Keys **immer** gesetzt, `""` wenn Server keine `usage` liefert (`type:"number"` ist nur UI-/Databus-Hint). Transiente Fehler (RateLimited/Unreachable) via `config.retry` wiederholbar. | `output` (Antwort), `param.model`, `param.promptTokens`, `param.completionTokens`, `param.totalTokens`, `param.finishReason` |
| `waitForCondition` | Hybrid | `conditionType` (`script` default / `pathExists` / `serviceRunning` / `portOpen` / `httpOk`), `intervalSeconds`, `timeoutSeconds`. Mode-spezifisch: `script` → `script` (PowerShell-Expr, **keine** `{{...}}`-Templates), `pathExists` → `path`, `serviceRunning` → `serviceName`, `portOpen` → `host`+`port`, `httpOk` → `url`. Getypten Modi nehmen `{{upstream.param.x}}` an — Engine quotet Werte sicher. | `param.attempts`, `param.elapsedSeconds`, `param.lastResult` |

### Prozess-Isolation (`runScript`, nur lokal)

`config.isolated: true` startet das Script als eigenen Prozess race-frei in einem Windows Job Object (`KILL_ON_JOB_CLOSE` → keine verwaisten Prozesse bei Host-Crash/Restart + Crash-/Leak-Containment). Opt-in Caps: `memoryLimitMb` (JOB_MEMORY, aggregat über alle Prozesse des Jobs — lässt Allokationen fehlschlagen, terminiert nicht) + `maxProcesses`. Erzwingt einen Prozess-Engine (nie Runspace-Pool); No-Op auf dem Remote/WinRM-Pfad. Impl: `IsolatedProcessLauncher` (STARTUPINFOEX + `PROC_THREAD_ATTRIBUTE_JOB_LIST` — Prozess startet direkt im Job, kein Assign-after-Spawn-Race).

**Handle-Inheritance-Hardening (fixt „Execution hängt in Running obwohl alle Nodes fertig sind"):** Die stdout/stderr-Anonymous-Pipes des isolierten Childs sind kurzzeitig *inheritable* im API-Prozess offen. Ein *anderer* gleichzeitiger `CreateProcess`/`Process.Start` mit `bInheritHandles:true` ohne `HANDLE_LIST` (non-isolated runScript, `startProgram`, user-`Start-Job`) konnte diese Write-Handles erben und die Pipe offenhalten → `ReadToEndAsync` erreicht nie EOF → der Step-Task terminiert nie → Execution bleibt ewig `Running`. Zwei Gegenmaßnahmen: (1) **`ProcessSpawnCoordinator`** serialisiert *alle* NodePilot-eigenen inheritable-handle-Spawns (Launcher-Fenster + non-isolated `Process.Start` + Executable-Probe) hinter einem Prozess-globalen Lock, sodass sich kein Vererbungsfenster mit einem anderen Spawn überschneidet. (2) **Bounded Drain:** nach `WaitForExit` + `TerminateJobObject` existiert kein legitimer Pipe-Writer mehr, daher wird der stdout/stderr-Drain auf `Engine:IsolatedDrainGraceSeconds` (default 5 s) begrenzt — bleibt ein Read nach dieser Frist offen (geleaktes Handle in einem Fremdprozess), gibt der Step das bereits gepufferte Output zurück, loggt eine Warnung und terminiert (statt ewig zu hängen). `DrainReadsAsync` observiert die abgebrochenen Reads, sodass kein `UnobservedTaskException` entsteht.

### runScript — Ausführungsort (local vs. remote & Self-Managed-Remoting)

`runScript` ist *hybrid*; der Dispatch entscheidet allein anhand der gesetzten Maschine ([RunScriptActivity.cs:88-91](../src/NodePilot.Engine/Activities/RunScriptActivity.cs#L88-L91)):

- **Maschine gesetzt** (non-loopback bzw. + Credential) → NodePilot baut die WinRM-Session über die gepoolte `WinRmSessionFactory` (`ExecuteRemoteAsync`). Das Script läuft auf dem Ziel, **kein** Session-Management im Script nötig.
- **Keine Maschine** (bzw. loopback ohne Credential) → der Node läuft **engine-local im API-Host** (`ExecuteLocalAsync`, Runspace-Pool bzw. isolierter Prozess). Von dort kann das Script die Remote-Verbindung **selbst** herstellen — SCOrch-Stil: `Invoke-Command -ComputerName SRV01 -Credential $c { … }` / `New-PSSession`. Sinnvoll für dynamische Ziellisten, Fan-out auf N Maschinen in **einem** Node oder Jump-Host-Ketten.

**Trade-offs beim Self-Managen** (bewusst außerhalb des managed WinRM-Pfads):

1. Läuft auf dem **API-Host**, nicht auf dem Ziel — der Host braucht Netz-/WinRM-Client-Zugriff (TrustedHosts/Kerberos/SSL) selbst.
2. Der DPAPI-**Credential-Store ist nicht verdrahtet** → `PSCredential` im Script selbst bauen; Secret via `{{globals.NAME}}` (im Output redigiert), nie hardcoden.
3. **Kein** Machine-Targeting/-Test (`POST /{id}/test`), keine `StepExecution.TargetMachine`-Zuordnung, keine per-Machine-Telemetrie/-Health.
4. Hardening (`Remote:RequireWinRmSsl`, SSL-Enforcement, Session-Pool) **greift nicht** — es hängt am Factory-Pfad.

---

## Trigger — Injected-Params

Trigger-Sources seeden ihre Event-Daten als `manual.*`-Variablen in den Run (`VariableResolver` schreibt `manual.<key>`), referenzierbar als `{{manual.<name>}}`. Jeder Trigger-Node spiegelt dieselben Keys zusätzlich als eigene `param.*`-Outputs (`{{<triggerVar>.param.<name>}}`). Es gibt **kein** `trigger.*`-Namespace — ein `{{trigger.file.path}}` bleibt unaufgelöstes Literal.

| Trigger | Injected Keys |
|---|---|
| `manualTrigger` | user-deklarierte Parameter-Namen → `{{manual.<name>}}` |
| `scheduleTrigger` | `firedAt`, `nextFireAt` (ISO-8601 UTC) |
| `fileWatcherTrigger` | `fileAction` (created/changed/deleted/renamed), `filePath`, `fileName` |
| `databaseTrigger` | `dbSentinel` (neuer Sentinel-Wert), `dbPrevious` |
| `eventLogTrigger` | `eventSource`, `eventEntryType`, `eventId`, `eventMessage`, `eventTimeWritten` |
| `webhookTrigger` | `webhookBody`, `webhookMethod`, `webhookPath`, `webhookQuery_<key>`, `webhookHeader_<key>` + pro `fieldMappings`-Eintrag der gemappte Name (JSONPath aus dem JSON-Body, Dialekt wie `jsonQuery`) |

**webhookTrigger-Verifizierung:** Default = Shared-Secret-Header (`X-Webhook-Secret` == `secret`). Der explizit versionierte Modus `signatureMode: "nodepilot-hmac-v2"` verlangt ein per CSPRNG erzeugtes Secret mit mindestens 32 UTF-8-Bytes, `X-NodePilot-Timestamp` (UNIX-Sekunden) und eine eindeutige `X-NodePilot-Delivery-Id`. Signiert wird `"NodePilot-HMAC-v2\n" + timestamp + "\n" + deliveryId + "\n" + METHOD + "\n" + escapedPath + "\n" + canonicalQuery + "\n" + rawBody` mit HMAC-SHA256. Query-Kanonisierung: jedes Key/Value-Paar separat, UTF-8/RFC3986-Percent-Encoding, ordinal nach encodetem Key sortiert; die Reihenfolge doppelter Werte bleibt erhalten; mit `&` verbunden. `{signaturePrefix}{hex}` (Default `sha256=…`) steht im `signatureHeader` (Default `X-NodePilot-Signature`). Freshness-Fenster = fünf Minuten; Delivery-IDs werden über den gemeinsamen DB-Unique-Guard clusterweit nur einmal akzeptiert. V2 übernimmt keine beliebigen Request-Header in Execution-Parameter, weil sie nicht signiert sind. **Breaking:** Legacy-`signatureMode: "hmac"` und Provider-native Body-only-HMACs werden abgelehnt; GitHub/GitLab/Alertmanager sind nicht direkt wire-kompatibel und benötigen einen verifizierenden Adapter. Constant-time-Vergleich; Fehlversuche kollabieren ins uniforme 404. `fieldMappings` (`[{name, path}]`, max 32, Werte auf 4096 Zeichen gekappt) extrahiert Body-Felder als eigene Params (`{{manual.<name>}}`); non-JSON-Body/nicht-matchende Pfade degradieren zu Leerstring statt Reject; `__`-Prefix + `webhook*`-Systemkeys sind reserviert.

---

## Edit-Lifecycle — UX-Flow & Button-States

### UX-Flow im Editor

1. Workflow ist Productive (Enabled, kein Lock) → Designer read-only, Banner „läuft produktiv". Toolbar zeigt **„Bearbeiten"** (Lock-Entry) und **„Disable"** (Kill-Switch). Save bleibt versteckt.
2. „Bearbeiten" → `lock` → Workflow ist Locked-by-Me + Disabled. Save wird sichtbar, Disable-Slot wechselt auf **„Publish"**.
3. „Save" speichert Zwischen-Stand (PUT, kein Status-Wechsel). Mehrfach OK.
4. „Publish" → atomar Save + Enable + Unlock. Workflow ist wieder Productive, Toolbar springt zurück zu „Bearbeiten" + „Disable".
5. Alternative aus Schritt 2: „Beenden" → `unlock`. Workflow bleibt Disabled, kein Auto-Enable. „Publish"-Slot ist weiterhin sichtbar und ruft jetzt `/enable` (statt `/publish`) — ein Klick reaktiviert ohne Edit-Roundtrip.

### Publish/Disable-Toggle (ein Button-Slot, vier States)

| Workflow-State | Label | Endpoint |
|---|---|---|
| `IsEnabled=true`, kein Lock | „Disable" (rot) | `/disable` |
| `IsEnabled=false`, lock-by-me | „Publish" (primary) | `/publish` (atomar Save+Enable+Unlock) |
| `IsEnabled=false`, kein Lock | „Publish" (primary) | `/enable` (nur scharfschalten) |
| `IsEnabled=false`, lock-by-other | „Publish" disabled | (keiner — Tooltip nennt Lock-Owner) |

Sichtbarkeit gegated durch `roleCanWrite` (Admin/Operator) — nicht durch Lock-by-Me. Viewer sehen den Slot nicht.

`canWrite` im Frontend = `role !== 'Viewer' && checkedOutByUserId === currentUserId`. Sämtliche `nodesDraggable`/`nodesConnectable`/Save/Tidy-Affordances greifen automatisch — kein zusätzlicher Edit-Mode-Toggle.

`currentUserId` wird über `/auth/me` und `LoginResponse.userId` exponiert (JWT ist httpOnly-Cookie, SPA kann ihn nicht decodieren).

---

## Edge-Reshape — Implementierungsdetails

- [smartEdgePath.ts](src/nodepilot-ui/src/components/designer/edges/smartEdgePath.ts) — `controlPoints`-Branch + `defaultControlPoints()` (port-aware: Right→+x, Left→-x, Bottom→+y, Top→-y)
- [EdgeReshapeHandles.tsx](src/nodepilot-ui/src/components/designer/edges/EdgeReshapeHandles.tsx) — 2 SVG-Handles + 2 Hint-Lines, Drag via `pointerdown/move/up` mit `setPointerCapture`, zoom-safe via `screenToFlowPosition`
- [edgeEditingContext.ts](src/nodepilot-ui/src/components/designer/edges/edgeEditingContext.ts) — Parent-owned Actions (`beginEdgeReshape`, `updateEdgeShape`, `resetEdgeShape`); History+Dirty zentral in WorkflowEditorPage. **Wichtig**: `commitHistory()` + `setIsDirty(true)` müssen **vor** `setEdges` feuern (siehe Delete-Handler-Pattern), sonst snapshottet `useWorkflowHistory` den bereits mutierten State und Undo ist kaputt.
- Sichtbar nur bei `selected=true` (nicht beim Hover) und nur auf Single-Segment-Edges (Backward-U-Loop hat 2 Segmente → `cubic Bezier` kann das nicht repräsentieren).
- Reset über das Edge-Context-Menu: „Edge-Form zurücksetzen" — entfernt nur `controlPoints` aus `data`, der Rest bleibt.
- Round-Trip: `data.controlPoints` läuft 1:1 durch Save/Load/Export/Import (Backend strippt nichts).

---

## Contract-Derivation — Semantik-Notizen

- `HasManualTrigger=false` heißt **nicht** „nicht callable" — `startWorkflow` ruft jeden enabled Workflow. Heißt nur: kein deklarierter Input-Vertrag, UI fällt auf freie ParameterTable zurück.
- **Mehrere returnData-Nodes:** `HasMultipleReturnDataNodes=true`. Pro Run gewinnt nur einer (last-write-wins auf dem **gesamten** JSON, nicht per-Key). Outputs sind „kann verfügbar sein", nicht garantiert. UI zeigt Warning-Badge.
- **Mehrere manualTrigger**: Parameter werden per Name dedupliziert. Bei divergierendem `type`/`default` zwischen zwei Triggers gewinnt die erste Deklaration, `HasConflict=true` wird gesetzt — UI rendert Warning, kein Hard-Fail. `Required` wird OR-aggregiert.
- **Reserved Output-Keys** (`__executionId`, `__status`, `__workflowId`, `__workflowName`) werden aus user-deklarierten `returnData.data` stillschweigend gefiltert und vom Engine separat injiziert.
- **Disabled Nodes** (manualTrigger / returnData mit `data.disabled=true`) werden ignoriert — matcht Engine-Skip-Verhalten.
- **By-name-Lookup:** exact-case gewinnt, sonst case-insensitive; mehrdeutige Namen (Name ist nicht unique) → `409 Conflict` statt stillem Zufallstreffer. Geteilte Semantik über `NodePilot.Data.WorkflowNameResolver` — identisch in `GET /by-name/{name}`, `GET /by-name/{name}/contract`, `POST /api/trigger/{name}` (Ambiguität kollabiert dort ins uniforme 404, M-29), Webhook-Route und Engine (`startWorkflow`/`forEach`, Ambiguität = Step-Fehler). UI zeigt damit nie einen Contract, den die Runtime nicht findet.

UI: [ContractMappingTable.tsx](src/nodepilot-ui/src/components/designer/properties/ContractMappingTable.tsx) ersetzt die freie `ParameterTable` in [StartWorkflowConfig.tsx](src/nodepilot-ui/src/components/designer/properties/activities/StartWorkflowConfig.tsx) wenn ein Contract derive-bar ist; bei Variable-Expression / unbekanntem Workflow / Loading bleibt die alte ParameterTable als Fallback. Required-Validation: red-border + Error nur wenn `required && !default && !value`. Empty-out eines Felds **entfernt** den Key (statt ihn auf `""` zu setzen) damit der Child-Default greift. Stale-Keys (im Parameter-Dict, nicht im Contract) werden mit Warning + Remove-Button gerendert, nicht still gepflegt.

---

## Step-Test mit Kontext — API-Details

`POST /api/workflows/{id}/steps/{stepId}/test` führt einen einzelnen Step in Isolation aus, ohne `WorkflowExecution`-Row zu erzeugen. Body:

| Feld | Zweck |
|---|---|
| `mockVariables` | Flat-Map `stepName.field → value` (z.B. `"checkDisk.output": "7"`, `"checkDisk.param.freeGb": "7"`). Wird vor JSON-Config-Resolution als künstliche `ActivityResult`-Lookup-Tabelle aufgebaut. |
| `configOverride` | Optional. Live-Editor-Stand der `data.config`-Subtree. Wenn gesetzt, ersetzt es die DB-persistierte Config — der Test reflektiert was der User gerade tippt, nicht den letzten Save. `targetMachineId`/`credentialId`/`outputVariable` bleiben aus der DB. |

Companion-Endpoints für die UI-„Mit letztem Run-Kontext"-Workflows:

- `GET /api/workflows/{id}/steps/{stepId}/test-context?executionId={guid}` — BFS durchs reverse Adjacency, joint gegen `StepExecutions` der gewählten Execution (oder der aktuellsten terminalen, wenn ohne Param). Liefert Schema-only (Werte=null) wenn die Ancestor in dieser Execution nicht gelaufen ist. Globals werden immer mitgeliefert. UI darf Globals **nicht** als `mockVariables` zurücksenden — die Engine pulled sie direkt aus `IGlobalVariableStore`.
- `GET /api/workflows/{id}/steps/{stepId}/test-context/runs?limit=10` — Dropdown-Quelle. `stepRan: false` markiert Runs, die diesen Step nicht ausgeführt haben (UI zeigt sie ausgegraut).

Implementierung: [StepTestContextProvider.cs](src/NodePilot.Engine/StepTester.cs), Frontend in [StepTestPanel.tsx](src/nodepilot-ui/src/components/designer/properties/StepTestPanel.tsx).

---

## Coverage Heatmap — Details

`GET /api/workflows/{id}/coverage?windowDays=N` aggregiert pro Step die letzten N Tage Executions (default 30, capped 365). Pro Step: `executedCount` (Succeeded + Failed), `failedCount`, `skippedCount` (Skipped + Cancelled — letzteres = junction-race), plus `lastExecutedAt`/`lastSucceededAt`/`lastFailedAt`. Cap auf die letzten 900 Executions im Window. Response trägt `oldestExecutionInWindow`.

UI: Toolbar-Toggle (Target-Icon, [EditorHeader.tsx](src/nodepilot-ui/src/components/designer/EditorHeader.tsx)), bei aktivem State nutzt [useCoverageHeatmap.ts](src/nodepilot-ui/src/hooks/useCoverageHeatmap.ts) den Endpoint und stamped `__coverage` aufs Node-Data. [ActivityNode](src/nodepilot-ui/src/components/designer/nodes/ActivityNode.tsx) tinted: `never` (0 Executions) → 40% opacity + grayscale, `rare` (<25% von Total) → 80% opacity, `common` (≥25%) → unverändert. Hover zeigt exact counts. Window-Days konfigurierbar via `useDesignStore.coverageWindowDays`.

Trade-Offs der V1:
- 25%-Threshold ist crude; präzise Verteilung gibt's via `step-stats`-Endpoint.
- Skipped-by-disabled vs. skipped-by-condition vs. skipped-by-upstream-failure ist nicht aufgeteilt — alles fällt in `skippedCount`.
- Edge-Coverage existiert nicht.

---

## KI-Features — Details

Drei opt-in Helfer (Default `Llm:Enabled=false`):

**Streaming (SSE):** `chat` + `generate-script` antworten als `text/event-stream` (Events `delta`/`building` (chat)/`proposal` (chat)/`done`/`error`) — Ausgabe ab dem ersten Token. Geteilte Infrastruktur: `ILlmClient.StreamAsync` (`IAsyncEnumerable<LlmStreamEvent>`, OpenAI `stream:true` + `stream_options.include_usage`, HTTP-400-Fallback ohne `stream_options`, HTTP-400-Fallback `max_tokens`→`max_completion_tokens` für neuere OpenAI-Modelle (o-Serie/GPT-5-Ära), 16-MiB-Byte-Cap), [SseResponseWriter.cs](src/NodePilot.Api/Ai/SseResponseWriter.cs) (Header + Event-Schreiben), [LlmErrorCodes.cs](src/NodePilot.Api/Ai/LlmErrorCodes.cs). Controller-Lifecycle pro Stream: **erstes Event peeken** (Pre-Stream-`LlmException` → normaler HTTP-Status, greift in `authedFetch`), dann Events; drei Ausgänge — Erfolg (Success-Audit + Metrik), Fehler (`event:error` + `LlmCalls result=error`), **Abbruch** (Client trennt → kein Error-Event, Audit `cancelled=true` + `result=cancelled`). Frontend liest via `postEventStream` (client.ts, `Accept: text/event-stream`) + robustem SSE-Frame-Parser in [ai.ts](src/nodepilot-ui/src/api/ai.ts); `AbortController` = Stop/Dialog-Close. `generate-workflow` bleibt **non-streaming** (JSON).

- **`POST /api/ai/generate-script`** (SSE) — Sparkles-Button im `runScript`-Editor (beide Call-Sites: Properties-Panel + Doppelklick, via `useAiScriptStream`-Hook). Backend ruft LLM mit Prompt + Upstream-Variablen-Schema (Cap `LlmOptions.MaxUpstreamVariables=30`) + dem **aktuellen Editor-Skript** (`GenerateScriptRequest.CurrentScript`, untrusted Kontextblock) als **Refactor-Basis** — ohne das halluziniert der LLM bei „refactor/fix das Skript" aus der Variablen-Liste. **Streaming-aware Fence-Stripping**. Frontend tippt die Tokens **live in Monaco** ([ScriptEditorDialog.tsx](src/nodepilot-ui/src/components/designer/ScriptEditorDialog.tsx)): Prompt-Dialog schließt **sofort** beim Klick auf Generieren (Editor-Overlay „Code wird generiert…" bis zum ersten Token, dann „generiert"-Pill + Stopp), Editor read-only während Streaming, Inserts an einer **explizit getrackten Position** (`advanceStreamPosition`, **nicht** `getSelection` — sonst verwürfeln die Tokens), gebatcht pro `requestAnimationFrame` als **eine Undo-Gruppe**, ReplaceAll leert erst beim ersten Token (Pre-Token-Fehler bleibt erhalten); Fehler erscheinen als Banner im Editor.
- **`POST /api/ai/generate-workflow`** — „KI generieren"-Button auf [WorkflowsPage](src/nodepilot-ui/src/pages/WorkflowsPage.tsx). Backend ruft LLM mit JSON-Mode + Few-Shot aus `workflow-example.json`, parser-pipeline mit Single-Retry (`LlmOptions.MaxJsonRetries=1`), Schema-Validierung gegen `nodes[]+edges[]`. UI zeigt Stats-Preview vor dem Anlegen. **Non-streaming.**
- **`POST /api/ai/chat`** (SSE) — KI-Workflow-Assistent: lila Button neben dem Standard/Experte-Toggle öffnet ein angedocktes Chat-Panel ([AiWorkflowChatPanel.tsx](src/nodepilot-ui/src/components/ai/AiWorkflowChatPanel.tsx)). Multi-Turn (`LlmRequest.Conversation`): erklärt den **aktuellen** Workflow (Markdown via `react-markdown`, live gestreamt) und schlägt auf Wunsch komplette Definitions-Umbauten vor (**Proposal-Karte** „poppt" am Ende — strukturiertes Changelog, selektives Übernehmen, Refine; da Merge/Validierung die volle Antwort braucht). Eigener Controller [AiChatController.cs](src/NodePilot.Api/Controllers/AiChatController.cs) mit `[Authorize]` (alle Rollen) — Änderungs-Proposals nur für Admin/Operator (`User.IsPrivileged()`), sonst serverseitig verworfen. Pipeline ([WorkflowAssistantService.cs](src/NodePilot.Ai/WorkflowAssistantService.cs)):
  - **Ausgabeformat** (statt JSON-Envelope, streamfreundlich): Markdown-Prosa, dann optional der Delimiter `===NODEPILOT-DEFINITION===` + `{nodes,edges}`. Die Prosa wird Token für Token als `delta` ausgegeben; alles nach dem Delimiter wird gepuffert und am Ende verarbeitet.
  - **System-Prompt** = `assistant-system.md` (Rolle, Schema, Secret-/Erhaltungs-/Injection-Regeln, Delimiter-Kontrakt) + `PromptCatalog.ActivityReference` (aus `workflow-system.md` herausgelöster Activity-Katalog **ohne** Generierungs-Output-Regeln) + dynamische `ActivityCatalog`-Metadaten der vorkommenden Node-Typen. Untrusted-Daten (das aktuelle, **secret-redigierte** Workflow-JSON) stehen in der **User-Message**, nicht im System-Prompt.
  - **Empty-Canvas-Design-Mode** (`IsEmptyCanvas`: 0 Nodes oder nur Trigger-Nodes — `activityType` endet auf `Trigger`): bei faktischer Erst-Erstellung hängt `BuildSystemPrompt` eine Design-Sektion + das **reiche Few-Shot-Beispiel `workflow-example.json`** an („mimic this structure & richness"), damit der Chat einen **verzweigten** Workflow vorschlägt statt einer dünnen linearen Kette (Parität zum `generate-workflow`-Pfad). Bei nicht-leerem Canvas bleibt der konservative „möglichst wenig ändern"-Edit-Modus.
  - **Secret-Redaktion**: `WorkflowDefinitionSecretRewriter.Rewrite(..., Redact, null)` maskiert `SecretConfigKeys` zu `***` vor jedem LLM-Call — Inline-Secrets verlassen die Instanz nie.
  - **Merge** ([WorkflowDefinitionMerge.cs](src/NodePilot.Ai/WorkflowDefinitionMerge.cs)): per Node-/Edge-`id` zurück aufs **unredigierte** Original — ausgelassene Felder (position, sourceHandle/targetHandle, parentId, group/sticky-Styles, credentialId, conditionExpression) werden erhalten; Secrets immer aus dem Original wiederhergestellt, von der KI gesetzte/abweichende Secret-Werte verworfen (+ Reply-Hinweis). Danach `WorkflowDefinitionStructuralValidator` + AI-Checks (Positionen, Trigger-Erhalt).
  - **Apply** läuft rein clientseitig auf den Canvas (kein DB-Write); Persistenz über den normalen Edit-Lock/Publish-Flow. Stale-Schutz: das Frontend hasht den Canvas-Stand (`hashDefinition`) und blockt das Apply, wenn er sich seit der Frage geändert hat.
  - **Tool-Calling** (opt-in `Llm:EnableToolCalling=false`): ist es an, läuft `WorkflowAssistantService.StreamChatAsync` eine OpenAI-Function-Calling-Schleife (`tool_choice:auto`, nur wenn es hilft). Das Modell darf read-only Tools auf der **secret-redigierten** Definition callen — `analyze_workflow` (deterministische Static-Analysis: fehlender Trigger, unreachable/orphan Steps, Zyklen, Remote-Step ohne Target-Machine, Strukturfehler — gleiche Codes wie der Canvas-Linter, via `WorkflowReviewAnalyzer` in `NodePilot.Core`) und `list_activity_types` (Activity-Katalog); Registry: `ChatToolRegistry`. Dazu drei **Execution-Log-Tools** — `list_recent_executions` (jüngste Läufe des geöffneten Workflows), `get_execution_steps` (Step-Details inkl. Output/ErrorOutput) und `get_failure_context` (One-Call: jüngster Failed-Run + Failed-Steps) — gespeist über `IExecutionLogReader` (Core-Interface) / `ExecutionLogReader` (Data, redigiert **immer** via `IAuditDetailsRedactor`, unabhängig vom Caller-Privileg — Outputs gehen ans externe LLM), Truncation in der Registry (1500/500 Zeichen, 2000 im Failure-Context, 100 Steps). **RBAC-Gate im Controller:** die WorkflowId ist client-kontrolliert; `AiChatController.Chat` prüft vor dem Stream Folder-Read (`IResourceAuthorizationService`) und reicht das Verdikt als `allowExecutionTools` an `StreamChatAsync` — kein Zugriff/unbekannt/ungespeichert → Reader wandert nicht in den `ChatToolContext`, die Execution-Tools werden nicht angeboten (`GetTools(context)` filtert) und ihre Handler antworten defensiv mit Error-JSON. Der Ownership-Check (`executionId` gehört zum autorisierten Workflow) lebt im Reader. Ergebnisse fließen als Tool-Messages zurück, dann produziert das Modell die finale Antwort/den Proposal. Gecappt durch `Llm:ToolCallMaxDepth=6` (gültig 1–10): max LLM-Runden mit Tool-Calls pro Turn — in der **letzten erlaubten Runde** sendet der Server **keine** `tools` (erzwingt Text-Antwort; vermeidet den `tool_choice:none`-Literal, den manche lokalen Endpoints mit HTTP 400 ablehnen). SSE-Stream erhält `tool_call`/`tool_result`-Events; das UI zeigt eine „🔧 analyze_workflow — running…/checked"-Anzeige. Braucht ein Modell, das Function-Calling zuverlässig kann (viele kleine lokale Modelle nicht); aus → Chat verhält sich exakt wie vorher (keine `tools` gesendet).
  - **`POST /api/ai/chat/applied`** (Admin/Operator, Folder-RBAC Edit) — schreibt Audit `AI_PROPOSAL_APPLIED` (mit Node-/Edge-Counts), wenn ein KI-Vorschlag auf den Canvas übernommen wird. **`GET /api/ai/chat/activity/{workflowId}`** (Admin/Operator, Folder-RBAC Read) — die KI-Audit-Einträge (`AI_WORKFLOW_EXPLAINED`/`AI_PROPOSAL_APPLIED`) eines Workflows, neueste zuerst; bewusst getrennt vom Admin-only `/api/audit`, damit Operatoren ihre eigene KI-Aktivität ohne globalen Audit-Zugriff sehen.
  - **Chat-UX (PR3)**: benannte Threads je Workflow (wechseln/umbenennen/löschen/neuer Chat), reload-persistenter Verlauf in localStorage (privacy-aware: strippt Canvas-Snapshots + Proposal-Definition-JSON, nie für ungespeicherte Workflows, gecappt auf ~200 Messages/Thread, Logout leert), Markdown-Export eines Threads und eine in-panel workflow-scoped „AI-Aktivität"-Ansicht. (Slash-Commands wurden bewusst **nicht** gebaut.)

**Transport**: OpenAI-kompatible HTTP-API über raw `HttpClient` — läuft gegen OpenAI Cloud, Ollama, LM Studio, vLLM, LocalAI, llama.cpp. Lokale Endpoints bevorzugt. Konfigurations-Keys + Modell-Empfehlungen siehe [docs/ai-features.md](docs/ai-features.md). Für Chat-Edits an großen Workflows ggf. `Llm:MaxTokens` erhöhen.

**Auth/Rate-Limit**: generate-Endpoints `[Authorize(Roles = "Admin,Operator")]`, chat `[Authorize]` (alle Rollen); alle drei `[EnableRateLimiting("ai-generate")]` (20/min/IP, hardcoded in [RateLimitingSetup.cs](src/NodePilot.Api/Hosting/RateLimitingSetup.cs)).

**Disabled-Antwort**: Wenn `Llm:Enabled=false` → 503 mit `code: LLM_DISABLED`. Andere Fehlerklassen siehe `LlmErrorKind` in [LlmException.cs](src/NodePilot.Ai/LlmException.cs).

**Hardening**:
- SSRF-Block für Cloud-Metadata-IPs in `Llm:BaseUrl` — einziger BaseUrl-Validierungspunkt [LlmEndpointGuard.cs](src/NodePilot.Ai/LlmEndpointGuard.cs) (`NormalizeAndValidateBaseUrl`/`IsCloudMetadataEndpoint`), plus Connect-Zeit-Guard `LlmConnectGuard` in [LlmServiceCollectionExtensions.cs](src/NodePilot.Ai/LlmServiceCollectionExtensions.cs)
- Fresh `SocketsHttpHandler` mit `UseProxy=false` (NICHT der `RestApiHttpClientProvider` — der hat SSRF-Guards die `127.0.0.1:11434` blocken würden)
- Klartext-`Llm:ApiKey` löst Startup-Hardening-Warning aus, analog `Smtp:Password` ([SecurityHardeningWarnings.cs:74](src/NodePilot.Api/Hosting/SecurityHardeningWarnings.cs#L74))

**Prompt-Injection-Residualrisiko**: Upstream-Variablen werden nur als Schema an den LLM gesendet — **niemals deren Werte**. Im System-Prompt als „untrusted JSON, not instructions" markiert. **Mitigation**: KI-generiertes Script wird am Cursor eingefügt (nicht stumm ersetzt), User muss aktiv reviewen.

**Drift-Schutz**: [PromptCatalogDriftTest.cs](tests/NodePilot.Engine.Tests/Ai/PromptCatalogDriftTest.cs) scannt `IActivityExecutor`-Implementations und assert-iert dass jede in `workflow-system.md` erwähnt ist (oder explizit in der Allowlist steht). Wer eine neue Activity mergt, muss den Prompt-Katalog ergänzen.

---

## CLI (`np`) — Befehlsbereiche & Details

Operations-CLI für Operatoren — eigenes Projekt unter [src/NodePilot.Cli/](src/NodePilot.Cli/), als `dotnet global tool` paketiert (`PackAsTool=true`, `ToolCommandName=np`). **Reiner HTTP-Client gegen die bestehenden REST-Endpoints** — keine eigene API-Surface, keine DB-Zugriffe.

**Befehlsbereiche:**
- `auth` — login/logout/whoami/**methods** (Discovery der Local/LDAP/Windows-SSO-Tiles)
- `workflow` — list/get/run/lock/unlock/publish/enable/disable/cancel-all/duplicate/delete/export/import/versions/rollback/force-unlock/import-scorch/stats/**contract**/**coverage**/**trigger**/**step-test**/**step-test-context**/**move-folder**
- `exec` — list/get/steps/cancel/retry/watch/resume/paused-steps
- Resources — `machine`, `credential`, `globals` (list/create/update/delete/**export**/**import**), `user` + **`shared-folder`** (org RBAC: list/create/rename/move/delete/permissions/grant/revoke) + **`maintenance`** (Wartungsfenster: list/get/create/update/delete) + **`system-alert`** (System-Alert-Policies, ADR 0008: catalog/list/get/create/update/enable/disable/delete/test-fire; create/update via `--file`) + **`alerting`** (Notification-Rules: list/get/create/update/delete/**test-fire**/**deliveries** [Zustell-Ledger, Filter `--rule`/`--status`]; Routen via `--email`/`--webhook`, Scope via `--folder`/`--workflow`)
- System — `audit list`, `health`, `cron next`, **`db`** (info/query — read-mode default, `--write` opt-in), `dashboard`, `observability` (summary/**query**/**query-range**), **`settings`** (status/system-info/get/put/test smtp|llm), **`secrets reencrypt`**, `config get|set`

Globale Flags: `--server`, `--profile`, `-o table|json|yaml`, `--no-color`, `-v`. Exit-Codes: 0 ok, 1 generic, 2 run failed/cancelled, 3 auth required, 4 permission denied.

**External-Trigger-Spezialfall:** `np workflow trigger <name>` ist session-unabhängig — der Endpoint ist anonym, gegated nur durch `X-Api-Key`. Schlüsselquellen in Präzedenz-Reihenfolge: `--api-key <K>` > `--api-key-stdin` > `NODEPILOT_TRIGGER_API_KEY` env. Optional `--idempotency-key <K>` für Replay-Schutz.

**Settings-Spezialfall:** `np settings ...` arbeitet section-basiert, file-roundtrip, ETag-gegated. Workflow: `np settings get Smtp --etag-only > etag.txt`, dann `np settings put Smtp --file smtp.json --etag $(cat etag.txt)`. Kein `set key=value`.

**Token-Storage:** DPAPI-encrypted (`CurrentUser`-Scope) unter `%APPDATA%\NodePilot\session-<profile>.dat`. Refresh transparent via `TokenRefreshHandler`. Klartext-Config (Server-URL, Default-Profile) liegt daneben in `config.json`.

**Architektur-Konvention:** Wer einen neuen API-Endpoint hinzufügt, der für Operatoren-Workflows relevant ist, legt parallel eine Methode in [NodePilotApiClient.cs](src/NodePilot.Cli/Api/NodePilotApiClient.cs) + ein Command unter `Commands/<Bereich>/` an. DTOs werden in `Cli/Api/Dtos/` **dupliziert** (kein ProjectReference auf `NodePilot.Api`).

---

## AuditLog — Vollständige Audit-Codes

> **Autoritative Quelle:** `NodePilot.Core.Audit.AuditActions` (Konstanten-Katalog). Die Liste unten ist eine Prosa-Übersicht; der Guard `AuditActionsCatalogTests` (Api.Tests) hält Katalog ↔ Verwendung in Sync. Neue Codes dort registrieren, nie als rohes Literal.

- `WORKFLOW_CREATED|UPDATED|DELETED|DUPLICATED|ROLLED_BACK|ENABLED|DISABLED|CANCEL_ALL`
- `WORKFLOW_LOCKED|UNLOCKED|PUBLISHED|FORCE_UNLOCKED` (Edit-Lock-Lifecycle)
- `MACHINE_CREATED|UPDATED|DELETED|CONNECTION_TESTED|CONNECTION_TEST_FAILED`
- `CREDENTIAL_CREATED|UPDATED|DELETED`
- `GLOBAL_VARIABLE_CREATED|UPDATED|DELETED`
- `LOGIN_SUCCESS|LOGIN_FAILED|LOGIN_LOCKED|LOGOUT|TOKEN_REFRESHED|USER_CREATED_BOOTSTRAP`
- `USER_CREATED|USER_ROLE_CHANGED|USER_ACTIVATED|USER_DEACTIVATED|USER_PASSWORD_RESET|USER_DELETED`
- `CREDENTIAL_DECRYPTED` (pro Decryption, nicht pro Run)
- `USER_LDAP_JIT_CREATED|JIT_UPDATED|LINKED|REFUSED_COLLISION|REFUSED_BOOTSTRAP` + `USER_WINDOWS_*` (gleiche Suffixe; Code ist `USER_{providerTag}_…` mit `providerTag` = LDAP|WINDOWS)
- `EXECUTION_STARTED|EXECUTION_CANCELLED|EXECUTION_RETRIED|EXECUTION_RESUMED|EXECUTION_STEP_OVER|EXECUTION_DEBUG_STOP`
- `WEBHOOK_TRIGGERED` | `EXTERNAL_TRIGGER_FIRED` (nur erfolgreiche Fires)
- `TRIGGER_FIRE_SUPPRESSED`
- `WORKFLOW_IMPORTED_SCORCH` | `WORKFLOW_EXPORTED` | `WORKFLOW_EXPORTED_BULK` | `WORKFLOW_IMPORTED`
- `AI_SCRIPT_GENERATED|AI_WORKFLOW_GENERATED|AI_WORKFLOW_EXPLAINED|AI_PROPOSAL_APPLIED` (Chat-Assistent; Details: nur Counts model/durationMs/modifyProposed/nodeCount/turnCount bzw. Node-/Edge-Counts bei Applied — kein Prompt-/JSON-Text)
- `DBADMIN_ROW_UPDATED` | `DBADMIN_ROW_DELETED`
- `DBADMIN_SQL_EXECUTED` | `DBADMIN_SQL_WRITE` (Admin SQL-Konsole; statement-preview in details)
- `SECRETS_REENCRYPTED` (Passphrase-Rewrap aller Secrets)
- `FOLDER_CREATED|FOLDER_UPDATED|FOLDER_MOVED|FOLDER_DELETED` | `WORKFLOW_MOVED` (Shared-Folders / RBAC Stufe A)
- `FOLDER_PERMISSION_UPDATED|FOLDER_PERMISSION_REVOKED` (Per-Folder-Grants)
- `MAINTENANCE_WINDOW_CREATED|UPDATED|DELETED|OVERRIDDEN` | `EXECUTION_BLOCKED_MAINTENANCE_WINDOW`
- `ALERT_RULE_CREATED|UPDATED|DELETED|ENABLED|DISABLED|TEST_FIRED` (Alerting / Notification-Rules — siehe `docs/alerting.md`)
- `SYSTEM_ALERT_POLICY_CREATED|UPDATED|DELETED|ENABLED|DISABLED|TEST_FIRED` (System-Alert-Policies, ADR 0008)
- `BACKUP_EXPORTED|BACKUP_RESTORED` (System-Configuration Backup, ADR 0001)
- `SETTINGS_{SMTP|LLM|RETENTION|AUTHENTICATION|LOGGING|OPENTELEMETRY|STATS|DBADMIN}_UPDATED` (Admin-Settings, ein Code pro Section)

**Audit-Write-Pipeline (Phase 3):** Jeder Audit-Write läuft durch `IAuditStager` (in [NodePilot.Core/Audit/](src/NodePilot.Core/Audit/)), inkl. der drei ehemals direkten Bypass-Pfade. HTTP-Controller nutzen `IAuditWriter` (in [NodePilot.Api/Audit/](src/NodePilot.Api/Audit/AuditWriter.cs)); der wrappt den Stager mit `HttpContextAccessor`-Actor-Resolution + ECS-Log-Forward + Support-Log-Whitelist-Check. Redaction + 4 KiB-Cap gelten überall einheitlich.

**Archive-Integrität:** `AuditLogRetentionService.ArchiveAsync` schreibt gzip-komprimierte `audit-{date}-{ticks}-{rand}.ndjson.gz` plus SHA-256-Sidecar. Periodische Verify-Pass (default daily) rechnet Hashes neu und alerted via Metric `nodepilot.audit_archive.hash_drift` bei Drift.

---

## Production Deployment — Referenz

Vollständige Operator-Doku: [deploy/README.md](deploy/README.md).

### Ziel-Topologie

- **Windows Service** unter einem **gMSA** (`DOMAIN\svc-nodepilot$`, `sc.exe create` mit leerem Passwort — `New-Service` kann gMSA nicht), Delayed-Auto-Start, Recovery-Actions
- **Kestrel bindet HTTPS direkt** auf den Ports aus `Kestrel:Https:HttpsPort|HttpPort`, Cert per Thumbprint aus `LocalMachine\My` — **kein IIS / Reverse Proxy**. SPA + API liegen zwingend auf **einer Origin**
- **Externes SQL Server 2022** (Trusted Connection) oder **PostgreSQL 16+** (user/password). gMSA-Login bzw. Postgres-Role braucht DDL-Rechte.
- **gMSA-Identität als WinRM-Auth**: `NegotiateWithImplicitCredential` in [WinRmSessionFactory.cs](src/NodePilot.Remote/WinRmSessionFactory.cs) erlaubt Kerberos gegen Ziel-Maschinen ohne gespeicherte Credentials, sofern resource-based Constrained Delegation eingerichtet ist

### Install-Dir / Data-Dir Split

| Pfad | Inhalt | Service-ACL |
|---|---|---|
| `C:\Program Files\NodePilot\` | `NodePilot.Api.exe`, DLLs, `wwwroot/` | Read |
| `C:\Program Files\NodePilot\appsettings.Production.json` | Config + Secrets | Read (Vererbung aus) |
| `C:\ProgramData\NodePilot\` | JWT-Key, Setup-Token, Logs, Install-Report | Modify (Vererbung aus) |

### Config-Keys (backward-compatible)

| Key | Zweck | Fallback |
|---|---|---|
| `Jwt:KeyPath` | Absoluter Pfad für `jwt-secret.key` | `{ContentRoot}/jwt-secret.key` |
| `Jwt:RotateInsecureKeyFile` | Einmalige, explizite Rotation einer unsicheren bestehenden Key-Datei; danach wieder auf `false` setzen. Invalidiert alle Sessions. | `false` |
| `Database:AllowInsecureTls` | Expliziter Development-Override, der die strikte DB-TLS-Prüfung des `DatabaseTlsBootValidator` deaktiviert; in Produktion `false` = Boot-Abbruch bei ungeprüftem Server-Zertifikat. | `false` |
| `Security:AdminSetupTokenPath` | Absoluter Pfad für `admin-setup.token` | `{ContentRoot}/admin-setup.token` |
| `Logging:File:Path` | Absoluter Pfad für Serilog-Rolling-File | `{ContentRoot}/logs/nodepilot-.log` |
| `Kestrel:Https:*` | Kestrel-direct-HTTPS aus Windows Cert Store | No-op → Default-Binding |

`AdminBootstrap.Validate/Consume/EnsureBootstrapTokenIfNeeded` akzeptieren optionalen `IConfiguration`-Parameter (Default `null`).

### `Credentials:DpapiScope` für Production

Installer-Template setzt `LocalMachine` ([appsettings.Production.json.template](deploy/templates/appsettings.Production.json.template)). `CurrentUser` bricht bei Service-Account-Wechsel. Siehe Warnung in [CredentialStore.cs](src/NodePilot.Data/CredentialStore.cs) (~Zeile 99).

### Stolperfallen (aus dem ersten Lab-Rollout gelernt)

- **PS 5.1-Kompatibilität**: `RandomNumberGenerator.Fill()` ist .NET-Core-only — stattdessen `RNGCryptoServiceProvider.GetBytes()`. Deploy-Skripte müssen auf PS 5.1 **und** PS 7 laufen
- **Em-Dashes (`—`) in PS-Scripts**: bricht PS 5.1 Parsing wenn Datei ohne BOM gespeichert. In Deploy-Skripten nur ASCII-Punctuation verwenden
- **`Set-StrictMode -Version Latest`** + `& npm ...`-Shim triggert `PropertyNotFoundStrict` auf `.Statement`. In Deploy-Skripten `Version 3.0` verwenden
- **`New-Service` unterstützt keine gMSA** (verlangt Passwort). `sc.exe create ... obj= DOMAIN\acct$ password= ""` ist der Workaround

---

## System-Configuration Backup (ADR 0001)

Voller DR-Snapshot der **Konfiguration** — getrennt vom redigierten Workflow-Export. Vollständige Designentscheidung: [docs/adr/0001-system-configuration-backup-restore.md](docs/adr/0001-system-configuration-backup-restore.md).

**Scope.** Enthalten: `folders` (Struktur + Grants), `users` (inkl. BCrypt-Hash), `credentials`, `machines`, `globalVariables` (+ Global-Variable-Ordner), `workflows`, `customActivities` (`CustomActivityBackupPart`, siehe `docs/custom-activities.md`), `alerting` (Custom-Regeln + System-Policies, ADR 0008), `settings` (nur `appsettings.runtime.json`). **Nicht** enthalten: AuditLog, Execution-History, StepExecutions, WorkflowVersions, Stats, SupportEvents, Alerting-Ledger/Suppression/Policy-State (transient) — dafür gilt der DB-eigene Backup-Pfad.

**Datei.** `.npbackup`, JSON-Envelope **`nodepilot-system-backup/v2`** — v2 fügt die `alerting`-Sektion hinzu; der Reader importiert v1 **und** v2, ältere Builds lehnen v2 sichtbar ab (`BackupSections.SupportedSchemas`). Struktur lesbar; nur Secret-Felder als `{"$enc":"<b64>"}`. Header `crypto` (kdf/iterations/salt/verifier) + Top-Level `mac`.

**Alerting-Sektion (v2).** `AlertingBackupPart` exportiert jede `NotificationRule` mit Routen (Route-Secret-Rewrap wie Credentials) + Scope-Targets. Restore remappt Targets über `FolderMap`/`WorkflowMap` und stempelt bei restaurierten enabled System-Policies ein frisches `ActivatedAt` (verhindert Back-Alerting der Historie).

**Crypto.** Passphrase → PBKDF2-SHA256 (600k Iter., per-File-Salt) → HKDF-Expand in drei Subkeys: `enc` (AES-256-GCM der `$enc`-Felder), `mac` (HMAC-SHA256 über kanonisches JSON der ganzen Datei), `verifier` (GCM eines bekannten Tokens → Passphrase-Check vor jedem Schreiben). [PassphraseSecretProtector.cs](src/NodePilot.Data/Security/PassphraseSecretProtector.cs). Rewrap-Pfad (Export: at-rest entschlüsseln → Passphrase verschlüsseln; Restore: umgekehrt) entspricht `ReencryptAllCredentialsAsync`.

**Endpoints** (alle `[Authorize(Roles="Admin")]`):

| Endpoint | Body | Zweck |
|---|---|---|
| `GET /api/backup/manifest` | — | Section-Counts |
| `POST /api/backup/export` | JSON `{sections[], passphrase}` | streamt `.npbackup` |
| `POST /api/backup/preview` | multipart `file` + `passphrase?` | Diff je Section; ohne Passphrase `integrityVerified=false` |
| `POST /api/backup/restore` | multipart `file` + `passphrase` + `policy` | wendet an |

**Export** zieht harte Dependencies automatisch mit (Workflows → Folders/Machines/Credentials) und versiegelt mit dem Whole-file-MAC. **Workflow-Secrets** liegen inline in `DefinitionJson` (`secret`/`apiKey`/`password`/`authToken`/`bearer`/`connectionString`) und werden über `WorkflowDefinitionSecretRewriter` mit `SecretHandling = Redact | EncryptForBackup | PlainInternal` behandelt — dieselbe Klasse, die der redigierte Workflow-Export nutzt. `targetMachineId`/`credentialId` sind GUID-Referenzen, kein Secret → ID-Remap beim Restore.

**Restore** (Service: [BackupRestoreService.cs](src/NodePilot.Api/Services/Backup/BackupRestoreService.cs)):
1. Passphrase via Verifier prüfen → sonst Abbruch. Whole-file-MAC prüfen → Mismatch = Abbruch (Tamper).
2. **Referenz-Validierung** (vor jedem Schreiben): jede harte Ref muss im Backup **oder** in der Ziel-DB (per `sourceId`) auflösbar sein, sonst Abbruch.
3. Eine DB-Transaktion, **gekapselt in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`** — Pflicht, weil Postgres/SQL Server eine Retrying-Strategy nutzen, die direkte `BeginTransaction` ablehnen. Reihenfolge: Users → Folder-Struktur → Credentials → Machines → Globals → Workflows → Folder-Grants. Jede Section füllt eine `sourceId→targetId`-Map; Folgereferenzen werden darüber remappt (`Machine.DefaultCredentialId`, `Folder.ParentFolderId`/`CreatedByUserId`, Workflow-Def-GUIDs, Grant-Principals). AD-Group-SIDs in Grants bleiben unverändert.
4. **Settings** danach, **außerhalb** der Transaktion (File via `RuntimeOverridesWriter`), eigene Ergebniszeile. **Replace, nicht Merge**: Top-Level-Overrides, die im Ziel existieren aber nicht im Backup, werden entfernt (`__meta` bleibt); `enc:v1:`-Werte werden re-sealed.

**Konflikt-Policy** (by-name-Match; default `skip`): `skip` / `rename` (Suffix `(Restored N)`) / `overwrite`. Format im `policy`-Feld: bare Wert (global) und/oder `section=policy`-Paare, komma-getrennt (z. B. `skip,users=overwrite`).

**Sicherungen:** Last-Admin-Schutz (Restore lässt nie 0 aktive Admins zurück → Abbruch). User-`overwrite` bumpt `SecurityStamp` + setzt `PasswordChangedAt` bei Hash-/Rollen-/Status-Änderung (invalidiert Sessions). Verwaiste Grants (Principal-User existiert nicht) werden mit Warnung übersprungen, nicht wiederbelebt.

**Audit:** `BACKUP_EXPORTED` (mit akkuratem `containsSecrets` — nur wahr wenn tatsächlich ein `$enc`-Feld versiegelt wurde) / `BACKUP_RESTORED` (Passphrase nie im Log). **Rate-Limit:** Policy `backup` (10/min/IP) auf dem ganzen Controller — Export liest Secrets, Restore ist ein schwerer Bulk-Write. **Fehlerbild:** falsche Passphrase / MAC-Fehler / unresolvable Refs / Last-Admin → 409; strukturell kaputte (aber MAC-valide) Datei → 400 (nicht 500). **CLI:** `np backup manifest|export|preview|restore`; Passphrase via `--passphrase-env`/`--passphrase-file`/Prompt, nie als Flag.

**Test-Coverage-Lücke (bewusst):** Die Execution-Strategy-Kapselung (Schritt 3) wird von den Tests über SQLites *non-retrying* Strategy ausgeführt, aber nicht *erzwungen* — ein Entfernen des Wrappers bliebe in SQLite-Tests grün und würde erst gegen echtes Postgres/SQL Server brechen. Der ADR-Kommentar + Code-Kommentar halten die Invariante fest.

---

## Security — Detail-Referenzen

- **Sessions:** 8h absolute Lifetime (Default), serverseitige `AuthSession`, atomare JTI-Rotation und Revocation via `TokenValidityMiddleware`. Externe Gruppen liegen in authority-scoped `DirectoryMemberships`, nicht im JWT. Key aus `Jwt:Key` oder `{ContentRoot}/jwt-secret.key` (generiert beim ersten Start). Details: [JwtKeyResolver.cs](src/NodePilot.Api/Security/JwtKeyResolver.cs).
- **Authentication-Pfade:** Local-BCrypt (Default `BreakGlassOnly`), LDAPS, Windows-Negotiate/Kerberos und OIDC Code + PKCE konvergieren auf dieselbe Session; SCIM 2.0 provisioniert Benutzer und Gruppen. Externe Identitäten werden kanonisch über `(Authority, Subject)` aufgelöst; LDAP und Windows verwenden dieselbe AD-`objectSid`. Setup + Troubleshooting in [docs/ldap-windows-sso.md](docs/ldap-windows-sso.md). Status bis zum realen AD-Feldtest: **AD SSO Preview**.
- **REST-API-Proxy:** `RestApi:Proxy:Enabled` (default `false`). Bei `true` sind `Address` (Pflicht), `BypassList`, `Username`/`Password` auswertbar. Per-Step-Override via Node-Config: `proxyMode` + `proxyAddress` + `noProxy`. Details in [RestApiHttpClientProvider.cs](src/NodePilot.Engine/Security/RestApiHttpClientProvider.cs).

### Hardening-Flags (Default-On, in Development relaxed)

Die Guard-Flags sind **hardened by default**: appsettings.json shippt sie als `true`, und ein **fehlender** Key liest ebenfalls als `true` (siehe `PathGuardTests`/`NetworkGuardTests`). `appsettings.Development.json` relaxt sie für lokale Iteration auf `false`. Ausnahme: `PrometheusScrapeAllowAnonymous` ist eine Relaxation und default `false`.

| Key | Default | Wirkung |
|---|---|---|
| `Remote:RequireWinRmSsl` | `true` | WinRM ohne SSL → Exception (Dev: `false`) |
| `RestApi:BlockPrivateNetworks` | `true` | Blockiert RFC1918/Loopback in `restApi` (Dev: `false`) |
| `RestApi:AllowedHosts` | `[]` | Exakte Host-/IP-Allow-Liste; zwingend für PowerShell-`waitForCondition`-Netzwerk-Probes und tatsächlich proxied `restApi`-Ziele/Redirects. Link-Local/Cloud-Metadata bleibt immer gesperrt (Dev: `localhost`/`127.0.0.1`/`::1`) |
| `FileSystemOperation:RejectTraversal` | `true` | Lehnt `..` in File-System-Op-Paths ab (Dev: `false`) |
| `SqlActivity:RequireConnectionRef` | `true` | Nur benannte `connectionRef` statt inline `connectionString` (Dev: `false`) |
| `StartProgram:DisallowShellExecute` | `true` | Verwirft `useShellExecute=true` (Dev: `false`) |
| `Trigger:Database:RequireConnectionRef` | `true` | Nur benannte `connectionRef` für `databaseTrigger` (Dev: `false`) |
| `Security:StrictAllowedHosts` | `true` | Boot-Abbruch bei unsicherem `AllowedHosts` (z.B. `*`) (Dev: `false`) |
| `Webhook:RequireSecret` | `true` | `webhookTrigger` erzwingt ein konfiguriertes Secret — verifiziert je nach `signatureMode` als `X-Webhook-Secret`-Header oder NodePilot-HMAC-v2-Signatur (Dev: `false`) |
| `Database:AllowInsecureTls` | `false` | Relaxation: deaktiviert die strikte DB-TLS-Prüfung (`Encrypt=Strict` / `SSL Mode=VerifyFull`). Prod-Default fail-closed; Dev: `true` |
| `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` | `false` | `/metrics` anonym erreichbar |

## Maintenance Windows — Semantik

Endpoints: siehe CLAUDE.md "API Endpoints" (Maintenance Windows-Zeile). CLI: `np maintenance`. Modelle/Enums in `src/NodePilot.Core/Models/MaintenanceWindow.cs` + `src/NodePilot.Core/Enums/Maintenance*.cs`, Evaluator `IMaintenanceWindowEvaluator`.

- **Admission-Control, kein Kill-Switch:** Ein Fenster gated nur **neu** zu admittierende Läufe. Es cancelt **nie** laufende Executions und re-gated **nie** ein `resume`/`retry` oder Sub-Workflow-Aufruf. (Für Sofort-Stopp: `disable` + `cancel-all` = Quarantäne.)
- **Modi (`MaintenanceMode`):** `Blackout` (verweigern) und `AllowOnly` (nur den genannten Scope zulassen). **Deny-wins** bei Überlappung: greift irgendein `Blackout`, wird blockiert — egal welche `AllowOnly`-Fenster sonst gelten.
- **Scope (`MaintenanceScopeKind`):** `Global` | `Folders` (Ordner-Subtree) | `Workflows` (explizite Liste).
- **Recurrence (`MaintenanceRecurrenceKind`):** `OneTime` | `Weekly` | `Cron`. Lokale Zeiten/DST werden zu halb-offenen UTC-Intervallen aufgelöst. `Cron` = Quartz-Ausdruck (`CronExpression`, mit Sekundenfeld, interpretiert in `TimeZoneId`) + `DurationMinutes` (1..10080, API-validiert via `Quartz.CronExpression.IsValidExpression`): das Fenster ist aktiv in `[fire, fire + duration)` je Fire. Ungültiger Ausdruck / fehlende Dauer / unbekannte Zone → Fenster inert (fail-open, wie unbekannte Zeitzone bei Weekly).
- **Deferral (`MaintenanceDeferralPolicy`):** v1 honoriert **nur `Skip`** (geblockter Lauf wird verworfen). `RunOnceAfter`/`RunAllAfter` sind im Enum reserviert, aber **nicht** implementiert (`MaintenanceWindow.cs:77`).
- **Verteilung:** `MaintenanceWindowSnapshotService` (always-on, **nicht** leader-gated) hält pro Knoten einen Snapshot aktuell, damit jeder Knoten die API-/Webhook-Last bewertet, die er bedient.

## Background Services — Inventar

Alle Hosted-Services werden gebündelt in [BackgroundServicesSetup.cs](src/NodePilot.Api/Hosting/BackgroundServicesSetup.cs) registriert (+ Cluster-Services in `ClusterSetup.cs`, SignalR-Bridge in `Program.cs`). Gating: **always-on** | **opt-in** (Config-Flag) | **leader-only** (nur im A/P-Leader aktiv).

| Service | Zweck | Gating |
|---|---|---|
| `TriggerOrchestrator` + Quartz | Trigger-Scan (5 s) + Quartz-Cron für `scheduleTrigger` | leader-only (im Cluster) |
| `ExecutionDispatchWorker` | Channel-basierter Dispatch der `Pending`-Executions an die Engine | always-on |
| `MaintenanceWindowSnapshotService` | Hält den Maintenance-Window-Snapshot pro Knoten aktuell | always-on (nicht leader-gated) |
| `ExecutionRetentionService` | Trimmt `WorkflowExecutions` (30 d) | opt-in `Retention:Executions:Enabled` |
| `AuditLogRetentionService` | Trimmt `AuditLogs` (365 d) + gzip/SHA-256-Archiv | opt-in `Retention:AuditLog:Enabled` |
| `WorkflowVersionsRetentionService` | Hält je Workflow die letzten N Versionen (50) | opt-in `Retention:WorkflowVersions:Enabled` |
| `SupportEventRetentionService` | Trimmt `SupportEvents` (90 d) | opt-in `Retention:SupportEvents:Enabled`, leader-only |
| `NotificationDispatcher` | Alerting: matcht Execution- + Signal-Events gegen Regeln, sendet via Sinks (~30 s) | opt-in by data, leader-only |
| `NotificationRetentionService` | Trimmt den Delivery-Ledger + stale Suppression-States (90 d) | opt-out `Retention:Notifications:Enabled`, leader-only |
| `IdempotencyKeyCleanupService` | Prunt Idempotency-Keys nach 24 h TTL | always-on (nicht abschaltbar) |
| `WorkflowStatsRefresher` | Berechnet `WorkflowStats`-Aggregat (siehe Stats) | always-on |
| `RevokedTokensCleanupService` | Täglicher Sweep der `RevokedTokens` (Audit M12) | always-on |
| `HubRevocationSweeper` | Schließt SignalR-Verbindungen bei Logout/Deaktivierung (Audit M2) | always-on |
| `SupportEventFlushService` | Gepufferter Flush von Support-Events in die DB | always-on (wenn DB-Projektion an) |
| `ClusterLeaderService` / `ClusterFencingHost` / `ClusterFailoverRecoveryHost` | Leader-Lease, Fencing, Failover-Recovery | nur `Cluster:Enabled` (siehe `docs/ha-active-passive.md`) |

**Alerting-Config:** `Alerting:Gauge:Enabled` (default `true`), `Alerting:Gauge:BacklogThreshold` (default `500`, Pending+Running), `Alerting:Gauge:PendingThreshold` (default `40`, nur Pending), `Alerting:Gauge:CancelRateThreshold` (default `10`) + `Alerting:Gauge:CancelRateWindowMinutes` (default `10`, globale Cancel-Rate), `Alerting:Gauge:ScheduleMissedGraceMinutes` (default `5`), `Alerting:Gauge:NoRecentSuccessHours` (default `24`), `Alerting:LongRunningSeconds` (default `600`, `ExecutionRunningLong`), `Alerting:QueuedLongSeconds` (default `300`, `ExecutionQueuedLong`). Signal-Events feuern pro ungesunder Episode höchstens einmal je Regel (EventKey = Episode-Start), der Filter wird aber jeden Pass neu geprüft (höhere `signalValue`-Schwelle wird nicht verschluckt). `LongRunningSeconds`/`QueuedLongSeconds`/`Gauge:Enabled` werden pro Pass aus der Live-Config überlagert (hot-reload — `NotificationDispatcher` liest `IConfiguration` am Kopf von `DispatchOnceAsync`, nur überlagern wenn Key gesetzt); Provider-Schwellen sind ebenfalls pro-Pass. `cancelledBy`-Event-Feld (`user`/`cancelAll`/`failover`/`reconciler`/`dispatch`/`system`) für `ExecutionCancelled`. Siehe `docs/alerting.md`.

## Stats — Workflow-KPI-Aggregat

Dashboard + Workflow-Listen lesen ein **vorberechnetes** `WorkflowStats`-Aggregat statt pro Request `WorkflowExecutions` zu scannen. Refresh durch `WorkflowStatsRefresher` (always-on).

| Key | Default | Wirkung |
|---|---|---|
| `Stats:RefreshIntervalMinutes` | `5` | Refresh-Intervall des Aggregats |
| `Stats:WindowDays` | `7` | Zeitfenster der aggregierten KPIs |

`GET /api/stats/dashboard` liefert daher den letzten Refresh-Stand, nicht Live-Zahlen. Mutationen am Setting schreiben `SETTINGS_STATS_UPDATED` ins AuditLog. `RefreshIntervalMinutes`/`WindowDays` sind hot-reloadable — der Refresher liest sie pro Pass aus der Live-Config (kein Neustart nötig).

## Support-Log & SupportEvents

Zwei Sub-Sinks aus demselben Serilog-Filter (für Operator-/Ticket-Diagnose):

1. **Plain-Text-Datei** `{Logging:SupportLog:Path}` (Default `<ContentRoot>/logs/`, Production-Installer: `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log`), `RetainedFileCountLimit` = 90 Tage, `FileSizeLimitBytes` Roll-Limit.
2. **DB-Tabelle `SupportEvents`** für den Web-Viewer (Filter/Cursor/Export) — Toggle `Logging:SupportLog:DbProjectionEnabled` (default `true`). Geschrieben über den gepufferten `SupportEventFlushService`, getrimmt durch `SupportEventRetentionService` (90 d, `Retention:SupportEvents`).

Endpoints: `GET /api/diagnostics/support-log|support-log/download|support-events|support-events/export` (Admin). UI: eigene Hauptmenü-Seite `/support-log` (Admin-only, im Sidebar direkt unter „Alerting" — bewusst außerhalb der Admin-Settings verortet, damit der Operator sie im Support-Fall direkt erreicht) → Toggle „Tabelle (DB) | Plain-Text (Datei)".

## Admin-Settings — Hot-Reload-Matrix

Admin-Settings-Saves persistieren atomar nach `appsettings.runtime.json` (hängt mit `reloadOnChange: true` dran, `RuntimeOverridesSetup.cs`). Pro Sektion trägt `SettingsSchema.cs` ein `IsHotReloadable`-Flag; `AdminSettingsController.PutSection` setzt den Restart-Marker (`RestartRequiredFor`) nur für `false`-Sektionen — rein datengetrieben, kein Controller-Branch. Die UI zeigt auf hot-reloadable Karten einen emerald „sofort wirksam"-Hinweis (`HotReloadHint`, i18n `adminSettings:hotReloadHint`), auf restart-pflichtigen den orangen `RestartBanner` (getrieben von `/api/admin/settings/status`).

| Sektion | Hot-Reload? | Consumer / Begründung |
|---|---|---|
| `Smtp` | ✓ | `SmtpNotificationSink` + `EmailActivity` lesen `IOptionsMonitor<SmtpOptions>.CurrentValue` pro Senden |
| `Llm` | ✓ | `ILlmClientFactory` + `WorkflowAssistantService` + Gates (`LlmQueryActivity`/`AiController`/`AiChatController`) lesen `IOptionsMonitor<LlmOptions>.CurrentValue` pro Use/Request |
| `Retention` | ✓ | `Execution`/`AuditLog`/`WorkflowVersions`/`Notification`/`SupportEvent`-RetentionService lesen `IOptionsMonitor<RetentionOptions>.CurrentValue` pro Schleifen-Pass (`RunIterationAsync`-Seam); `ArchivePath`-Wechsel invalidiert den Cache → Re-Probe (AuditLog bewahrt Compliance-Invariante). `IdempotencyKeyCleanupService` bleibt bewusst config-frei (fixe 24h-TTL) |
| `Stats` | ✓ | `WorkflowStatsRefresher` liest `IConfiguration.GetValue` pro Pass |
| `Threading` | ✓ | `ThreadPoolTuningService` re-appliert `ThreadPool.SetMinThreads` bei Start + `ChangeToken.OnChange` (Boot-Call bleibt für Cold-Start-Prewarm) |
| `FileSystemOperation` | ✓ | `PathGuard` liest `FileSystemOperation:RejectTraversal`/`AllowedRoots` pro Use aus `IConfiguration` |
| `SqlActivity` | ✓ | `SqlActivity` liest `SqlActivity:RequireConnectionRef` pro Use aus `IConfiguration` |
| `StartProgram` | ✓ | `StartProgramActivity` liest `StartProgram:DisallowShellExecute` pro Use aus `IConfiguration` |
| `Webhook` | ✓ | `WebhooksController` liest `Webhook:RequireSecret` pro Request aus `IConfiguration` |
| `ExternalTrigger` | ✓ | `ExternalTriggerController` liest `ExternalTrigger:ApiKey` pro Request aus `IConfiguration` |
| `DbAdmin` | ✓ | `DbAdminQueryExecutor` liest `IOptionsMonitor<DbAdminOptions>.CurrentValue` pro Query (Referenz-Consumer) |
| `Authentication` | ✗ | LDAP/Windows-SSO-Options beim Boot in die Auth-Builder eingebunden |
| `Logging` | ✗ | Serilog-Logger einmal beim Boot konfiguriert (Reload würde neue Pipeline needed) |
| `OpenTelemetry` | ✗ | OTel-SDK einmal beim Boot gebaut |
| `Security` | ✗ | `StrictAllowedHosts`/`AllowedHosts` einmal beim Boot gelesen |
| `RestApi` | ✗ | Mixed: `BlockPrivateNetworks` live, `Proxy` an `RestApiActivity` boot-fest → konservativ ganze Sektion restart-pflichtig |
| `Remote` | ✗ | Mixed: `Provider`+SSL+Timeouts+Pool boot-fest gebunden → konservativ ganze Sektion restart-pflichtig |
| `Engine` | ✗ | Concurrency-Caps beim Boot in den Engine-Channel/Pool gebaut |
| `ExecutionDispatch` | ✗ | Queue/Channel beim Boot gebaut |

**Mixed-Section-Limits:** `RestApi` und `Remote` mischen live- und boot-feste Keys in einer Sektion. Da `IsHotReloadable` nur Section-Granularität kennt, können diese nicht gemischt sein — sie bleiben konservativ restart-pflichtig (als bekannte Einschränkung dokumentiert, kein Flag-Per-Field). `Retention:ArchivePath` ist davon ausgenommen: der Pfad wird aktiv re-validiert, ein Wechsel löst eine Re-Probe aus (kein Neustart).
