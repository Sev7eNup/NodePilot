# Performance-Verbesserungen NodePilot

Dokumentiert alle umgesetzten Performance-Optimierungen.

---

## Aktueller Stand (Stand 2026-05-07)

Single source of truth für die Live-Konfiguration. Alle weiter unten dokumentierten Sessions sind chronologische Historie und können von diesen Werten abweichen — bei Konflikt zählt diese Sektion.

**Live in [`appsettings.json`](../src/NodePilot.Api/appsettings.json):**

```json
"ExecutionDispatch":      { "Capacity": 2048, "WorkerCount": 600 },
"Engine": {
  "MaxConcurrentExecutions": { "Global": 5000, "PerUser": 2000 },
  "MaxConcurrentSteps": 600,
  "Runspace":               { "MinRunspaces": 256, "MaxRunspaces": 768 }
},
"Threading": { "MinWorkerThreads": 768, "MinIoCompletionThreads": 768 },
"ConnectionStrings": {
  "Postgres":          "...;Maximum Pool Size=800;Minimum Pool Size=40;Connection Idle Lifetime=60",
  "DefaultConnection": "...;Min Pool Size=40;Max Pool Size=800"
},
"Logging": { "LogLevel": { "Default": "Warning" } }
```

**Live in `C:\NodePilot-Postgres\data\postgresql.conf`:**

```
max_connections = 600
shared_buffers  = 512MB
```

**Live im Code:** Default-Cap **128** gleichzeitige Sub-Workflow-Aufrufe (geteilt zwischen `startWorkflow` und `forEach`), über die injizierte [`ISubWorkflowGate`](../src/NodePilot.Core/Interfaces/ISubWorkflowGate.cs) / [`InMemorySubWorkflowGate`](../src/NodePilot.Engine/Activities/InMemorySubWorkflowGate.cs) — **kein** prozess-weiter statischer Semaphore mehr (frühere `SubWorkflowLimiter`-Klasse; die Session-Logs unten beschreiben den damaligen Stand).

