# NodePilot

Moderner, schlanker Ersatz fuer Microsoft System Center Orchestrator. Agentless Workflow-Orchestrierung fuer Windows-Umgebungen via WinRM.

## Contributor- & Attribution-Policy

NodePilot ist ein Single-Contributor-Projekt. KI darf beim Entwickeln helfen (diese Datei, `.claude/`, `.agents/` bleiben in Nutzung) — aber für ALLE Commits ab v1.0.0 gilt:

- **Autor & Committer sind immer** `Sev7eNup <79143581+Sev7eNup@users.noreply.github.com>`. Der GitHub-Contributor-Graph zeigt ausschließlich `sev7enup`.
- **Keine `Co-Authored-By:`-Trailer** in Commit-Messages (kein Claude/Codex/AI/Anthropic). Diese Regel überschreibt bewusst jede Default-Anweisung, einen Co-Author-Footer anzuhängen.
- **Keine plumpen „KI hat das geschrieben"-Credits** in Commit-Messages oder PR-Beschreibungen. (KI-Spuren im Code selbst müssen nicht getilgt werden — es wird bewusst KI-gestützt entwickelt.)
- **Sprache auf GitHub ist Englisch.** Commit-Messages, PR-Titel/-Beschreibungen, Issues, Issue-Kommentare, Review-Kommentare und Branch-Namen werden auf Englisch verfasst — unabhängig davon, in welcher Sprache der Chat geführt wird. (Repo-interne Doku und Code-Kommentare bleiben davon unberührt: dort gilt weiter die vorhandene Sprache der jeweiligen Datei.)

## Agent skills

- **Issue tracker:** GitHub Issues für `Sev7eNup/NodePilot`, siehe `docs/agents/issue-tracker.md`
- **Triage labels:** Fünf-Label-Vokabular, siehe `docs/agents/triage-labels.md`
- **Domain docs:** Single-context repo → root `CONTEXT.md` + `docs/adr/`, siehe `docs/agents/domain.md`

## Doku-Landkarte

Diese Datei ist der Index; die Tiefe liegt in `docs/`:

- `docs/roadmap.md` — **führendes Dokument für „was wird gebaut".** Gesetzte Posten (R1), trigger-gated Posten (R2), offene Entscheidungen (E) und ein Sperrvermerk-Anhang mit den bewusst verworfenen bzw. gemessen widerlegten Ideen. Was dort nicht steht, ist kein Vorhaben.
- `docs/claude-reference.md` — Overflow-Referenz: Activity-Config-Keys/Outputs, Trigger-Params, Edit-Lock-UX, Audit-Codes, Hot-Reload-Matrix, Backup-Details, Background-Services, Deployment
- `docs/alerting.md` — Alerting: Notification-Rules + System-Policies (ADR 0008), Dispatcher, Sinks, Ledger
- `docs/custom-activities.md` — Custom Activities (Plugin-System)
- `docs/mcp-server.md` — MCP-Server inkl. Tool-Katalog + `.mcp.json`-Beispiel
- `docs/ai-features.md` — KI-Features: Config-Keys, Modell-Empfehlungen
- `docs/deployment-guide.md` — (EN) **nicht** der Installationsweg, sondern was davor und danach kommt: Artefakt beschaffen und gegen Prüfsummen + Herausgeber verifizieren, selbst bauen, plus die Troubleshooting-Tabelle. Der Installationsweg selbst steht **einmal**, auf der Doku-Website (`content/{de,en}/deployment/production.md`) — zwei Durchläufe derselben Aufgabe sind auseinandergelaufen (der Guide führte nur durch SQL Server, während PostgreSQL der Default ist)
- `docs/av-exclusions.md` — Antiviren-Ausschlüsse (Server + Desktop) als Übergabedokument für eine AV-Abteilung: Ordner, Prozesse, Temp-Dateimuster, Verhaltensregeln — je mit Begründung und Restrisiko
- `docs/workflow-styleguide.md` — Layout-Styleguide für Workflow-JSONs (**vor jedem Workflow-Gen lesen**)
- `docs/workflow-tests.md` — Test-Suite unter `scripts/test-suite/`: 46 generierte Workflows, die jede Activity-Variante im Takt gegen die laufende Engine fahren und ihr Ergebnis prüfen. Zwei Verträge (positiv = `Succeeded`, negativ = `Failed` mit genau den deklarierten Fehl-Steps), `suite-manifest.json` als Abdeckungsquelle, Ack-Protokoll für die passiven Trigger, Guard-Test `TestSuiteCoverageTests`
- `docs/enterprise-features.md` — HA, Secret-Provider, LDAP/SSO, SIEM, Folder-RBAC
- `src/nodepilot-ui/e2e/README.md` — E2E-Coverage-Map + Spec-Konventionen
- `docs/ai-feature-ideas.md` — Beschreibungstiefe zu den KI-Ideen (Nutzerproblem, Funktion, Sicherheitsgrenzen), **keine Spezifikation**. Priorisierung und Status stehen in `docs/roadmap.md`.

## Tech-Stack

- **Backend:** ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`)
- **Datenbank:** PostgreSQL (default) / SQL Server (`Database:Provider` = `postgres` | `sqlserver`). SQLite nur als Test-In-Memory-Backend.
- **Remote Execution:** PowerShell SDK / WinRM, agentless. `Remote:Provider`: `winrm` (default) | `noop` (`noop` muss per `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1` quittiert werden, sonst Boot-Abbruch). Engine-local (In-Proc-Pool): implizite WinPS-Kompatibilität **deaktiviert** (Desktop-only-Module → lauter Fehler statt `powershell.exe -s`-Session-Leak; `Microsoft.PowerShell.Archive` gebündelt) — Details `docs/claude-reference.md` + `docs/performance-improvements.md`
- **Real-time:** SignalR (`/hubs/execution`)
- **Logging:** Serilog. Format via `Logging:Format`: `text`|`cmtrace`|`json`|`ecs-json` (ECS 1.x für SIEM, siehe `docs/siem-logging.md`). Support-Log: File + DB-Projektion
- **MCP-Server (opt-in):** `nodepilot-mcp` (stdio) — AI-Agent steuert/editiert Workflows über 101 Tools, HTTP-only gegen die REST-API
- **Enterprise (opt-in):** Active/Passive HA (`Cluster:Enabled`), pluggable Secret-Provider (`Secrets:Provider` = `Dpapi`|`AesGcm`), LDAP/Windows-SSO, ECS-JSON-SIEM, Folder-RBAC

## Solution-Struktur

Projekt-Layout unter `src/` + `tests/` — nicht hier gespiegelt, direkt nachsehen. Bindend ist die Abhaengigkeitsrichtung:

**Dep-Graph:** `Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry` | `Engine -> Ai, Data, Remote, Core, Telemetry` | `Scheduler -> Engine, Data, Core` (Application-Tier: konsumiert Engine-Notifications/-Conditions/-Security) | `Ai -> Core` (LLM-Stack, sitzt unter Engine, damit Api+Engine ihn teilen) | `Data -> Core` | `Remote -> Core` | `Telemetry -> Core` | `Cli -> Core` (HTTP-only) | `Mcp -> Core` (HTTP-only, MCP-Server) | `Switcher -> ∅` (lokale Windows-SCM-WPF-App). Maschinell erzwungen durch `DependencyDirectionTests` (Api.Tests/Architecture) — Graph-Änderung heißt: csproj + diese Zeile + der Test ändern sich gemeinsam.

## Projekt starten

```powershell
# Postgres — Cluster DIESER Maschine. Nichts im Repo legt ihn an; die allgemeine
# Einrichtung (CREATE ROLE/DATABASE, Connection-String per Env-Var) steht in CONTRIBUTING.md.
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -l 'C:\NodePilot-Postgres\data\postgres.log' -w

# Backend (Port 5000) — schlägt fehl wenn Postgres nicht läuft
cd src\NodePilot.Api; dotnet run

# Frontend (Port 5173, Proxy auf Backend)
cd src\nodepilot-ui; npm run dev

