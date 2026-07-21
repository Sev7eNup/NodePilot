# Codebase-Lernplan

Systematischer Einstieg in die NodePilot-Codebase — von außen nach innen, von Modellen zum Runtime-Verhalten. Reihenfolge ist bewusst so gewählt, dass jede Schicht auf der vorherigen aufbaut, ohne dass du vorgreifen musst.

Zeitbudget realistisch: **2–3 Werktage** für ein solides Mental Model.

---

## Phase 0 — Lay of the Land (30–45 min)

Bevor du eine einzige Klasse liest, schau dir die Karte an:

1. [CLAUDE.md](../CLAUDE.md) — Tech-Stack, Modul-Graph, Activity-Typen-Tabelle, Edit-Lifecycle. **Wichtigster Single-Source-of-Truth-Read.**
2. [CONTEXT.md](../CONTEXT.md) — Architektur-Narrativ, was NodePilot überhaupt ist und welche Probleme es löst.
3. [docs/adr/](adr/) — Architecture Decision Records. **Aktuell als ADR ausformuliert:**
   - [0001 — System Configuration Backup/Restore](adr/0001-system-configuration-backup-restore.md)

   Weitere Architektur-Entscheidungen sind im Code + in CLAUDE.md/CONTEXT.md dokumentiert, aber (noch) nicht als eigenständige ADR geschrieben — u.a. Backend-owned Activity Catalog, Workflow-Definition Owns Runtime Semantics, Execution-Dispatch Owns Pending Lifecycle, Explicit Settings-Section-Adapters, Incremental PowerShell-Operation-Module.
4. Dependency-Graph einprägen (steht in CLAUDE.md): `Api → Engine, Scheduler, Data, Remote, Core, Telemetry`. **Core hat null Abhängigkeiten** — das ist dein Anker.

**Ziel:** Du sollst auf „warum gibt es Engine **und** Scheduler separat?" und „wer kennt wen?" sofort antworten können.

---

## Phase 1 — Backend, Schicht für Schicht

### 1A. NodePilot.Core — die Sprache des Systems (1–2 h)

Core enthält nur Models, Enums, Interfaces. Keine Logik. Das ist dein Vokabular für alles, was danach kommt.

**Reihenfolge:**

1. [src/NodePilot.Core/Enums/](../src/NodePilot.Core/Enums/) — alle 5 Files durchlesen. Klein, kondensiert: `ExecutionStatus`, `UserRole`, `AuthProvider`, `FolderPrincipalType`, `SharedFolderRole`.
2. Die zwei wichtigsten Aggregate:
   - [Workflow.cs](../src/NodePilot.Core/Models/Workflow.cs) — Was ist eine Workflow-Row? Achte auf `DefinitionJson`, `IsEnabled`, `CheckedOutByUserId`/`CheckedOutAt` (Edit-Lock).
   - [WorkflowExecution.cs](../src/NodePilot.Core/Models/WorkflowExecution.cs) + [StepExecution.cs](../src/NodePilot.Core/Models/StepExecution.cs) — Lauf-Historie.
3. Das JSON-Graph-Modell (das, was im `DefinitionJson` parst):
   - [WorkflowGraph.cs](../src/NodePilot.Core/Models/WorkflowGraph.cs) — `nodes[]` + `edges[]`. **Hier liegt das eigentliche Workflow-Format.** Vergleiche mit dem JSON-Snippet in CLAUDE.md Abschnitt „Workflow-JSON Format".
4. Die zwei Engine-Interfaces:
   - [IWorkflowEngine.cs](../src/NodePilot.Core/Interfaces/IWorkflowEngine.cs) — Engine-Public-API.
   - [IActivityExecutor.cs](../src/NodePilot.Core/Interfaces/IActivityExecutor.cs) — Contract, das jede Activity erfüllt. **Drei Methoden, eine Klasse — schau, wie schlank das ist.**
5. Restliche Models nur überfliegen: [Credential.cs](../src/NodePilot.Core/Models/Credential.cs), [ManagedMachine.cs](../src/NodePilot.Core/Models/ManagedMachine.cs), [GlobalVariable.cs](../src/NodePilot.Core/Models/GlobalVariable.cs), [AuditLogEntry.cs](../src/NodePilot.Core/Models/AuditLogEntry.cs).