Ausgelegt für **bis zu 500 parallele Workflows** auf einem 20-Core-Host. Skalierungs-Heuristiken für andere Topologien siehe [Default-Tuning für 500-par.-WF-Topologie](#default-tuning-für-500-par-wf-topologie) weiter unten.

---

## Umgesetzte Optimierungen

### Hochlast-Skalierung (Grundlagen)

Vier Stellschrauben, alle mit Config-Override. Werte sind **historische Defaults aus `5fd8ddb`** (50-WF-Topologie, 16-Core); aktuelle Defaults stehen oben unter "Aktueller Stand".

| Commit | Bereich | Was wurde verbessert |
|---|---|---|
| `5fd8ddb` | ExecutionDispatch (WorkerCount/Capacity) | **WorkerCount ist die echte Concurrency-Grenze, Queue der Spike-Buffer.** Dispatcher-Worker `await`-en `engine.ExecuteAsync` für die gesamte Workflow-Lebensdauer. Bursts > WorkerCount warten in der Queue (Capacity), statt alle gleichzeitig auf den Engine-Cap zu rennen. Die `Engine:MaxConcurrentExecutions:Global` / `PerUser`-Caps sitzen darüber als Sanity-Upper-Bound für pathologische Fälle (Trigger-Loops, Sub-Workflow-Kaskaden) und sollen im Normalbetrieb nie greifen. **Frühere Fire-and-Forget-Variante verworfen** — sie umging den Backpressure-Mechanismus, ließ alle Bursts gleichzeitig auf den Engine-Cap laufen und führte zu ~92 % Failed-Rate bei 50-WF-Stress-Tests. |
| `5fd8ddb` | WorkflowScheduler (`Engine:MaxConcurrentSteps`) | **Globaler Step-Semaphore** kappt gleichzeitige In-Flight-Steps prozessweit. Ohne dieses Gate sättigten 500+ concurrent Step-Tasks ThreadPool, DbContext-Writes, Redaction-Regex-Pässe und DI-Scope-Erstellung. `<=0` deaktiviert das Gate komplett (volle Parallelität, akzeptierte CPU-Saturation). Deadlock-Hinweis: `startWorkflow` mit `waitForCompletion=true` hält einen Slot während das Kind läuft — bei tiefer Verschachtelung Wert proportional zur erwarteten Call-Depth × Anzahl-Roots erhöhen. |
| `5fd8ddb` | PowerShellEngineFactory (`engine: "auto"`) | **`auto` mappt auf in-process Runspace-Pool (PS5.1)** statt fresh `pwsh.exe`-Prozess-Spawn pro Step. ProcessExecutionEngine kostete ~50–200 ms Process-Start + Temp-`.ps1`-Datei + Module-Load + ~30 MB RAM pro Aufruf — bei 50 WFs × 5–10 Skript-Steps = hunderte Prozesse. RunspaceExecutionEngine reused Runspaces aus einem Pool: < 5 ms pro Skript. **Hard-Default im Code ist immer noch `min(64, ProcessorCount × 4)`** ([`PowerShellEngineFactory.cs:26`](../src/NodePilot.Engine/PowerShell/PowerShellEngineFactory.cs#L26)) — die in `appsettings.json` gesetzten 768 sind der Override; ohne Config-Override fällt der Pool auf 64 zurück. Workflows, die PS7-only-Features brauchen (`Foreach-Object -Parallel`, Ternary, …), setzen `engine: "pwsh"` explizit; Fallback-Reihenfolge wenn Runspace-Init scheitert: pwsh → powershell. |
| `5fd8ddb` | ThreadPool MinThreads | `ThreadPool.SetMinThreads(workers, iocp)` aus Config. Default-MinThreads ist nur `ProcessorCount`; .NET injiziert darüber hinaus nur ~1–2 neue Threads pro Sekunde. Bei einem 50-Burst auf POST/execute warteten die letzten ~30 Anfragen mehrere Sekunden bevor sie überhaupt einen Thread bekamen. Tunable via `Threading:MinWorkerThreads` / `Threading:MinIoCompletionThreads`. |

**Tests:** [`WorkflowSchedulerTests.RunAsync_WithGlobalStepCap_LimitsConcurrentInFlightSteps`](../tests/NodePilot.Engine.Tests/Execution/WorkflowSchedulerTests.cs) verifiziert das Gate-Verhalten, [`ExecutionDispatchServiceTests.EnqueueAsync_WorkerCallback_AwaitsEngineForFullWorkflowLifetime`](../tests/NodePilot.Api.Tests/ExecutionDispatch/ExecutionDispatchServiceTests.cs) pinnt den Backpressure-Contract (Worker hält Slot bis Engine fertig).

### Engine / Hot-Path

| Commit | Bereich | Was wurde verbessert |
|---|---|---|
| `9fbf000` | WorkflowDefinitionCache | Definition-JSON wird einmal geparst + kompiliert, danach aus LRU-Cache (max 512 Einträge) bedient — kein Re-Parse pro Execution |
| `9fbf000` | ExecutionDispatch | Dedizierter Dispatch-Worker mit `Channel<T>` statt `Task.Run` pro Request — verhindert ThreadPool-Starvation unter Last |
| `9fbf000` | DB-Write-Metrics | `WorkflowDbWriteMetrics.SaveChangesMeasuredAsync` mit OTel-Instrumentierung — Profiling ohne Code-Änderung möglich |
| `52f2fb4` | VariableResolver | Pre-compiled `static readonly Regex` statt Regex-Kompilierung pro Template-Ersetzung; `JsonDocument.RootElement.Clone()` statt undisposed Root |
| `52f2fb4` | ConditionEvaluator | `"matches"`-Operator nutzt `ConcurrentDictionary`-Regex-Cache statt `new Regex()` pro Evaluation |
| `04f3094` | JsonDocument-Disposal | `RunScriptActivity` + `VariableResolver` disposen den pooled Buffer korrekt (`using var doc`) — verhindert PooledByteBufferWriter-Leak |
| `4cbd38e` | ActivityRegistry | War `Scoped` (Dictionary-Rebuild pro Step-DI-Scope), jetzt `Singleton` — Type-Map wird einmal aufgebaut |
| `ae84c98` | SQL Server Retry | `EnableRetryOnFailure(maxRetry: 5, maxDelay: 10s)` — Deadlocks (Error 1205) und transiente Timeouts werden transparent retried statt als Fehler zu propagieren |
| `ae84c98` | PK-Violation-Absorber | `SaveChangesIdempotentAsync` absorbiert `SqlException 2627/2601` nach Retry-Doppel-INSERT — Execution läuft durch statt abzubrechen |
| `ae84c98` | StepRunner Error-Path | DB-Writes im `catch`-Block des StepRunners propagieren nie zum WorkflowScheduler — kein Scheduler-Abort wenn DB-Write im Fehler-Pfad selbst fehlschlägt |

### Datenbank / API

| Commit | Bereich | Was wurde verbessert |
|---|---|---|
| `eed3041` | WorkflowsController | "Letzte 20 Executions pro Workflow" war O(N×M) korrelierte Subquery — ersetzt durch `ROW_NUMBER() OVER (PARTITION BY WorkflowId ORDER BY StartedAt DESC)` |
| `eed3041` | SystemHealthWriter | Heartbeat-Writes von allen 5s auf min. 30s Abstand gedrosselt (Debounce via `ConcurrentDictionary`) — SQLite-fsync-Rate um ~6× reduziert |
| `5a9b78c` | `GetStepStats` SQL-Variable-Cap | `WHERE WorkflowExecutionId IN (@execIds…)` mit > 999 Parametern crasht SQLite (`SQLITE_LIMIT_VARIABLE_NUMBER`). `OrderByDescending(StartedAt).Take(900)` cappt den IN-Set; für die p50/p95-Statistik in Designer-Hover absolut ausreichend. Crash war im Log als "An unhandled exception … too many SQL variables" sichtbar nach 5+ aufeinanderfolgenden 200-WF-Stress-Tests. |
| `5a9b78c` | `GET /executions?activeOnly=true` | Filter auf `Running\|Pending\|Paused`. Live View (`hydrateActive` in `useSignalR.ts`) ruft den Endpunkt mit `activeOnly=true` auf statt 500 historische Rows pro Tab-Open zu laden. Spürbare Verbesserung der initialen Live-View-Ladezeit bei vielen Historie-Einträgen. |

### SignalR / UI

| Commit | Bereich | Was wurde verbessert |
|---|---|---|
| `7c33436` | SignalR Channel | Channel-Mode von `DropOldest` auf `DropNewest` — frühe `StepCompleted`-Events unter Burst-Load nicht mehr verworfen |
| `7c33436` | Running-Step-Finalizer | Bei `ExecutionStatusChanged` (terminal) werden noch laufende Steps auf Terminal-Status gesetzt — kein ewig drehender Spinner im UI |
| `5a9b78c` | `SendBatchAsync` Parallel-Fan-Out | Sequenzielles `foreach + await SendAsync` ersetzt durch `Task.WhenAll`. Bei 200 parallelen Executions × ~1 ms pro Group-Send überschritt der sequenzielle Pfad das 50 ms Flush-Intervall → Backlog → DropNewest → Event-Verlust. Per-Group-Try/Catch in eigener `SendGroupSafeAsync`-Methode bleibt erhalten. |
| `5a9b78c` | `applyLiveEvents` O(N+M) Batch | War O(N × M) durch Per-Event-`{...prev}`-Spread des kompletten Executions-Dicts. Ein einziger Spread upfront, dann In-Place-Update einzelner Einträge im Working-Copy. Bei 200 Executions × 200 Events pro Batch = 40 000× weniger Property-Copies. |
| `5a9b78c` | `sortLiveExecutions` Key-Precompute | `steps.some()` lief vorher inside des Comparators (O(N log N) Aufrufe). Jetzt einmal pro Execution beim Map auf Sort-Keys. |

### Remote / WinRM

| Commit | Bereich | Was wurde verbessert |
|---|---|---|
| `5a9b78c` | PowerShell-Module-Load-Race-Retry | Concurrent WinRM-Sessions die simultan dasselbe Module auto-importen (z.B. `NetTCPIP` via `Get-NetIPAddress`) triggern eine PowerShell-interne `InvalidOperationException: "Collection was modified; enumeration operation may not execute."` — die Module-Registry ist nicht thread-safe. Transient: `WinRmSession.ExecuteScriptAsync` retried bis zu 3× mit randomisiertem 100–350 ms × Attempt Backoff. Detection via `ErrorOutput.Contains("Collection was modified")`. |

---

## Tuning-Erkenntnisse: Step-Concurrency-Cap unter Postgres

Mehrere Iterationen am `Engine:MaxConcurrentSteps`-Cap mit überraschenden Ergebnissen.
Wichtigste Lehre: der "richtige" Wert hängt **nicht** primär an CPU/Memory, sondern an
der nachgelagerten Event-Pipeline.

### Historisch: SQLite-Decke (obsolet — App-DB SQLite ist seit `2431619` raus)

Nur als Lessons-Learned-Box, falls jemand aus Versehen einen weiteren SQLite-Provider
einzieht (z.B. für einen anderen Datastore):

> SQLite WAL erlaubt nur **einen Writer gleichzeitig**. EF Core's `SaveChangesAsync` ist am
> Microsoft.Data.Sqlite-Provider **nicht echt async** — der Write läuft inkl. der
> `busy_timeout`-Backoff-Loop in nativem P/Invoke und blockiert den ThreadPool-Thread.
> Längeres `busy_timeout` heißt **mehr** geblockte Threads, nicht weniger Fehler — fail-fast
> befreit den Thread schneller als wartender Lock. Bei 200-WF-Bursts gegen SQLite mit
> `MaxConcurrentSteps=512` und `busy_timeout=30 s` war der Server praktisch unbrauchbar.

### Postgres-Decke: SignalR-Event-Flood + Frontend-Render

Postgres MVCC + Npgsql echt async eliminiert die ThreadPool-Block-Decke komplett. Aber
der nächste Flaschenhals taucht auf:

| `MaxConcurrentSteps` | `Maximum Pool Size` | Beobachtetes Symptom |
|---|---|---|
| 1024 | 300 | UI träge, weniger Steps angezeigt als ausgeführt. **Ursache:** 1024 gleichzeitige Steps feuern `StepStarted`/`StepCompleted` → SignalR-Notifier-Batch füllt schneller als das 50 ms Flush-Intervall drainen kann → DropNewest greift → Events gehen verloren → Live View zeigt < real. Plus: Frontend macht massive React-Re-Renders pro Batch. |
| 200 | 200 | Equilibrium für 200-WF-Bursts. Cap matched genau `ExecutionDispatch:WorkerCount`, jeder Run macht im Schnitt ~1 Step gleichzeitig — SignalR-Batch bleibt unter Drop-Threshold, Frontend ist responsive. Inzwischen via Live-Tab-Virtualisierung + `React.memo` (siehe unten) auf 600/800 angehoben. |

**Lehren Postgres:**
- DB ist nicht mehr der Cap — die **Event-Pipeline** ist es. Frontend-Virtualisierung hat das Plateau anschließend nochmal verschoben.
- Step-Cap deutlich über `WorkerCount` zu setzen produziert nur Event-Drops, keinen Throughput-Gewinn.
- Connection-Pool sinnvoll dimensionieren: Faustregel `MaxPoolSize ≈ MaxConcurrentSteps + Headroom (Controllers, Retention, SignalR)`. Aktueller Wert siehe Single-Source-Block oben.

### Wege, das Plateau weiter zu heben (nicht umgesetzt)

- **SignalR-Batch-Coalescing**: bei sehr vielen `StepStarted`/`StepCompleted` für dieselbe Execution könnten Events innerhalb eines Batches zu einem Delta verdichtet werden (statt N einzelner Events → 1 zusammengefasstes "Steps X bis Y abgeschlossen"). Würde den Cap-Headroom über das aktuelle Plateau heben.
- **Step-Write-Serialisierung über dedizierten Channel** (Batch-Writes für `StepExecution`-Persists): bei 57 000 Step-Save-Calls pro Stress-Test wäre Aggregation in einem 50-ms-Channel mathematisch attraktiv. Bricht aber die Live-Step-Anzeige (UI sieht Steps nicht in DB bevor der Batch flushed) und macht die `_runningExecutions`-Cancellation-Pfade komplizierter. Zu hohes Architektur-Risiko, in Auto-Mode-Iteration 2026-05-06 explizit verworfen.

---

## Sub-Workflow-Limiter Konsolidierung (Session 2026-05-08)

Architektur-Korrektur, kein Performance-Win. Auslöser: Audit der vier Performance-Hebel-Vorschläge (CIM/Spawn-Workflow-Rewrite, OTel-in-Dev, ForEach-Limiter-Lücke, Step-Slot-Holding bei `waitForCompletion`). Drei davon waren entweder workflow-spezifisch (CIM/Spawn) oder durch Auto-Mode-Iteration 2026-05-06 bereits widerlegt (Step-Slot-Yield, OTel-Default). Der vierte — **ForEach-Limiter-Diskrepanz** — war eine echte Code-vs-Doc-Drift.

### Was war kaputt

Der Doc-Comment in [`ForEachActivity.cs`](../src/NodePilot.Engine/Activities/ForEachActivity.cs) versprach:

> `<=0` means "no per-foreach limit" (still bounded by the engine-wide ChildSemaphore, so an accidental thousand-item collection can't saturate the process)

Tatsächlich rief die Activity `engine.ExecuteAsync(childWorkflow, ...)` direkt auf — **ohne** durch die `ChildSemaphore` aus `StartWorkflowActivity` zu gehen. Es griff nur das ForEach-lokale `gate` mit Hard-Cap 64 pro Instanz. Bei N parallelen Parent-Workflows × ForEach mit `maxParallelism=64` ergab das N × 64 Sub-Workflows in flight — engine-wide völlig ungebremst.

In den 500-WF-Stress-Tests des Tech-Demo-XL ist das nicht aufgefallen, weil der dortige Workload `startWorkflow` (durch ChildSemaphore gebremst) statt `forEach` nutzt. Ein realer ForEach-heavy Workload (z.B. 50 parallele Trigger-Fires × `forEach` über 100 Items) hätte durch diese Lücke die Engine schnell saturieren können — genau das Szenario, gegen das die ChildSemaphore eigentlich schützt.

### Was geändert wurde

| Datei | Änderung |
|---|---|
| [`SubWorkflowLimiter.cs`](../src/NodePilot.Engine/Activities/SubWorkflowLimiter.cs) (neu) | Statische Klasse mit `Semaphore = new SemaphoreSlim(128, 128)`. Single Source of Truth für die engine-weite Sub-Workflow-Concurrency-Cap. |
| [`StartWorkflowActivity.cs`](../src/NodePilot.Engine/Activities/StartWorkflowActivity.cs) | Private `ChildSemaphore` + `DefaultMaxConcurrentChildren` entfernt; alle 7 Acquire/Release/Error-Stellen auf `SubWorkflowLimiter.Semaphore` umgestellt. |
| [`ForEachActivity.cs`](../src/NodePilot.Engine/Activities/ForEachActivity.cs) | Item-Loop greift nach lokalem `gate.WaitAsync` zusätzlich `SubWorkflowLimiter.Semaphore.WaitAsync(itemsCts.Token)`. Reihenfolge: lokal → global. Release in finally: global → lokal. Stale Comment-Verweise auf `ChildSemaphore` korrigiert. |
| [`SubWorkflowLimiterTests.cs`](../tests/NodePilot.Engine.Tests/Activities/SubWorkflowLimiterTests.cs) (neu) | 3 Tests in `[Collection]` mit `DisableParallelization=true`: Capacity-Konstante, Acquire/Release-Sichtbarkeit, Cancellation-Honor bei erschöpftem Limiter. |

### Wait-Semantik in ForEach

Bewusste Design-Entscheidung: das lokale `gate` wird **vor** dem globalen Limiter gehalten. Das bindet den Backpressure ans ForEach-eigene Concurrency-Budget statt alle Items gleichzeitig auf den engine-wide Cap rennen zu lassen. Cancellation-Token gilt für beide Waits — bei Parent-Cancellation oder fail-fast werden gequeuete Items als `Skipped` markiert (Status `"cancelled before start"`).

Kein Timeout auf dem globalen Wait innerhalb von ForEach (anders als bei `startWorkflow`, wo nach 5 s mit "engine at sub-workflow concurrency limit" gefailt wird). Begründung: ForEach ist explizit für Bulk-Iteration gedacht — ein 100-Item-ForEach mit voll ausgelastetem Limiter sollte queuen, nicht mit 100× Timeout-Errors abrauchen.

### Erwarteter Impact

- **Bestehende Stress-Tests (500-WF Tech-Demo-XL):** kein Effekt, da diese Workloads kein `forEach` nutzen.
- **ForEach-heavy Workloads:** korrekte engine-weite Backpressure. Bei voll ausgelastetem Limiter queuen ForEach-Items statt unbegrenzt durchzulaufen. Das ist die nun korrekt durchgesetzte Semantik des Doc-Comments — vorher Versprechen ohne Implementierung.
- **`ChildSemaphore`-Tuning-Lehre aus Iter 10 (2026-05-06) gilt fort:** den Cap auf >128 zu heben verschiebt nur den Stau eine Schicht weiter — gilt jetzt auch für ForEach-induzierte Last.

### Nicht umgesetzt aus dem 4-Punkte-Audit

| Vorschlag | Status |
|---|---|
| CIM/Process-Spawn im Workflow ersetzen | Workflow-spezifisch, nicht Engine-Scope. Eingangsthema für künftige Designer-Lints. |
| OTel in `appsettings.Development.json` für Stress deaktivieren | ~3-8 % erwarteter Win, billiger Fix wenn nötig. Aktuell nicht wirksam, weil OTLP-Default-Exporter nicht aktiv ist (nur Prometheus-Scrape). |
| Step-Slot-Yield bei `startWorkflow(waitForCompletion=true)` | Mechanisch real, aber **nicht binding**: `MaxConcurrentSteps 600 → 1500` wurde 2026-05-06 mit 42 % schlechter widerlegt. Step-Slots maskieren das CIM/Spawn-Ceiling — Yield-Mechanismus würde nur den Engpass verlagern. Zurückgestellt bis Profiling Step-Slot-Starvation als echten Bottleneck nachweist. |

---

## Noch offen

Aktuelle Restbestand-Sektion siehe weiter unten ("Offene Backend-Items" + "Offene Frontend-Items"). Die Auto-Mode-Iteration vom 2026-05-06 hat zusätzlich verifiziert, dass die verbleibenden klassischen Engine-Tuning-Hebel (DB-Pool weiter aufbohren, `MaxConcurrentSteps`/`Runspace` >600 ziehen, `ChildSemaphore` >128) **aktiv schädlich** sind — Details in der gleichnamigen Session-Sektion am Ende dieser Datei.

---

## 500-Job-Stress-Test-Tuning (Session 2026-05-04)

Skalierung von "50 parallele Workflows" auf **500 parallele Workflows** mit Postgres als Backend. Zentrale Lehren: DB-Pool und CPU sind nie der echte Bottleneck — der RunspacePool für `runScript`-Activities ist es.

### Auslöser

Stress-Test [`scripts/stress-test/launch-50-master.py`](../scripts/stress-test/launch-50-master.py) (`NODEPILOT_STRESS_PARALLEL=500`, Barrier-synced) lieferte sporadische Step-Failures mit der EF-Generic-Meldung:

```
System.InvalidOperationException: An exception has been raised that is likely due to a transient failure.
 ---> Npgsql.PostgresException (0x80004005): 53300:
      remaining connection slots are reserved for roles with the SUPERUSER attribute
```

Step-Output war korrekt (`LogActivity` lief erfolgreich durch), aber der anschließende `SaveChangesMeasuredAsync("step.terminal")` bei [StepRunner.cs:152](../src/NodePilot.Engine/Execution/StepRunner.cs#L152) wurde von Postgres abgelehnt → Outer-Catch bei [StepRunner.cs:195-220](../src/NodePilot.Engine/Execution/StepRunner.cs#L195-L220) überschrieb den `Status=Succeeded` mit `Status=Failed` und `ErrorOutput=ex.Message`.

### Code-Fixes

| Datei | Was wurde verbessert |
|---|---|
| [`Program.cs:177-192`](../src/NodePilot.Api/Program.cs#L177-L192) | **Postgres `EnableRetryOnFailure`** analog zum SQL-Server-Branch (`maxRetryCount: 5`, `maxRetryDelay: 10s`). Vorher: jede transiente Connection-Rejection wurde direkt zum Step-Failure. Postgres-Provider erkennt SQLSTATE 53300 (`too_many_connections`) bereits als transient — keine `errorCodesToAdd` nötig. |
| [`WorkflowDbWriteMetrics.cs:55-67`](../src/NodePilot.Engine/Execution/WorkflowDbWriteMetrics.cs#L55-L67) | **Idempotent-Wrapper für Postgres-Replays:** der existierende `SqlException 2627/2601`-Catch (SQL Server PK/UNIQUE) wurde um `PostgresException { SqlState: "23505" }` (Postgres unique_violation) ergänzt. Wenn EF Core nach erfolgtem INSERT replayt, hitst der Retry den Unique-Constraint — der Wrapper resetet Added → Unchanged und gibt 0 zurück. Tests: [`WorkflowDbWriteMetricsTests.cs`](../tests/NodePilot.Engine.Tests/Execution/WorkflowDbWriteMetricsTests.cs). |

### DB-Tuning (Postgres + App-Pool)

Korrekte Hierarchie ist `max_connections (Server) > Maximum Pool Size (App-Pool) + Headroom`. Pool-Limit über Server-Limit hinaus → Connections werden mid-flight rejected.

**Postgres** (`postgresql.conf` der lokalen Instanz `C:\NodePilot-Postgres\data\postgresql.conf`):

```
max_connections    = 600       # default 100 — viel zu wenig für 500 par. WFs
shared_buffers     = 512MB     # default 128MB — mit mehr Connections steigt Backend-RAM-Bedarf
                                # superuser_reserved_connections bleibt default 3
```

**App** (Werte für diese Session-Iteration; aktuelle Live-Werte siehe Single-Source-Block ganz oben):

```json
"ConnectionStrings": {
  "Postgres": "...;Maximum Pool Size=480;Minimum Pool Size=20;Connection Idle Lifetime=60"
},
"ExecutionDispatch": { "Capacity": 1024, "WorkerCount": 512 },
"Engine": {
  "MaxConcurrentSteps": 512,
  "Runspace": { "MinRunspaces": 128, "MaxRunspaces": 512 }
}
```

Postgres restart via `pg_ctl restart -D <data> -m fast`, App-Restart greift über die appsettings.

### RunspacePool-Tuning — der echte Bottleneck

**Erkenntnis:** PowerShell SDK skaliert nicht linear. `MaxRunspaces` höher zu setzen ist nur dann wirksam, wenn der Pool warm ist — Cold-Start kostet ~60-90 Sek bei 500 simultanen runScript-Anfragen.

| MaxRunspaces | runScript avg-Latenz (cold-incl.) | Steady-State (warm) | Bemerkung |
|---|---|---|---|
| 64 (Default-Cap) | 4.5s | – | Pool zu klein → Queue baut sich auf |
| 256 | 20.6s | – | Mehr Slots aber Cold-Start spürbar |
| 512 | 29.7s | **2.7s** | Cold-Start ~90 Sek, dann ideal |

**Pre-Warm via `MinRunspaces=128`:** Beim ersten runScript nach API-Start werden 128 Runspaces auf einmal aufgebaut (~5-10 Sek einmalig), danach sofort warm verfügbar. Verlagert die Cold-Start-Strafe vom ersten Stress-Test-Burst auf den ersten harmlosen Workflow nach API-Restart.

**Hard cap im Code:** [`PowerShellEngineFactory.cs:26`](../src/NodePilot.Engine/PowerShell/PowerShellEngineFactory.cs#L26) hat noch immer `Math.Min(64, ProcessorCount × 4)` als Default — die 512 kommen ausschließlich aus `appsettings.json`. Ohne Override-Config kappt der Code auf 64 zurück.

### Bottleneck-Hierarchie (Snapshot aus dieser Session-Iteration)

In aufsteigender Wahrscheinlichkeit der Sättigung bei 500 par. WFs (Werte gegen die Session-Config Pool=480 / WorkerCount=512 / MaxConcurrentSteps=512 — die Hierarchie selbst gilt unverändert mit den aktuellen 800/600/600-Werten, die Auslastungs-Prozente verschieben sich proportional):

| Komponente | Auslastung im Steady-State | Bottleneck? |
|---|---|---|
| **CPU** | 2 von 20 Kernen (10 %) | Nein, weit entfernt |
| **DB-Pool** | 1-85 von 480 (max 18 %) | Nein |
| **DB-Server (max_connections=600)** | < 100 used | Nein, Headroom ~500 |
| **`MaxConcurrentSteps=512`** | ~150 in-flight | Nein, ~30 % genutzt |
| **`WorkerCount=512`** | wenig genutzt (Engine async) | Nein |
| **RunspacePool=512** | ~256-400 simultan | **Ja** — `runScript`-Steps haben 5-50s Latenz und blocken Slots |
| **Workflow-Topologie** | sequenzielle Steps lassen sich nicht parallelisieren | Strukturell |

**Effektive Parallelität (Little's Law):**

```
in_flight_steps  ≈  throughput  ×  avg_latency
                 ≈  30 steps/s  ×  5 s
                 ≈  ~150 Steps simultan in Bearbeitung
```

→ "500 Workflows Running" ≠ 500 Steps gleichzeitig. Die anderen ~350 Workflows warten auf Vorgänger-Steps, junctionieren oder pollen `waitForCondition`.

### Frontend-Caps angepasst (Live-Tab Sichtbarkeit)

[`useSignalR.ts`](../src/nodepilot-ui/src/hooks/useSignalR.ts) und [`ExecutionPanel.tsx`](../src/nodepilot-ui/src/components/designer/ExecutionPanel.tsx):

| Konstante | Vorher | Jetzt | Wirkung |
|---|---|---|---|
| `MAX_ACTIVE_DISPLAYED` | 50 | `Infinity` | Alle Running/Pending in der Liste, kein "+N more"-Cutoff |
| `MAX_AUTO_HYDRATE` | 3 | 10 | Top-10 frischeste Runs bekommen Step-Details automatisch |

`LiveExecutionAccordionItem` kompaktifiziert (history-Stil): `min-h-8` raus, `py-1`→`py-0.5`, Icons `14px`→`11px`, `space-y-[0.2rem]`→`space-y-px`. Zeilenhöhe von ~32px auf ~20px → ~40 % mehr Zeilen pro Sichtfeld. Counter-Badge bei "Live" identisch zum History/Output-Pattern (Pulse-Dot + Zahl).

### Daten-Migrationen (Workflow-Definitions-Bugs)

| Bug | Betroffene Workflows | SQL |
|---|---|---|
| `"activityType":"fileOperation"` (Engine kennt nur `fileSystemOperation`) | 17 | `REPLACE` + `Version+1` + `UpdatedAt=now()` |
| `"type":"junction"` Top-Level (Frontend nodeTypes-Map kennt nur `activity`) | 10 | `REPLACE` von `"type":"junction"` zu `"type":"activity"` — `data.activityType:"junction"` und `data.config.mode` waren bereits modern |

`Version`-Bump invalidiert [`WorkflowDefinitionCache`](../src/NodePilot.Engine/Execution/WorkflowDefinitionCache.cs) automatisch beim nächsten `/execute`. `WorkflowVersions`-Tabelle behält den Vor-Migration-Snapshot — Rollback möglich.

### Default-Tuning für 500-par.-WF-Topologie

Skalierungs-Heuristik. Der Live-Stand (20-Core / 500 par.) ist nach `15c9c32` auf
600/600/768/256 mit DB-Pool=800 nachgezogen worden; die Tabelle hier zeigt die
Werte aus dem 2026-05-04-Stand der Heuristik plus die aktuelle Live-Zeile separat.

| Last-Profil | WorkerCount | MaxConcurrentSteps | MaxRunspaces | MinRunspaces | DB-Pool | Postgres `max_connections` |
|---|---|---|---|---|---|---|
| 8-Core / 20 par. WFs | 20 | 256 | 32 | 1 | 30 | 100 (default) |
| 16-Core / 50 par. WFs | 50 | 512 | 64 | 1 | 100 | 100 (default) |
| 20-Core / 500 par. WFs (Stand 2026-05-04) | 512 | 512 | 512 | 128 | 480 | 600 |
| **20-Core / 500 par. WFs (live, Stand 2026-05-07)** | **600** | **600** | **768** | **256** | **800** | **600** |
| 32-Core / 1000 par. WFs | 1024 | 1024 | 1024 | 256 | 800 | 1200 |

**Memory-Daumenregel Postgres:** Backend ~10MB pro Connection + `shared_buffers`. Bei `max_connections=600` und `shared_buffers=512MB` zieht Postgres bis zu ~6.5 GB.

**Memory-Daumenregel RunspacePool:** ~30MB pro initialisiertem Runspace. `MinRunspaces=128` → ~3.8 GB Idle-RAM. `MaxRunspaces=512` voll ausgelastet → ~15 GB.

### Tests / Verifikation

- Backend: [`WorkflowDbWriteMetricsTests.cs`](../tests/NodePilot.Engine.Tests/Execution/WorkflowDbWriteMetricsTests.cs) deckt Postgres-23505-Idempotenz ab
- Frontend: 920/920 Tests grün nach Cap-Anpassungen ([`useSignalR.test.tsx`](../src/nodepilot-ui/src/__tests__/hooks/useSignalR.test.tsx) Tests neu strukturiert um die Infinity-Cap-Semantik abzubilden)
- End-to-End: 500-Job-Stress-Test läuft komplett durch in ~3.8 Min Wall-Time (vorher ~5 Min mit Pool=256)

---

## SQL-Server-Readiness + DB-Aggregation + SQLite-Removal (Session 2026-05-04 Phase 2)

Audit-Erhebung über Backend + Frontend + DB-Layer mit drei parallelen Code-Audits. Ergebnis: Backend-Hot-Path bei 8.5/10, SQL-Server-Readiness bei 6/10 (vier Foot-Guns offen), Frontend bei 7.5/10. Daraus folgten vier in-Reihe-Patches:

### SQL-Server-Production-Foundation

| Bereich | Was wurde verbessert | Datei |
|---|---|---|
| RCSI Auto-Setup | `Enable-SqlReadCommittedSnapshot` Helper im Installer-Pre-Flight. SQL-Server-Pendant zu Postgres-MVCC: ohne RCSI blockieren `WorkflowStatsRefresher`-GROUP-BYs jeden parallelen `INSERT`. Idempotent (`SELECT is_read_committed_snapshot_on`-Check), failure → loud warning + DBA-Snippet, kein hard-fail. | [Install-NodePilot.ps1:345-401](../deploy/Install-NodePilot.ps1#L345-L401) |
| Pool-Sizing | `Min Pool Size=20;Max Pool Size=480` in der SQL-Server-ConnectionString eingeführt; Postgres-ConnString im Installer ebenfalls gefixt (war ohne Pool-Sizing — analog zur Dev-config). **Inzwischen auf `Min=40;Max=800` für beide Provider hochgezogen** ([siehe Single-Source-Block oben](#aktueller-stand-stand-2026-05-07)). | [appsettings.json](../src/NodePilot.Api/appsettings.json), [Install-NodePilot.ps1:680](../deploy/Install-NodePilot.ps1#L680) |
| MaxBatchSize + CommandTimeout | `MaxBatchSize=200` (vs. SqlServer-Default 42 → 24 Round-Trips für 1000 Steps werden 5), `CommandTimeout=120s` (vs. 30s — `WorkflowStatsRefresher`-Sweeps reißen das sonst). Override via `Database:MaxBatchSize` / `CommandTimeoutSeconds`. Beide Provider. | [Program.cs:155-220](../src/NodePilot.Api/Program.cs#L155-L220) |
| 3 Covering-Indexe | `(WorkflowId, StartedAt DESC) INCLUDE (Status, CompletedAt)`, `(Status, StartedAt DESC) INCLUDE (WorkflowId, CompletedAt, TriggeredBy)`, `(WorkflowExecutionId, StartedAt)`. Annotations doppelt gesetzt: `SqlServer:Include` + `Npgsql:IndexInclude` — beide Provider lesen jeweils ihre eigene und kriegen echte covering-Indexe (Postgres v11+). EF-Migration manuell um Annotations ergänzt (SQLite-Scaffolder übernimmt sie nicht). | [NodePilotDbContext.cs:41-79](../src/NodePilot.Data/NodePilotDbContext.cs#L41-L79), [Migration `AddCoveringIndexesForSqlServer`](../src/NodePilot.Data/Migrations/20260504184154_AddCoveringIndexesForSqlServer.cs) |

**Live verifiziert via `EXPLAIN ANALYZE`:** alle drei Indexe von Postgres genutzt (`Bitmap Index Scan` / `Index Scan using IX_…`). activeOnly-Query exec-time 0.135 ms.

### DB-Aggregation (Hot-Path-C#-→-SQL Migration)

| Endpoint | Vorher | Nachher |
|---|---|---|
| `ExecutionsController.GetStepStats` | 900 execs × N steps full-row materialisiert, P95/Avg/Failed-Counts in C# | Q1: GROUP BY `(StepId)` für Total/Failed-Counts auf DB. Q2: schmale `(StepId, StartedAt, CompletedAt)`-Projektion (~24 Bytes/Row statt ~KB) für Avg/P95/Last in C# — DateTime-Subtraction nicht via EF in SQL möglich (SQLite-incompat), aber Per-Row-Datentransfer ist die echte Optimierung. |
| `DashboardController.execs24h` | `WorkflowExecutions` der letzten 24h voll materialisiert + In-Memory-Bucketing in 24 Hour-Buckets | Single GROUP BY `(Year, Month, Day, Hour, Status)` auf DB → max 24 × 5 Aggregat-Rows. EF compiles `e.StartedAt.Year` etc. nativ auf beiden Providern. |
| `ConditionEvaluator.matches` | `new Regex(...)` pro Edge-Eval | `ConcurrentDictionary`-Cache mit Cap 256, JIT-Compile entfällt bei wiederkehrenden Patterns |

**Lehre: provider-portable DB-Aggregation hat eine Grenze.** Volle DB-side-Aggregation für P95 würde `PERCENTILE_CONT` brauchen — auf Postgres und SQL Server divergent. Pragmatischer Mittelweg: Counts/Avg/Buckets auf DB (provider-neutral via LINQ), Perzentile auf Client mit minimaler Per-Row-Projektion.

### SQLite-Removal (App-DB only)

User-Anweisung: "SQLite wird nicht mehr verwendet" — App-interne Production-DB ist nur noch Postgres oder SQL Server. **User-Features bleiben** (SqlActivity mit `provider: sqlite`, DatabaseTriggerSource für SQLite-Files — Workflow-Aktivitäten gegen externe Datenbanken). **Tests bleiben** auf SQLite-In-Memory als praktisches Test-Backend.

| Datei | Änderung |
|---|---|
| [Program.cs:155-225](../src/NodePilot.Api/Program.cs#L155-L225) | SQLite-Branch raus, Default-Provider Postgres, throws bei unbekanntem Provider |
| `SqliteOptimizationInterceptor.cs` + Test | gelöscht (war für App-DB-WAL/busy_timeout-PRAGMAs) |
| [appsettings.json](../src/NodePilot.Api/appsettings.json) | `ConnectionStrings:Sqlite` raus |
| [CLAUDE.md](../CLAUDE.md) | Datenbank-Tabelle reduziert auf 2 Provider, Migrations-Postprocessing-Doku angepasst (provider-spezifische Type-Strings müssen jetzt aus `uuid`/`text`/`uniqueidentifier`-Annotations weg, nicht mehr aus den SQLite-`TEXT`/`INTEGER`-Strings) |

### Frontend: Live-Tab-Virtualisierung + React.memo

Im 200-Job-Stress-Test war nach den Backend-Optimierungen der nächste sichtbare Engpass die Live-Tab-Render-Last (200+ Accordion-Items linear im DOM, jedes mit eigenen `filter()`-Scans).

| Datei | Änderung |
|---|---|
| [ExecutionPanel.tsx:287-359](../src/nodepilot-ui/src/components/designer/ExecutionPanel.tsx#L287-L359) | LiveTab via `@tanstack/react-virtual` virtualisiert. Variable-height handling über `measureElement` (collapsed ~26 px, expanded `panelHeight - 80`). overscan: 6. |
| LiveExecutionAccordionItem | `React.memo` mit Custom-Comparator: Reference-Equality auf `execution`. `useSignalR.applyLiveEvents` produziert immutable updates (`next[id] = { ...exec, … }`), daher schlägt der Comparator bei jedem echten Status-Update zu. Bei `expanded === true` wird Re-Render durchgelassen damit `LiveExecutionDetail` aktuell bleibt. |
| ActivityNode | `React.memo` mit `data/selected/isConnectable`-Vergleich. Bei 200-Node-Graphen reconcile-n sonst alle 200 Nodes synchron bei jedem useDesignStore-Tick. Interne Store-Subscriptions bleiben aktiv (Style-Switches greifen weiterhin). |
| PropertiesPanel | `React.memo` mit Daten-Props-Comparator. Inline-Closures aus dem Editor (`onClose`, `onUpdate`, …) bewusst ignoriert — sind semantisch stabil aber Page-Render erzeugt neue Refs. Sie im Vergleich würde Memo de facto deaktivieren. |
| `__tests__/setup.ts` | `HTMLElement.offsetHeight/Width` + `getBoundingClientRect` non-zero defaults für jsdom. react-virtual rendert sonst nichts in Tests (zero-height parent → 0 visible items). |

**Vor diesem Patch hatte das Frontend null `React.memo`-Wrapper im gesamten Code** (per Grep verifiziert) — eine offene Architektur-Flanke, die bei großen Workflows messbar war.

### Frontend: SignalR-driven Query-Invalidation (F3)

Architekturelle Konsolidierung: NodePilot betrieb zwei parallele Daten-Feeds für dieselbe Information — SignalR-Hub (push) plus React-Query mit `refetchInterval` (pull). Im Multi-Tab-/Multi-User-Setup multipliziert sich das (5 User × ExecutionsPage = 60 polls/min auf eine Tabelle, die sich nur bei Status-Changes ändert).

| Datei | Änderung |
|---|---|
| [App.tsx:19-31](../src/nodepilot-ui/src/App.tsx#L19-L31) | `refetchOnWindowFocus: false` als QueryClient-Default. Tab-Wechsel feuert nicht mehr alle aktiven Queries neu ab. |
| [useSignalR.ts](../src/nodepilot-ui/src/hooks/useSignalR.ts) | `useQueryClient` + `scheduleQueryInvalidate(workflowId)` mit 500 ms Debounce. Aufgerufen aus `enqueueLiveEvent` bei jedem `ExecutionStatusChanged` (terminale + non-terminale, weil Listen-Counts auch bei `Running`-Wechseln updaten). Reconnect-Handler ruft denselben Path → schließt das Reconnect-Loch in den Caches. Cleanup im unmount löscht den Debounce-Timer. |
| [ExecutionsPage.tsx](../src/nodepilot-ui/src/pages/ExecutionsPage.tsx), [ExecutionPanel.tsx](../src/nodepilot-ui/src/components/designer/ExecutionPanel.tsx) | `refetchInterval: 5000` aus den Listen-Queries entfernt. SignalR triggert die Invalidation gezielt. |
| [DashboardPage.tsx](../src/nodepilot-ui/src/pages/DashboardPage.tsx) | Polling beibehalten, aber von 10s auf 30s. Dashboard hat selbst keine SignalR-Connection (nur Editor + ExecutionsPage). Multi-Tab-User mit offenem Editor profitieren via gemeinsamem QueryClient zusätzlich. |
| [`__tests__/hooks/useSignalR.test.tsx`](../src/nodepilot-ui/src/__tests__/hooks/useSignalR.test.tsx) | `renderHook` lokal überschrieben mit Wrapper-Helper, der pro Render einen frischen `QueryClient` + `QueryClientProvider` bereitstellt. Sonst crasht jeder useWorkflowSignalR-Hook-Test im `useQueryClient()`-Aufruf. |

### Audit-Closeout: drei finale Indexe (`4cc4acf`)

Die ursprüngliche SQL-Server-Readiness-Audit-Top-5 hatte fünf Index-Items plus
eine Bad-Index-Anmerkung. Drei waren in `4f2c3a5` umgesetzt; die letzten drei folgen
in dieser separaten Migration `IndexCleanupAuditAndStartedAt`:

| Aktion | Index | Wirkung |
|---|---|---|
| ➕ neu | `IX_WorkflowExecutions_StartedAt` (standalone) | Sweep-Pfade ohne WorkflowId-Filter (`ExecutionRetentionService`, `WorkflowStatsRefresher` 4× alle 5 min, `DashboardController execs24h`) verwenden jetzt `Index Scan` statt Bucket-Scan über `(Status, StartedAt)`-Composite |
| ➕ neu | `IX_AuditLog_Action_Timestamp` (Action ASC, Timestamp DESC) | `GET /api/audit?action=X` mit `OrderByDescending(Timestamp)` aus einem Index-Pass — kein Sort + Key-Lookup pro Match |
| ➖ ersetzt | `IX_AuditLog_Action` (single-column) | gedroppt; vom composite mitgedeckt |
| ➖ entfernt | `IX_Workflows_IsEnabled` (low-cardinality bool) | Optimizer wählte ihn nie (~50/50 Selektivität, kleine Tabelle), pure Insert-Overhead → gedroppt |

Migration manuell aufgeräumt: scaffolden gegen den aktiven Postgres-Provider erzeugte
seitenlange `AlterColumn`-Phantom-Operations für Postgres-spezifische Type-Mappings
(`uuid`, `text`, `timestamp with timezone`). Diese sind nicht Teil der Index-Änderung —
entfernt damit die Migration sauber vier Drop/Create-Index-Calls enthält.

Live gegen Postgres verifiziert via `EXPLAIN`. Damit ist die ursprüngliche
SQL-Server-Audit-Top-5 zu 100 % umgesetzt — Audit-Score SQL Server **6/10 → 9/10**.

### 500-Parallel-Tuning (`15c9c32`)

Nach User-Vorgabe "max 500 parallel jobs jemals" wurden die Engine-Werte mit
~20 % Burst-Headroom über 500 hochgezogen. Engine-Defaults sind seit `15c9c32`
unverändert; nur die DB-Pool-Größe wurde später separat noch von 480 → 800
nachgezogen (siehe Single-Source-Block oben).

| Setting | Vorher | Jetzt (live) | Begründung |
|---|---|---|---|
| `ExecutionDispatch.WorkerCount` | 512 | **600** | 500 + 20 % Headroom für Burst-Backpressure |
| `ExecutionDispatch.Capacity` | 1024 | **2048** | Queue-Buffer für Bursts |
| `Engine.MaxConcurrentSteps` | 512 | **600** | Match WorkerCount (stabiles Plateau aus Memory) |
| `Engine.Runspace.MaxRunspaces` | 512 | **768** | 50 % über typischem In-flight (~250-400) |
| `Engine.Runspace.MinRunspaces` | 128 | **256** | Mehr Pre-Warm-Polster gegen Cold-Start |
| `Threading.MinWorkerThreads/IO` | 512 | **768** | Match Runspace-Cap |
| Postgres `max_connections` | 600 | **600** unverändert | Reicht; App-Pool=800 + ~50 Overhead = 850 — minimal über `max_connections`, in der Praxis nie ausgereizt (Peak ~140 Conns bei 200-WF-Tests). Bei sustainted Pool>600-Auslastung müsste `max_connections` mit hoch. |

**500-Parallel-Stress-Test verifiziert** (Tech-Demo-XL, 114 Steps):

| Metrik | Wert |
|---|---|
| Total Wall-Clock | **226.7 s** (3 min 47 s) — bei UI-Anzeige ~4 min ist die Differenz nur ~13 s Eviction-Tail |
| Per-Run | min 205.3 / p50 215.9 / p95 217.9 / max 225.9 s |
| Total Steps | 500 × 114 = **57 000** |
| Throughput | **~251 steps/s sustained** |
| POST /execute Burst | launch-wall 1.65 s, p95-latency 1.5 s |
| Postgres-Conns Peak | 95 / 480 (Tech-Demo-XL ist delay/junction-heavy, kaum DB-Schreiblast) |

Vergleich Smoke-Test (10 Steps, dichte DB-Last) bei gleichem Tuning: 27.8 s
für 5500 Steps = 198 steps/s, Postgres-Pool peakte bei 481/480 (Pool exakt
ausgereizt, Server-Headroom auf 600).

### Lasttest-Ergebnisse (200 parallel × Tech-Demo-XL, 114 Steps)

Auf 20-Core / Postgres `max_connections=600` / Pool=480 nach allen Patches:

| Metrik | Wert |
|---|---|
| Total Wall-Clock | 103.5 s |
| Total Steps ausgeführt | 22 800 |
| Throughput | ~220 steps/s sustained |
| POST /execute Burst (200 par.) | Launch-wall 692 ms, p95-latency 555 ms, max 686 ms |
| `activeOnly` Latency unter 220 active execs | p50 13 ms, max 26 ms |
| Dashboard Latency unter Last | p50 22 ms, max 28 ms |
| step-stats Latency unter Last | p50 10 ms, max 11 ms |
| Postgres-Connections-Peak | 139 / 480 (29 %) |
| API Working Set Peak | 5.4 GB (RunspacePool) |

**Cold-Cache-Benchmarks (idle backend):**
- `activeOnly`: p50 1 ms, p95 3 ms (Index-Only-Scan via covering index)
- `dashboard` (8440 execs): p50 13 ms (DB-GROUP-BY)
- `step-stats` (114 steps × 900 execs): p50 159 ms (~102k rows, schmale Projektion)

**Bottleneck-Hierarchie heute:**
1. RunspacePool (256-400 Slots in-flight, primary)
2. POST /execute Burst-Backpressure (Dispatcher-Worker absorbiert, kein Drop)
3. CPU + DB-Connections + Hot-Endpoints sind nicht der Engpass

### Offene Frontend-Items

- F2: Route-Level `React.lazy` (Main-Bundle 1.79 MB → ~700 KB) + `manualChunks` + Material-Symbols-Font-Subset (1.1 MB eager)
- WorkflowEditorPage 1661-Zeilen-Refactor (Code-Health, kein Perf)

### Offene Backend-Items (alle Größe S–M)

- `ExecutionRetentionService`: `ExecuteDeleteAsync` statt Load+RemoveRange wenn Archive disabled
- 6 Stellen `new JsonSerializerOptions` → shared static (DatabaseTrigger, ReturnData/ForEach/Junction/Xml)
- `StepRunner` Logger-Scope: `LoggerMessage.DefineScope` statt `new Dictionary` (3× pro Step)
- `HubRevocationSweeper` inkrementell statt Full-Sweep alle 30s

(`GetStepHealth` war in einer früheren Version dieser Liste — inzwischen mit `Take(20)` + schmaler `(StepId, Status, StartedAt)`-Projektion ausreichend schlank, [`ExecutionsController.cs:237`](../src/NodePilot.Api/Controllers/ExecutionsController.cs#L237).)

---

## Anti-Pattern: Eager RunspacePool-Pre-Warm (Session 2026-05-04, verworfen)

Versuch (verworfen via revert in `ba2a7f1`): das Cold-Start-Tail des ersten Bursts nach API-Restart eliminieren, indem alle `MinRunspaces` Runspaces aktiv beim Boot via IHostedService instanziiert werden (statt das SDK-Default-Verhalten "lazy on-demand bis zur Last"). Plus `MinRunspaces` von 256 auf 768 hochziehen damit der Pool nicht auf das niedrigere Plateau zurückschrumpft.

**Ergebnis: 28 % Wall-Clock-Regression** beim 500-parallel-Test (Tech-Demo-XL, 114 Steps).

| Variante | Total Wall-Clock | Threads Peak | WS Peak |
|---|---|---|---|
| Lazy, `MinRunspaces=256` (Default) | **226.7 s** | 906 | 1141 MB |
| Eager Pre-Warm + `MinRunspaces=768` | **290.7 s ❌** | 2211 | 6864 MB |
| Lazy, `MinRunspaces=256` (Verifikation nach Rollback) | 218.6 s ✓ | 2124 | 6066 MB |

**Warum Pre-Warm hier kontraproduktiv ist** (für Hardware mit ~20 Cores):

1. **OS-Thread-Kosten dominieren über Runspace-Init-Kosten.** 768 always-warm Runspaces halten dauerhaft ~1300 zusätzliche OS-Threads aktiv. Bei ~20 CPU-Cores wird der Scheduler-Context-Switch-Overhead messbar — die echte Workload kriegt weniger CPU.
2. **Pool wächst unter Last sowieso organisch.** Der Verifikations-Test zeigt: bei 500 parallel klettern Threads auf 2124 und WS auf 6 GB — egal ob Pre-Warmed oder Lazy. Der Pool fasst die ~750 Runspaces, die er für 500 Workflows braucht, *innerhalb der ersten Sekunden* on-demand auf. Pre-Warm spart dadurch nichts, sondern fügt nur initialen Idle-Overhead hinzu.
3. **GC-Pressure durch große Heaps.** 768 vorab-instanziierte Runspaces erzeugen hunderte Heap-Roots → häufigere Gen2-Collections, Stop-the-world-Pausen treffen die Step-Latency unter Last.
4. **Pre-Warm-Phase saturiert ThreadPool.** Die 9 s mit 768 parallelen `$null`-Scripts beim Boot belasteten den ThreadPool kurz nach Start — zustand der erst abklingen muss bevor Workflows sauber laufen.

**Was Memory von früher dazu sagt** (`MinRunspaces=128`): *"Beim ersten runScript nach API-Start werden 128 Runspaces auf einmal aufgebaut (~5–10 Sek einmalig)"* — das war der akzeptable Sweet-Spot. Bei `MinRunspaces ≤ ~256` ist Pre-Warm-Vorteil > Idle-Overhead. Bei deutlich mehr kippt das Verhältnis.

**Lehre:**

> **Wenn der nächste "lass uns Pre-Warm bauen oder MinRunspaces hochziehen"-Versuch kommt: erst mit dem hier dokumentierten 500-parallel-Test verifizieren, bevor committen.** Das bauchgesteuerte "warmer Pool muss schneller sein" ist hier konkret falsch.

Aktuelle Production-Config ist bewusst lazy: `MinRunspaces=256, MaxRunspaces=768`. Pool wächst on-demand auf den tatsächlichen Bedarf, schrumpft auf 256 zurück im Idle. Kein IHostedService.

---

## 500-Job-Stress-Test Iteration (Session 2026-05-06, autonom)

Branch [`perf/throughput-iterate-2026-05-05-auto`](#) · Commit `75224cd`. Ziel war −10 % gegenüber einer vom User berichteten Baseline von **179 s**. Erreicht: **161–163 s** auf frischem Server (∼9 %, knapp unter Ziel). Gemessen am Median einer eigenen 3-Run-Baseline (168 s) liegt der Win bei ~3 %, mit hoher Varianz. Die Session war wertvoller für ihre **Ausschlussbefunde** als für die paar Prozent Gewinn — vier Tuning-Hebel die intuitiv plausibel klingen, sind hier aktiv schädlich.

Stress-Profil identisch zu den älteren Sessions: [`scripts/stress-test/launch-50-master.py`](../scripts/stress-test/launch-50-master.py) mit `NODEPILOT_STRESS_PARALLEL=500`, Workflow `NodePilot Tech-Demo XL` (114 Nodes / 190 Edges, 24 Targets gegen `localhost`-In-Process-Bypass).

### Was funktioniert hat (committed)

| Datei | Änderung | Effekt |
|---|---|---|
| [`OutputRedactor.cs:98-105`](../src/NodePilot.Engine/Security/OutputRedactor.cs#L98-L105) | **Fast-Path:** vor den 16 compiled Regex-Pässen ein `IndexOf`-Probe gegen die Trigger-Substring-Liste (`password`, `token`, `Authorization`, `-----BEGIN`, `AKIA`, `eyJ`, `xox`, `glpat-`, `ghp_`, …). Inputs ohne irgendeinen Trigger gehen direkt zurück. Custom-Patterns aus `Logging:Redaction:Patterns` erzwingen weiterhin den Slow-Path. | In-Noise auf der 500-WF-Wall, aber jeder Step-Output (stdout + stderr + OutputParameters + TraceOutput) flutet hier durch. Bei 57 000 Step-Outputs pro Stress-Test entlastet der Probe vermutlich CPU-Time, die unter Saturation woanders fehlt — schwer separat messbar. **Risiko:** wenn ein Custom-Trigger neue Pattern-Form ergänzt wird, muss `DefaultTriggerKeywords` angepasst werden, sonst rutschen Matches durch. Tests in [`OutputRedactorTests.cs`](../tests/NodePilot.Engine.Tests/Security/OutputRedactorTests.cs) decken jeden Default-Pattern ab. |
| [`MachineResolver.cs:11-21,42-48`](../src/NodePilot.Engine/Execution/MachineResolver.cs#L11-L48) | **Negative-Cache** (process-wide `ConcurrentDictionary<string, DateTime>`, TTL 30 s): wenn ein `targetMachineId`-String zur Ad-hoc-Fallback-Maschine resolvet (= keine `ManagedMachine` mit dieser ID/Hostname/Name in der DB), wird der negative Befund gemerkt. Hits gegen registrierte Maschinen werden **nicht** gecacht (EF-Tracking-Semantik braucht frisch tracked entities pro Scope). Invalidierung: `MachineResolver.InvalidateCache()` (Hook für Test/Admin nach Machine-Insert). | Bei einem 500-WF-Run gegen `localhost` werden ~12 000 redundante `Find` + `FirstOrDefault` SELECTs eliminiert. Per-Hit-Kosten waren 1–2 ms, also ~12–24 s kumulativ über alle Steps; parallelisiert über 600 In-Flight-Slots ergibt das ein paar 10 ms Wall-Clock-Ersparnis. Marginal, aber kostenfrei. |
| [`SubWorkflowLimiter.cs`](../src/NodePilot.Engine/Activities/SubWorkflowLimiter.cs) | **`ChildSemaphore` 64 → 128** (process-wide Cap auf gleichzeitige Sub-Workflow-Aufrufe). Default-Annahme bisher: 64 reicht überall. Tatsächlich: bei 500 Parents × 3 `startWorkflow`-Calls = 1500 Sub-Workflow-Invocations, die seriell durch 64 Slots fließen. | r1-Win 161.6 s vs. 162.4 s default — innerhalb der Run-zu-Run-Varianz. Begründung trotzdem stichhaltig (siehe Iteration 10 unten zur Falle, das nicht auf 600 zu heben). Konstante seit 2026-05-08 in `SubWorkflowLimiter` extrahiert (vorher private in `StartWorkflowActivity`). |
| [`appsettings.json`](../src/NodePilot.Api/appsettings.json) | `Logging:LogLevel:Default = "Warning"` (war `Information`) | ~3 % über 3 Runs. Serilog-Filter ist billig, aber bei 57 000 Steps × mehreren `LogInformation`-Calls pro Step pro Engine-Layer wird auch das billige messbar — gerade wenn Saturation-bedingt jede CPU-µs woanders gebraucht wird. |

### Was getestet und verworfen wurde

Mehr Permutationen als hier wirklich helfen — die wichtigste Erkenntnis ist warum sie nicht gewinnen.

**Iter 1 — Concurrency-Caps hochsetzen (`MaxConcurrentSteps` 600→1500, `Runspace` 768→1500, `Threading` 768→1024):** **240 s, 42 % schlechter.**

> Naive Erwartung: mehr Slots → mehr Parallelität → schneller. Realität: das System saturiert *downstream* der Engine. Bei 500 parallelen Workflows × mehr-Steps-pro-Slot werden zusätzliche Steps gleichzeitig in den **CIM-Provider** ([`Get-CimInstance Win32_OperatingSystem`](../scripts/test-master-all-activities.json) wird im Demo-Workflow von `collect-host` gerufen) und in **Process-Spawn** (`startProgram` mit `cmd.exe`/`powershell.exe`) gepumpt. CIM-Provider serialisieren intern, und die OS-Process-Spawn-Rate wird vom CSRSS gedrosselt. Mehr Slots → längere Queue dort → höhere Per-Step-Latenz → Engine-Cap wird nutzlos hoch.
>
> **Lehre:** Concurrency-Cap hochziehen ist nur dann wirksam, wenn der nächste Layer den Druck aufnehmen kann. Bei `localhost`-Workflows ist der nächste Layer das OS, und das macht keinen Deal mit Engine-Settings.

**Iter 2 — Concurrency-Caps reduzieren (`MaxConcurrentSteps` 600→300, `Runspace` 768→384, `Threading` 768→512):** **184 s, 9 % schlechter.**

> Spiegelbild: weniger Slots → weniger Druck → schneller je Step → schneller insgesamt. Realität: 500 Workflows × 1 Step gleichzeitig brauchen *mindestens* 500 Slots, sonst stehen sie am Engine-Gate. Bei 300 Slots wartet jeder Workflow durchschnittlich 200 Slot-Releases. Defaults (600/768/768) sind in beide Richtungen nahe Optimum — sie gleichen genau zwischen "alle 500 WFs haben einen Slot wenn sie ihn brauchen" und "nicht so viel parallel dass das OS thrashed".

**Iter 4 — DB-Pool aufbohren** (`Maximum Pool Size` 800→1500, `Database:PoolSize` 1024→2048, `No Reset On Close=true`): **null Effekt.**

> Postgres ist mit den Defaults nicht der Flaschenhals. Während Stress-Tests sind nur ~85 von 480 Pool-Slots belegt (siehe Bottleneck-Tabelle aus der 2026-05-04-Session). Mehr Pool-Slots zu reservieren ist reine Luftbuchung. `No Reset On Close` (Postgres-spezifisch — überspringt das `DISCARD ALL` beim Pool-Return) klingt wie ein Mikro-Win, ist aber mit dem üblichen `EF Core SaveChangesAsync`-Pattern (immer fresh Connection aus DbContext) im Rauschen.

**Iter 10 — `ChildSemaphore` von 64 auf 600 heben (statt auf 128):** **189 s, 16 % schlechter.**

> Hier wird's interessant. Die Annahme "1500 Sub-WF-Calls hinter 64 Slots = serieller Wait" stimmt — aber den Cap auf 600 zu heben verschiebt nur den Saturation-Punkt: jetzt laufen 500 Top-Level + bis zu 600 Sub-Workflows fast gleichzeitig, drücken alle in den Engine-Step-Gate (`MaxConcurrentSteps=600`), in den DbContext-Pool (1024 Pool aber mit Connection-Limit 800), in den Runspace-Pool, und in den ThreadPool. Die saubere Sweet-Spot-Beobachtung war 128 — gerade hoch genug dass die Queue Druck ablassen kann, niedrig genug dass kein Downstream thrashed.
>
> **Lehre:** ein Process-wide Semaphore wie `ChildSemaphore` ist immer gegen die schwächste downstream Resource zu dimensionieren, nicht gegen den theoretischen Bedarf. Erhöhen ohne den Engine-Step-Gate gleichzeitig anzufassen heißt nur, den Stau eine Schicht weiter zu schieben.

**Iter VACUUM ANALYZE auf StepExecutions/WorkflowExecutions:** **null Effekt.** Autovacuum hatte `n_dead_tup` ohnehin schon nahe 0 gehalten. Erwähnenswert weil der erste Reflex bei "DB wird langsamer über Runs" oft "VACUUM" ist — bringt hier nichts, weil Postgres das schon erledigt.

### Nicht umgesetzt (zu riskant für Auto-Mode)

- **Batch-Writes für `StepExecution`-Persists**: bei 57 000 Step-Save-Calls / Stress-Test pro `SaveChangesAsync` wäre Aggregation in einem 50-ms-Channel mathematisch attraktiv. Bricht aber die Live-Step-Anzeige (UI sieht Steps nicht in DB bevor der Batch flushed) und macht die `_runningExecutions`-Cancellation-Pfade komplizierter (Cancellation muss durch den Channel propagieren). Zu hohes Architektur-Risiko für eine Auto-Mode-Iteration.
- **Workflow-Definition rewrite** (z.B. `Get-CimInstance Win32_*` in `collect-host` durch gecachten/aggregierten Aufruf ersetzen): wäre die einzige Stellschraube mit Aussicht auf 2-stelligen Win, aber außerhalb des Engine-Scopes. Anmerkung im memory file für künftige Sessions.
- **OS-Tuning** (`MaxShellsPerUser`, CSRSS-Desktop-Heap): per-Host, nicht im Engine-Repo regelbar.

### Variance & DB-Drift (Methoden-Hinweis für künftige Iterationen)

Die DB-Tabelle `StepExecutions` wuchs während dieser Session von ~3.0 M auf ~3.2 M Rows — jedes 500-WF-Stress fügt ~57 000 hinzu. **Run-3 nach demselben Server-Start war konsistent ~10 s langsamer als Run-1**, in jeder Iteration. Folge: Median über 3 sequenzielle Runs vermischt Code-Effekt und Drift-Effekt. Faire Iter-Vergleiche brauchen entweder (a) `TRUNCATE` zwischen Runs (vom Sandbox-Layer als unauthorisiert blockiert — korrekt, weil destruktiv auf Stateful Service), oder (b) `git stash`-Reset und Server-Restart **zwischen jedem einzelnen Run** und nur Run-1 vergleichen. Letzteres ist die hier am Ende angewandte Methodik.

### Hot-Path-Beobachtungen (single vs. 500-parallel)

Ein einzelner Tech-Demo-XL-Run liegt bei **21 s** (kritischer Pfad — Delays und Sub-Workflows). Bei 500-parallel werden die einzelnen Steps massiv langsamer, was den Top-down-Tuning-Spielraum erklärt:

| Step | Single-shot | 500-parallel | Slowdown | Ursache |
|---|---:|---:|---:|---|
| `collect-host` (`Get-CimInstance Win32_OperatingSystem` + `Win32_ComputerSystem`) | 158 ms | 20 900 ms | **132×** | CIM-Provider serialisiert per-Host |
| `r11-prog-ps` (`powershell.exe -NoProfile -Command "Write-Output 'ps-ok'"`) | 509 ms | 5 100 ms | **10×** | OS-Process-Spawn-Rate (CSRSS) |
| `bot05-waitcond` (Script `$true`, intervalSeconds=2) | <1 s | 11 700 ms | groß | Pool-Wait erste PS-Eval beim Burst-Start |
| `r05-prog-cmd` (`cmd.exe /c echo …`) | <1 s | 6 500 ms | groß | Process-Spawn (siehe `r11-prog-ps`) |

Die 132×-Verlangsamung von `collect-host` ist die **dominante Engine-Decke** für diese Workflow-Topologie — und sie ist außerhalb der NodePilot-Engine. Solange ein Workflow CIM-Provider-Operationen auf demselben Host für 500 parallele Runs macht, wird der CIM-Provider zur Decke, und kein Engine-Tuning ändert daran etwas.

### Verifizierung des Status quo

| Run-1 (frischer Server, Postgres warm) | Wall-Clock |
|---|---:|
| Default-Config + ohne Code-Änderungen | 162.4 s |
| Mit allen committeten Tweaks (Logging Warning + Redactor Fast-Path + Resolver Negative-Cache + ChildSem 128) | 161.6 s |

Δ ist innerhalb der Mess-Varianz. Die 9 % gegenüber der vom User berichteten 179-s-Initial-Baseline kommen wahrscheinlich primär vom Server-Restart selbst (90-Min-warm-laufender Server hatte mehr GC-Pressure / LOH-Fragmentierung als ein frisch gestarteter), nicht von den Code-Änderungen. **Die Code-Änderungen sind defensiv-billig, nicht durchschlagend.**

### Memory-Pointer

Detail-Findings (Tabellen, Iter-Logs) liegen zusätzlich in der Auto-Memory unter [`project_throughput_iterate_2026_05_06.md`](#) — primär für künftige Auto-Mode-Sessions, damit dieselben Hebel (`MaxConcurrentSteps` 1500, `ChildSemaphore` 600, DB-Pool 1500, VACUUM ANALYZE) nicht erneut probiert werden.
