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
| `llmQuery` | Engine-local | `prompt` (Pflicht; `{{templates}}` erlaubt), `systemPrompt` (optional; leer = Passthrough, kein synthetischer Default), `jsonMode` (bool → `response_format:json_object`; Antwort wird **nicht** validiert). **Per-Node-Overrides** (leer → das aktive LLM-Profil): `baseUrl` (absolute http/https; via `LlmEndpointGuard` validiert, Cloud-Metadata blockiert), `model`, `apiKey` (Secret, auto-redigiert), `maxTokens` (>0), `temperature` (0..2, per-Node-only — kein globaler Knopf; leer = Provider-Default), `timeoutSeconds` (>0). **`Llm:Enabled=true` + auflösbares aktives Profil Pflicht** (sonst sauberer Step-Fehler). `output` = Antworttext (roh, füttert Downstream-Databus; `OutputRedactor` scrubbt nur Persistenz/SignalR). Token-Keys **immer** gesetzt, `""` wenn Server keine `usage` liefert (`type:"number"` ist nur UI-/Databus-Hint). Transiente Fehler (RateLimited/Unreachable) via `config.retry` wiederholbar. | `output` (Antwort), `param.model`, `param.promptTokens`, `param.completionTokens`, `param.totalTokens`, `param.finishReason` |
| `waitForCondition` | Hybrid | `conditionType` (`script` default / `pathExists` / `serviceRunning` / `portOpen` / `httpOk`), `intervalSeconds`, `timeoutSeconds`. Mode-spezifisch: `script` → `script` (PowerShell-Expr, **keine** `{{...}}`-Templates), `pathExists` → `path`, `serviceRunning` → `serviceName`, `portOpen` → `host`+`port`, `httpOk` → `url`. Getypten Modi nehmen `{{upstream.param.x}}` an — Engine quotet Werte sicher. | `param.attempts`, `param.elapsedSeconds`, `param.lastResult` |

### Prozess-Isolation (`runScript`, nur lokal)

`config.isolated: true` startet das Script als eigenen Prozess race-frei in einem Windows Job Object (`KILL_ON_JOB_CLOSE` → keine verwaisten Prozesse bei Host-Crash/Restart + Crash-/Leak-Containment). Opt-in Caps: `memoryLimitMb` (JOB_MEMORY, aggregat über alle Prozesse des Jobs — lässt Allokationen fehlschlagen, terminiert nicht) + `maxProcesses`. Erzwingt einen Prozess-Engine (nie Runspace-Pool); No-Op auf dem Remote/WinRM-Pfad. Impl: `IsolatedProcessLauncher` (STARTUPINFOEX + `PROC_THREAD_ATTRIBUTE_JOB_LIST` — Prozess startet direkt im Job, kein Assign-after-Spawn-Race).