**Verständnis-Check:** Skizziere auf Papier das ERD von Workflow → WorkflowExecution → StepExecution. Wenn du das ohne IDE-Hilfe kannst, sitzt es.

---

### 1B. NodePilot.Data — Persistenz (45 min)

1. [src/NodePilot.Data/NodePilotDbContext.cs](../src/NodePilot.Data/NodePilotDbContext.cs) — alle `DbSet<>`s + `OnModelCreating` für Indexes/Constraints überfliegen. **Achte auf composite Indexes und Unique-Constraints** — die erzählen die Geschäftsregeln.
2. [src/NodePilot.Data/MigrationBootstrapper.cs](../src/NodePilot.Data/MigrationBootstrapper.cs) — wie wird die DB beim Start aktualisiert. Kurz.
3. [src/NodePilot.Data/CredentialStore.cs](../src/NodePilot.Data/CredentialStore.cs) — DPAPI-Encrypt/Decrypt. Das ist der einzige Ort, an dem Klartext-Passwörter durchlaufen.
4. [src/NodePilot.Data/Migrations/](../src/NodePilot.Data/Migrations/) — nur die **drei jüngsten** Migrations öffnen, nicht alle. Du willst nur ein Gefühl bekommen, wie EF-Migrations hier aussehen (provider-agnostisch — siehe CLAUDE.md „Datenbank").

---

### 1C. NodePilot.Remote — WinRM in einem Satz (15 min)

1. [IRemoteSessionFactory.cs](../src/NodePilot.Core/Interfaces/IRemoteSessionFactory.cs) (liegt in Core).
2. [src/NodePilot.Remote/WinRmSessionFactory.cs](../src/NodePilot.Remote/WinRmSessionFactory.cs) — wie wird eine PowerShell-Session aufgebaut. Reicht zum Überfliegen.

Mehr brauchst du anfangs nicht. WinRM-Tiefe lohnt erst, wenn du an `runScript` o.ä. baust.

---

### 1D. NodePilot.Engine — das Herzstück (3–4 h, der wichtigste Block)

**Schritt 1 — eine simple Activity lesen, um den `IActivityExecutor`-Vertrag zu verinnerlichen:**

- [DelayActivity.cs](../src/NodePilot.Engine/Activities/DelayActivity.cs) — 30 Zeilen, trivial.
- [LogActivity.cs](../src/NodePilot.Engine/Activities/LogActivity.cs) — minimal, zeigt Logging-Pattern.

**Schritt 2 — eine komplexe Activity, um Variable-Resolution + Result-Shape zu sehen:**

- [RunScriptActivity.cs](../src/NodePilot.Engine/Activities/RunScriptActivity.cs) — Auto-Quoting, strukturierter Output-Capture, Remote-Session-Nutzung. **Das hier ist der Lehrling-Killer-Test** — wenn du verstehst, was hier passiert, hast du 70 % der Engine.
- [BaseActivity.cs](../src/NodePilot.Engine/Activities/BaseActivity.cs) — geteilte Helpers.
- [ConfigExtensions.cs](../src/NodePilot.Engine/Activities/ConfigExtensions.cs) — wie wird `data.config` aus JSON gelesen.

**Schritt 3 — Variable-Resolution + Retry, die quer durch alle Activities laufen:**

- [VariableResolver.cs](../src/NodePilot.Engine/Execution/VariableResolver.cs) — `{{step.output}}`-Auflösung.
- [RetryPolicy.cs](../src/NodePilot.Engine/Execution/RetryPolicy.cs) — backoff-Modi.

**Schritt 4 — der Orchestrator:**

- [StepRunner.cs](../src/NodePilot.Engine/Execution/StepRunner.cs) — führt **einen** Step aus (Retry-Loop, Timeout, Redaction, Persist).
- [WorkflowEngine.cs](../src/NodePilot.Engine/WorkflowEngine.cs) — **die Datei**, die du dir Zeit nehmen musst. Such gezielt nach:
  - `ExecuteWorkflowAsync` — Entry-Point eines Laufs.
  - Wie wird die Ready-Queue gefüllt, was sind „Roots"? (siehe CLAUDE.md → „WorkflowEngine — Execution-Modell").
  - Wie funktioniert das `inFlight`-Dict?
  - Per-Step-DI-Scope: such die Stelle, an der pro Step ein neuer `IServiceScope` erzeugt wird.