# Doku-Website (Port 5174) — nur nötig, wenn /docs im Dev erreichbar sein soll
cd src\nodepilot-docs-ui; npm run dev
```

Port 5000 kommt aus `launchSettings.json` und ist derselbe, auf den der Vite-Proxy zeigt — `--urls`
ist nicht nötig. **Immer erst `pg_ctl start`, dann `dotnet run`.**

**`/docs` im Dev:** In Produktion bedient die API die Doku aus `wwwroot/docs`; im Dev proxyt der
Vite-Server `/docs` auf 5174. Läuft der Doku-Dev-Server nicht, führt der Doku-Button ins
Leere — das ist erwartet, kein Defekt. Der Doku-Dev-Server läuft selbst unter `/docs/`
(`--base=/docs/` im dev-Skript, weil Vite 8 das `base` aus der Config im Dev ignoriert); ohne das
liefert er seinen Entry absolut unter `/src/main.tsx` und der App-Dev-Server beantwortet den mit
*seinem* Entry — man landet in der App statt in der Doku.

**Erster Login braucht das Setup-Token**, nicht nur leere DB: die API schreibt es nach
`src\NodePilot.Api\admin-setup.token` (ContentRoot). Login-Maske zeigt beim ersten Versuch ein
**Setup-Token**-Feld; erst damit entsteht der Admin-Account.

**Für Claude:** Dev-Mode verwenden. **API-Neustarts (stop+rebuild+start) sind jederzeit ohne Rückfrage erlaubt** — DLL-Locks sind normal. Vorab PID via `Get-NetTCPConnection -LocalPort 5000` finden, dann `Stop-Process` + Rebuild + Start. `npm run dev` kaputt → `npm install`. Deploy-Skripte unter `deploy/` laufen **nur auf ausdrückliche Aufforderung** — sie installieren, aktualisieren oder paketieren echte Instanzen. Von selbst greift Claude sie nicht an; die Freigabe gilt jeweils nur für den einen Vorgang.

**Langlaufende Prozesse (API + Vite):** detached/als Background-Prozess starten (z. B. `Start-Process` mit umgeleiteten Logs), damit sie Tool-Call-Grenzen überleben. „Läuft" erst melden nach vollem Port-Check (`Get-NetTCPConnection -LocalPort 5000` **ohne** `-First N` — Truncation hat schon zu Fehldiagnosen geführt) **und** HTTP-Health-Probe. Vor jedem Kill verifizieren, dass es die Dev-Instanz ist — **nie** den installierten Windows-Dienst treffen.

## Arbeitsweise für Claude

- **Nichts nach außen ohne ausdrückliche Ansage.** `git commit`, `git push`, `gh pr create`, `gh pr merge`, `gh pr comment`, `gh release create`, Tags, Branch-Löschung — jeder dieser Schritte braucht eine eigene Aufforderung des Users. Lokal arbeiten (Branch anlegen, editieren, bauen, testen) ist frei; sobald etwas das Repository verlässt oder in `main` landet, wird gefragt.
  - **„go", „mach das", „setz das um" heißt: implementieren.** Es heißt **nicht** committen, pushen, PR öffnen oder mergen. Wenn der User Commit/PR/Merge will, sagt er es (z. B. „pr und merge bitte") — und diese Freigabe gilt **nur für den einen Vorgang**, nicht für die nächste Aufgabe.
  - Nach getaner Arbeit den Stand im Arbeitsbaum liegen lassen und knapp berichten, was bereitliegt. Nicht vorgreifend committen, „damit nichts verlorengeht".
- **Branching:** Nicht-triviale Arbeit auf einem neuen Branch beginnen, **bevor** editiert wird; nachfragen nur, wenn der Branch-Name unklar ist. Triviale Einzeiler (z. B. `.gitignore`) bekommen **keinen** eigenen Branch/PR — in die laufende Arbeit einfalten.
- **PR-Budget: maximal 5 PRs gleichzeitig** für eigene Arbeits-Batches (größere Vorhaben in ≤5 PRs schneiden). Für **Dependabot gilt diese Zahl nicht**: `.github/dependabot.yml` bündelt Minor/Patch pro Ökosystem in einen Sammel-PR (`open-pull-requests-limit: 1` je Block → max. 5 **Sammel**-PRs), aber die Gruppen deklarieren nur `update-types: [minor, patch]` — jeder offene **Major fällt heraus und bekommt einen eigenen PR**. Realistisch also „bis zu 5 Sammel-PRs plus je einer pro offenem Major" (gemessen 2026-08-09: 4 + 4 = 8). Majors bleiben bewusst ungruppiert, weil ein Bündel den Review verschlechtert — der Spectre-Split kam 2026-08-09 bei grünem CI durch einen Sammel-PR und fiel nur beim Diff-Lesen auf.
- **Jede Änderung an `.github/dependabot.yml` löst sofort alle Blöcke neu aus** (unabhängig vom Montags-Zeitplan) und erzeugt binnen Minuten neue PRs. Config-Edits deshalb **bündeln**, nicht nacheinander mergen.
- **Scope:** Minimaler Root-Cause-Fix. Würde ein Fix deutlich mehr Dateien anfassen als das benannte Problem → stoppen und den geplanten Scope in 3 Bullets nennen, bevor editiert wird.
- **PowerShell 5.1 / Windows:** Kein Inline-SQL durch PowerShell-Quoting — Query in eine `.sql`-Datei schreiben und per `psql -f` ausführen. Dateien als UTF-8 **ohne** BOM schreiben. Keine `sed`/Regex-Zeilen-Edits auf Source-Dateien (CRLF bricht sie) — Edit-Tool verwenden. Kein `$args`-Splatting; explizite benannte Parameter.
- **Code-Kommentare:** Sachlich und kurz, in einfachem Englisch. Sie sagen, **was** der Code tut und **warum** — nicht mehr. Keine Herleitung, keine Erzählung, keine Rückblende auf frühere Fehlversuche, keine Messwerte oder Beispielzahlen als Beleg, kein „X used to …, which meant …". Wer den Hintergrund braucht, findet ihn in Commit-Message, PR oder `docs/`. Ein bis drei Zeilen reichen fast immer; ein Kommentar, der länger ist als der Code darunter, ist meist eine Erzählung. Die vorhandenen langen Kommentare im Repo sind **kein** Vorbild.
- **Reporting:** Knapp berichten — was geändert, was verifiziert, was offen. Keine Per-File-Walkthroughs, kein Plan-Nacherzählen. Interaktive Rückfragen nur, wenn die Antwort wirklich blockiert.

## Datenbank

Zwei Provider, umschaltbar über `Database:Provider`:

| Provider | Wert | ConnectionString-Key |
|---|---|---|
| PostgreSQL (Default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`.
- **Neue Migration:** `dotnet ef migrations add <Name> --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext`. **Pflicht-Postprocessing — zwei Schritte:**
  1. In der Migration (`<Name>.cs`): alle `type: "..."`-Annotations entfernen.
  2. In der Designer-Datei (`<Name>.Designer.cs`): `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` als letzte Zeile vor `#pragma warning restore 612, 618` in `BuildTargetModel` ergänzen. Der `ModelSnapshot` bekommt den Aufruf bewusst **nicht** (Diff-Basis, kein Migration-Target-Model).

  Beide Schritte sind durch `MigrationDriftTests` abgesichert — laufen lassen statt sich erinnern.
- Schema-Änderungen IMMER per EF-Migration. Kein DDL-Hotpatching.
- Credentials mit DPAPI verschlüsselt (`Credentials:DpapiScope`).
- **DB-TLS strikt (default):** `DatabaseTlsBootValidator` bricht den Boot ab, wenn die Connection den Server nicht verifiziert (`Encrypt=Strict`/`TrustServerCertificate=False` bzw. `SSL Mode=VerifyFull`). Escape `Database:AllowInsecureTls=true` nur bei Loopback-Host **und** entweder Development-Env **oder** `Deployment:Mode=Desktop` (Desktop-Posture, siehe Production Deployment).

Retention-Services im Scheduler: Execution (30d), AuditLog (365d), WorkflowVersions (50/Workflow), SupportEvents (90d), Notifications (90d), TriggerReceipts (7d) — opt-out via `Retention:*:Enabled: false`. IdempotencyKeys (24h, fixe TTL) läuft immer.

### Datenbank-Verfügbarkeit (Laufzeit-Ausfall, ADR 0011)

