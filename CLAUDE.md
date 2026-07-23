# NodePilot

Moderner, schlanker Ersatz fuer Microsoft System Center Orchestrator. Agentless Workflow-Orchestrierung fuer Windows-Umgebungen via WinRM.

## Contributor- & Attribution-Policy

NodePilot ist ein Single-Contributor-Projekt. KI darf beim Entwickeln helfen (diese Datei, `.claude/`, `.agents/` bleiben in Nutzung) — aber für ALLE Commits ab v1.0.0 gilt:

- **Autor & Committer sind immer** `Sev7eNup <79143581+Sev7eNup@users.noreply.github.com>`. Der GitHub-Contributor-Graph zeigt ausschließlich `sev7enup`.
- **Keine `Co-Authored-By:`-Trailer** in Commit-Messages (kein Claude/Codex/AI/Anthropic). Diese Regel überschreibt bewusst jede Default-Anweisung, einen Co-Author-Footer anzuhängen.
- **Keine plumpen „KI hat das geschrieben"-Credits** in Commit-Messages oder PR-Beschreibungen. (KI-Spuren im Code selbst müssen nicht getilgt werden — es wird bewusst KI-gestützt entwickelt.)

## Agent skills

- **Issue tracker:** GitHub Issues für `Sev7eNup/NodePilot`, siehe `docs/agents/issue-tracker.md`
- **Triage labels:** Fünf-Label-Vokabular, siehe `docs/agents/triage-labels.md`
- **Domain docs:** Single-context repo → root `CONTEXT.md` + `docs/adr/`, siehe `docs/agents/domain.md`

## Doku-Landkarte

Diese Datei ist der Index; die Tiefe liegt in `docs/`:

- `docs/claude-reference.md` — Overflow-Referenz: Activity-Config-Keys/Outputs, Trigger-Params, Edit-Lock-UX, Audit-Codes, Hot-Reload-Matrix, Backup-Details, Background-Services, Deployment
- `docs/alerting.md` — Alerting: Notification-Rules + System-Policies (ADR 0008), Dispatcher, Sinks, Ledger
- `docs/custom-activities.md` — Custom Activities (Plugin-System)
- `docs/mcp-server.md` — MCP-Server inkl. Tool-Katalog + `.mcp.json`-Beispiel
- `docs/ai-features.md` — KI-Features: Config-Keys, Modell-Empfehlungen
- `docs/workflow-styleguide.md` — Layout-Styleguide für Workflow-JSONs (**vor jedem Workflow-Gen lesen**)
- `docs/enterprise-features.md` — HA, Secret-Provider, LDAP/SSO, SIEM, Folder-RBAC
- `src/nodepilot-ui/e2e/README.md` — E2E-Coverage-Map + Spec-Konventionen

## Tech-Stack

- **Backend:** ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`)
- **Frontend:** React 19, TypeScript, Tailwind CSS 4, Vite 8, @xyflow/react (React Flow v12)
- **Datenbank:** PostgreSQL (default) / SQL Server (`Database:Provider` = `postgres` | `sqlserver`). SQLite nur als Test-In-Memory-Backend.
- **Remote Execution:** PowerShell SDK / WinRM, agentless. `Remote:Provider`: `winrm` (default) | `noop` (`noop` muss per `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1` quittiert werden, sonst Boot-Abbruch)
- **Real-time:** SignalR (`/hubs/execution`)
- **Auth:** JWT Bearer + BCrypt, Rollen: Admin / Operator / Viewer
- **Scheduling:** Quartz.NET + `TriggerOrchestrator` in `NodePilot.Scheduler`
- **Logging:** Serilog. Format via `Logging:Format`: `text`|`cmtrace`|`json`|`ecs-json` (ECS 1.x für SIEM, siehe `docs/siem-logging.md`). Support-Log: File + DB-Projektion
- **Observability:** OpenTelemetry opt-in. Prometheus-Scrape via Config.
- **Hosting:** Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`
- **MCP-Server (opt-in):** `nodepilot-mcp` (stdio) — AI-Agent steuert/editiert Workflows über 99 Tools, HTTP-only gegen die REST-API
- **Enterprise (opt-in):** Active/Passive HA (`Cluster:Enabled`), pluggable Secret-Provider (`Secrets:Provider` = `Dpapi`|`AesGcm`), LDAP/Windows-SSO, ECS-JSON-SIEM, Folder-RBAC

## Solution-Struktur

```
src/
  NodePilot.Core/       # Domain Models, Interfaces, Enums (keine Abhaengigkeiten)
  NodePilot.Ai/         # LLM-Stack: ILlmClient/OpenAI-Transport + SSRF-Guard + LlmClientFactory, Prompt-Katalog, Script-/Workflow-Gen + Chat-Assistent-Services (nur Core-Abh.; von Api UND Engine genutzt)
  NodePilot.Data/       # EF Core DbContext, CredentialStore, MigrationBootstrapper
  NodePilot.Remote/     # WinRM Session Factory + Session, NoOp-Factory
  NodePilot.Engine/     # WorkflowEngine, ActivityRegistry, Activities, Trigger
  NodePilot.Scheduler/  # TriggerOrchestrator + ITriggerSource-Impl.
  NodePilot.Telemetry/  # OTel-Setup, Constants, Options, PrometheusClient
  NodePilot.Api/        # ASP.NET Core Host, Controller, SignalR Hub, DTOs
  NodePilot.Cli/        # `np` CLI — dotnet global tool, Spectre.Console.Cli
  NodePilot.Mcp/        # `nodepilot-mcp` MCP-Server — dotnet global tool, HTTP-only
  nodepilot-ui/         # React SPA (Vite + Tailwind + React Flow)
  nodepilot-docs-ui/    # Doku-Website (Vite + React SPA) — EIGENER Markdown-Korpus unter content/ (kuratiertes Rewrite, KEIN Render von docs/)
tests/
  NodePilot.Engine.Tests/   # xUnit
  NodePilot.Ai.Tests/       # xUnit (LLM-Client-Factory, Endpoint-Guard, Secret-Redactor)
  NodePilot.Data.Tests/     # xUnit
  NodePilot.Api.Tests/      # xUnit
  NodePilot.Cli.Tests/      # xUnit + WireMock.Net
  NodePilot.Mcp.Tests/      # xUnit + WireMock.Net + stdio-Prozess-MCP-Smoke
  NodePilot.LoadTests/      # eigenes Last-Harness
  NodePilot.TestCommons/    # geteilte Test-Infrastruktur
```