- [StartupRecovery.cs](../src/NodePilot.Engine/Execution/StartupRecovery.cs) — Crash-Recovery beim Boot.

**Schritt 5 — DI-Registrierung:**

- Wo werden alle `IActivityExecutor`s registriert? → in [Program.cs](../src/NodePilot.Api/Program.cs) per `AddScoped<IActivityExecutor, ...>`. Such die Stelle, scroll durch die ganze Liste. Das ist dein Activity-Katalog.

**Verständnis-Check:** Trace gedanklich einen Workflow mit 3 Nodes (Trigger → runScript → returnData). Wo entsteht der `ExecutionId`, welcher Code persistiert den ersten `StepExecution`-Row, wer markiert den letzten Step als `Succeeded`?

---

### 1E. NodePilot.Scheduler — Trigger (30 min)

Nur kurz, weil meiste Logik in Engine bleibt:

1. [src/NodePilot.Scheduler/](../src/NodePilot.Scheduler/) — Ordnerstruktur ansehen.
2. `TriggerOrchestrator` (BackgroundService, 5s-Scan-Loop) — die einzige Klasse, die du wirklich lesen musst.
3. Eine `ITriggerSource`-Implementierung als Beispiel: Schedule oder Webhook.

---

### 1F. NodePilot.Api — der HTTP-Layer (2 h)

**Schritt 1 — Host-Setup:**

- [Program.cs](../src/NodePilot.Api/Program.cs) — **lange Datei, aber nicht komplex**. Von oben nach unten lesen:
  - DI-Registrierungen (DbContext-Provider-Switch, Activities, Engine, Stores)
  - Auth-Konfiguration (JWT + optional LDAP/Windows)
  - SignalR-Hub-Mapping (`/hubs/execution`)
  - SPA-Fallback (`MapFallbackToFile("index.html")`)
- [AuthenticationSetup.cs](../src/NodePilot.Api/Security/AuthenticationSetup.cs) — JWT-Cookie + Bearer + CSRF.

**Schritt 2 — DTOs (vor den Controllern!):**

- [src/NodePilot.Api/Dtos/](../src/NodePilot.Api/Dtos/) — überfliegen. Sind die Request/Response-Shapes für REST.

**Schritt 3 — Controller in der **richtigen** Reihenfolge:**

1. [AuthController.cs](../src/NodePilot.Api/Controllers/AuthController.cs) — Login/Logout/Me, JWT-Cookie-Setting.
2. [WorkflowsController.cs](../src/NodePilot.Api/Controllers/WorkflowsController.cs) — der Brot-und-Butter-Controller. CRUD + `/execute` + `/duplicate` + Edit-Lock-Endpoints (`/lock`, `/unlock`, `/publish`, `/force-unlock`).
3. [WorkflowEditingController.cs](../src/NodePilot.Api/Controllers/WorkflowEditingController.cs) — Edit-Lock-Lifecycle. Lies parallel CLAUDE.md → „Edit-Lifecycle (SCOrch-style Edit-Lock)".
4. [ExecutionsController.cs](../src/NodePilot.Api/Controllers/ExecutionsController.cs) — List, Cancel, Retry, Resume.
5. [WebhooksController.cs](../src/NodePilot.Api/Controllers/WebhooksController.cs) + [ExternalTriggerController.cs](../src/NodePilot.Api/Controllers/ExternalTriggerController.cs) — anonyme Eingangstore.
6. Den Rest überfliegen — sind alle nach gleichem Muster gebaut.

**Schritt 4 — SignalR Real-time:**

- Such nach `ExecutionHub` (vermutlich `src/NodePilot.Api/Hubs/`). Wer schreibt rein? → der `IExecutionNotifier` (Interface in Core, Impl in Api). Find Usages auf `IExecutionNotifier` zeigt dir alle Event-Quellen.