Prozessweiter In-Memory-Breaker (`NodePilot.Data.Availability`): fällt die DB zur Laufzeit aus, antwortet `/api` sofort `503 DATABASE_UNAVAILABLE` (+ `Retry-After`, `reason`, `retryable`) statt Minuten zu hängen; eine Sonde (`SELECT 1` auf eigener ungepoolter Verbindung — **nie** `CanConnectAsync`, ein hängender Server besteht das) erkennt und schließt; Erholung automatisch nach 2 Erfolgen inkl. gezieltem `ClearPool`. **Einzelschreiber-Regel: nur die Sonde publiziert `Available`, EF-Interceptors degradieren nur.** Ein Command-Timeout öffnet nie direkt — er *armt* die Sonde (`Armed`-Zustand; eine langsame Abfrage ist kein Ausfall und bleibt `DATABASE_TIMEOUT`). Klassifizierung in `DbErrorClassifier.Classify` (geordnete Präzedenz; **Kontext schlägt Form** — Npgsql liefert Connect- und Command-Timeout in identischer Exception-Gestalt). `BreakerAware*ExecutionStrategy` ersetzt `EnableRetryOnFailure` (wiederholt nie Command-Timeouts, global; 53300/Deadlock-Retry bleibt). Hintergrunddienste parken via `WaitUntilServableAsync` (wirft nie; Gate **über** der Leader-Prüfung), Trigger-Fires werden gezählt gedroppt statt gepuffert; Engine pausiert vor jedem *neuen* Step und korrigiert die zeilenzählende Finalisierung per In-Memory-Flag (sonst würde eine gescheiterte Activity nach Erholung als Succeeded finalisiert). Health: `/healthz/ready` = schneller 503 fürs LB, `/healthz/database` = **immer 200** mit Status fürs SPA (Banner + TopBar-Ampel; Poll 15 s/3 s). Boot bleibt fail-closed (`Database:StartupWaitSeconds`) — der Boot-Block wird bewusst **nicht** nachgeholt (StartupRecovery/Setup-Token, siehe ADR). Config `Database:Probe:*`, `Database:ConnectTimeoutSeconds` (liegt gemessen **doppelt** auf dem kritischen Pfad), `Database:AuthReadTimeoutSeconds` (wie alle Availability-Budgets typisierte, restart-pflichtige Boot-Config; bewusst **kein** `SettingsSchema`-Eintrag — der Connection-String gehört auf keine HTTP-Fläche). Details: `docs/adr/0011-*.md` + `docs/claude-reference.md`.

**Bekannte Falle (bewusst so):** `HostOptions.BackgroundServiceExceptionBehavior` bleibt auf `StopHost`, und die sieben Retention-Dienste haben ihren breiten Catch eine Ebene *unter* der host-fatalen Grenze — Code, der in deren `RunIterationAsync` außerhalb des inneren `try` landet, kann den Host töten.

## API Endpoints

Routen + Rollen-Gating stehen an den Controllern in `src/NodePilot.Api/Controllers/` (`[Route]`/`[Authorize]`) — dort nachsehen statt hier spiegeln. Die Rollen-Matrix der sicherheitsrelevanten Endpoints steht unter `## Autorisierung`; Semantik der nicht offensichtlichen Endpoints unter `## Workflow-Kontrollfluss` und in `docs/claude-reference.md`.

**Nicht getroffene `/api`-Pfade antworten `404 application/problem+json`** (`code: NOT_FOUND`), nicht mit dem SPA-Bundle — ein eigener `MapFallback("/api/{**rest}")` steht vor `MapFallbackToFile("index.html")`. Betrifft Tippfehler, verschobene Endpoints und Routenparameter, die ihre Typ-Constraint verfehlen (`/api/global-variables/nicht-eine-guid`). Deep-Links außerhalb von `/api` gehen weiterhin an die SPA.

## Workflow-Kontrollfluss

| Endpoint | Semantik |
|---|---|
| `POST /execute` | Startet Lauf. Body: `{"parameters": {}, "timeoutSeconds": N, "debug": bool}`. 202 + ExecutionId. |
| `POST /enable` / `/disable` | Kill-Switch. `enable` verlangt einen lock-freien Workflow — jeder bestehende Lock (auch der eigene) → 423. `disable` ignoriert Locks. |
| `POST /cancel-all` | Cancelt alle `Running`- **und** `Pending`-Executions des Workflows. |
| `PUT /concurrency-limit` | Setzt `MaxConcurrentExecutions` (1..1000, `null` = unbegrenzt). Body: `{"maxConcurrentExecutions": N}` — Property ist **Pflicht** (fehlend → 400, sonst würde `{}` das Limit still löschen). `0` wird abgelehnt. Operativ: kein Edit-Lock, kein Version-Bump, kein History-Snapshot. |
| `POST /executions/{id}/cancel\|retry\|resume` | Einzelner Lauf. Resume-Body: `{"stepId": "<node-id>", "mode": "continue"\|"stepOver"\|"stop", "overrides": {}}` — `stepId` ist **Pflicht** (`ResumeDebugRequest`), ohne ihn 400. |

**Disable+cancel-all = Quarantäne.**