**Dep-Graph:** `Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry` | `Engine -> Ai, Data, Remote, Core, Telemetry` | `Ai -> Core` (LLM-Stack, sitzt unter Engine, damit Api+Engine ihn teilen) | `Data -> Core` | `Remote -> Core` | `Telemetry -> Core` | `Cli -> Core` (HTTP-only) | `Mcp -> Core` (HTTP-only, MCP-Server)

## Projekt starten

```powershell
# Postgres (lokales Dev-Cluster)
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -l 'C:\NodePilot-Postgres\data\postgres.log' -w

# Backend (Port 5000) — schlägt fehl wenn Postgres nicht läuft
cd src\NodePilot.Api; dotnet run --urls "http://localhost:5000"

# Frontend (Port 5173, Proxy auf Backend)
cd src\nodepilot-ui; npm run dev
```

Erster Login erstellt Admin-Account. **Immer erst `pg_ctl start`, dann `dotnet run`.**

**Für Claude:** Dev-Mode verwenden. **API-Neustarts (stop+rebuild+start) sind jederzeit ohne Rückfrage erlaubt** — DLL-Locks sind normal. Vorab PID via `Get-NetTCPConnection -LocalPort 5000` finden, dann `Stop-Process` + Rebuild + Start. `npm run dev` kaputt → `npm install`. Deploy-Skripte unter `deploy/` **niemals** ausführen.

## Datenbank

Zwei Provider, umschaltbar über `Database:Provider`:

| Provider | Wert | ConnectionString-Key |
|---|---|---|
| PostgreSQL (Default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`.
- **Neue Migration:** `dotnet ef migrations add <Name> --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext`. **Pflicht-Postprocessing:** alle `type: "..."`-Annotations entfernen.
- Schema-Änderungen IMMER per EF-Migration. Kein DDL-Hotpatching.
- Credentials mit DPAPI verschlüsselt (`Credentials:DpapiScope`).
- **DB-TLS strikt (default):** `DatabaseTlsBootValidator` bricht den Boot ab, wenn die Connection den Server nicht verifiziert (`Encrypt=Strict`/`TrustServerCertificate=False` bzw. `SSL Mode=VerifyFull`). Dev-only-Escape: `Database:AllowInsecureTls=true` (nur Loopback).

Retention-Services im Scheduler: Execution (30d), AuditLog (365d), WorkflowVersions (50/Workflow), SupportEvents (90d), Notifications (90d) — opt-out via `Retention:*:Enabled: false`. IdempotencyKeys (24h, fixe TTL) läuft immer.

## API Endpoints

| Bereich | Endpoints |
|---|---|
| Workflows | `GET/POST/PUT/DELETE /api/workflows`, `POST /{id}/execute\|duplicate\|enable\|disable\|cancel-all\|lock\|unlock\|publish\|force-unlock` |
| Versionen | `GET /{id}/versions`, `GET /{id}/versions/{v}`, `POST /{id}/rollback/{v}` |
| Contract | `GET /{id}/contract`, `GET /by-name/{name}/contract` |
| Step-Test | `POST /{id}/steps/{stepId}/test`, `GET .../test-context`, `GET .../test-context/runs` |
| Import/Export | `GET /export`, `GET /{id}/export`, `POST /import`, `POST /import-scorch` |
| Executions | `GET /api/executions`, `GET /{id}`, `GET /{id}/steps`, `POST /{id}/cancel\|retry\|resume` |
| Coverage / Step-Telemetrie | `GET /{id}/coverage?windowDays=N`, `GET /{id}/step-health`, `GET /{id}/step-stats?windowDays=N` |
| Machines | `GET/POST/PUT/DELETE /api/machines`, `POST /{id}/test` |
| Credentials | `GET/POST/PUT/DELETE /api/credentials` |
| Global Variables | `GET /api/global-variables` (Admin/Op), `POST/PUT/DELETE` + `POST /{id}/move-folder` (Admin); optionales `folderId` auf Create/Update |
| Global-Variable-Ordner | `GET /api/global-variable-folders` (Admin/Op), `POST` + `PUT/DELETE /{id}` + `POST /{id}/move` (Admin) — Baum ohne RBAC, rein organisatorisch |
| Auth | `POST /api/auth/login\|logout\|windows`, `GET /api/auth/me\|methods` |
| Audit | `GET /api/audit` (Admin, Cursor-Pagination), `GET /api/audit/export?format=csv|ndjson` |
| Trigger | `POST /api/trigger/{workflowNameOrId}` (`X-Api-Key`) |
| Webhooks | `POST\|GET\|PUT\|DELETE /api/webhooks/{workflow}/{path}` (Verb muss `webhookTrigger.method` matchen) |
| Observability | `GET /api/observability/config\|query\|query_range\|summary` |
| Backup | `GET /api/backup/manifest`, `POST /api/backup/export`, `POST /{preview\|restore}` (Admin, multipart für preview/restore) |
| Users | `GET/POST /api/users`, `PUT/DELETE /api/users/{id}` (Admin) |
| Maintenance Windows | `GET /api/maintenance-windows` + `GET /{id}` + `GET /affecting/{workflowId}` (Admin/Op), `POST`, `PUT/DELETE /{id}` (Admin) |
| Alerting (Custom) | `GET /rules` + `GET /rules/{id}` + `GET /deliveries` (Admin/Op), `POST/PUT/DELETE /rules[/{id}]` + `POST /rules/{id}/enable\|disable\|test-fire` (Admin), `POST /preview-filter` (Admin/Op) — nur `Kind=Custom`, Basis `/api/alerting` |
| Alerting (System-Policies, ADR 0008) | `GET /api/alerting/system/catalog` + `GET /policies[/{id}]` (Admin/Op), `POST/PUT/DELETE /policies[/{id}]` + `POST /policies/{id}/enable\|disable\|test-fire` (Admin), `POST /preview` (Admin/Op) — nur `Kind=System` |
| Shared Folders | `GET/POST /api/shared-workflow-folders`, `PUT/DELETE /{id}`, `POST /{id}/move`, `POST /api/workflows/{workflowId}/move-folder` |
| Folder-Permissions | `GET/POST /api/shared-workflow-folders/{folderId}/permissions`, `PUT/DELETE /{permissionId}` |
| Settings | `GET /api/admin/settings` + `GET\|PUT /{section}`, `GET /status\|system-info`, `POST /test/smtp\|test/llm` (Admin) |
| Dashboard | `GET /api/stats/dashboard` |
| Operations (Live-Ops Mission Control) | `GET /api/operations/graph` (alle Rollen, RBAC-folder-scoped; Snapshot = Nodes/Edges/Running/Recent; Live-Deltas via SignalR `ops-feed`-Gruppe) |
| DB-Admin | `GET /api/dbadmin/tables`, `GET\|PATCH\|DELETE /tables/{name}/rows`, `GET /info`, `POST /query` (Admin) |
| Diagnostics | `GET /api/diagnostics/support-log\|support-log/download\|support-events\|support-events/export` (Admin) |
| Activity-Catalog | `GET /api/activity-catalog` (Designer-Palette, statische Built-ins) |
| Custom Activities | `GET /api/custom-activities` (alle Rollen; `?includeDisabled=true` Admin/Op), CRUD + `POST /{id}/rollback/{v}` (Admin/Op; enabled nur Admin), `POST /{id}/enable\|disable` (Admin), `GET /export` + `POST /import` (Admin/Op) |
| Scheduler | `GET /api/triggers/schedule/next-fires` |
| System | `GET /api/system/host-info` (alle Rollen — Host-Identität des API-Hosts, prozess-gecacht) |
| AI | `POST /api/ai/generate-script\|generate-workflow` (Admin/Op), `POST /api/ai/chat` (alle Rollen), `POST /api/ai/chat/applied` + `GET /api/ai/chat/activity/{workflowId}` (Admin/Op), `POST /api/ai/knowledge/ask` (alle Rollen) + `GET /api/ai/knowledge/capabilities` — opt-in, siehe KI-Features |
| Secrets | `POST /api/secrets/reencrypt` (Admin) |
| Health | `GET /healthz/live`, `GET /healthz/ready`, `GET /healthz/leader` (HA-Leader-Probe, fail-closed) (AllowAnonymous) |

## Workflow-Kontrollfluss

| Endpoint | Semantik |
|---|---|
| `POST /execute` | Startet Lauf. Body: `{"parameters": {}, "timeoutSeconds": N, "debug": bool}`. 202 + ExecutionId. |
| `POST /enable` / `/disable` | Kill-Switch. `enable` erfordert kein Lock (423 sonst). `disable` ignoriert Locks. |
| `POST /cancel-all` | Cancelt alle Running-Executions des Workflows. |
| `POST /executions/{id}/cancel\|retry\|resume` | Einzelner Lauf. Resume-Body: `{"mode": "continue"\|"stepOver"\|"stop", "overrides": {}}`. |

**Disable+cancel-all = Quarantäne.**

## Edit-Lifecycle (SCOrch-style Edit-Lock)

Workflows haben einen per-User-Edit-Lock (`CheckedOutByUserId` + `CheckedOutAt`). Mutierende Endpoints liefern `423 Locked` wenn Caller nicht Lock-Owner. `Disable` ist **nicht** lock-gegated (Incident-Kill-Switch).

| Endpoint | Verhalten |
|---|---|
| `POST /lock` | Atomar `IsEnabled=false` + Lock-Fields setzen. 409 wenn schon gelockt. |
| `POST /unlock` | Lock-Fields auf null. `IsEnabled` bleibt unverändert. |
| `POST /publish` | Atomar: Save + `IsEnabled=true` + Unlock. |
| `POST /force-unlock` | Admin-only. Bricht fremden Lock. |

UX-Flow und Button-State-Matrix: siehe `docs/claude-reference.md`. Kurz: `canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId`.

## Activity-Typen

"Remote" = `targetMachineId`/WinRM. "Engine-local" = im API-Prozess. `(controlFlow)` = Kategorie `ControlFlow` im backend `ActivityCatalog` (Palette-Achse, unabhängig vom Scope).

- **Remote:** `fileOperation`, `folderOperation`, `textFileEdit`, `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `powerManagement`, `scheduledTask`, `fileHash`, `zipOperation`
- **Engine-local:** `restApi`, `sql`, `emailNotification`, `delay`, `xmlQuery`, `jsonQuery`, `log`, `generateText`, `llmQuery` + controlFlow: `junction`, `forEach`, `decision`, `startWorkflow`, `returnData`
- **Hybrid:** `runScript`, `waitForCondition`

Config-Keys & Output-Semantik pro Activity: siehe `docs/claude-reference.md`.

**Retry pro Step:** `config.retry` mit `maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`.
**Execution-Timeout:** `timeoutSeconds` im Execute-Body + per-Step `config.timeoutSeconds`.
**Prozess-Isolation (`runScript`, nur lokal):** `config.isolated: true` → eigener Prozess in einem Windows Job Object (Crash-/Leak-Containment, keine verwaisten Prozesse), opt-in Caps `memoryLimitMb`/`maxProcesses`; No-Op auf dem Remote/WinRM-Pfad. Inheritable-Pipe-Handles gegen Cross-Inheritance geschützt (`ProcessSpawnCoordinator` serialisiert alle inheritable Spawns) + Bounded stdout/stderr-Drain nach Prozess-Exit (`Engine:IsolatedDrainGraceSeconds`, default 5 s) — verhindert „Execution hängt in Running" durch geleakte Pipe-Handles. Details: `docs/claude-reference.md`.

## Custom Activities (Plugin-System)

User-authored, PowerShell-backed Activities (UI: „Custom Nodes") — reine **runScript-Presets** (dieselbe Engine/Isolation/Marker-Capture/Redaction), keine zweite Script-Engine. Volle Doku: `docs/custom-activities.md`.

- **Dispatch:** Node-`activityType = custom:<key>` → ein einziger Sentinel-registrierter `CustomActivityExecutor`; Definition-Bezug im Config (`__customDefinitionId` authoritativ + `__customKey` Drift-Guard). Der Wrapper captured NUR die deklarierten Outputs (+ `exitCode`).
- **Governance:** Create/Edit/Delete = Admin+Operator **nur solange disabled** (Draft); Enable/Disable + Mutation enabled Defs = Admin-only. Latest-wins; jede Execution speichert `StepExecution.CustomActivity{Key,Version,Hash}`. Kein `secret`-Input-Typ — Secrets via `{{globals.X}}`/Credentials.
- **Architektur-Konvention:** Geteilte Facts-Schicht `NodePilot.Core.Activities.CustomActivityType`/`CustomActivityValidation`; Frontend-Spiegel `lib/customActivities.ts` (Runtime-Katalog via `useCustomActivityCatalog`). `activityCatalog.generated.ts` + Parity-Test bleiben **unberührt**.

## Alerting (Notification-Rules)

User-definierte Regeln, die bei passenden Ereignissen über Kanäle (SMTP / Generic-Webhook + HMAC) benachrichtigen. Opt-in **per Daten** (idle bis eine Regel existiert). Volle Doku: `docs/alerting.md`.

- **Zwei Arten:** Custom-Regeln (`Kind=Custom`, Execution-Events, Filter-AST = derselbe `ConditionEvaluator` wie Edge-Conditions) und System-Policies (`Kind=System`, ADR 0008 — 12 katalogisierte `ISystemAlertSource`s für Infra-/Signal-Alerts, ausgewertet vom `SystemAlertEvaluator`).
- **Kern:** Entität `NotificationRule` (+ Routes/Targets) + getrennte State-Tabellen (Suppression, Delivery-Ledger `NotificationDeliveryAttempt`, Dispatcher-Watermark). `NotificationDispatcher` (leader-gated, ~30 s) matcht → suppressed (Cooldown/Flap) → persistiert Pending-Attempt VOR jedem I/O → sendet (exactly-once pro `(rule, route, occurrence)`).
- **Governance:** Read Admin/Op; alle Mutationen + Test-Fire Admin-only; neue Regeln entstehen disabled. Secrets in Responses redigiert.
- **Frontend/CLI/MCP:** Seite `/alerts` (2 Tabs, wiederverwendeter `ConditionBuilder`), `np alerting` + `np system-alert`, MCP-Tools für beides.

## Architektur-Konventionen

- **Neue Activity:** Klasse in `Engine/Activities/`, `IActivityExecutor` implementieren — Auto-Discovery via `AddNodePilotActivities()` (scannt `NodePilot.Engine`), **keine** DI-Verdrahtung in `Program.cs` nötig. UI: Eintrag in `library/activityCategories.ts` (`buildActivityCategories`) + `*Config`-Komponente unter `properties/activities|triggers/` + Registrierung in `properties/activityConfigMap.ts` (eine Zeile — `PropertiesPanel.tsx` wird **nicht** editiert). Frontend-Katalog-Spiegel `lib/activityCatalog.generated.ts` ergänzen: Mirror von `NodePilot.Core.Activities.ActivityCatalog`, **kein** Codegen-Skript (von Hand pflegen) — `isRemote`/Timeout-Flags speisen die abgeleiteten `REMOTE_ACTIVITY_TYPES`/`TIMEOUT_ACTIVITY_TYPES`; `ActivityCatalogFrontendSyncTests` erzwingt Gleichstand mit dem Backend-Katalog. Downstream-Outputs in `describeNodeOutputs` in `lib/upstreamVariables.ts`.
- **Neuer API Controller:** In `Api/Controllers/`, DTOs in `Api/Dtos/`. Parallel CLI-Command anlegen (siehe unten).
- **Neue Frontend-Seite:** In `nodepilot-ui/src/pages/`, Route in `App.tsx`
- **Neuer Custom Node:** In `nodepilot-ui/src/components/designer/nodes/`, in `nodeTypes` Map
- **Frontend-Stack:** UI-Strings über `react-i18next` (Namespaces je `src/i18n/locales/{de,en}/`, Default DE) — neue sichtbare Strings in **beide** Sprachen. Client-State via Zustand-Stores (`src/stores/`), Server-State via TanStack React Query (`refetchOnWindowFocus:false`, SignalR invalidiert Caches).
- **Models/Interfaces:** Immer in `NodePilot.Core`
- **Doc-Sync:** Feature-Änderungen halten alle Doku-Flächen synchron — README, `docs/*.md`, `E2ETests.md` + `e2e/README.md` und die Doku-Website `src/nodepilot-docs-ui/content/` (eigener kuratierter Korpus, kein Render von `docs/`).

## Workflow-JSON Format

```json
{
  "nodes": [{
    "id": "step-123", "type": "activity",
    "position": { "x": 100, "y": 200 },
    "data": {
      "label": "Check Disk", "activityType": "runScript",
      "targetMachineId": "guid", "credentialId": null,
      "outputVariable": "diskCheck",
      "config": { "script": "Get-PSDrive C", "timeoutSeconds": 60 }
    }
  }],
  "edges": [{
    "id": "e1", "source": "step-123", "target": "step-456",
    "type": "labeled",
    "data": {
      "label": "On Success", "condition": "step-123.success", "disabled": false,
      "controlPoints": { "cp1x": 240, "cp1y": 200, "cp2x": 360, "cp2y": 200 }
    }
  }]
}
```

`data.controlPoints` überschreibt Auto-Routing. Fehlt es → bestehendes Routing greift. Implementierungsdetails: siehe `docs/claude-reference.md`.

Layout-Styleguide für Workflow-JSONs: **zuerst** `docs/workflow-styleguide.md` lesen. Referenz-Beispiel: `scripts/test-master-all-activities.json`.

## Datenbus / Variable Resolution

- `{{varName.output}}` — Stdout
- `{{varName.error}}` — Stderr
- `{{varName.success}}` — Step-Erfolg (`"true"` / `"false"`)
- `{{varName.param.xxx}}` — OutputParameter
- `{{globals.NAME}}` — Globale Variable
- Kein `outputVariable` → Step-ID wird verwendet: `{{step-123.output}}`

**Contract-Garantie:** Nur diese vier Tails (`output`, `error`, `success`, `param.X`) werden aufgelöst — andere Tails bleiben als Literal. Unresolved → granulare Diagnostik (StepRunner T-7.1).

**Strukturierter Output:** `runScript` captured automatisch deklarierte Variablen als `param.*`. `$hostName = ...` → `{{step.param.hostName}}`.

**RunScript Auto-Quoting:** `{{step.output}}` wird als Single-Quoted String eingesetzt. Im Script `$x = {{step.output}}` schreiben, NICHT `$x = '{{step.output}}'`.

**RunScript Erfolg (fehler-basiert, einheitlich über alle Engines):** Ein Step scheitert **nur** bei einem terminierenden PowerShell-Fehler (`throw` / `Write-Error` unter dem `Stop`-Wrapper). Ein `exit N` macht den Step **nicht** rot. Opt-in `config.successExitCodes` (komma-separiert) macht non-zero Exit-Codes wieder zum Fehlschlag. Der Exit-Code liegt immer als `{{step.param.exitCode}}` an. **Engine-Asymmetrie:** `successExitCodes`/`param.exitCode` greifen für Native-Command-Codes (`$LASTEXITCODE`) in allen Engines; ein script-eigenes `exit N` ist nur im Prozess/isoliert-Pfad als Wert sichtbar (Runspace kann `exit` nicht beobachten → `0`). Impl: Wrapper-`try/catch` + `###NODEPILOT_ERROR###`-Marker, zentrales Gating in `RunScriptActivity`.

## Edge Conditions

- `stepId.success` / `stepId.failed` — Shortcut
- `null` / leer — Immer
- `disabled: true` — übersprungen
- `conditionExpression` — Typ `comparison` (==, !=, <, >, <=, >=, contains, startsWith, endsWith, matches, isEmpty, isNotEmpty, isTrue, isFalse), `group` (AND/OR), `not`. Operanden: `variable` oder `literal`.

## Sub-Workflows & Contract

`startWorkflow` ruft Child-Workflow auf (frischer DI-Scope) — jeden enabled Workflow, unabhängig vom Trigger-Typ (die übergebenen `parameters` landen als `manual.*` im Child-Run). `waitForCompletion: true` (default) → Parent blockiert, Child-`returnData` als `param.*` gespiegelt. Max Call-Depth: 10.

Contract-Derivation: `GET /{id}/contract` liefert Inputs aus `manualTrigger.parameters` + Outputs aus `returnData.data`-Keys + System-Outputs (`__executionId`, `__status`, `__workflowId`, `__workflowName`). By-name-Lookup (API + Engine + Trigger/Webhook): exact-case gewinnt, sonst case-insensitive; mehrdeutige Namen → 409 bzw. Step-Fehler (`WorkflowNameResolver`). Semantik-Details: `docs/claude-reference.md`.

## Trigger

| Trigger | Backing |
|---|---|
| `scheduleTrigger` | Quartz cron |
| `fileWatcherTrigger` | FileSystemWatcher |
| `databaseTrigger` | Timer + SELECT-Polling |
| `eventLogTrigger` | EventLog.EntryWritten |
| `webhookTrigger` | HTTP `/api/webhooks/{name}/{path}` |
| `manualTrigger` | UI / API |

`TriggerOrchestrator` scannt alle 5 s. Trigger-Daten landen als `manual.*`-Variablen im Run (`{{manual.<name>}}`) + als `param.*` des Trigger-Nodes — **kein** `trigger.*`-Namespace. Key-Namen pro Trigger-Typ: siehe `docs/claude-reference.md`.

**webhookTrigger-Hardening:** Verifizierung per `signatureMode` — `header` (default, `X-Webhook-Secret`) oder `nodepilot-hmac-v2` (HMAC-SHA256 über Freshness-Metadaten + Methode + Pfad + kanonische Query + Raw-Body; verlangt CSPRNG-Secret ≥32 Bytes, `X-NodePilot-Timestamp` und einmalige `X-NodePilot-Delivery-Id` mit clusterweitem Replay-Guard/5-min-Fenster). Legacy `hmac` (Body-only, GitHub/GitLab/Alertmanager-nativ) wird abgelehnt → Adapter nötig. `fieldMappings` extrahiert JSON-Body-Felder per JSONPath als eigene `manual.*`-Params. Details: `docs/claude-reference.md`.

## WorkflowEngine — Execution-Modell

- **Event-driven:** Queue + `inFlight`-Dict. Roots = **ausschließlich Trigger-Nodes** (`manualTrigger`/`scheduleTrigger`/`webhookTrigger`/`fileWatcherTrigger`/`databaseTrigger`/`eventLogTrigger`); ohne (aktiven) Trigger → 0 Roots → Execution `Failed`. **Kein** `inDegree==0`-Fallback. Disabled Trigger nie Root. Orphan-/Nicht-Trigger-Activities ohne eingehende Edge laufen **nie** → `Skipped`.
- **Cancellation:** `_runningExecutions` Dict (Guid → CTS).
- **Per-Step-DI-Scope:** eigener Scope pro Step → scope-lokaler `DbContext`.
- **Startup-Reconciler:** hängende `Running`/`Pending`/`Paused` → `Cancelled`.

## Build & Test

```bash
dotnet build                                    # Backend (Central Package Management, siehe Directory.Packages.props)
cd src/nodepilot-ui && npm run build            # Frontend
dotnet test                                     # Alle Backend-Tests
dotnet test tests/NodePilot.Engine.Tests        # Einzelnes Projekt
cd src/nodepilot-ui && npm test                 # Frontend (watch)
cd src/nodepilot-ui && npm run test:run         # Frontend (CI)
cd src/nodepilot-ui && npm run test:coverage    # Frontend (Coverage-Gate, vitest.config.ts)
cd src/nodepilot-ui && npm run lint             # Frontend (ESLint)
cd src/nodepilot-ui && npm run test:e2e         # E2E (Playwright, baut SPA + vite preview)
```

**Konventionen:**
- **Tests sind Pflicht.** Jeder relevante Code-Change braucht passenden Test-Code in derselben Änderung.
- Coverage-Gates: Backend >= 45% Line-Coverage, Frontend siehe `vitest.config.ts`.
- Naming: `MethodName_Scenario_ExpectedResult`
- Remote-Layer (WinRM) IMMER gemockt.
- DB-Tests: SQLite in-memory.

**E2E (Playwright):** Hermetische Specs in `src/nodepilot-ui/e2e/` — alle APIs via `page.route` gemockt (kein Backend/Postgres nötig; Predicate-Catch-All in `e2e/fixtures/mockApi.ts`). **Vor neuen Specs: `src/nodepilot-ui/e2e/README.md` lesen.** Fast-Iteration gegen laufenden Dev-Server (kein Build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

**Nightly:** Windows-Task `NodePilot Nightly Tests` (täglich 22:00) fährt via `scripts/nightly-tests.ps1` alle drei Suiten (je 1× Retry bei Flake), Report nach `C:\temp\nodepilot-nightly\` (+ `latest.md`). Das Skript gibt vorm Rebuild Port 5000 frei + killt verwaiste `testhost`-Prozesse. Manuell: `powershell -File scripts/nightly-tests.ps1`; Zeit ändern: `scripts/register-nightly-task.ps1 -Time HH:mm`.

## CLI (`np`)

Reiner HTTP-Client gegen REST-Endpoints. `dotnet global tool`. Befehlsbereiche: `auth`, `workflow`, `exec`, `machine`, `credential`, `globals`, `user`, `shared-folder`, `maintenance`, `alerting`, `system-alert`, `audit`, `backup`, `db`, `cron`, `health`, `dashboard`, `operations`, `observability`, `settings`, `secrets`, `config`. Details: `docs/claude-reference.md`.

**Architektur-Konvention:** Neuer API-Endpoint → parallel CLI-Methode in `NodePilotApiClient.cs` + Command anlegen. DTOs in `Cli/Api/Dtos/` duplizieren (kein ProjectReference).

## MCP-Server (`nodepilot-mcp`)

Reiner HTTP-Client gegen die REST-API (wie die CLI) + In-Proc-Analyse gegen `NodePilot.Core` — **kein** neuer Backend-Pfad (99 Tools, 3 Resources), Transport stdio; reused die DPAPI-Session der CLI (`np auth login`). Destruktive Tools (`delete_*`, `force_unlock_workflow`, `cancel_all_executions`, `test_step`) werden nur bei `NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true` registriert; Workflow-Definitionen werden vor Tool-Output secret-redigiert, bei publish/patch werden echte Secrets aus der gespeicherten Version wiederhergestellt. Volle Doku: `docs/mcp-server.md`.

**Architektur-Konvention:** Neuer API-Endpoint → Methode in `NodePilot.Mcp/Api/NodePilotApiClient.cs` (DTOs in `Mcp/Api/Dtos/` dupliziert) + `[McpServerTool]`-Methode in der passenden `Tools/*Tools.cs` (destruktiv → `DestructiveTools` + `get_safety_status`-Liste pflegen), ggf. Klasse in `Program.cs` via `WithTools<T>()` registrieren (**nie** `WithToolsFromAssembly`), WireMock-Test ergänzen. Frontend-Databus-/Lint-Logik wird in `Mcp/Analysis/` gespiegelt (`upstreamVariables.ts`, `activityConfigFacts.ts`, `workflowLint.ts`).

## Autorisierung

| Endpoint | Admin | Operator | Viewer |
|---|---|---|---|
| `GET /api/{workflows,executions,machines}` | ✓ | ✓ | ✓ |
| `POST /api/workflows`, `PUT`, `POST /{id}/duplicate\|execute` | ✓ | ✓ | ✗ |
| `POST /api/machines`, `PUT` | ✓ | ✓ | ✗ |
| `GET\|POST\|PUT /api/credentials` | ✓ | ✓ | ✗ |
| `POST /api/executions/{id}/cancel` | ✓ | ✓ | ✗ |
| `DELETE /{workflows,machines,credentials}/{id}` | ✓ | ✗ | ✗ |
| `GET /api/alerting/rules`, `POST /preview-filter` | ✓ | ✓ | ✗ |
| `POST/PUT/DELETE /api/alerting/rules`, `POST /{id}/enable\|disable\|test-fire` | ✓ | ✗ | ✗ |
| `POST /api/trigger/{name}` | API-Key via `X-Api-Key`-Header |

Initial-Admin: erster Login bei leerer DB (One-Shot-Token `admin-setup.token`).

## Security

- **JWT:** 12h, `jti`-Revocation. Key aus `Jwt:Key` oder auto-generiertes `jwt-secret.key`.
- **Auth-Pfade:** Local-BCrypt (immer an) + LDAP (`Authentication:Ldap:Enabled`) + Windows-Negotiate (`Authentication:Windows:Enabled`). Alle konvergieren auf JWT-Cookie + CSRF-Token. Siehe `docs/ldap-windows-sso.md`.
- **External Trigger:** nur aktiv wenn `ExternalTrigger:ApiKey` gesetzt.
- **Rate-Limiting:** login 50/Min, refresh 20/Min, webhook 60/Min, trigger 30/Min, ai-generate 20/Min, audit 60/Min, backup 10/Min (per-IP, Sliding-Window).
- **Output-Redaction:** `OutputRedactor` maskiert Secrets. Immer aktiv. Custom-Patterns via `Logging:Redaction:Patterns`.
- **Localhost-Bypass:** ohne Credentials läuft in-process. **Produkt-Feature, kein Guard einziehen.**
- **Security-Headers (Non-Dev):** HSTS, CSP, X-Frame-Options=DENY, nosniff, Referrer-Policy.
- **SignalR-Auth:** httpOnly `np_auth`-Cookie wird beim WebSocket-Upgrade automatisch mitgeschickt (nur `/hubs/`); kein `?access_token=`-Querystring.
- **REST-API-Proxy:** `RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode`.

Hardening-Flags: `Remote:RequireWinRmSsl`, `RestApi:BlockPrivateNetworks`, `RestApi:AllowedHosts` (exakte Outbound-Allow-Liste für `waitForCondition`-Probes + proxied `restApi`-Ziele/Redirects), `FileSystemOperation:RejectTraversal`, `SqlActivity:RequireConnectionRef`, `StartProgram:DisallowShellExecute`, `Trigger:Database:RequireConnectionRef`, `Security:StrictAllowedHosts`, `Webhook:RequireSecret` — **default `true`** (hardened; fehlender Key liest als `true`, `appsettings.Development.json` relaxt auf `false`). `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` + `Database:AllowInsecureTls` default `false`. Details: `docs/claude-reference.md`.

## Admin-Settings Hot-Reload

Admin-Settings-Saves persistieren atomar nach `appsettings.runtime.json` (`reloadOnChange: true`). Pro Sektion trägt `SettingsSchema.cs` ein `IsHotReloadable`-Flag; nur `false`-Sektionen setzen den Restart-Marker (UI: emerald `HotReloadHint` vs. oranger `RestartBanner`). 11 Sektionen sind hot-reloadable, 8 restart-pflichtig; harter Kern (JWT, DB, Kestrel, Cluster/HA, `Remote:Provider`) bleibt boot-fixed. **Consumer-Regel:** hot-reloadable Werte via `IOptionsMonitor<T>.CurrentValue` bzw. rohes `IConfiguration` pro Use/Pass lesen — nie `IOptions<T>.Value`-Snapshot. Vollständige Matrix + Mixed-Section-Limits: `docs/claude-reference.md`.

## AuditLog

`IAuditWriter` injizieren, `await _audit.LogAsync(AuditActions.VerbNomen, "Resource", resourceId, detailsJson, ct)` **nach** `SaveChanges`. Schreibfehler darf Mutation nie abbrechen. Passwörter/Secrets nie in Details.

Audit-Codes folgen dem Muster `VERB_NOMEN` und sind **zentral** in `NodePilot.Core.Audit.AuditActions` registriert — nie ein rohes String-Literal am Call-Site (Guard: `AuditActionsCatalogTests`). Pipeline: `IAuditStager` (Core) + `IAuditWriter` (Api, wrappt Stager); Archive gzip + SHA-256-Sidecar. Code-Übersicht: `docs/claude-reference.md`.

## KI-Features

Opt-in (`Llm:Enabled=false` default), OpenAI-kompatibler Endpunkt, Rate-Limit 20/min/IP. Drei Helfer + eine Activity; Details: `docs/claude-reference.md` + `docs/ai-features.md`.

- **`POST /api/ai/generate-script`** (Admin/Op, SSE-Streaming — tippt live in Monaco) + **`POST /api/ai/generate-workflow`** (Admin/Op, JSON).
- **`POST /api/ai/chat`** (alle Rollen, SSE) — Workflow-Assistent: erklärt/ändert den aktuellen Workflow; Proposals nur Admin/Op, Merge per Node-ID aufs unredigierte Original (Secrets/Layout erhalten). Secrets werden vor jedem LLM-Call redigiert (`WorkflowSecretRedactor`). **Tool-Calling** opt-in (`Llm:EnableToolCalling`): read-only Analyse- + Execution-Log-Tools, gecappt via `Llm:ToolCallMaxDepth`. Threads/Verlauf/Export clientseitig persistent.
- **Globaler AI-Chat / Wissens-Assistent** (`POST /api/ai/knowledge/ask`, SSE; `GET /api/ai/knowledge/capabilities`) — seitenweiter read-only Q&A in `/ai-chat`, canvas-frei. Vier admin-toggelbare Wissensquellen (Sektion `AiKnowledge`, hot-reloadbar, alle `false`-default außer Docs/Operational): **Docs** (`DocsEnabled`), **Operational** (`OperationalEnabled`, RBAC-folder-gescoped — liefert nur die Workflow-spezifische **Definition** (`get_workflow_definition`, secret-redigiert), **statische Analyse** (`analyze_workflow`) und **Cron-Voraussage** (`get_next_scheduled_fires`); reine Listen wie "welche Workflows/Läufe/Maschinen gibt es" werden über die DB-Quelle per text2sql beantwortet), **Source-Code** (`SourceCodeEnabled`, Admin/Op), **DB / text2sql** (`DbEnabled`, Admin/Op). DB-Tools (`list_db_tables`/`get_db_table`/`execute_readonly_sql`) über `ISqlKnowledgeReader`: Schema inkl. Provider/FKs ohne Secret-Spalten; zentraler Executor-Guard (64 KiB, Single-Statement, Read-only-Whitelist + Dangerous-Token/Routine-Block), geschützte Spaltenreferenzen vor Ausführung abgelehnt, Result-Masking + `IAuditDetailsRedactor`, Row-Cap 200, valides Truncation-JSON. DB-Tools Strict mit Best-Effort-Fallback; Audit nur Query-Anzahl/Fingerprint. Sources sind nur bei `Llm:EnableToolCalling` als Capability sichtbar.
- **`llmQuery`-Activity:** Engine-lokal, Prompt→Text; per-Node-Overrides `baseUrl`/`model`/`apiKey`/`maxTokens`/`temperature`/`timeoutSeconds`/`jsonMode`, **gated durch `Llm:Enabled`** (zentraler Kill-Switch). Teilt Transport + SSRF-Guard via `ILlmClientFactory`; einziger BaseUrl-Validierungspunkt ist `LlmEndpointGuard.NormalizeAndValidateBaseUrl`. Prompt-Excluded (nicht für Workflow-Auto-Generierung).
- **Hardening:** SSRF-Block (Cloud-Metadata), `UseProxy=false`, Klartext-ApiKey-Warning, Prompt-Injection-Mitigation (Schema-only, User-reviewed Insert). Drift-Schutz: `PromptCatalogDriftTest.cs`. Audit: `AI_*`-Codes.

## Workflow Import/Export

`GET /{id}/export` / `GET /export` / `POST /import`. Envelope `nodepilot-workflow-export/v1`. Import erzeugt neue Einträge, Namenskollisionen → Suffix `" (Imported 2)"`. SCOrch-Import via `POST /import-scorch`. Ziel-Folder via `?folderId=` (fehlt → Root); RBAC = Edit auf dem gewählten Folder. **Secrets werden hier redigiert** (`***`) — Teilen-Artefakt, kein DR.

## System-Configuration Backup (ADR 0001)

Getrennt vom Workflow-Export: voller DR-Snapshot der Konfiguration (Workflows+Folders, Machines, Credentials, Globals, Users, Custom Activities, Alerting, Settings — **keine** Execution-History/Audit/Stats). Admin-only, Envelope `nodepilot-system-backup/v2` (`.npbackup`); Secrets per **Passphrase-Rewrap** (PBKDF2→HKDF→AES-GCM) + Whole-file-HMAC. Restore: Ref-Validierung, transaktional mit ID-Remap, Konflikt-Policy (skip/rename/overwrite), Last-Admin-Schutz. UI `/backup`, CLI `np backup manifest|export|preview|restore`. Details: `docs/claude-reference.md`.

## Konventionen & Feinheiten

- **Keine Abwärtskompatibilität:** Keine Shims, Feature-Flags, optionale Defaults für sanfte Migration. Sauber durchziehen: `NOT NULL`, Required-Properties, alte Code-Pfade ersatzlos löschen. Alte DB → Migrations fahren, fertig.
- **Disabled Edges:** Target-Node wird nicht zum Root. Alle eingehenden disabled → `Skipped`.
- **Kein Root (trigger-los oder nur Zyklen):** Nodes vorhanden, aber kein (aktiver) Trigger → 0 Roots → Execution `Failed` (ErrorMessage nennt den fehlenden Trigger/Start). **Leerer** Workflow (0 Nodes) → läuft mit 0 Steps durch (`Succeeded`).
- **`POST /execute`:** asynchron, 202 + ExecutionId. Fortschritt via SignalR.
- **Workflow-Version-History:** `Update`/`Rollback` snapshotten vorherige Definition.
- **Idempotency-Keys:** `POST /api/trigger/{name}` akzeptiert `Idempotency-Key`-Header.
- **Node-Level `disabled`:** `data.disabled: true` → Node wird `Skipped`, Downstream ohne andere Quellen auch.
- **Step-Debugger:** `POST /execute` mit `debug: true` → Breakpoints, SignalR `StepPaused`, Resume via `POST /executions/{id}/resume`.

## Production Deployment

Produktiv-Rollout über `deploy/`-Skripte — Claude führt sie **nicht** aus. Vollständige Doku: `deploy/README.md`. Architektur (gMSA, Kestrel-HTTPS, Install-Dir-Split, Config-Keys, Stolperfallen): siehe `docs/claude-reference.md`.