---

## Phase 2 — Frontend (2–3 h)

### 2A. Bootstrap

1. [main.tsx](../src/nodepilot-ui/src/main.tsx) — Vite-Entry, Provider-Stack.
2. [App.tsx](../src/nodepilot-ui/src/App.tsx) — **die Routing-Karte**. Hier siehst du alle Pages auf einen Blick.
3. [vite.config.ts](../src/nodepilot-ui/vite.config.ts) — Proxy-Konfiguration (5173 → 5000).

### 2B. Pages in Lern-Reihenfolge

Starte mit **einfach**, eskaliere zu **komplex**:

1. [LoginPage.tsx](../src/nodepilot-ui/src/pages/LoginPage.tsx) — einfaches Form + Auth-Flow. Sieh, wie `methods` discovered wird.
2. [MachinesPage.tsx](../src/nodepilot-ui/src/pages/MachinesPage.tsx) — klassisches CRUD-Pattern. Stellvertretend auch für CredentialsPage, GlobalVariablesPage, UsersPage — alle nach gleichem Schema.
3. [DashboardPage.tsx](../src/nodepilot-ui/src/pages/DashboardPage.tsx) — wie werden Stats aggregiert dargestellt.
4. [ExecutionsPage.tsx](../src/nodepilot-ui/src/pages/ExecutionsPage.tsx) — Listen + SignalR-Live-Updates.
5. [WorkflowsPage.tsx](../src/nodepilot-ui/src/pages/WorkflowsPage.tsx) — Folders + KI-Generierung.
6. [WorkflowEditorPage.tsx](../src/nodepilot-ui/src/pages/WorkflowEditorPage.tsx) — **die Hammer-Seite**. React Flow, Drag&Drop, History/Undo, Edit-Lock-UX, Properties-Panel. Plane dafür einen separaten Nachmittag ein. Stelle dir explizit diese Fragen beim Lesen:
   - Wo lebt der React-Flow-State (`nodes`, `edges`)?
   - Was tut `useWorkflowHistory`?
   - Wie wird `canWrite` berechnet, wie greift es in `nodesDraggable` / `nodesConnectable`?
   - Wo passiert Save (PUT) vs. Publish (atomar)?

### 2C. Designer-Komponenten (nur wenn du am Editor arbeitest)