**Per-Workflow-Parallelität (SCOrch „max running instances"):** `Workflow.MaxConcurrentExecutions`
begrenzt, wie viele Läufe *eines* Workflows gleichzeitig laufen — über **alle** Aufrufer hinweg
(manuell, Trigger, Webhook, External-Trigger, `startWorkflow`, `forEach`; Debug eingeschlossen).
Ist das Limit erreicht, wird **eingereiht statt abgelehnt**: über die Outbox dispatchte Läufe
bleiben `Pending` (`DeferredByConcurrencyLimit`), die synchronen Sub-Workflow-Pfade warten am
Step- bzw. Per-Item-Timeout. Ein Zähler für beide Wege: `IWorkflowConcurrencyGate` (Core,
In-Memory-Impl in der Engine, Singleton — Vorbild `ISubWorkflowGate`). Der Dispatch-Claim
überspringt Workflows am Limit, sonst verhungern andere hinter deren Rückstau. Nicht versioniert
und nicht im Update/Publish-Body — Details `docs/claude-reference.md`.

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

- **Zwei Arten:** Custom-Regeln (`Kind=Custom`, Execution-Events, Filter-AST = derselbe `ConditionEvaluator` wie Edge-Conditions) und System-Policies (`Kind=System`, ADR 0008 — 14 katalogisierte `ISystemAlertSource`s für Infra-/Signal-/Security-Alerts, ausgewertet vom `SystemAlertEvaluator`; `audit-event` macht das Audit-Log in-product alarmierbar).
- **Kern:** Entität `NotificationRule` (+ Routes/Targets) + getrennte State-Tabellen (Suppression, Delivery-Ledger `NotificationDeliveryAttempt`, Dispatcher-Watermark). `NotificationDispatcher` (leader-gated, ~30 s) matcht → suppressed (Cooldown/Flap) → persistiert Pending-Attempt VOR jedem I/O → sendet (exactly-once pro `(rule, route, occurrence)`).
- **Governance:** Read Admin/Op; alle Mutationen + Test-Fire Admin-only; neue Regeln entstehen disabled. Secrets in Responses redigiert.
- **Frontend/CLI/MCP:** Seite `/alerts` (2 Tabs, wiederverwendeter `ConditionBuilder`), `np alerting` + `np system-alert`, MCP-Tools für beides.

## Architektur-Konventionen

- **Neue Activity:** Klasse in `Engine/Activities/`, `IActivityExecutor` implementieren — Auto-Discovery via `AddNodePilotActivities()` (scannt `NodePilot.Engine`), **keine** DI-Verdrahtung in `Program.cs` nötig. Die UI-Seite (Palette-Eintrag, `*Config`-Komponente, handgepflegter Katalog-Spiegel) ist Pflichtteil derselben Änderung — Mechanik in `src/nodepilot-ui/CLAUDE.md`. **Ebenfalls Pflicht:** ein Eintrag in `src/NodePilot.Core/Activities/Embedded/activity-config-reference.json` (Purpose + Config-Keys + optionale `promptNotes`). Daraus werden AI-Prompt-Katalog *und* MCP-Config-Tools gespeist; `ActivityConfigReferenceTests` prüft Vollständigkeit **und** dass jeder dokumentierte Key vom Executor wirklich gelesen wird — ein erfundener Key erzeugt sonst Nodes, die korrekt aussehen und nichts tun.
- **Neuer API Controller:** In `Api/Controllers/`, DTOs in `Api/Dtos/`. **Immer parallel** CLI-Command *und* MCP-Tool anlegen — Mechanik in `src/NodePilot.Cli/CLAUDE.md` bzw. `src/NodePilot.Mcp/CLAUDE.md`.
- **Frontend:** Seiten/Nodes/i18n/State-Konventionen in `src/nodepilot-ui/CLAUDE.md`. **Farben immer über Design-Tokens/CSS-Variablen** — nie Tailwind-Farbliterale hardcoden (`text-gray-900`, `bg-white` brechen die Dark-Skins). Natives `<select>`: `option:hover` ist in Chromium nicht stylbar → Custom-Dropdown-Komponente verwenden.
- **Models/Interfaces:** Immer in `NodePilot.Core`
- **Doc-Sync:** Feature-Änderungen halten alle Doku-Flächen synchron — README, `docs/*.md`, `docs/testing/E2ETests.md` + `e2e/README.md` und die Doku-Website `src/nodepilot-docs-ui/content/` (eigener kuratierter Korpus, kein Render von `docs/`). **Die Website ist zweisprachig:** jede Seite existiert unter `content/de/<pfad>.md` **und** `content/en/<pfad>.md` — beide Bäume sind deckungsgleich, eine neue Seite braucht beide Dateien plus je einen Titel-Eintrag in `src/i18n/locales/{de,en}.json`. Markdown-Querverweise werden **ohne** Sprach-Präfix geschrieben (`../enterprise/folder-rbac`); die aktive Sprache setzt `DocPage` zur Laufzeit davor. Der AI-Wissenskorpus (`NodePilot.Api.csproj`) zieht bewusst **nur** `content/en/`. **Die Website hat zwei Auslieferungen:** GitHub Pages (`docs-pages.yml`) und `wwwroot/docs` im Server-Artefakt bzw. Desktop-Paket, das die API unter `/docs` anonym bedient (`Hosting/DocsSiteSetup.cs`) — deshalb darf `index.html` der Website **kein Inline-`<script>`** enthalten (CSP `script-src 'self'`, Guard: `document-head.test.ts`).

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
- `{{manual.NAME}}` — Trigger-Input des Laufs (dieselben Keys liegen zusätzlich als `param.*` des Trigger-Nodes an). Deklarierte `manualTrigger`-Parameter werden beim Laufstart mit ihrem `default` in den Namespace geseedet, wenn der Aufrufer sie weglässt — beide Schreibweisen liefern damit denselben Wert, und die Execution-Zeile protokolliert den effektiven Input. Ein deklarierter Parameter **ohne** Default bleibt abwesend (und die Referenz scheitert).
- Kein `outputVariable` → Step-ID wird verwendet: `{{step-123.output}}`

**Ein veröffentlichter Wert hat genau einen Besitzer (SCOrch-Modell).** Die qualifizierte Form `{{aktivität.param.name}}` ist die verbindliche und löst bei **jedem** Nachfahren auf, nicht nur beim direkten Nachfolger. Daneben gibt es den unqualifizierten Kurznamen (im `runScript` als `$name` injiziert) — den legt der Resolver nur an, wenn **genau eine** Aktivität auf dem Vorgängerpfad diesen Namen veröffentlicht. Bei zwei Publishern wird **nichts** gebunden, statt einen Gewinner zu ziehen: der kam früher aus der Hash-Reihenfolge der Ahnenmenge und konnte sich nach einem Prozess-Neustart ändern. Der Canvas-Linter meldet das als `dup-published-param`; im Skript bleibt der Wert über `$Params['stepA.param.name']` oder ein `{{stepA.param.name}}`-Template erreichbar. Statische Katalog-Outputs (`exitCode` an jedem `runScript`) kollidieren bauartbedingt und werden **nicht** gemeldet.

**Contract-Garantie:** Drei Muster im `VariableResolver` — `GlobalsPattern` (`globals.NAME`), `ManualPattern` (`manual.NAME`) und `StepPattern` mit genau vier Tails (`output`, `error`, `success`, `param.X`). Andere Tails bleiben als Literal. Unresolved → granulare Diagnostik (StepRunner T-7.1); ein unbekannter Trigger-Input bekommt einen eigenen Befund („Unknown trigger input(s)") statt als fehlender Step gemeldet zu werden. Ein neuer Namespace braucht ein eigenes Muster: `manual.NAME` hat als Tail einen frei gewählten Namen und kann von `StepPattern` prinzipiell nicht getroffen werden — genau daran scheiterte es bis 1.2.7 still (Platzhalter blieb stehen, Step meldete Erfolg).

**Sichtbarkeits-Scope (Ahnen-only):** Ein Step sieht **ausschließlich** Ergebnisse seiner Graph-Vorgänger (`AncestorIndex` + `AncestorScopedResults`, einmal pro Lauf aus `ReverseAdjacency`). Eine Referenz auf einen Knoten aus einem **parallelen Zweig** löst nie auf — auch dann nicht, wenn dieser Zweig zufällig schon fertig ist. Vorher entschied das Timing darüber, ob derselbe Workflow lief oder mit „Unresolved template variable" scheiterte. Designer-Variablenpicker, Step-Tester und MCP-Analyzer scopen ohnehin schon auf Ahnen; die Engine zieht damit nach. **Ein Ahne ohne Ergebnis bleibt unauflösbar** — das ist bei `junction`/waitAny (der unterlegene Zweig läuft nicht) und bei übersprungenen Knoten korrekt so.

**Out-of-Scope-Gate gilt auch für `runScript`/Custom Activities.** Beide sind von der allgemeinen T-7.1-Prüfung ausgenommen (sie lösen ihre Templates selbst mit PS-Quoting auf, ein übriges `{{...}}` kann legitimer Skripttext sein) — **nicht** aber vom Cross-Branch-Fall: eine Referenz auf einen Knoten **des Graphen**, der nicht auf dem Vorgängerpfad liegt, ist nie legitimer Skripttext. Maßgeblich ist die Graph-Zugehörigkeit, **nicht** ob der Knoten schon ein Ergebnis hat — die frühere Ergebnis-Prüfung machte das Gate zum Rennen: derselbe Verweis war fatal, wenn der Nachbarzweig zufällig zuerst fertig war, und wurde sonst durchgewinkt. Ohne dieses Gate lief `$wert = {{sibling.output}}` als Literal in PowerShell, der Step meldete **Erfolg** und schrieb `Ergebnis: {sibling.output}` — grün mit Platzhalter statt Wert. Tippfehler/unbekannte Steps bleiben bei `runScript` weiterhin tolerant.

**Strukturierter Output:** `runScript` captured die Variablen, die das Skript **selbst zuweist**, als `param.*`. `$hostName = ...` → `{{step.param.hostName}}`. **Nicht** dabei: durchgereichte Upstream-Parameter (ein Skript, das `$hostName` nur liest, publiziert es nicht als eigenen Output) und PowerShell-Automatiken/Preference-Variablen (`$_`, `$foreach`, `$Matches`, `$VerbosePreference`, … — Liste in `NodePilot.Core.Activities.PowerShellReservedVariables`). Umgesetzt über zwei geschachtelte Scopes im Wrapper: Injektion außen, User-Skript innen, `Get-Variable -Scope Local` sieht damit genau das Zugewiesene.

**RunScript Auto-Quoting:** `{{step.output}}` wird als Single-Quoted String eingesetzt. Im Script `$x = {{step.output}}` schreiben, NICHT `$x = '{{step.output}}'`.

**RunScript Erfolg (fehler-basiert, einheitlich über alle Engines):** Ein Step scheitert **nur** bei einem terminierenden PowerShell-Fehler (`throw` / `Write-Error` unter dem `Stop`-Wrapper). Ein `exit N` macht den Step **nicht** rot. Opt-in `config.successExitCodes` (komma-separiert) macht non-zero Exit-Codes wieder zum Fehlschlag. Der Exit-Code liegt immer als `{{step.param.exitCode}}` an und bezieht sich auf das letzte native Kommando **dieses** Skripts — der Wrapper setzt `$LASTEXITCODE` und `$Error` vor dem Skript zurück, weil beide sonst im prozesslang offenen Runspace-Pool aus einem fremden Lauf überleben. **Engine-Asymmetrie:** `successExitCodes`/`param.exitCode` greifen für Native-Command-Codes (`$LASTEXITCODE`) in allen Engines; ein script-eigenes `exit N` ist nur im Prozess/isoliert-Pfad als Wert sichtbar (Runspace kann `exit` nicht beobachten → `0`). Ein **Parse-Fehler** ist ein terminierender Fehler und damit auf jeder Engine rot: der Wrapper schreibt vor der ersten Anweisung einen `###NODEPILOT_START###`-Marker, und die Prozess-Engines werten dessen Fehlen als „Skript lief nie" (der Exit-Code taugt dafür nicht, weil ein gewolltes `exit N` die Abschluss-Marker ebenfalls überspringt). Impl: Wrapper-`try/catch` + `###NODEPILOT_ERROR###`-Marker, zentrales Gating in `RunScriptActivity`.

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

**Config-Vertrag (eine Vokabel, zwei Laufzeiten):** Jeder Trigger-Node wird von zwei Pfaden gelesen — Node-Executor (`Engine/Triggers/`, manueller Diagnose-Lauf) und Hintergrundquelle (`Scheduler/Sources/`, feuert real). Beide parsen über die geteilte Schicht `Core/Triggers/` (`EventLogTriggerSettings`, `DatabaseTriggerSettings`): Keys, Defaults, Validierung, Matching, Connection-Auflösung liegen dort **einmal**. Neuer Trigger-Key = Settings-Klasse + `activity-config-reference.json` + Designer-Feld; `TriggerContractParityTests` bricht sonst. Alias-Keys (`level`→`entryType`, `intervalSeconds`→`pollingIntervalSeconds`) bleiben gültig und werden von beiden Pfaden gelesen — exakter Key gewinnt. `databaseTrigger` feuert bei **Sentinel-Änderung** (erste Spalte der ersten Zeile), nicht pro Zeile; `lookbackMinutes` gilt nur für den manuellen Lauf. Details: `docs/claude-reference.md`.

**Kein Nachholen nach Neustart oder Failover.** Der durable Cursor je Trigger-Node dient der
Deduplizierung und der Diagnose, nicht dem Backfill: Beim Start spult jede Quelle ihn auf den
aktuellen Stand vor, ohne zu feuern, und meldet das übersprungene Fenster in einer Log-Zeile plus
`nodepilot.scheduler.triggers.fires_skipped`. Der **laufende** Betrieb ist davon unberührt — ein
beobachtetes Signal, das an einer DB-Störung scheitert, wird weiterhin wiederholt, bis es angenommen
ist, und FileWatcher/EventLog holen Benachrichtigungen nach, die ihnen im Betrieb entgehen. Preis:
Dateien und EventLog-Einträge aus einem Stillstandsfenster werden nicht verarbeitet. Details je
Quelle: `docs/claude-reference.md`.

**Selbstheilung (Laufzeit-Tod einer Quelle):** Jede `ITriggerSource` beantwortet `Health` — vertraglich ein **reiner In-Memory-Read** (der Orchestrator wertet ihn sequenziell für *jeden* Trigger im 5-s-Pass aus; ein blockierender Probe dort legt die Reconciliation aller Workflows lahm). Meldet eine Quelle `unhealthy`, wirft der Orchestrator sie raus und der vorhandene Add-Pfad baut sie mit Exponential-Backoff (5 s→300 s, unbegrenzt) neu auf — ein `fileWatcherTrigger`, dessen UNC-Freigabe verschwindet, läuft also von selbst wieder an, sobald der Pfad zurück ist. Ein `FileSystemWatcher` lässt sich dabei **nicht** in-place re-armen (`EnableRaisingEvents` liest auf der Leiche noch `true`, der Setter ist ein No-Op) — nur eine frische Instanz hilft. Buffer-Overflow gilt bewusst **nicht** als Fault (Runtime stellt den Read neu aus, Evicten würde flappen). Alertbar über die System-Policy `trigger-unhealthy`; Details + Config-Keys: `docs/claude-reference.md`.

**webhookTrigger-Hardening:** Verifizierung per `signatureMode` — `header` (default, `X-Webhook-Secret`) oder `nodepilot-hmac-v2` (HMAC-SHA256 über Freshness-Metadaten + Methode + Pfad + kanonische Query + Raw-Body; verlangt CSPRNG-Secret ≥32 Bytes, `X-NodePilot-Timestamp` und einmalige `X-NodePilot-Delivery-Id` mit clusterweitem Replay-Guard/5-min-Fenster). Legacy `hmac` (Body-only, GitHub/GitLab/Alertmanager-nativ) wird abgelehnt → Adapter nötig. `fieldMappings` extrahiert JSON-Body-Felder per JSONPath als eigene `manual.*`-Params. Details: `docs/claude-reference.md`.

## WorkflowEngine — Execution-Modell

- **Event-driven:** Queue + `inFlight`-Dict. Roots = **ausschließlich Trigger-Nodes** (`manualTrigger`/`scheduleTrigger`/`webhookTrigger`/`fileWatcherTrigger`/`databaseTrigger`/`eventLogTrigger`); ohne (aktiven) Trigger → 0 Roots → Execution `Failed`. **Kein** `inDegree==0`-Fallback. Disabled Trigger nie Root. Orphan-/Nicht-Trigger-Activities ohne eingehende Edge laufen **nie** → `Skipped`.
- **Expliziter Fan-in:** Nur eine `junction` darf mehrere eingehende Edges haben; jede andere Activity hat maximal eine. Designer und SCOrch-Import fügen bei Bedarf eine `waitAll`-Junction ein, die Strukturvalidierung schützt Save/Publish/API. Junction-Conditions werden über alle relevanten Eingänge ausgewertet, nicht über die zuletzt abgeschlossene Edge.
- **Cancellation:** `_runningExecutions` Dict (Guid → CTS).
- **Per-Step-DI-Scope:** eigener Scope pro Step → scope-lokaler `DbContext`.
- **Startup-Reconciler:** `Running`/`Paused` und inkonsistente `Pending` ohne Dispatch Intent → `Cancelled`; `Pending` mit durablem Outbox-Intent bleibt erhalten und wird neu geleast.

## Build & Test

Standard-Invocations (`dotnet build|test`, in `src/nodepilot-ui` die `package.json`-Scripts). Backend nutzt Central Package Management (`Directory.Packages.props`).

**Konventionen:**
- **Tests sind Pflicht.** Jeder relevante Code-Change braucht passenden Test-Code in derselben Änderung.
- Coverage-Gates: Backend Line >= 85 % / Branch >= 70 % — **erzwungen in `.github/workflows/ci.yml`, das ist die einzige autoritative Zahl** (Ratsche — nur anheben, nie senken; gemessen 2026-07-27: 89,0/74,1). Frontend siehe `vitest.config.ts`. Messverfahren + Assembly-Filter: `docs/claude-reference.md` (Abschnitt „Coverage-Messung"). Genuin untestbare Infrastruktur trägt `[ExcludeFromCodeCoverage]` **mit Begründungskommentar** am Typ bzw. an der Methode; `coverage.runsettings` zieht das Attribut aus dem Nenner.
- Naming: `MethodName_Scenario_ExpectedResult`
- Remote-Layer (WinRM) IMMER gemockt.
- DB-Tests: SQLite in-memory.

### Testumfang pro Änderung

**Tests schreiben ≠ alle Tests ausführen.** Die Pflicht oben gilt unverändert für das *Schreiben*; lokal *ausgeführt* wird nur, was die Änderung betrifft. Die Voll-Suite ist gemessen unverhältnismäßig (6.277 Backend-Testfälle, 218 Vitest-Dateien, 74 E2E-Specs) und liefert lokal kein neues Signal: das Netz hängt an `ci.yml`, das auf **jedem PR und jedem Push auf main** läuft (Coverage-Gate + E2E eingeschlossen).

**Der Nightly ist kein verlässlicher zweiter Boden.** Er läuft als Windows-Task um 22:00 gegen den ausgecheckten Baum und wird verpasst, sobald die Maschine dann aus ist — gemessen am 2026-08-31: letzter Lauf 2026-08-22, acht verpasste Läufe. Wer sich auf ihn beruft, prüft vorher `C:\temp\nodepilot-nightly\latest.md` auf sein Datum.

**Ausnahme, seit 2026-08-20:** Ein PR, der **ausschließlich** `*.md` oder `docs/images/**` anfasst, überspringt Frontend, Desktop und E2E — der vorgeschaltete `changes`-Job entscheidet das. **Backend und docs-ui laufen immer**, weil Markdown für sie eine Eingabe ist: `DocumentationCountsTests` liest README, CLAUDE.md, `docs/mcp-server.md` und sechs Seiten der Doku-Website, `MonitoringDeploymentSecurityTests` liest README + `grafana/README.md`, der Sprach-Parity-Guard liest `content/{de,en}`. Ein pauschales `paths-ignore: ['**/*.md']` hätte genau die README-Brüche durchgelassen, die CI beim Kürzen gefangen hat. Pushes auf `main` laufen **immer** vollständig; jeder Fehlerpfad der Erkennung endet bei „alles ausführen". CodeQL filtert separat über `paths-ignore` — es analysiert nur C#/TS, dort ist Markdown wirklich irrelevant.

Default bei Feature-Arbeit:

```powershell
# Backend — ein Projekt, eine Klasse/ein Namespace
dotnet test tests/NodePilot.Engine.Tests --filter "FullyQualifiedName~WorkflowCallGraphBuilder"

# Frontend — einzelne Datei oder Verzeichnis
cd src\nodepilot-ui; npx vitest run src/__tests__/lib/opsTimeline.test.ts

# E2E — eine Spec, gegen laufenden Dev-Server (kein Build)
cd src\nodepilot-ui; npx playwright test e2e/operations.spec.ts --config=playwright.dev.config.ts
```

**Eskalation nur bei Anlass, nie prophylaktisch:**

1. **Scoped** (Default) — Filter auf die geänderte Klasse/Komponente.
2. **Projekt-Suite** (`dotnet test tests/NodePilot.Api.Tests`) — wenn die Änderung *innerhalb* des Projekts quer liegt: geteilte Basisklasse, DI-Verdrahtung, `Program.cs`.
3. **Voll-Suite** — nur bei (a) expliziter Bitte des Users, (b) Release-Cut/Direct-Push auf main, (c) inhärent globaler Änderung (`Directory.Packages.props`, Dependency-Bump, projektweites Refactoring).

**Coverage lokal nie messen** — `--collect:"XPlat Code Coverage"` bzw. `npm run test:coverage` sind CI-Jobs, kein lokaler Schritt.

**Reporting:** nicht „alle Tests grün", sondern **welche** gelaufen sind (Projekt + Filter + Anzahl). Was nicht lief, wird als „von CI abgedeckt" benannt, nicht verschwiegen.

### Guard-Tests: auf Trigger, nicht auf Verdacht

Scoped Testing übersieht genau eine Fehlerklasse — die Parity-/Drift-Tests, die Konsistenz zwischen weit auseinanderliegenden Dateien erzwingen. Sie liegen über sechs Testprojekte verteilt, sind also nicht „mal eben zusammen" ausführbar. Deshalb: **eine Auslöser-Fläche angefasst → genau diesen Test fahren**, statt sicherheitshalber alles.

| Angefasst | Guard-Test | Projekt |
|---|---|---|
| Activity + `activity-config-reference.json` + Frontend-Katalog-Spiegel | `ActivityCatalogTests`, `ActivityConfigReferenceTests`, `ActivityCatalogFrontendSyncTests` | Engine.Tests |
| Neue EF-Migration / Designer-Postprocessing | `MigrationDriftTests` | Data.Tests |
| `*.csproj`-Referenzen / Dep-Graph | `DependencyDirectionTests` | Api.Tests |
| Neuer Audit-Code | `AuditActionsCatalogTests` | Api.Tests |
| API-DTO (+ CLI-Spiegel) | `ApiDtoParityTests` | Cli.Tests |
| Trigger-Config-Key | `TriggerContractParityTests` | Engine.Tests |
| `SettingsSchema.cs` / Admin-Settings-UI | `AdminSettingsFrontendSyncTests` | Api.Tests |
| AI-Prompt-Katalog | `PromptCatalogDriftTest` | Ai.Tests |
| Alerting-Katalog / System-Policies | `AlertingCatalogFrontendSyncTests`, `SystemAlertCatalogTests` | Engine.Tests |
| Workflow-Analyzer (`WorkflowAnalyzer`/`WorkflowDataBusAnalyzer` in Core — MCP **und** AI-Chat) | `WorkflowAnalyzerFrontendParityTests` | Engine.Tests |
| Template-Grammatik / Variable-Resolution | `TemplateGrammarParityTests` | Engine.Tests |
| Metrics-Dashboard-Katalog | `MetricsDashboardCatalogTests` | Api.Tests |
| `vite.config.ts`-Proxy / Dev-Ports | `AppSettingsHygieneTests` | Api.Tests |
| `index.css` / `designer-atelier.css` designer-light tokens | `designerLightParity.test.ts` | nodepilot-ui |
| Font-Tokens / Monaco-Stack | `fontTokens.test.ts` | nodepilot-ui |

**E2E (Playwright):** hermetische Specs in `src/nodepilot-ui/e2e/`, alle APIs gemockt (kein Backend/Postgres nötig). Konventionen: `src/nodepilot-ui/CLAUDE.md` + `src/nodepilot-ui/e2e/README.md`.

**Desktop-Shell:** `src/nodepilot-desktop` hat eine eigene vitest-Suite (node-Env) für die reine Logik — `config.ts` (desktop.json-Handoff-Validierung), `security.ts` (Cert-Pinning, Navigations-Containment) + `skins.ts` (Skin-Icon-Auflösung aus der Favicon-Meldung der SPA). `npm run test:run`; eigener CI-Job `desktop`.

**Nightly:** Windows-Task `NodePilot Nightly Tests` (täglich 22:00) fährt via `scripts/nightly-tests.ps1` alle vier Suiten (je 1× Retry bei Flake), Report nach `C:\temp\nodepilot-nightly\` (+ `latest.md`). Das Skript gibt vorm Rebuild Port 5000 frei + killt verwaiste `testhost`-Prozesse. Manuell: `powershell -File scripts/nightly-tests.ps1`; Zeit ändern: `scripts/register-nightly-task.ps1 -Time HH:mm`.

## Clients (`np` CLI + `nodepilot-mcp`)

Beide sind reine HTTP-Clients gegen die REST-API — **kein** eigener Backend-Pfad. Beide werden **von beiden Installern mitgeliefert**: Server-Artefakt und Desktop-Paket enthalten `tools\np\np.exe` und `tools\mcp\nodepilot-mcp.exe`; `Install-NodePilot.ps1`/`Update-NodePilot.ps1` hängen `tools\np` idempotent an die Maschinen-`PATH` (gemeinsame Helfer in `deploy/MachinePath.ps1`, Uninstall entfernt sie wieder), der MCP-Server wird per absolutem Pfad in `.mcp.json` referenziert und bewusst **nicht** in die PATH aufgenommen. Aus einem Source-Checkout weiterhin per `dotnet publish`; **keine** `dotnet global tool`s — `PackAsTool` verträgt das geerbte `net10.0-windows`-TFM nicht (NETSDK1146, siehe `docs/roadmap.md`-Sperrvermerk). Der MCP-Server ergänzt In-Proc-Analyse gegen `NodePilot.Core` (101 Tools, 3 Resources, stdio) und reused die DPAPI-Session der CLI (`np auth login`).

**Jeder neue API-Endpoint braucht beide Clients.** Mechanik, Befehlsbereiche und Tool-Katalog: `src/NodePilot.Cli/CLAUDE.md`, `src/NodePilot.Mcp/CLAUDE.md`, `docs/mcp-server.md`, `docs/claude-reference.md`.

## Autorisierung

| Endpoint | Admin | Operator | Viewer |
|---|---|---|---|
| `GET /api/{workflows,executions,machines}` | ✓ | ✓ | ✓ |
| `POST /api/workflows`, `PUT`, `POST /{id}/duplicate\|execute` | ✓ | ✓ | ✗ |
| `POST /api/machines`, `PUT` | ✓ | ✓ | ✗ |
| `GET\|POST\|PUT /api/credentials` | ✓ | ✓ | ✗ |
| `POST /api/executions/{id}/cancel` | ✓ | ✓ | ✗ |
| `DELETE /{workflows,machines,credentials}/{id}` | ✓ | ✗ | ✗ |
| `DELETE /api/shared-workflow-folders/{id}` | Folder-`Edit`, nur leer | Folder-`Edit`, nur leer | ✗ |
| `DELETE /api/shared-workflow-folders/{id}?recursive=true` | ✓ | ✗ | ✗ |
| `GET /api/alerting/rules`, `POST /preview-filter` | ✓ | ✓ | ✗ |
| `POST/PUT/DELETE /api/alerting/rules`, `POST /{id}/enable\|disable\|test-fire` | ✓ | ✗ | ✗ |
| `POST /api/trigger/{name}` | API-Key via `X-Api-Key`-Header |

**Ordner-Löschen hat zwei Sicherheitsgrenzen:** Ein leerer Ordner bleibt eine Folder-`Edit`-
Mutation. `?recursive=true` entfernt dagegen auch Workflows und deren Execution-Historie und ist
deshalb wie `DELETE /api/workflows/{id}` global Admin-only. Die Folder-Capabilities liefern dafür
`canDelete` getrennt von `canEdit`; die UI darf den rekursiven Delete nicht aus `canEdit` ableiten.

**Der Global-Variablen-Ordnerbaum kennt dieselbe Mechanik, bleibt aber Admin-only.**
`DELETE /api/global-variable-folders/{id}[?recursive=true]` löscht Unterordner samt Variablen,
hängt aber wie jede Globals-Mutation an `[Authorize(Roles = "Admin")]` — es gibt dort kein
Per-Ordner-RBAC, an dem sich lockern ließe. Frontend-seitig teilen sich beide Bäume die
Löschmechanik (`hooks/useFolderBulkDelete.ts`, `components/common/FolderBulkBar.tsx`,
`lib/folderSelection.ts`); die Baum-Komponenten selbst sind weiterhin Klone.

Initial-Admin: erster Login bei leerer DB (One-Shot-Token `admin-setup.token`).

## Security

- **Session:** absolute Lebensdauer **8h** (`Authentication:SessionAbsoluteLifetimeHours`, default 8; `AuthSessionIssuer`). Refresh verlängert die absolute Grenze **nicht**. `jti`-Revocation. Key aus `Jwt:Key` oder auto-generiertes `jwt-secret.key`.
- **Auth-Pfade:** Local-BCrypt (`Authentication:LocalLoginMode`, Produktionsdefault **`BreakGlassOnly`** — nur explizit markierte Notfallkonten; `Enabled`/`Disabled` möglich) + LDAP (`Authentication:Ldap:Enabled`) + Windows-Negotiate (`Authentication:Windows:Enabled`) + OIDC (`Authentication:Oidc:Enabled`, release-gated, + SCIM-Controller). Alle konvergieren auf JWT-Cookie + CSRF-Token. Siehe `docs/ldap-windows-sso.md`.
- **External Trigger:** `X-Api-Key` wird bevorzugt gegen SHA-256-Hashes unter `ExternalTrigger:Keys:<id>` geprüft; jeder Eintrag hat eine GUID-only `AllowedWorkflowIds`-Liste. Die komplette `Keys`-Map kommt atomar aus dem höchstprioren Provider, der sie deklariert (`Keys: {}` widerruft alle niedrigeren Keys); auch Scope-Arrays sind provider-atomar (`[]` = deny-all). Zusätzlich braucht der Workflow einen aktiven `manualTrigger`. Legacy-`ApiKey` ist ohne eigene `AllowedWorkflowIds`-Liste inert. Idempotency wird per kanonischer Integration-ID + Key-Fingerprint + Workflow domain-separiert; die DB speichert nur den Digest.
- **Rate-Limiting:** login 50/Min, refresh 20/Min, webhook 60/Min, trigger 30/Min, ai-generate 20/Min, audit 60/Min, backup 10/Min (per-IP, Sliding-Window).
- **Output-Redaction:** `OutputRedactor` maskiert Secrets. Immer aktiv. Custom-Patterns via `Logging:Redaction:Patterns`.
- **Localhost-Bypass / Operator-Trust:** ohne Credentials läuft in-process unter der NodePilot-Service-Identität. `Operator` ist bewusst ein vertrauenswürdiger Automation-Author und darf solchen Workflow-Code publizieren/ausführen. Folder-RBAC ist keine Code-Sandbox. **Produkt-Feature, keinen Require-Target-Guard einziehen.**
- **Security-Headers (Non-Dev):** HSTS, CSP, X-Frame-Options=DENY, nosniff, Referrer-Policy.
- **SignalR-Auth:** httpOnly `np_auth`-Cookie wird beim WebSocket-Upgrade automatisch mitgeschickt (nur `/hubs/`); kein `?access_token=`-Querystring.
- **REST-API-Proxy:** `RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode`.

Hardening-Flags: `Remote:RequireWinRmSsl`, `RestApi:BlockPrivateNetworks`, `RestApi:AllowedHosts` (exakte Outbound-Allow-Liste für proxied `restApi`-Ziele/Redirects; Ausnahme von `BlockPrivateNetworks`), `WaitForCondition:AllowedHosts` (**eigene** Liste für die PowerShell-Probes `portOpen`/`httpOk` — bewusst getrennt, damit „eigenen Dienst prüfen" nicht zugleich `restApi` zu Loopback öffnet; default `["localhost"]`; **alleinige** Autorität für beide Probe-Typen, `RestApi:*` wird nicht mitgeprüft — Link-Local bleibt gesperrt), `FileSystemOperation:RejectTraversal`, `SqlActivity:RequireConnectionRef`, `StartProgram:DisallowShellExecute`, `Trigger:Database:RequireConnectionRef`, `Security:StrictAllowedHosts`, `Webhook:RequireSecret` — **default `true`** (hardened; fehlender Key liest als `true`, `appsettings.Development.json` relaxt auf `false`). `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` + `Database:AllowInsecureTls` default `false`. Details: `docs/claude-reference.md`.

## Admin-Settings Hot-Reload

Admin-Settings-Saves persistieren atomar nach `appsettings.runtime.json` (`reloadOnChange: true`). Pro Sektion trägt `SettingsSchema.cs` ein `IsHotReloadable`-Flag; nur `false`-Sektionen setzen den Restart-Marker (UI: emerald `HotReloadHint` vs. oranger `RestartBanner`). 13 Sektionen sind hot-reloadable, 9 restart-pflichtig; harter Kern (JWT, DB, Kestrel, Cluster/HA, `Remote:Provider`) bleibt boot-fixed.

**Dimensionierung:** `Performance:ManualTuning` (default **`false`**) entscheidet, ob `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` und `ExecutionDispatch:WorkerCount` aus erkannter CPU+RAM abgeleitet (`PerformanceSizing` in Core, Boot-Snapshot via `PerformancePlanFactory`) oder verbatim aus der Config genommen werden. Aus = hardware-adaptiv, die konfigurierten Zahlen bleiben als inertes Preset stehen. Restart-pflichtig. Wartender Dispatch liegt in der DB-Outbox; eine Queue-Capacity gibt es nicht. **`Engine:MaxConcurrentExecutions:*` ist ausgenommen** (Sicherheits-Cap, nicht Tuning — gilt in beiden Modi). Details: `docs/performance-improvements.md`. **Consumer-Regel:** hot-reloadable Werte via `IOptionsMonitor<T>.CurrentValue` bzw. rohes `IConfiguration` pro Use/Pass lesen — nie `IOptions<T>.Value`-Snapshot. Vollständige Matrix + Mixed-Section-Limits: `docs/claude-reference.md`.

## AuditLog

`IAuditWriter` injizieren, `await _audit.LogAsync(AuditActions.VerbNomen, "Resource", resourceId, detailsJson, ct)` **nach** `SaveChanges`. Schreibfehler darf normale Mutation nie abbrechen. Ausnahme: DB-Admin-Write-SQL läuft fail-closed — ohne vorab persistierten `DBADMIN_SQL_WRITE_ATTEMPTED`-Eintrag wird das SQL nicht ausgeführt. Passwörter/Secrets nie in Details.

Audit-Codes folgen dem Muster `VERB_NOMEN` und sind **zentral** in `NodePilot.Core.Audit.AuditActions` registriert — nie ein rohes String-Literal am Call-Site (Guard: `AuditActionsCatalogTests`). Pipeline: `IAuditStager` (Core) + `IAuditWriter` (Api, wrappt Stager); Archive gzip + SHA-256-Sidecar. Code-Übersicht: `docs/claude-reference.md`.

## KI-Features

Opt-in (`Llm:Enabled=false` default), OpenAI-kompatibler Endpunkt, Rate-Limit 20/min/IP. Drei Helfer + eine Activity; Details: `docs/claude-reference.md` + `docs/ai-features.md`.

**LLM-Proxy:** `Llm:Proxy:Mode` = `Off` (default, Direktverbindung) | `System` (Proxy des Dienstkontos) | `Custom` (`Address` + `BypassList`-Globs), dazu `Username`/`Password` bzw. `UseDefaultCredentials`. Ein Block für die ganze Installation, gilt für alle LLM-Aufrufe inkl. Test-Button. Sitzt bewusst **nicht** im `SocketsHttpHandler`, sondern in `LlmConfiguredProxy : IWebProxy` (liest `IOptionsMonitor` pro Request) — nur deshalb bleibt die Sektion hot-reloadable. Mit Proxy sieht `LlmConnectGuard` nur noch den Proxy-Endpunkt; Details + Begründung: `docs/claude-reference.md`.

**LLM-Profile:** Verbindungen liegen als benannte Profile unter `Llm:Profiles:<id>` (Objekt gekeyt nach unveränderlicher Id, kein Array — Secret-Erhalt matcht per Id und übersteht Rename/Reorder). `Llm:ActiveProfileId` wählt das eine aktive Profil; global bleiben nur diese beiden Keys, alles Verbindungsförmige inkl. `EnableToolCalling`/`ToolCallMaxDepth` sitzt im Profil. Kein „nimm das erste"-Fallback: passt nichts → 503 `LLM_NO_ACTIVE_PROFILE` (Boot läuft trotzdem, nur Warning). Ausgeliefert wird `"Profiles": {}` — ein Profil in der Basis-Config wäre über die UI nie löschbar (additive Provider-Kette), Delete-Versuch → 400 `LLM_PROFILE_NOT_DELETABLE`. **Keine scoped `ILlmClient`-Registrierung** (würde vor dem Action-Gate auflösen); Consumer nehmen `ILlmClientFactory`.

- **`POST /api/ai/generate-script`** (Admin/Op, SSE-Streaming — tippt live in Monaco) + **`POST /api/ai/generate-workflow`** (Admin/Op, JSON).
- **`POST /api/ai/chat`** (alle Rollen, SSE) — Workflow-Assistent: erklärt/ändert den aktuellen Workflow; Proposals nur Admin/Op, Merge per Node-ID aufs unredigierte Original (Secrets/Layout erhalten). Secrets werden vor jedem LLM-Call redigiert (`WorkflowSecretRedactor`). **Tool-Calling** opt-in am aktiven Profil (`Llm:Profiles:<id>:EnableToolCalling`): read-only Analyse- + Execution-Log-Tools, gecappt via `ToolCallMaxDepth` desselben Profils. Threads/Verlauf/Export clientseitig persistent.
- **Globaler AI-Chat / Wissens-Assistent** (`POST /api/ai/knowledge/ask`, SSE; `GET /api/ai/knowledge/capabilities`) — seitenweiter read-only Q&A in `/ai-chat`, canvas-frei. Vier admin-toggelbare Wissensquellen (Sektion `AiKnowledge`, hot-reloadbar, alle `false`-default außer Docs/Operational): **Docs** (`DocsEnabled`), **Operational** (`OperationalEnabled`, RBAC-folder-gescoped — liefert nur die Workflow-spezifische **Definition** (`get_workflow_definition`, secret-redigiert), **statische Analyse** (`analyze_workflow`) und **Cron-Voraussage** (`get_next_scheduled_fires`); reine Listen wie "welche Workflows/Läufe/Maschinen gibt es" werden über die DB-Quelle per text2sql beantwortet), **Source-Code** (`SourceCodeEnabled`, Admin/Op), **DB / text2sql** (`DbEnabled`, ausschließlich globaler Admin). DB-Tools (`list_db_tables`/`get_db_table`/`execute_readonly_sql`) über `ISqlKnowledgeReader`: Schema inkl. Provider/FKs ohne Secret-Spalten; zentraler Executor-Guard (64 KiB, Single-Statement, Read-only-Whitelist + Dangerous-Token/Routine-Block), geschützte Spaltenreferenzen vor Ausführung abgelehnt, Result-Masking + `IAuditDetailsRedactor`, Row-Cap 200, valides Truncation-JSON. Folder-Grants erhöhen nie auf Raw-SQL; Operators behalten nur die typisierten, folder-gescopten Tools. DB-Tools Strict mit Best-Effort-Fallback; Audit nur Query-Anzahl/Fingerprint. Sources sind nur sichtbar, wenn das aktive Profil `EnableToolCalling` gesetzt hat.
- **`llmQuery`-Activity:** Engine-lokal, Prompt→Text; per-Node-Overrides `baseUrl`/`model`/`apiKey`/`maxTokens`/`temperature`/`timeoutSeconds`/`jsonMode`, **gated durch `Llm:Enabled`** (zentraler Kill-Switch). Teilt Transport + SSRF-Guard via `ILlmClientFactory`; einziger BaseUrl-Validierungspunkt ist `LlmEndpointGuard`.
- **Zwei Wire-Dialekte, kein Config-Key:** `LlmEndpointGuard.ResolveEndpoint` leitet aus dem `BaseUrl`-Pfad ab, wohin gepostet wird und wer antwortet — `…/responses` → `OpenAiResponsesLlmClient` (OpenAI Responses API), sonst `OpenAiCompatibleLlmClient`; endet der Pfad schon auf `/chat/completions`, wird **nichts** mehr angehängt. Gemeinsames HTTP-Plumbing in `LlmHttpTransport`. Die vier Quirk-Fallbacks (`max_tokens`→`max_completion_tokens`, `stream_options`, `response_format`, `strict`) sind Chat-Completions-only und im Responses-Client bewusst nicht vorhanden; dieser sendet immer `store: false`.
- **Erreichbarkeit ≠ Antwortzeit:** `TimeoutSeconds` ist reines **Antwort**-Budget. Der Verbindungsaufbau hat eigene Konstanten in `LlmConnectGuard` — `ConnectPhaseTimeout` (15 s, DNS+TCP im ConnectCallback) und `HandshakeTimeout` (30 s, als `SocketsHttpHandler.ConnectTimeout`, die einzige Stelle die den TLS-Handshake binden kann). Die Ordnung `HandshakeTimeout > ConnectPhaseTimeout` ist tragend (per Test gepinnt): nur deshalb darf ein gefeuertes `ConnectTimeout` als TLS-Stufe gelesen werden. Fehler nennen die Stufe (`LLM endpoint DNS:|TCP:|TLS:`); Debug-Logging der aufgelösten Adressen unter `NodePilot.Ai.LlmConnect`. Details: `docs/claude-reference.md`.
- **Hardening:** SSRF-Block (Cloud-Metadata), Proxy nur nach Opt-in (`Llm:Proxy:Mode`, default `Off`), Klartext-ApiKey-/Proxy-Passwort-Warning, Prompt-Injection-Mitigation (Schema-only, User-reviewed Insert). Drift-Schutz: `PromptCatalogDriftTest.cs`. Audit: `AI_*`-Codes.

## Workflow Import/Export

`GET /{id}/export` / `GET /export` / `POST /import`. Envelope `nodepilot-workflow-export/v1`. Import erzeugt neue Einträge immer disabled und liefert deren IDs; explizite Aktivierung danach via `POST /{id}/enable` beziehungsweise `np workflow enable <id>`. CLI-Importe geben mit `-o json` den vollständigen Report maschinenlesbar auf stdout aus. Namenskollisionen → Suffix `" (Imported 2)"`. SCOrch-Import via `POST /import-scorch` — übernimmt die Job-Concurrency des Runbooks
(`<MaxParallelRequests>`) originalgetreu als `MaxConcurrentExecutions`, inklusive `1`. Ziel-Folder via `?folderId=` (fehlt → Root); RBAC = Edit auf dem gewählten Folder. **Secrets werden hier redigiert** (`***`) — Teilen-Artefakt, kein DR.

## System-Configuration Backup (ADR 0001)

Getrennt vom Workflow-Export: portables Konfigurations-Backup (Workflows+Folders, Machines, Credentials, Globals, Users, Custom Activities, Alerting, Settings — **keine** Execution-History/Audit/Stats und kein vollständiges DR). Admin-only, ausschließlich Envelope `nodepilot-system-backup/v4` (`.npbackup`); kompletter Payload passphrasenbasiert verschlüsselt und authentifiziert. Preview braucht die Passphrase; unvollständige Exporte und Restores brechen fail-closed ab. Native DB-, ProgramData-, Konfigurations- und Key-Sicherung plus Restore-Drill bleiben für DR erforderlich. UI `/backup`, CLI `np backup manifest|export|preview|restore`. Details: `docs/claude-reference.md`.

## Konventionen & Feinheiten

- **Keine Abwärtskompatibilität:** Keine Shims, Feature-Flags, optionale Defaults für sanfte Migration. Sauber durchziehen: `NOT NULL`, Required-Properties, alte Code-Pfade ersatzlos löschen. Alte DB → Migrations fahren, fertig.
- **Disabled Edges:** Target-Node wird nicht zum Root. Alle eingehenden disabled → `Skipped`.
- **Kein Root (trigger-los oder nur Zyklen):** Nodes vorhanden, aber kein (aktiver) Trigger → 0 Roots → Execution `Failed` (ErrorMessage nennt den fehlenden Trigger/Start). **Leerer** Workflow (0 Nodes) → läuft mit 0 Steps durch (`Succeeded`).
- **`POST /execute`:** asynchron, 202 + ExecutionId. Fortschritt via SignalR.
- **Workflow-Version-History:** `Update`/`Rollback` snapshotten vorherige Definition.
- **Idempotency-Keys:** `POST /api/trigger/{name}` akzeptiert `Idempotency-Key`-Header; Replay/Reservation gilt nur innerhalb desselben authentifizierten External-Trigger-Key-Principals und Workflows. `Pending` Execution + Reservation + geschützter Dispatch Intent werden in derselben Transaktion angenommen und nach Startup/Failover weiter dispatched. Nur inkonsistente Legacy-`Pending` ohne Intent werden abgebrochen und freigegeben; für bereits gestartete Executions bleibt die Reservation wegen unbekannter externer Seiteneffekte bestehen.
- **Node-Level `disabled`:** `data.disabled: true` → Node wird `Skipped`, Downstream ohne andere Quellen auch.
- **Step-Debugger:** `POST /execute` mit `debug: true` → Breakpoints, SignalR `StepPaused`, Resume via `POST /executions/{id}/resume`.

## Production Deployment

Produktiv-Rollout über `deploy/`-Skripte — Claude führt sie **nur auf ausdrückliche Aufforderung** aus (Release-Artefakte über `deploy/Build-Artifact.ps1` sind der übliche Anlass; ein Rollout auf eine laufende Instanz braucht dieselbe eigene Freigabe). Vollständige Doku: `deploy/README.md`. Architektur (gMSA, Kestrel-HTTPS, Install-Dir-Split, Config-Keys, Stolperfallen): siehe `docs/claude-reference.md`.

**Desktop-App (Electron, `deploy/desktop/`):** zweites Shipping-Ziel — offline Win-11-x64-Installer, alles als Boot-Start-Dienste. Posture `Deployment:Mode` (`Server`|`Desktop`, default `Server`): Desktop relaxiert **nur** loopback-DB-TLS + Kestrel-`ListenLocalhost`; Rest bleibt Production-gehärtet. (Das Warten auf die DB vor dem Migration-Bootstrap ist **kein** Desktop-Sonderfall mehr — `DatabaseReadinessGate` läuft in beiden Modi, Details in `docs/claude-reference.md`.) Icons kommen aus `scripts/generate-desktop-icons.ps1` (Default blau, Fenster-/Tray-Icon folgt zur Laufzeit dem SPA-Skin über `page-favicon-updated`). Volle Doku (Architektur, Dienste-Identitäten, First-Run-Admin-Handoff): `deploy/desktop/README.md`.