**Handle-Inheritance-Hardening (fixt „Execution hängt in Running obwohl alle Nodes fertig sind"):** Die stdout/stderr-Anonymous-Pipes des isolierten Childs sind kurzzeitig *inheritable* im API-Prozess offen. Ein *anderer* gleichzeitiger `CreateProcess`/`Process.Start` mit `bInheritHandles:true` ohne `HANDLE_LIST` (non-isolated runScript, `startProgram`, user-`Start-Job`) konnte diese Write-Handles erben und die Pipe offenhalten → `ReadToEndAsync` erreicht nie EOF → der Step-Task terminiert nie → Execution bleibt ewig `Running`. Zwei Gegenmaßnahmen: (1) **`ProcessSpawnCoordinator`** serialisiert *alle* NodePilot-eigenen inheritable-handle-Spawns (Launcher-Fenster + non-isolated `Process.Start` + Executable-Probe) hinter einem Prozess-globalen Lock, sodass sich kein Vererbungsfenster mit einem anderen Spawn überschneidet. (2) **Bounded Drain:** nach `WaitForExit` + `TerminateJobObject` existiert kein legitimer Pipe-Writer mehr, daher wird der stdout/stderr-Drain auf `Engine:IsolatedDrainGraceSeconds` (default 5 s) begrenzt — bleibt ein Read nach dieser Frist offen (geleaktes Handle in einem Fremdprozess), gibt der Step das bereits gepufferte Output zurück, loggt eine Warnung und terminiert (statt ewig zu hängen). `DrainReadsAsync` observiert die abgebrochenen Reads, sodass kein `UnobservedTaskException` entsteht.

### runScript — Ausführungsort (local vs. remote & Self-Managed-Remoting)

`runScript` ist *hybrid*; der Dispatch entscheidet allein anhand der gesetzten Maschine ([RunScriptActivity.cs:88-91](../src/NodePilot.Engine/Activities/RunScriptActivity.cs#L88-L91)):

- **Maschine gesetzt** (non-loopback bzw. + Credential) → NodePilot baut die WinRM-Session über die gepoolte `WinRmSessionFactory` (`ExecuteRemoteAsync`). Das Script läuft auf dem Ziel, **kein** Session-Management im Script nötig.
- **Keine Maschine** (bzw. loopback ohne Credential) → der Node läuft **engine-local im API-Host** (`ExecuteLocalAsync`, Runspace-Pool bzw. isolierter Prozess). Von dort kann das Script die Remote-Verbindung **selbst** herstellen — SCOrch-Stil: `Invoke-Command -ComputerName SRV01 -Credential $c { … }` / `New-PSSession`. Sinnvoll für dynamische Ziellisten, Fan-out auf N Maschinen in **einem** Node oder Jump-Host-Ketten.

**Engine-local = PowerShell-SDK, nicht volles pwsh:** Der In-Process-Pool shippt nur die acht Core-Module plus das gebündelte `Microsoft.PowerShell.Archive` (eager importiert, `PSModules\` im Output — Grund: WinPSCompat-Session-Leak 2026-07-30, siehe `docs/performance-improvements.md`). **Implizite Windows-PowerShell-Kompatibilität ist deaktiviert** (`powershell.config.json` mit `DisableImplicitWinCompat` neben der SDK-DLL, platziert via `Directory.Build.targets`): Ein Desktop-only-Modul (z. B. die System32-Kopien ohne Core-Flag) schlägt engine-local **laut** fehl statt still einen `powershell.exe -s`-Kindprozess pro Pool-Runspace zu akkumulieren. CDXML-Module (ScheduledTasks, NetTCPIP, Defender, …) laden weiterhin nativ; bewusstes `Import-Module -UseWindowsPowerShell` bleibt möglich (die so erzeugte Session gehört dann dem Script — inkl. Aufräumen). `pwsh`/`powershell.exe`-Prozess-Engines und der WinRM-Pfad sind nicht betroffen (eigenes `$PSHOME` bzw. Remote-Seite).

**Trade-offs beim Self-Managen** (bewusst außerhalb des managed WinRM-Pfads):

1. Läuft auf dem **API-Host**, nicht auf dem Ziel — der Host braucht Netz-/WinRM-Client-Zugriff (TrustedHosts/Kerberos/SSL) selbst.
2. Der DPAPI-**Credential-Store ist nicht verdrahtet** → `PSCredential` im Script selbst bauen; Secret via `{{globals.NAME}}` (im Output redigiert), nie hardcoden.
3. **Kein** Machine-Targeting/-Test (`POST /{id}/test`), keine `StepExecution.TargetMachine`-Zuordnung, keine per-Machine-Telemetrie/-Health.
4. Hardening (`Remote:RequireWinRmSsl`, SSL-Enforcement, Session-Pool) **greift nicht** — es hängt am Factory-Pfad.

---

## Trigger — Injected-Params

Trigger-Sources seeden ihre Event-Daten als `manual.*`-Variablen in den Run (`VariableResolver` schreibt `manual.<key>`), referenzierbar als `{{manual.<name>}}`. Jeder Trigger-Node spiegelt dieselben Keys zusätzlich als eigene `param.*`-Outputs (`{{<triggerVar>.param.<name>}}`). Es gibt **kein** `trigger.*`-Namespace — ein `{{trigger.file.path}}` bleibt unaufgelöstes Literal.

> **Häufigster Autorenfehler:** `{{trigger.doctorEmail}}` statt `{{trigger.param.doctorEmail}}`. Das `param.` fehlt, damit ist der Tail keiner der vier gültigen — die Referenz bleibt wörtlich stehen und wandert unbemerkt in die Config (z.B. als E-Mail-Empfänger). Im Muster-Workflow-Set steckten 9 solcher Referenzen in 3 Workflows.

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

## `WorkflowExecution.ErrorMessage` — Triage-Summary

Beim Übergang in einen terminalen Zustand befüllt die Engine `ErrorMessage` mit einer kompakten
Zusammenfassung statt sie (wie früher) auf `null` zu lassen. Format:

```
Activity "<label>" failed[: <error>][ (+N more failed activities)]
```

- **`<label>`** = `StepExecution.StepName`, Fallback `StepId` wenn leer.
- **`<error>`** = `StepExecution.ErrorOutput` der ersten fehlgeschlagenen Step; fehlt der Teil komplett, wenn `ErrorOutput` leer ist.
- **`(+N …)`** erscheint nur bei mehr als einem fehlgeschlagenen Step.
- **Auswahl der „ersten" Failure:** `OrderBy(StartedAt).ThenBy(Id)` — deterministisch auch wenn parallele Branches gleichzeitig scheitern.
- **Redaction + Cap:** wie `InputParametersJson`/`ReturnData` durch `RedactAndCap(…, 32 KiB)` — Secrets maskiert, Überlänge mit `... [truncated]` abgeschnitten.
- **Nur bei `Failed`.** Ein Lauf, der crasht statt sauber zu terminieren, trägt weiterhin die redigierte Exception-Message.

**Autoritativ bleibt `StepExecution.ErrorOutput`.** `ErrorMessage` ist reine Triage-Oberfläche für
Execution-Listen, Alerting-Notifications und `startWorkflow`-Parent-Läufe — nie als Fehler-Parsing-Quelle verwenden.

Impl: [WorkflowEngine.cs](src/NodePilot.Engine/WorkflowEngine.cs) (`failureSummary`).

### `ExecutionResponse.StepsTotal` ist **kein** Fortschritts-Nenner

`StepsTotal`/`StepsCompleted`/`FailedSteps` werden vom Listen-Endpoint `GET /api/executions` **und** von `GET /api/executions/{id}` befüllt (`Execute`/`Retry` lassen die Defaults stehen — eine frische `Pending`-Zeile hat noch keine Steps). `FailedSteps` ist nach `(StartedAt, Id)` sortiert; `StartedAt` allein ist bei parallelen Zweigen kein stabiler Sortierschlüssel.

**Für einen laufenden Lauf sind die Zahlen kein Fortschritt.** `Engine:DeferRunningStateWrite` ist default `true` ([WorkflowEngine.cs](src/NodePilot.Engine/WorkflowEngine.cs)): die `StepExecution`-Zeile wird in [StepRunner.cs](src/NodePilot.Engine/Execution/StepRunner.cs) zwar beim Step-Start `Add`-ed, aber erst im terminalen Zustand gespeichert — ein gerade laufender Step hat **gar keine Zeile**. Daraus folgt für einen laufenden Lauf: `StepsTotal` zählt nur die bereits **fertigen** Steps, `StepsCompleted/StepsTotal` liest sich durchgehend als „100 %", und der seit zehn Minuten hängende Step ist unsichtbar.

Deshalb zeigt Live-Ops bewusst **keinen Prozentbalken**, sondern die Zahl fertiger Steps plus Stagnations-Alter. Auch `Workflow.ActivityCount` taugt nicht als Ersatz-Nenner: es zählt Trigger und deaktivierte Nodes nicht mit ([WorkflowDefinitionDocument.cs](src/NodePilot.Core/WorkflowDefinitions/WorkflowDefinitionDocument.cs), `BuildMetadata`), während `StepExecution`-Zeilen ausgeführte Trigger und `Skipped`-Zeilen enthalten — dazu kommen dynamische Loop-Iterationen.

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

**Activity-Katalog im Prompt (generiert, nicht handgepflegt):** Der Abschnitt „Activity catalog" in `activity-reference.md` ist nur der Platzhalter `<!--ACTIVITY_CATALOG-->`; `PromptCatalog` ersetzt ihn beim Konstruieren durch `ActivityCatalogPromptRenderer.Render()` aus `ActivityCatalog.All` + `NodePilot.Core.Activities.ActivityConfigReference` (`src/NodePilot.Core/Activities/Embedded/activity-config-reference.json`, `schemaVersion: 2` — Purpose, Config-Keys, `promptNotes`). **Es gibt keine Prompt-Ausschlussliste mehr** — jede Activity im Katalog ist dem Modell bekannt. Vorher war `llmQuery` bewusst ausgesperrt, worauf die Generierung für KI-Aufrufe einen handgebauten `restApi`-POST erzeugte. Dieselbe JSON speist die MCP-Resource `nodepilot://activity-config-reference` und `get_activity_config_reference` — sie liegt deshalb in `Core` (Dep-Graph: `Ai -> Core`, `Mcp -> Core`). Custom Activities (`custom:<key>`) hängen Generierung und Chat pro Request an (`RenderCustomActivities`, nur enabled, Freitext geflattet, gecappt auf 40 Einträge / ~10 k Zeichen). Guards: `PromptCatalogDriftTest` (jeder Typ + jeder required Key im gerenderten Prompt) und `ActivityConfigReferenceTests` (jeder dokumentierte Key wird vom Executor wirklich gelesen).

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
  - **Tool-Calling** (opt-in `Llm:Profiles:<id>:EnableToolCalling`, am aktiven Profil): ist es an, läuft `WorkflowAssistantService.StreamChatAsync` eine OpenAI-Function-Calling-Schleife (`tool_choice:auto`, nur wenn es hilft). Das Modell darf read-only Tools auf der **secret-redigierten** Definition callen — `analyze_workflow` (deterministische Static-Analysis: fehlender Trigger, unreachable/orphan Steps, Zyklen, Remote-Step ohne Target-Machine, Strukturfehler — gleiche Codes wie der Canvas-Linter, via `WorkflowReviewAnalyzer` in `NodePilot.Core`) und `list_activity_types` (Activity-Katalog); Registry: `ChatToolRegistry`. Dazu drei **Execution-Log-Tools** — `list_recent_executions` (jüngste Läufe des geöffneten Workflows), `get_execution_steps` (Step-Details inkl. Output/ErrorOutput) und `get_failure_context` (One-Call: jüngster Failed-Run + Failed-Steps) — gespeist über `IExecutionLogReader` (Core-Interface) / `ExecutionLogReader` (Data, redigiert **immer** via `IAuditDetailsRedactor`, unabhängig vom Caller-Privileg — Outputs gehen ans externe LLM), Truncation in der Registry (1500/500 Zeichen, 2000 im Failure-Context, 100 Steps). **RBAC-Gate im Controller:** die WorkflowId ist client-kontrolliert; `AiChatController.Chat` prüft vor dem Stream Folder-Read (`IResourceAuthorizationService`) und reicht das Verdikt als `allowExecutionTools` an `StreamChatAsync` — kein Zugriff/unbekannt/ungespeichert → Reader wandert nicht in den `ChatToolContext`, die Execution-Tools werden nicht angeboten (`GetTools(context)` filtert) und ihre Handler antworten defensiv mit Error-JSON. Der Ownership-Check (`executionId` gehört zum autorisierten Workflow) lebt im Reader. Ergebnisse fließen als Tool-Messages zurück, dann produziert das Modell die finale Antwort/den Proposal. Gecappt durch `ToolCallMaxDepth` des aktiven Profils (Default 6, gültig 1–10): max LLM-Runden mit Tool-Calls pro Turn — in der **letzten erlaubten Runde** sendet der Server **keine** `tools` (erzwingt Text-Antwort; vermeidet den `tool_choice:none`-Literal, den manche lokalen Endpoints mit HTTP 400 ablehnen). SSE-Stream erhält `tool_call`/`tool_result`-Events; das UI zeigt eine „🔧 analyze_workflow — running…/checked"-Anzeige. Braucht ein Modell, das Function-Calling zuverlässig kann (viele kleine lokale Modelle nicht); aus → Chat verhält sich exakt wie vorher (keine `tools` gesendet).
  - **`POST /api/ai/chat/applied`** (Admin/Operator, Folder-RBAC Edit) — schreibt Audit `AI_PROPOSAL_APPLIED` (mit Node-/Edge-Counts), wenn ein KI-Vorschlag auf den Canvas übernommen wird. **`GET /api/ai/chat/activity/{workflowId}`** (Admin/Operator, Folder-RBAC Read) — die KI-Audit-Einträge (`AI_WORKFLOW_EXPLAINED`/`AI_PROPOSAL_APPLIED`) eines Workflows, neueste zuerst; bewusst getrennt vom Admin-only `/api/audit`, damit Operatoren ihre eigene KI-Aktivität ohne globalen Audit-Zugriff sehen.
  - **Rechter Panel-Slot**: Chat und `EditorRightPanel` (Node-/Edge-Properties, BulkEdit) belegen dieselbe Fläche — der geöffnete Chat überlagert sie. Jede **Einzel**-Selektion gibt den Slot zurück: ein `useEffect` auf `selected` in `WorkflowEditorPage` schließt den Chat (deckt Canvas-Klick, Marquee mit einem Treffer, Drop, Suche/`jumpToNode`, Tastatur-Navigation, Kontextmenü ab), zusätzlich schließt `onNodeClick` den Fall „Klick auf den bereits selektierten Node" (ReactFlow feuert dabei kein `onSelectionChange`). **Mehrfachauswahl** hält den Chat offen (`selected` ist dann `null`, und `onNodeClick` ignoriert Shift/Ctrl/Meta-Klicks) — sie ist der „Auswahl (N)"-Kontext des Chats.
  - **Chat-UX (PR3)**: benannte Threads je Workflow (wechseln/umbenennen/löschen/neuer Chat), reload-persistenter Verlauf in localStorage (privacy-aware: strippt Canvas-Snapshots + Proposal-Definition-JSON, nie für ungespeicherte Workflows, gecappt auf ~200 Messages/Thread, Logout leert), Markdown-Export eines Threads und eine in-panel workflow-scoped „AI-Aktivität"-Ansicht. (Slash-Commands wurden bewusst **nicht** gebaut.)

**Transport**: OpenAI-kompatible HTTP-API über raw `HttpClient` — läuft gegen OpenAI Cloud, Ollama, LM Studio, vLLM, LocalAI, llama.cpp. Lokale Endpoints bevorzugt. **Zwei Wire-Dialekte**, ohne Config-Key aus dem `BaseUrl`-Pfad abgeleitet (`LlmEndpointGuard.ResolveEndpoint` → `LlmEndpointTarget`): Pfad endet auf `/responses` → [OpenAiResponsesLlmClient.cs](src/NodePilot.Ai/OpenAiResponsesLlmClient.cs), sonst [OpenAiCompatibleLlmClient.cs](src/NodePilot.Ai/OpenAiCompatibleLlmClient.cs); endet der Pfad bereits auf `/chat/completions`, wird nichts mehr angehängt. Beide teilen [LlmHttpTransport.cs](src/NodePilot.Ai/LlmHttpTransport.cs) (Send/Auth/Timeout/Fehler-Mapping/16-MiB-Cap/SSE-Framing); die vier Kompatibilitäts-Fallbacks (`max_tokens`, `stream_options`, `response_format`, `strict`) sind Chat-Completions-only und im Responses-Client bewusst nicht vorhanden. Der Responses-Client sendet immer `store: false` (die API defaultet auf 30 Tage Retention, Chat Completions speichert nichts). Konfigurations-Keys + Dialekt-Tabelle + Modell-Empfehlungen siehe [docs/ai-features.md](docs/ai-features.md). Für Chat-Edits an großen Workflows ggf. `MaxTokens` des aktiven Profils erhöhen.

**Auth/Rate-Limit**: generate-Endpoints `[Authorize(Roles = "Admin,Operator")]`, chat `[Authorize]` (alle Rollen); `[EnableRateLimiting("ai-generate")]` (20/min/IP, hardcoded in [RateLimitingSetup.cs](src/NodePilot.Api/Hosting/RateLimitingSetup.cs)) sitzt auf allen drei AI-Controllern — `AiController`, `AiChatController` **und** `AiKnowledgeController` —, gilt also auch für `/api/ai/knowledge/ask`.

**DB-Timeout → 503 `DATABASE_TIMEOUT`**: Ein Command-Timeout ist ein transienter Lastzustand, kein Bug — `DatabaseTimeoutExceptionHandler` liefert 503 + `Retry-After` statt eines anonymen 500 (Erkennung provider-agnostisch über `DbErrorClassifier.IsCommandTimeout`: SQL Server `-2`, Postgres `57014`, `TimeoutException`; die Kette wird über `InnerException` abgelaufen, weil EF und seine Retry-Strategie doppelt wrappen). Die interaktive Workflow-Liste setzt zusätzlich ein eigenes 15-s-Command-Timeout statt der 120 s aus `Database:CommandTimeoutSeconds` — EF behandelt Timeouts als transient und wiederholt sie, aus einem langsamen Query wurden sonst bis zu sechs volle Timeouts hintereinander. Frontend-Seite: der globale `QueryCache.onError` toastet jeden fehlgeschlagenen Query (opt-out per `meta.silentError`), weil eine Seite, die nur `data`/`isLoading` liest, einen Fehler sonst als leere Liste rendert. Siehe ADR 0007, Amendment 2026-08-03.

**Disabled-Antwort**: Wenn `Llm:Enabled=false` → 503 mit `code: LLM_DISABLED`; wenn an, aber `Llm:ActiveProfileId` kein vorhandenes Profil benennt → 503 `LLM_NO_ACTIVE_PROFILE` (`LlmAvailability` in [LlmAvailability.cs](src/NodePilot.Api/Configuration/LlmAvailability.cs)). Andere Fehlerklassen siehe `LlmErrorKind` in [LlmException.cs](src/NodePilot.Ai/LlmException.cs).

**Profile (`Llm:Profiles:<id>`)**: Verbindungen liegen als benannte Profile vor, gekeyt nach unveränderlicher Id; `Llm:ActiveProfileId` wählt das eine aktive. Global bleiben nur `Enabled` + `ActiveProfileId`, alles Verbindungsförmige (inkl. `EnableToolCalling`/`ToolCallMaxDepth` — Modell-Eigenschaft) sitzt im Profil (`LlmProfileOptions`).
- **Objekt-statt-Array** ist der tragende Entscheid: `WriteSecretField` löst `__unchanged__` per **Id** gegen `previousSection` auf, also überlebt der API-Key Rename *und* Reorder. Env-Overrides bleiben lesbar (`Llm__Profiles__openai__ApiKey`), und `StripEnvLockedKeys` rekursiert in `JsonObject`, aber nicht in `JsonArray`.
- **Löschsemantik**: Die Runtime-Override-Datei ist nur ein weiterer Provider *über* der Basis-Config, der Merge ist additiv — ein in `appsettings.json`/Env definiertes Profil käme nach dem Reload zurück. `EffectiveSourceDetector.DetectNonRuntimeSource` beantwortet „gehört das Profil der UI?"; das Read-DTO trägt das Ergebnis als `LlmProfileSettingsDto.ManagedBy`, ein Delete eines fremden Profils wird mit **400 `LLM_PROFILE_NOT_DELETABLE`** abgelehnt (Adapter-Hook `ISettingsSectionAdapter.CheckWriteAllowed`). Deshalb shippen `appsettings.json` + Deploy-Templates `"Profiles": {}`.
- **Lazy-Resolve**: Es gibt **keine** scoped `ILlmClient`-Registrierung mehr — `Create()` wirft ohne aktives Profil, und eine Container-Registrierung würde beim *Controller-Bau* auflösen, also vor dem Action-Gate (503 würde zu 500). Alle vier Services (`ScriptGeneration`/`WorkflowGeneration`/`WorkflowAssistant`/`KnowledgeAssistant`) nehmen `ILlmClientFactory` und rufen `Create()` im Call.
- **Nested-DTO-Validierung ist Handarbeit**: `Validator.TryValidateObject` rekursiert nicht in Collection-Elemente, `LlmSettingsDto.Validate` validiert daher jedes Profil explizit und meldet `Profiles[i].Feld`.
- **Dynamische ConfigKeys**: Die Llm-Adapter-Keys hängen von den Profil-Ids ab → `DelegateSettingsSectionAdapter` hat dafür einen `Func<IReadOnlyList<string>>`-Overload.

**Hardening**:
- SSRF-Block für Cloud-Metadata-IPs in **jeder** `Llm:Profiles:<id>:BaseUrl` (nicht nur der aktiven — Profilwechsel ist ein restart-freier Save). Eine geteilte Regel für Boot *und* Save-Simulation: [LlmProfileValidation.cs](src/NodePilot.Ai/LlmProfileValidation.cs), aufgerufen von `AddNodePilotAi` und `LlmConfigBootValidator`. Einziger BaseUrl-Validierungspunkt bleibt [LlmEndpointGuard.cs](src/NodePilot.Ai/LlmEndpointGuard.cs) (`NormalizeAndValidateBaseUrl`/`IsCloudMetadataEndpoint`), plus Connect-Zeit-Guard `LlmConnectGuard` in [LlmServiceCollectionExtensions.cs](src/NodePilot.Ai/LlmServiceCollectionExtensions.cs). `Enabled=true` ohne auflösbares Profil ist bewusst nur eine **Warning** — KI ist opt-in und darf den Boot nicht blockieren.
- Fresh `SocketsHttpHandler` mit `UseProxy=false` (NICHT der `RestApiHttpClientProvider` — der hat SSRF-Guards die `127.0.0.1:11434` blocken würden)
- Klartext-ApiKey je Profil löst Startup-Hardening-Warning aus, analog `Smtp:Password` ([SecurityHardeningWarnings.cs](src/NodePilot.Api/Hosting/SecurityHardeningWarnings.cs))
- `SettingsSchema.IsUnchangedSecretValue` behandelt `__unchanged__` **und** die Anzeige-Maske `"********"` als „unverändert" — vorher hätte ein Client, der die GET-Antwort zurück-PUTet, die Maske als neuen Key verschlüsselt und den echten still zerstört (gilt jetzt für alle Sektionen, auch `Smtp:Password`).

**Prompt-Injection-Residualrisiko**: Upstream-Variablen werden nur als Schema an den LLM gesendet — **niemals deren Werte**. Im System-Prompt als „untrusted JSON, not instructions" markiert. **Mitigation**: KI-generiertes Script wird am Cursor eingefügt (nicht stumm ersetzt), User muss aktiv reviewen.

**Drift-Schutz**: [PromptCatalogDriftTest.cs](tests/NodePilot.Engine.Tests/Ai/PromptCatalogDriftTest.cs) scannt `IActivityExecutor`-Implementations und assert-iert dass jede in `workflow-system.md` erwähnt ist (oder explizit in der Allowlist steht). Wer eine neue Activity mergt, muss den Prompt-Katalog ergänzen.

---

## CLI (`np`) — Befehlsbereiche & Details

Operations-CLI für Operatoren — eigenes Projekt unter [src/NodePilot.Cli/](src/NodePilot.Cli/), `AssemblyName=np`. Ausgeliefert per `dotnet publish` + `PATH`-Eintrag, **kein** `dotnet global tool`: `PackAsTool` verträgt kein Platform-TFM, und das Projekt hängt transitiv an `net10.0-windows` (NETSDK1146). **Reiner HTTP-Client gegen die bestehenden REST-Endpoints** — keine eigene API-Surface, keine DB-Zugriffe.

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
  - **Jeder** Login-Fehlschlag ist auditiert — der `reason` im Details-JSON ist das einzige Mittel, die im Browser bewusst identische Meldung „Invalid credentials" aufzuschlüsseln: `local_login_policy` (Modus verbietet lokalen Login), `bootstrap_token_invalid`, `ldap_invalid_credentials` (Bind abgelehnt), `ldap_user_object_not_found` (Bind OK, aber kein Objekt mit passendem `userPrincipalName` — meist ein leeres UPN-Attribut), `no_allowed_directory_group` (eigener Code `USER_DIRECTORY_ACCESS_REFUSED`), `pre_jit_account_throttle` (als `LOGIN_LOCKED`), `infrastructure_failure` (zusätzlich HTTP 503)
- `USER_CREATED|USER_ROLE_CHANGED|USER_ACTIVATED|USER_DEACTIVATED|USER_PASSWORD_RESET|USER_DELETED`
- `CREDENTIAL_DECRYPTED|CREDENTIAL_DECRYPT_FAILED` (pro Decryption-Versuch, nicht pro Run; Fehlerdetails enthalten nur Provider und Fehlerklasse)
- `USER_LDAP_JIT_CREATED|JIT_UPDATED|LINKED|REFUSED_COLLISION|REFUSED_BOOTSTRAP` + `USER_WINDOWS_*` (gleiche Suffixe; Code ist `USER_{providerTag}_…` mit `providerTag` = LDAP|WINDOWS)
- `EXECUTION_STARTED|EXECUTION_CANCELLED|EXECUTION_RETRIED|EXECUTION_RESUMED|EXECUTION_STEP_OVER|EXECUTION_DEBUG_STOP|EXECUTION_RECOVERED_FAILOVER`
- `WEBHOOK_TRIGGERED` | `EXTERNAL_TRIGGER_FIRED` (nur erfolgreiche Fires)
- `TRIGGER_FIRE_SUPPRESSED`
- `WORKFLOW_IMPORTED_SCORCH` | `WORKFLOW_EXPORTED` | `WORKFLOW_EXPORTED_BULK` | `WORKFLOW_IMPORTED` | `CUSTOM_ACTIVITY_EXPORTED`
- `AI_SCRIPT_GENERATED|AI_WORKFLOW_GENERATED|AI_WORKFLOW_EXPLAINED|AI_PROPOSAL_APPLIED` (Chat-Assistent; Details: nur Counts model/durationMs/modifyProposed/nodeCount/turnCount bzw. Node-/Edge-Counts bei Applied — kein Prompt-/JSON-Text)
- `AI_KNOWLEDGE_ASKED` (globaler Wissens-Chat `/ai-chat`; Details: model/durationMs/toolCalls/turnCount/cancelled, die vier Quellen-Flags und bei text2sql `dbQueryCount` + stabile `dbQueryFingerprints` — **kein** Prompt- und kein SQL-Text)
- `DBADMIN_ROWS_VIEWED` | `DBADMIN_ROW_UPDATED` | `DBADMIN_ROW_DELETED`
- `DBADMIN_SQL_EXECUTED` | `DBADMIN_SQL_WRITE_ATTEMPTED` | `DBADMIN_SQL_WRITE` (Admin SQL-Konsole; Preview, SHA-256, Byte- und Statement-Anzahl in Details; direkter Zugriff auf `AuditLog` ist blockiert)
- `SECRETS_REENCRYPTED` (Passphrase-Rewrap aller Secrets)
- `FOLDER_CREATED|FOLDER_UPDATED|FOLDER_MOVED|FOLDER_DELETED` | `WORKFLOW_MOVED` (Shared-Folders / RBAC Stufe A)
- `FOLDER_PERMISSION_UPDATED|FOLDER_PERMISSION_REVOKED` (Per-Folder-Grants)
- `MAINTENANCE_WINDOW_CREATED|UPDATED|DELETED|OVERRIDDEN` | `EXECUTION_BLOCKED_MAINTENANCE_WINDOW`
- `ALERT_RULE_CREATED|UPDATED|DELETED|ENABLED|DISABLED|TEST_FIRED` (Alerting / Notification-Rules — siehe `docs/alerting.md`)
- `SYSTEM_ALERT_POLICY_CREATED|UPDATED|DELETED|ENABLED|DISABLED|TEST_FIRED` (System-Alert-Policies, ADR 0008)
- `BACKUP_EXPORTED|BACKUP_RESTORED` (System-Configuration Backup, ADR 0001)
- `AUDIT_LOG_EXPORTED` | `SUPPORT_EVENTS_EXPORTED` | `SUPPORT_LOG_DOWNLOADED` (sensible Diagnose-/Compliance-Exporte)
- `CLUSTER_LEADERSHIP_ACQUIRED` (HA-Lease mit Node-ID und Fencing-Epoch)
- `SETTINGS_{SMTP|LLM|RETENTION|AUTHENTICATION|LOGGING|OPENTELEMETRY|STATS|DBADMIN}_UPDATED` (Admin-Settings, ein Code pro Section)

**Audit-Write-Pipeline (Phase 3):** Jeder Audit-Write läuft durch `IAuditStager` (in [NodePilot.Core/Audit/](src/NodePilot.Core/Audit/)), inkl. der drei ehemals direkten Bypass-Pfade. HTTP-Controller nutzen `IAuditWriter` (in [NodePilot.Api/Audit/](src/NodePilot.Api/Audit/AuditWriter.cs)); der wrappt den Stager mit `HttpContextAccessor`-Actor-Resolution + ECS-Log-Forward + Support-Log-Whitelist-Check. Redaction + 4 KiB-Cap gelten überall einheitlich.

Audit-Fehler brechen normale Mutationen nicht ab. Die einzige bewusst fail-closed ausgelegte Ausnahme ist der beliebige DB-Admin-SQL-Schreibmodus: Er benötigt vor der Ausführung einen persistierten `DBADMIN_SQL_WRITE_ATTEMPTED`-Eintrag; ohne verfügbares Audit wird das SQL nicht ausgeführt.

**Archive-Integrität:** `AuditLogRetentionService.ArchiveAsync` schreibt gzip-komprimierte `audit-{date}-{ticks}-{rand}.ndjson.gz` plus SHA-256-Sidecar. Periodische Verify-Pass (default daily) rechnet Hashes neu und alerted via Metric `nodepilot.audit_archive.hash_drift` bei Drift.

---

## Production Deployment — Referenz

Vollständige Operator-Doku: [deploy/README.md](deploy/README.md). Zweites Shipping-Ziel ist die
**Desktop-App** — siehe [Desktop-Deployment — Referenz](#desktop-deployment--referenz) weiter unten.

### Ziel-Topologie

- **Windows Service** unter einem **gMSA** (`DOMAIN\svc-nodepilot$`, `sc.exe create` mit leerem Passwort — `New-Service` kann gMSA nicht) **oder** `-UseLocalSystem` (Netzwerk-Identität = Computer-Konto `DOMAIN\<host>$`; einfachster Einzelserver-Pfad), Delayed-Auto-Start, Recovery-Actions
- **Signiertes Artefakt**: `Build-Artifact.ps1 -SigningCertificateThumbprint` erzeugt ZIP + Manifest + detached CMS (`.p7s`); `Install-NodePilot.ps1 -TrustedArtifactSignerThumbprint` verifiziert Pin **und Kette** (`ArtifactSecurity.ps1`) — der Signer (bzw. sein Self-Signed-Cert) muss auf dem Ziel in `LocalMachine\Root`
- **Pre-Flight liegt in `deploy/Preflight.ps1`**, nicht im Installer: `Invoke-NodePilotPreflight` sammelt Ergebnisobjekte (`Pass|Fail|Warn|Skipped` + Remediation-Snippet), `Assert-NodePilotPreflight` bricht erst danach ab. Damit ist dieselbe Prüflogik hinter einem „Erneut prüfen"-Button wiederverwendbar — **und genau deshalb darf dort nichts mutieren**. `Enable-SqlReadCommittedSnapshot` (`ALTER DATABASE … WITH ROLLBACK IMMEDIATE`) bleibt installer-seitig und läuft nach bestandenem Pre-Flight. `Test-DeploymentTemplates.ps1` erzwingt die Regel über den geparsten **AST** statt per Regex, weil die Datei Remediation-Kommandos legitim als Anzeigetext enthält
- **GUI-Setup** (`deploy/server/`, Inno Setup 6, ~52 MB): zweiter Weg zur *selben* Server-Installation, ruft `Install-NodePilot.ps1` unverändert auf. Pascal-Layer bleibt dünn (Seiten + Payload); der Wizard schreibt eine ACL-geschützte **Answer-File** und ruft `Invoke-NodePilotSetup.ps1` (`InitSession`/`Probe`/`Provision`/`Apply`/`Cleanup`). Grund für die Datei statt einer Kommandozeile: `-PostgresPassword` ist `[SecureString]` und kann über `powershell.exe -File` **gar nicht** übergeben werden — nebenbei fällt `/SILENT /ANSWERFILE=` für SCCM ab. Vertrag + Splat-Abbildung in `SetupContract.ps1`, verhaltensgetestet durch `Test-SetupAdapter.ps1`. Ergebnisse als **INI** (Inno hat `GetIniString`, aber kein JSON). Volle Doku inkl. der sieben gemessenen Inno-Fallen: `deploy/server/README.md`
- **Deinstallation entfernt nie die Datenbank**, auch nicht optional — der Installer legt sie nicht an. Einzige Frage ist das Datenverzeichnis (`-PurgeData` / `/PURGEDATA=1`), Default überall **behalten**. `-PurgeData` nimmt vorher per `takeown`+`icacls` die Besitzrechte, weil `jwt-secret.key` owner-only auf das Dienstkonto ACL-t ist; der `icacls`-Grant trägt **keine** `(OI)(CI)`-Flags (auf Blattdateien stillschweigend verworfen, `icacls` meldet trotzdem Erfolg)
- **Installations-Marker** `HKLM\SOFTWARE\NodePilot\Server` (`InstallPath`, `DataPath`, `ServiceName`, `Version`, `DbProvider`, `HttpsPort`) wird auf dem Erfolgspfad geschrieben und vom Uninstaller entfernt — vorher lag diese Information nur in `install-report.txt` **innerhalb** `DataPath`, also erst lesbar, wenn man `DataPath` schon kennt
- **Kestrel bindet HTTPS direkt** auf den Ports aus `Kestrel:Https:HttpsPort|HttpPort`, Cert per Thumbprint aus `LocalMachine\My` — **kein IIS / Reverse Proxy**. SPA + API liegen zwingend auf **einer Origin**
- **Externes SQL Server 2022 ab CU1** (Trusted Connection; Build ≥ `16.0.4003.1` — Runtime verbindet mit `Encrypt=Strict`/TDS 8.0, 2022 RTM korrumpiert TDS-8.0-RPC-Streams mit Error 8005, Installer-Preflight prüft das) oder **PostgreSQL 16+** (user/password). gMSA-Login bzw. Postgres-Role braucht DDL-Rechte.
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
| `Database:AllowInsecureTls` | Escape-Hatch der strikten DB-TLS-Prüfung (`DatabaseTlsBootValidator`). Wirkt **nur** bei Loopback-DB-Host **und** (Development-Env **oder** `Deployment:Mode=Desktop`) — auf einem Produktions-Server bricht der Boot trotz `true` ab. | `false` |
| `Security:AdminSetupTokenPath` | Absoluter Pfad für `admin-setup.token` | `{ContentRoot}/admin-setup.token` |
| `Logging:File:Path` | Absoluter Pfad für Serilog-Rolling-File | `{ContentRoot}/logs/nodepilot-.log` |
| `Kestrel:Https:*` | Kestrel-direct-HTTPS aus Windows Cert Store | No-op → Default-Binding |

`AdminBootstrap.Validate/Consume/EnsureBootstrapTokenIfNeeded` akzeptieren optionalen `IConfiguration`-Parameter (Default `null`).

### `Credentials:DpapiScope` für Production

Installer-Template setzt `LocalMachine` ([appsettings.Production.json.template](deploy/templates/appsettings.Production.json.template)). `CurrentUser` bricht bei Service-Account-Wechsel. Siehe Warnung in [CredentialStore.cs](src/NodePilot.Data/CredentialStore.cs) (~Zeile 99).

### Dienst-Startverhalten (Boot)

Der Dienst steht auf **`start= auto`** (nicht `delayed-auto`) und wartet die Datenbank selbst ab.

- **`DatabaseReadinessGate` läuft in beiden Deployment-Modi**, direkt vor `MigrationBootstrapper`. Gewartet wird **nur** auf Erreichbarkeit (`CanConnectAsync`); ein Schema-/Migrationsfehler ist deterministisch und wird nie wiederholt, sondern schlägt sofort durch.
- **`Database:StartupWaitSeconds`** (default 120, boot-fixed) steuert die Obergrenze. `0` oder negativ = einmal prüfen, dann weiter (dokumentierter Opt-out); Werte über **10 Minuten** werden gekappt — sonst hängt ein `86400`-Tippfehler den Dienststart wortlos einen Tag lang. Unlesbare Werte fallen auf 120 zurück.
- **`depend= Netlogon` nur auf dem gMSA-Pfad.** Ein gMSA-Logon holt sein Passwort beim DC, bevor der Prozess existiert — kein In-Process-Warten kann das abfangen, der Fehlschlag ist Event 7000. LocalSystem braucht die Abhängigkeit nicht: es meldet sich immer an, und seine DB-Verbindung (Computerkonto-Kerberos) deckt das Gate ab.
- **Warum nicht mehr `delayed-auto`:** die Verzögerung war der Ersatz für ein Warten, das es auf dem Server-Pfad nicht gab, und war an beiden Enden falsch. Gemessen auf CM1: Boot 11:37:45, SQL Server bereit 11:37:53, Dienststart 11:39:48 — 115 Sekunden Leerlauf, die für den Operator wie ein kaputter Dienst aussehen. Umgekehrt startete er bei einer Datenbank, die länger als die feste Frist braucht, weiterhin zu früh; einzige Rettung war die Absturz-Neustart-Schleife der SCM-Recovery-Aktionen.
- **Bekannte Kante:** existiert die Zieldatenbank noch gar nicht (EF würde sie per `Migrate()` anlegen), meldet `CanConnectAsync` „nicht erreichbar" und der Boot wartet die volle Frist ab, bevor er sie anlegt. Auf dem Server-Pfad tritt das nicht auf — der Preflight prüft `Server/Database` und lässt eine Installation gegen eine fehlende Datenbank nicht zu.

### Stolperfallen (aus dem ersten Lab-Rollout gelernt)

- **PS 5.1-Kompatibilität**: `RandomNumberGenerator.Fill()` ist .NET-Core-only — stattdessen `RNGCryptoServiceProvider.GetBytes()`. Deploy-Skripte müssen auf PS 5.1 **und** PS 7 laufen
- **Em-Dashes (`—`) in PS-Scripts**: bricht PS 5.1 Parsing wenn Datei ohne BOM gespeichert. In Deploy-Skripten nur ASCII-Punctuation verwenden
- **`Set-StrictMode -Version Latest`** + `& npm ...`-Shim triggert `PropertyNotFoundStrict` auf `.Statement`. In Deploy-Skripten `Version 3.0` verwenden
- **`New-Service` unterstützt keine gMSA** (verlangt Passwort). `sc.exe create ... obj= DOMAIN\acct$ password= ""` ist der Workaround
- **`$PSHOME\Modules` muss auch im Server-Artefakt gestaged werden**: `dotnet publish` legt SMA.dll in die Wurzel, die Core-Module aber unter `runtimes\win\lib\<tfm>\Modules` → ohne Kopie nach `<stage>\Modules` scheitert **jede** runScript-Activity mit „The term 'Write-Output' is not recognized" (implizite WinPS-Compat, die das früher maskierte, ist seit PR #87 bewusst aus). `Build-Artifact.ps1` staged seit 2026-08-01 wie der Desktop-Build
- **CSP vs. Code-Editoren**: CodeMirror 6 (style-mod) und Monaco injizieren Laufzeit-`<style>`-Elemente → `style-src` braucht `'unsafe-inline'` (M-3 für Styles teilrevertiert, `script-src 'self'` bleibt strikt; Monaco hat keine Nonce-API). Guard: `SecurityPipelineSetupTests` — Dev und E2E fahren ohne diese Middleware, ein Direktiven-Regress fiele sonst erst auf dem Server auf
- **`TokenValidityMiddleware` rejected nur auf `/api` + `/hubs`**: der SPA-Fallback-Endpoint trägt kein `[AllowAnonymous]`, ein abgelaufenes `np_auth` machte damit die gesamte SPA **inklusive `/login`** unerreichbar (rohes 401-JSON statt Seite). Außerhalb dieser beiden Präfixe wird die ungültige Identität gestrippt statt abgelehnt
- **Update-Skript-Semantik** (aus dem Lab-Rollout): Prozess-Guard bricht **vor** dem ersten Delete ab, wenn noch etwas aus dem InstallPath läuft (gestoppter Dienst ≠ freie DLLs — gemappte Images liefern „Access denied"). **Der SCM meldet `SERVICE_STOPPED` vor dem Prozessende**, deshalb wartet `ServiceControl.ps1` (geteilt mit dem Installer) erst 30 s und beendet dann Verbliebene per `-Force`; ohne das Warten scheiterte der Lauf an genau dem Prozess, den er selbst gestoppt hatte (Lab 2026-08-03, Exit 4). Die Reihenfolge „warten **vor** `sc.exe delete`" ist bindend — umgekehrt verwaist ein lebender Prozess, den danach nichts mehr über den SCM adressieren kann; `appsettings.Production.json` fällt beim Wipe **zuletzt** (steht bewusst nicht im Backup); die Health-Probe leitet ihren Port aus `Kestrel:Https:HttpsPort` der installierten Config ab — der 443-Parameterdefault rollte sonst ein gesundes 8443-Upgrade zurück. **Ein erfolgreicher Update lässt den Dienst LAUFEN**, unabhängig von seinem Zustand vor dem Lauf — ein Rollback stellt den Ausgangszustand wieder her (startet nichts, was vorher bewusst gestoppt war). **Einzige Dienstkonfiguration, die ein Update anfasst:** `start= auto` (Normalisierung von Altbeständen auf `delayed-auto`, sonst erreichte der Fix nur Neuinstallationen). Identität, `depend=` und Recovery-Aktionen bleiben Installer-Sache — per Contract

---

## Desktop-Deployment — Referenz

Operator-Doku: [deploy/desktop/README.md](deploy/desktop/README.md). Nutzerseitiger Vergleich aller
drei Betriebsarten: docs-ui `deployment/overview.md`.

Zweites Shipping-Ziel: maschinenweiter, **offline** Win-11-x64-`.exe`-Installer (Inno Setup) mit
gebündeltem PostgreSQL 16 (nur `bin`/`lib`/`share`) + self-contained .NET, beides als
Boot-Start-Dienste (`NodePilot` = LocalSystem, `NodePilotDb` = NetworkService, `depend=`), plus
Electron als dünner Viewer.

**Posture `Deployment:Mode`** (`Server` default | `Desktop`, [DeploymentMode.cs](src/NodePilot.Api/Configuration/DeploymentMode.cs); unbekannter Wert = Boot-Error). Desktop relaxiert **nur** drei Dinge:
- `DatabaseTlsBootValidator`: `AllowInsecureTls` wird bei **Loopback-DB** zur Warning statt Error
- `KestrelHttpsConfigurator`: `ListenLocalhost` statt `ListenAnyIP` (`LoopbackOnly`, nicht abschaltbar)
- `DatabaseReadinessGate`: Warten auf DB-Erreichbarkeit vor dem Migration-Bootstrap (nur Erreichbarkeit, keine Migrationsfehler) — läuft in **beiden** Deployment-Modi, siehe „Dienst-Startverhalten"

**Konsequenzen (nicht offensichtlich):** Der Loopback-Bind trifft den **kompletten Listener** — SPA,
`/api/*`, `/hubs/*`, `/healthz`, `/api/webhooks/*`. Es ist **nicht** so, dass einzelne Routen gesperrt
wären und der Rest der API aus dem Netz erreichbar bliebe (häufiges Missverständnis). Daraus:
**eingehende Webhooks und die externe Trigger-API unbrauchbar** (letztere zusätzlich per leerem
`ExternalTrigger:ApiKey` aus), kein Team-Zugriff; nur lokales Login (`LocalLoginMode=Enabled`);
lokale `runScript` laufen als **SYSTEM**; Remote-WinRM braucht hinterlegte Credentials;
**HA unmöglich** (Cluster+DPAPI = Boot-Error, kein `Jwt:Key`).

**Was unverändert bleibt:** alle nicht-eingehenden Trigger (`schedule`/`fileWatcher`/`database`/
`eventLog`/`manual`) und jede ausgehende Automatisierung (WinRM, `restApi`, `sql`, SMTP,
Alerting-Webhooks). Merksatz: *ausgehend alles, eingehend nichts.*

**Windows-/PS-5.1-Stolperfallen im Provisionierer** (alle real aufgetreten):
- `RandomNumberGenerator.Fill` und `GetCertHashString(HashAlgorithmName)` sind .NET-Core-/4.8-APIs — unter PS 5.1 nicht vorhanden
- **PostgreSQL re-execed unter Restricted-Token** (droppt Administrators) → `pgdata`/pwfile brauchen den **User-SID**
- `sc.exe create binPath=` bricht bei Leerzeichen-Pfad (`C:\Program Files\…`) **still** ab → `New-Service`
- `admin-setup.token` ist owner-only (SYSTEM): elevierter Admin darf weder lesen noch DACL ändern → einfachster Weg ohne ACL-Änderung: `robocopy <DataPath> $env:TEMP admin-setup.token /B` (Backup-Semantik). Alternativ `takeown /a` + `icacls`-Lesegrant an die **Administrators-Gruppe** (bleibt im trusted-Set von `RestrictedFileWriter`; Ownership auf den persönlichen Admin-User dagegen invalidiert die Datei)
- `Invoke-WebRequest` scheitert unter PS 5.1 an Kestrels Loopback-TLS trotz gesunder API → `curl.exe`-Fallback
- **`$PSHOME\Modules` muss mitgeliefert werden**, sonst schlägt jedes `runScript` fehl (Modul-Staging im Build)
- Deploy-Skripte **ASCII-only** halten (UTF-8-no-BOM wird als ANSI gelesen)

**Icons (skin-folgend):** [scripts/generate-desktop-icons.ps1](scripts/generate-desktop-icons.ps1)
rendert `src/nodepilot-desktop/assets/` aus den Brand-Assets der SPA — Default-Set (`icon.ico`
16/32/48/256, `icon.png`, `tray.png`) **blau** aus `appicon-dark.png` plus `skins/<id>.png` +
`<id>-tray.png` je Skin. Zur Laufzeit folgt die Shell dem Skin: die SPA schreibt bei jedem Wechsel
`/appicon-<skin>.png` in `<link rel="icon">`, Chromium meldet das als `page-favicon-updated`, und
[skins.ts](src/nodepilot-desktop/src/skins.ts) mappt es zurück auf `skins/<id>.*` (Fenster- +
Tray-Icon). Bewusst **kein** Preload/IPC am Produktions-SPA-Fenster — die Shell liest ein Signal,
das der Renderer ohnehin sendet. Die Skin-Liste ist **nicht** gespiegelt: der Generator leitet sie
aus den vorhandenen `appicon-*.png` ab, die Shell aus den erzeugten Dateien (unbekannter Skin →
aktuelles Icon bleibt). Nur exe/Installer/Startmenü-Icon bleibt fix — Windows löst die aus der Datei
auf. Output ist gitignored, die Quellen sind versioniert.

**Dev-Loop:** `Sync-DesktopApp.ps1` (~1 Min) statt Installer-Rebuild; Electron-Shell via `npm start`
direkt aus dem Quellcode gegen die installierte Backend-Instanz (davor einmal `npm run icons`,
sonst ist `assets/` leer).

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
| | | *UI:* feuert bereits beim Dateiauswählen (Struktur-Vorschau), der „Vorschau"-Button ist der Re-Run nach Passphrase-Eingabe |
| `POST /api/backup/restore` | multipart `file` + `passphrase` + `policy` | wendet an |

**Export** zieht harte Dependencies automatisch mit (Workflows → Folders/Machines/Credentials) und versiegelt mit dem Whole-file-MAC. **Workflow-Secrets** liegen inline in `DefinitionJson` (`secret`/`apiKey`/`password`/`authToken`/`bearer`/`connectionString`) und werden über `WorkflowDefinitionSecretRewriter` mit `SecretHandling = Redact | EncryptForBackup | PlainInternal` behandelt — dieselbe Klasse, die der redigierte Workflow-Export nutzt. `targetMachineId`/`credentialId` sind GUID-Referenzen, kein Secret → ID-Remap beim Restore.

**Restore** (Service: [BackupRestoreService.cs](src/NodePilot.Api/Services/Backup/BackupRestoreService.cs)):
1. Passphrase via Verifier prüfen → sonst Abbruch. Whole-file-MAC prüfen → Mismatch = Abbruch (Tamper).
2. **Referenz-Validierung** (vor jedem Schreiben): jede harte Ref muss im Backup **oder** in der Ziel-DB (per `sourceId`) auflösbar sein, sonst Abbruch.
3. Eine DB-Transaktion, **gekapselt in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`** — Pflicht, weil Postgres/SQL Server eine Retrying-Strategy nutzen, die direkte `BeginTransaction` ablehnen. Reihenfolge: Users → Folder-Struktur → Credentials → Machines → Globals → Workflows → Folder-Grants. Jede Section füllt eine `sourceId→targetId`-Map; Folgereferenzen werden darüber remappt (`Machine.DefaultCredentialId`, `Folder.ParentFolderId`/`CreatedByUserId`, Workflow-Def-GUIDs, Grant-Principals). AD-Group-SIDs in Grants bleiben unverändert.
4. **Settings** danach, **außerhalb** der Transaktion (File via `RuntimeOverridesWriter`), eigene Ergebniszeile. **Replace, nicht Merge**: Top-Level-Overrides, die im Ziel existieren aber nicht im Backup, werden entfernt (`__meta` bleibt); `enc:v1:`-Werte werden re-sealed.

**Konflikt-Policy** (by-name-Match; default `skip`): `skip` / `rename` (Suffix `(Restored N)`) / `overwrite`. Format im `policy`-Feld: bare Wert (global) und/oder `section=policy`-Paare, komma-getrennt (z. B. `skip,users=overwrite`).

**Sicherungen:** Last-Admin-Schutz (Restore lässt nie 0 aktive Admins zurück → Abbruch). User-`overwrite` bumpt `SecurityStamp` + setzt `PasswordChangedAt` bei Hash-/Rollen-/Status-Änderung (invalidiert Sessions). Verwaiste Grants (Principal-User existiert nicht) werden mit Warnung übersprungen, nicht wiederbelebt.

**Audit:** `BACKUP_EXPORTED` (Counts je Sektion und akkurates `containsSecrets` — nur wahr wenn tatsächlich ein `$enc`-Feld versiegelt wurde) / `BACKUP_RESTORED` (effektive Konflikt-Policy und Ergebnis je Sektion; Passphrase nie im Log). **Rate-Limit:** Policy `backup` (10/min/IP) auf dem ganzen Controller — Export liest Secrets, Restore ist ein schwerer Bulk-Write. **Fehlerbild:** falsche Passphrase / MAC-Fehler / unresolvable Refs / Last-Admin → 409; strukturell kaputte (aber MAC-valide) Datei → 400 (nicht 500). **CLI:** `np backup manifest|export|preview|restore`; Passphrase via `--passphrase-env`/`--passphrase-file`/Prompt, nie als Flag.

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
| `RestApi:AllowedHosts` | `[]` | Exakte Host-/IP-Allow-Liste für tatsächlich proxied `restApi`-Ziele/Redirects — die Ausnahme von `BlockPrivateNetworks`. Link-Local/Cloud-Metadata bleibt immer gesperrt (Dev: `localhost`/`127.0.0.1`/`::1`) |
| `WaitForCondition:AllowedHosts` | `["localhost"]` | **Eigene** Liste für die PowerShell-Probes `portOpen`/`httpOk`. Diese können das Ziel beim Connect nicht erneut prüfen (kein `ConnectCallback`), akzeptieren deshalb nur exakt gelistete Hosts; leere Liste lehnt jede Probe ab. Bewusst getrennt von `RestApi:AllowedHosts`, damit „eigenen Dienst prüfen" nicht zugleich `restApi` zu Loopback öffnet — dessen URLs können aus Trigger-Payloads stammen. Vergleich exakt: `127.0.0.1` deckt `localhost` nicht ab (Dev: alle drei Schreibweisen) |
| `FileSystemOperation:RejectTraversal` | `true` | Lehnt `..` in File-System-Op-Paths ab (Dev: `false`) |
| `SqlActivity:RequireConnectionRef` | `true` | Nur benannte `connectionRef` statt inline `connectionString` (Dev: `false`) |
| `StartProgram:DisallowShellExecute` | `true` | Verwirft `useShellExecute=true` (Dev: `false`) |
| `Trigger:Database:RequireConnectionRef` | `true` | Nur benannte `connectionRef` für `databaseTrigger` (Dev: `false`) |
| `Security:StrictAllowedHosts` | `true` | Boot-Abbruch bei unsicherem `AllowedHosts` (z.B. `*`) (Dev: `false`) |
| `Webhook:RequireSecret` | `true` | `webhookTrigger` erzwingt ein konfiguriertes Secret — verifiziert je nach `signatureMode` als `X-Webhook-Secret`-Header oder NodePilot-HMAC-v2-Signatur (Dev: `false`) |
| `Database:AllowInsecureTls` | `false` | Relaxation: deaktiviert die strikte DB-TLS-Prüfung (`Encrypt=Strict` / `SSL Mode=VerifyFull`) — greift nur bei Loopback-Host + (Development **oder** `Deployment:Mode=Desktop`). Prod-Default fail-closed; Dev: `true` |
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
| `Stats:DurationSampleCap` | `1000` | Max. Dauer-Samples je Workflow für avg/p50/p95 (neueste zuerst) |

`GET /api/stats/dashboard` liefert daher den letzten Refresh-Stand, nicht Live-Zahlen. Mutationen am Setting schreiben `SETTINGS_STATS_UPDATED` ins AuditLog. `RefreshIntervalMinutes`/`WindowDays` sind hot-reloadable — der Refresher liest sie pro Pass aus der Live-Config (kein Neustart nötig).

**Warum der Sample-Cap:** Zähler und Zeitstempel aggregiert die DB serverseitig, Perzentile lassen sich aber nicht provider-agnostisch in LINQ ausdrücken. Die Dauer-Samples werden deshalb pro Workflow über den deckenden Index `(WorkflowId, StartedAt DESC)` geholt und hart gedeckelt — vorher materialisierte ein Pass **jeden** Erfolgslauf des Fensters im Speicher, alle 5 Minuten, für drei Kennzahlen.

**Zeitfenster, Balken-Cap und Dichte.** `GET /api/operations/graph?windowMinutes=` ist serverseitig auf `{20, 60, 240}` geklemmt (alles andere → 20) und steuert **nur** die beendeten Läufe. `running[]` ist bewusst nie gefenstert — ein seit sechs Stunden laufender Job muss in jedem Fenster sichtbar bleiben, das ist ja der Stuck-Fall.

`RecentCap = 1000` gilt für **jedes** Fenster, weil er ein **Render-Budget** ist und kein Fenster-Budget: er begrenzt, wie viele Balken die Konsole bei jedem Uhr-Tick neu positioniert und bei jedem 5-s-Poll neu empfängt — beides hängt nicht daran, wie weit zurück jemand geschaut hat. Auf einer ausgelasteten Anlage (gemessen: 982 beendete Läufe/h über 24 Workflows) deckt der Cap ~30 min ab; alles darüber hinaus mit Rohzeilen zu füllen hieße ~3.900 Zeilen alle 5 s — nicht tragbar.

Die Abdeckung des Fensters übernimmt deshalb **`density[]`**: pro Workflow gebucketete Zähler (`total`/`failed`/`cancelled`) über das **ganze** Fenster, berechnet **nur wenn** der Cap gegriffen hat (ruhige Anlage → zweite Query und Payload entfallen komplett). Feste Bucket-**Anzahl** statt fester Breite (`DensityBucketTarget = 48`, also 25 s bei 20 min, 5 min bei 4 h) — dadurch kostet ein breiteres Fenster nichts extra, egal wie viele Läufe dahinterstehen. Aggregiert wird **in-memory** über einen gedeckelten Scan (`DensityScanCap = 20.000`), bewusst nicht in SQL: portables Datums-Bucketing über Postgres, SQL Server **und** das SQLite-Test-Backend ist ein Übersetzungs-Minenfeld, ein schmaler Range-Read dagegen billig.

Fünf Meta-Felder mit fünf verschiedenen Bedeutungen: `RecentSinceUtc` = *angeforderter* linker Rand (und Anker von Bucket 0), `OldestReturnedCompletedAt` = ältester *tatsächlich gelieferter* Abschluss (die Naht zwischen Balken und Dichte), `RecentTruncated` = ob der Cap gegriffen hat, `DensityBucketSeconds` = Bucket-Breite, `DensityCapped` = ob schon das Aggregat gedeckelt wurde (die Zähler sind dann eine Untergrenze, keine Summe).

Die Buckets decken absichtlich das ganze Fenster ab und nicht nur die fehlende Strecke: die Naht fällt mitten in einen Bucket (ein dort abgeschnittener Bucket zählte sich selbst zu klein), und so ist die Bucket-Summe die ehrliche Fenster-Gesamtzahl für die Hinweiszeile. Das **Clipping** an der Naht ist eine reine Render-Entscheidung in `buildDensityCells` — rechts davon ist jeder Lauf ohnehin ein Balken. Das „keine Historie"-Band entfällt, sobald Dichte da ist: es behauptet „für diese Strecke kam nichts zurück", und die Dichte ist genau die Widerlegung. Ein Workflow **nur** mit Dichte (jeder seiner Läufe fiel hinter den Cap) bekommt in `assignLanes` trotzdem eine Lane — sonst hätte seine Historie nichts zum Zeichnen und der Workflow läse sich als untätig.

**Freeze ist ein Darstellungs-Freeze.** Eingefroren werden nur die Render-Inputs (Snapshot, `locallySettled`, Uhr). Der SignalR-Feed bleibt verbunden, `seedRunning` reconciled weiter, und Hintergrund-Invalidierungen dürfen weiterhin Requests auslösen. `useOperationsFeed()` darf **niemals** bedingt aufgerufen werden: ohne den Feed schriebe `applyStatus` keine Tombstones mehr, und ein Refetch nach dem Auftauen könnte Läufe wiederbeleben, die währenddessen terminiert sind. Der Query-Key ist `['operations-graph', windowMinutes]`; beim Fensterwechsel verliert die alte Query ihren Observer und pollt von selbst nicht weiter.

**Schritt-Aktivität statt Fortschritt.** `OpsRunningExecution` trägt `StepsFinished`, `LastCompletedStepName`, `LastProgressAt` und `ActiveStepCount` — **nullable**, und `null` heißt „nicht angereichert", nie „nichts passiert" (Cap: die 300 ältesten laufenden Läufe; `0` wäre eine Falschaussage). Bewusst **kein** Prozentsatz: jeder verfügbare Nenner ist falsch (Step-Zeilen enthalten ausgeführte Trigger und `Skipped`, `ActivityCount` enthält beides nicht, Loops führen Nodes mehrfach aus). `LastProgressAt` ignoriert `Skipped`-Zeilen — ein nie ausgeführter Zweig ist kein Fortschritt und würde die Stagnations-Uhr gratis zurücksetzen. Die Anreicherung wird bei leerem `running[]` komplett übersprungen; ein idles System zahlt nichts.

**Overdue-Schwellwert kommt aus dem Alerting, nicht aus der UI.** `OpsSnapshotMeta.OverdueSeconds` liest `Alerting:LongRunningSeconds` mit demselben Default (600) und demselben `Math.Max(1, …)`-Floor wie `LongRunningExecutionCollector` — die Timeline hebt einen Lauf also exakt in dem Moment hervor, in dem die Alerting-Regel für ihn feuern würde. Roh pro Request gelesen (Sektion ist hot-reloadbar). Die Semantik ist ebenfalls gespiegelt: nur `Running` zählt als überfällig. `Pending` (noch nicht gestartet) und `Paused` (Breakpoint) sind andere Zustände und werden mit diesem Schwellwert nicht bewertet. **Nicht** angefasst: der separat hart kodierte 30-min-Wert für `DashboardStats.LongRunningCount` — Vereinheitlichung hängt an Roadmap-Posten #14.

**Live-Ops-Aktionsrechte hängen am Knoten, nicht am Snapshot.** `OpsNode.CanRun` (Cancel / Retry / Cancel-all) und `OpsNode.CanEdit` (Disable / Quarantäne) kommen aus `GetWorkflowCapabilitiesAsync` je Ordner — einmal pro **distinktem** Ordner aufgelöst, für globale Admins per Short-Circuit ohne Query. Ein früheres snapshot-weites `OpsCapabilities.CanCancel` leitete sich allein aus der **globalen** Rolle ab und log damit: `cancel` verlangt serverseitig zusätzlich `ResourceOp.Run` auf dem Ordner, `disable` sogar `ResourceOp.Edit`. Ein globaler Operator mit bloßem Folder-Viewer-Recht bekam aktive Buttons und danach 403. Die beiden Rechte sind bewusst getrennt — Quarantäne ist nicht „Cancel mit mehr Wumms", sondern eine Edit-Operation.

**Quarantäne ist nicht atomar.** Der Client ruft `disable` **vor** `cancel-all` — umgekehrt startet der `TriggerOrchestrator` die Läufe beim nächsten 5-s-Sync einfach neu. Scheitert `cancel-all` nach erfolgreichem `disable`, ist der Workflow sicher aus, seine Läufe laufen aber weiter; dieser Teilzustand hat eine eigene Meldung und wird über „Alle Läufe abbrechen" allein nachgeholt. Die Erfolgsmeldung nennt `total`, nicht `signalled`: `signalled` zählt nur die in-memory erreichten Läufe, verwaiste Zeilen aus einem früheren API-Prozess werden zusätzlich per `ExecuteUpdateAsync` zwangs-gecancelt.

**Armed-Trigger-Zeilen tragen ein Wartungsfenster-Verdikt.** `ArmedTriggerInfo.BlockedByWindowName` nennt das Fenster, das den angezeigten Start unterdrücken wird — sonst `null`. Ausgewertet wird per `IMaintenanceWindowEvaluator.Evaluate` **zum vorhergesagten Feuerzeitpunkt** (`NextFireUtc`), nicht zu „jetzt": `TriggerOrchestrator` wendet exakt dasselbe Prädikat im Moment des Feuerns an, das Verdikt ist also dieselbe Frage und keine Näherung. Zeilen ohne Vorhersage (`event-driven`, `polling`) fallen auf „jetzt" zurück. Bewusst `Evaluate` und **nicht** `GetWindowsAffecting` — nur ersteres drückt den AllowOnly-außerhalb-Fenster-Block aus und wendet Deny-wins an. Grenze: bewertet gegen den Fenster-Snapshot **dieser** Response; ein zwischenzeitlich angelegtes oder geändertes Fenster schlägt erst mit dem nächsten Poll durch. Das Live-Ops-Departure-Board markiert solche Zeilen, **versteckt sie aber nicht** und ändert ihre Sortierung nicht — der Sinn ist ja, dass der Operator den unterdrückten Start sieht.

**Dashboard-Query lädt keine Workflow-Definitionen.** Der Endpoint braucht nur Trigger-Metadaten und liest sie aus der denormalisierten Spalte `TriggerTypesJson`; die volle `DefinitionJson` wird ausschließlich für Workflows mit `scheduleTrigger`/`databaseTrigger` nachgeladen (Cron-Ausdruck bzw. Poll-Intervall stehen nur im Graphen). Definitionen sind unbegrenzter Text inklusive aller Inline-Skripte — im Repo-Beispielset 21–42 KB pro Workflow — und der Endpoint ist nicht selten: `useSidebarBadges` pollt ihn **minütlich aus jedem offenen Browser**, auf jeder Seite. Eine `NULL`-Spalte (Zeile älter als der Boot-Backfill) fällt auf die Definition zurück, damit kein Workflow still aus der Armed-Liste fällt.

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
| `Llm` | ✓ | `ILlmClientFactory` + `WorkflowAssistantService` + Gates (`LlmQueryActivity`/`AiController`/`AiChatController`) lesen `IOptionsMonitor<LlmOptions>.CurrentValue` pro Use/Request — gilt auch für den Profilwechsel (`ActiveProfileId`) |
| `AiKnowledge` | ✓ | `KnowledgeChatOrchestrator`, Tool-Registry und `/api/ai/knowledge/capabilities` lesen `IOptionsMonitor<AiKnowledgeOptions>.CurrentValue` pro Use — Source-Toggles und Root-Pfade greifen ohne Restart |
| `Retention` | ✓ | `Execution`/`AuditLog`/`WorkflowVersions`/`Notification`/`SupportEvent`-RetentionService lesen `IOptionsMonitor<RetentionOptions>.CurrentValue` pro Schleifen-Pass (`RunIterationAsync`-Seam); `ArchivePath`-Wechsel invalidiert den Cache → Re-Probe (AuditLog bewahrt Compliance-Invariante). `IdempotencyKeyCleanupService` bleibt bewusst config-frei (fixe 24h-TTL) |
| `Stats` | ✓ | `WorkflowStatsRefresher` liest `IConfiguration.GetValue` pro Pass |
| `Threading` | ✓ | `ThreadPoolTuningService` re-appliert `ThreadPool.SetMinThreads` bei Start + `ChangeToken.OnChange` (Boot-Call bleibt für Cold-Start-Prewarm). **Nur bei `Performance:ManualTuning=true`** — unter Auto-Dimensionierung folgt der Service dem Boot-Plan, sonst würde ein Reload allein den ThreadPool in einen anderen Modus ziehen als Runspace-Pool und Dispatch-Queue |
| `FileSystemOperation` | ✓ | `PathGuard` liest `FileSystemOperation:RejectTraversal`/`AllowedRoots` pro Use aus `IConfiguration` |
| `WaitForCondition` | ✓ | `NetworkGuard.RequireExplicitlyAllowlistedHost` liest `WaitForCondition:AllowedHosts` aus der Live-`IConfiguration` bei jedem Probe-Aufruf — gilt sofort ohne Restart |
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
| `Performance` | ✗ | `ManualTuning` entscheidet, wie Runspace-Pool und Dispatch-Queue dimensioniert werden — beide entstehen einmal beim Boot. Der Plan wird deshalb genau einmal aufgelöst (`PerformancePlanFactory`) und als Singleton geteilt |
| `Engine` | ✗ | Concurrency-Caps beim Boot in den Engine-Channel/Pool gebaut |
| `ExecutionDispatch` | ✗ | Queue/Channel beim Boot gebaut |

**Dimensionierung (`Performance:ManualTuning`, default `false`):** Ohne den Schalter leitet NodePilot `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` und `ExecutionDispatch:*` aus erkannter CPU und erkanntem Speicher ab. **`Engine:MaxConcurrentExecutions:*` ist bewusst ausgenommen** — Sicherheits-Cap gegen Trigger-Schleifen/Sub-Workflow-Kaskaden, rein config-gesteuert (500/200), gilt in beiden Modi (`PerformanceSizing` in `NodePilot.Core`, reiner Algorithmus; Erkennung + `Deployment:Mode` liefert `PerformancePlanFactory` in der Api, weil Core nicht rückwärts referenzieren darf). Die in den Sektionen gespeicherten Zahlen sind dann **inert** — `GET /api/admin/settings/effective-sizing` liefert die tatsächlich wirksamen Werte samt bindender Grenze (`Cpu`/`Ram`/`Floor`/`Ceiling`/`Manual`), die UI graut die Felder aus und zeigt den aktiven Wert. Formeln, Ceilings und die Speicher-Budgetierung: `docs/performance-improvements.md`.

**UI-Zustände der Performance-Karten:** Zwei Flags, nicht eines — die Checkbox im Karten-Kopf (`chosen`, live, das was ein Save schreiben würde) und der Boot-Modus (`booted` = `effective-sizing.manualTuning`). Ausgegraut sind die plan-regierten Felder **nur** wenn beide auf Auto stehen; sobald manuelles Tuning angeklickt ist, sind sie editierbar (sonst könnte man die Werte, die der Neustart aufnimmt, gar nicht eintippen). Der Hinweis pro Karte folgt derselben Matrix: `auto/auto` → „gespeichert, aber nicht in Kraft", `manuell/auto` bzw. `auto/manuell` → „gewählt, aber noch nicht in Kraft — bis zum Service-Neustart gilt …", `manuell/manuell` → kein Hinweis. „Aktiv: N (…)"-Werte und der Threading-Hot-Reload-Hinweis hängen dagegen am **Boot**-Modus: `ThreadPoolTuningService` folgt dem Boot-Plan, ein Checkbox-Klick macht ein Threading-Save nicht live.

**Mixed-Section-Limits:** `RestApi` und `Remote` mischen live- und boot-feste Keys in einer Sektion. Da `IsHotReloadable` nur Section-Granularität kennt, können diese nicht gemischt sein — sie bleiben konservativ restart-pflichtig (als bekannte Einschränkung dokumentiert, kein Flag-Per-Field). `Retention:ArchivePath` ist davon ausgenommen: der Pfad wird aktiv re-validiert, ein Wechsel löst eine Re-Probe aus (kein Neustart).