- [src/nodepilot-ui/src/components/designer/nodes/](../src/nodepilot-ui/src/components/designer/nodes/) — `ActivityNode`, `TriggerNode`, `JunctionNode` etc.
- [src/nodepilot-ui/src/components/designer/edges/](../src/nodepilot-ui/src/components/designer/edges/) — Custom Edge mit Label und dem Pen-Tool (siehe CLAUDE.md → „Edge-Reshape").
- [src/nodepilot-ui/src/components/designer/properties/](../src/nodepilot-ui/src/components/designer/properties/) — `PropertiesPanel` + per-Activity Config-Komponenten.

### 2D. API-Client & State

- [src/nodepilot-ui/src/api/](../src/nodepilot-ui/src/api/) — wie wird HTTP gegen Backend aufgerufen, wo lebt der Auth-Header.
- Find Usages auf `useDesignStore` zeigt dir, was global im Zustand liegt vs. was per-Page-State ist.

---

## Phase 3 — End-to-End-Trace eines konkreten Falls

**Das ist der wichtigste Schritt — er bindet Backend und Frontend zusammen.**

Nimm dir den User-Flow „User klickt **Run** auf einen Workflow" und folge dem Pfad:

1. **UI:** Run-Button in WorkflowEditorPage oder WorkflowsPage → API-Call `POST /api/workflows/{id}/execute`.
2. **Controller:** `WorkflowsController.Execute(...)` → erzeugt `WorkflowExecution`-Row mit Status=`Pending`/`Queued` → returnt 202.
3. **Dispatch:** Such, wer Pending-Executions an `IWorkflowEngine` übergibt. (Hint: `IExecutionDispatchQueue` + `WorkflowScheduler` in Engine.)
4. **Engine:** `WorkflowEngine.ExecuteWorkflowAsync` → Roots ermitteln → Ready-Queue füllen → `StepRunner` pro Node → Activity-Resolution via DI-Scope.
5. **Activity:** `RunScriptActivity.ExecuteAsync` → Variable-Resolution → WinRM-Session → Output-Redaction → Result.
6. **Persist:** `StepExecution` wird mit Output + ErrorOutput + OutputParametersJson geschrieben.
7. **Notify:** `IExecutionNotifier.StepCompletedAsync` → SignalR-Push an `/hubs/execution`.
8. **UI:** ExecutionsPage / WorkflowEditorPage hört SignalR-Events ab und re-rendert.

Mach dir dabei eine handschriftliche Skizze (oder draw.io). **Wer eine eigene Skizze hat, hat verstanden.**

---

## Phase 4 — Hands-on Übungen (optional, aber stark empfohlen)

Wenn du nur liest, vergisst du. Drei kleine Aufgaben, sortiert nach steigender Tiefe:

1. **Trivial:** Setze einen Breakpoint in `RunScriptActivity.ExecuteAsync`, starte API+UI lokal (Postgres-Cluster vorher hochfahren, siehe CLAUDE.md „Projekt starten"), trigger einen Test-Workflow, beobachte die Variable-Resolution.
2. **Mittel:** Füge eine neue Activity hinzu — z.B. `currentTime` die einfach `DateTime.UtcNow` als Output liefert. Folge der Checkliste in CLAUDE.md → „Architektur-Konventionen → Neue Activity". Du wirst genau einmal an jeder relevanten Stelle vorbeikommen.
3. **Anspruchsvoll:** Lies einen offenen Issue im GitHub-Tracker ([docs/agents/issue-tracker.md](agents/issue-tracker.md) für Setup) und versuche, ohne zu coden, allein aus dem Wissen welche Files angefasst werden müssten.

---

## Phase 5 — Speziell-Themen (nur on demand)

Diese liest du, wenn du sie wirklich brauchst — nicht prophylaktisch:

- **Sicherheit/Auth:** [docs/ldap-windows-sso.md](ldap-windows-sso.md), [src/NodePilot.Api/Security/](../src/NodePilot.Api/Security/)
- **HA Active/Passive:** Cluster-Leader-Election (Memory-Pointer `project_ha_active_passive.md`)
- **Audit-Pipeline:** [src/NodePilot.Core/Audit/](../src/NodePilot.Core/Audit/) (`IAuditStager`) + [src/NodePilot.Api/Audit/](../src/NodePilot.Api/Audit/) (`AuditWriter`)
- **KI-Features:** [docs/ai-features.md](ai-features.md), [src/NodePilot.Ai/](../src/NodePilot.Ai/) (LLM-Stack) + [src/NodePilot.Api/Ai/](../src/NodePilot.Api/Ai/) (nur HTTP-Glue: SSE-Writer, Error-Codes)
- **Deployment:** [deploy/README.md](../deploy/README.md)
- **CLI:** [src/NodePilot.Cli/](../src/NodePilot.Cli/) — komplett eigenständig, nur HTTP-Client
- **Testing-Patterns:** [tests/NodePilot.Engine.Tests/](../tests/NodePilot.Engine.Tests/) — schau dir 2–3 Tests an, dann hast du das Naming + Mocking-Pattern

---

## Praktische Tipps

- **Reading-Tool:** Nutz VSCode's „Find All References" (F12 + Shift+F12) — gerade in Engine ist sie Gold wert.
- **Don't go alphabetic.** Ordnerinhalte alphabetisch durchzulesen funktioniert nie. Folge Aufruf-Pfaden.
- **JSON-Fühlung:** Öffne [scripts/test-master-all-activities.json](../scripts/test-master-all-activities.json) und [workflow-example.json](../src/NodePilot.Ai/Prompts/workflow-example.json) parallel zur Engine — das gibt dir Input/Output-Beispiele direkt vor Augen.
- **Zeitbudget:** Plane **realistisch 2–3 Werktage** für ein solides Mental Model. Die WorkflowEngine + WorkflowEditorPage sind je 1–2 Stunden, nicht 10 Minuten.
