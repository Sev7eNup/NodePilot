# NodePilot

Moderner, schlanker Ersatz fuer Microsoft System Center Orchestrator. Agentless Workflow-Orchestrierung fuer Windows-Umgebungen via WinRM.

**Diese Datei ist der Index, nicht die Doku.** Sie enthält Verhaltensregeln, Nachschlage-Tabellen und Invarianten — die Tiefe liegt in `docs/`.

**Pflege:** Ein Feature-PR fügt hier höchstens eine Zeile hinzu. Wer einen Absatz schreiben will, schreibt ihn in `docs/claude-reference.md` und verlinkt ihn von hier. Keine Fehler-Rückblenden („vorher war es so…"), keine Messwerte als Beleg — dieselbe Regel wie für Code-Kommentare. Steht eine Erklärung schon in `docs/`, steht hier nur der Zeiger. Umfang ist per `DocumentationCountsTests` gedeckelt.

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

- `docs/roadmap.md` — **führendes Dokument für „was wird gebaut".** Gesetzte Posten (R1), trigger-gated Posten (R2), offene Entscheidungen (E), Sperrvermerk-Anhang mit den verworfenen Ideen. Was dort nicht steht, ist kein Vorhaben.
- `docs/claude-reference.md` — **Overflow-Referenz dieser Datei.** Activity-Config-Keys/Outputs, Trigger-Params, Edit-Lock-UX, Audit-Codes, Hot-Reload-Matrix, Backup-Details, Background-Services, Deployment, Coverage-Messung
- `docs/alerting.md` — Notification-Rules + System-Policies (ADR 0008), Dispatcher, Sinks, Ledger
- `docs/custom-activities.md` — Custom Activities (Plugin-System)
- `docs/mcp-server.md` — MCP-Server inkl. Tool-Katalog + `.mcp.json`-Beispiel
- `docs/ai-features.md` — KI-Features: Config-Keys, Modell-Empfehlungen
- `docs/deployment-guide.md` — (EN) **nicht** der Installationsweg, sondern was davor und danach kommt: Artefakt verifizieren, selbst bauen, Troubleshooting. Der Installationsweg steht **einmal**, auf der Doku-Website (`content/{de,en}/deployment/production.md`).
- `docs/av-exclusions.md` — Antiviren-Ausschlüsse (Server + Desktop) als Übergabedokument für eine AV-Abteilung
- `docs/workflow-styleguide.md` — Layout-Styleguide für Workflow-JSONs (**vor jedem Workflow-Gen lesen**)
- `docs/workflow-tests.md` — Test-Suite unter `scripts/test-suite/`: 46 generierte Workflows gegen die laufende Engine, `suite-manifest.json` als Abdeckungsquelle, Guard-Test `TestSuiteCoverageTests`
- `docs/enterprise-features.md` — HA, Secret-Provider, LDAP/SSO, SIEM, Folder-RBAC
- `docs/ai-feature-ideas.md` — Beschreibungstiefe zu den KI-Ideen, **keine Spezifikation**. Priorisierung und Status stehen in `docs/roadmap.md`.
- `src/nodepilot-ui/e2e/README.md` — E2E-Coverage-Map + Spec-Konventionen
- `src/nodepilot-ui/demo/` — **Browser-Demo** der SPA für GitHub Pages (`/demo/`): dieselbe App gegen ein In-Memory-Backend. Eigener Entry, dritter Vite-Build (`vite.demo.config.ts` → `dist-demo/`). Abhängigkeitsrichtung ausschließlich `demo/` → `src/`; Hub, Doku-Link und Auth-Transport werden vom Demo-Entry **injiziert**. Details: `src/nodepilot-ui/CLAUDE.md`
- `src/nodepilot-docs-ui/src/site/` — **Projekt-Website** (DE/EN, Vanilla-TS) an der Pages-Wurzel, Doku darunter unter `/docs/`. Eigener Vite-Build, **nie** Teil von `dist/` oder des Produkts. `src/nodepilot-docs-ui/scripts/assemble-site.mjs` baut `_site/`; **jede Eingabe ist Pflicht** — eine optionale ließe einen Deploy die alte Demo weiterveröffentlichen. `npm run preview:site` ist die einzige vollständige lokale Vorschau. Routen sind **echte Adressen** (`/product/`, englische Segmente, Rechtsseiten deutsch), die das Prerender-Plugin nach dem Build je Route als eigene Datei schreibt, samt `404.html`, `sitemap.xml` und `robots.txt`. Neue Route → `ROUTE_PATHS`, `routePages()` **und** `SITE_ROUTE_SEGMENTS`. Impressum/Datenschutz liefert der User. Details: `src/nodepilot-docs-ui/README.md`

## Tech-Stack

- **Backend:** ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`)
- **Datenbank:** PostgreSQL (default) / SQL Server (`Database:Provider` = `postgres` | `sqlserver`). SQLite nur als Test-In-Memory-Backend.
- **Remote Execution:** PowerShell SDK / WinRM, agentless. `Remote:Provider`: `winrm` (default) | `noop` (`noop` braucht `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1`, sonst Boot-Abbruch). Engine-local (In-Proc-Pool): implizite WinPS-Kompatibilität **deaktiviert**, `Microsoft.PowerShell.Archive` gebündelt — Details `docs/claude-reference.md` + `docs/performance-improvements.md`
- **Real-time:** SignalR (`/hubs/execution`)
- **Logging:** Serilog. Format via `Logging:Format`: `text`|`cmtrace`|`json`|`ecs-json` (ECS 1.x für SIEM, siehe `docs/siem-logging.md`). Support-Log: File + DB-Projektion
- **MCP-Server (opt-in):** `nodepilot-mcp` (stdio) — AI-Agent steuert/editiert Workflows über 102 Tools, HTTP-only gegen die REST-API
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

Port 5000 kommt aus `launchSettings.json` und ist derselbe, auf den der Vite-Proxy zeigt. **Immer erst `pg_ctl start`, dann `dotnet run`.**

**`/docs` im Dev:** In Produktion bedient die API die Doku aus `wwwroot/docs`; im Dev proxyt Vite `/docs` auf 5174. Läuft der Doku-Dev-Server nicht, führt der Doku-Button ins Leere — erwartet, kein Defekt. Der Doku-Dev-Server läuft selbst unter `/docs/` (`--base=/docs/` im dev-Skript, **weil Vite 8 das `base` aus der Config im Dev ignoriert**); ohne das landet man in der App statt in der Doku.

**Erster Login braucht das Setup-Token**, nicht nur leere DB: die API schreibt es nach `src\NodePilot.Api\admin-setup.token` (ContentRoot). Die Login-Maske zeigt beim ersten Versuch ein **Setup-Token**-Feld; erst damit entsteht der Admin-Account.

**Für Claude:** Dev-Mode verwenden. **API-Neustarts (stop+rebuild+start) sind jederzeit ohne Rückfrage erlaubt** — DLL-Locks sind normal. Vorab PID via `Get-NetTCPConnection -LocalPort 5000` finden, dann `Stop-Process` + Rebuild + Start. `npm run dev` kaputt → `npm install`. Deploy-Skripte unter `deploy/` laufen **nur auf ausdrückliche Aufforderung** — die Freigabe gilt jeweils nur für den einen Vorgang.

**Langlaufende Prozesse (API + Vite):** detached/als Background-Prozess starten (z. B. `Start-Process` mit umgeleiteten Logs), damit sie Tool-Call-Grenzen überleben. „Läuft" erst melden nach vollem Port-Check (`Get-NetTCPConnection -LocalPort 5000` **ohne** `-First N`) **und** HTTP-Health-Probe. Vor jedem Kill verifizieren, dass es die Dev-Instanz ist — **nie** den installierten Windows-Dienst treffen.

## Arbeitsweise für Claude

- **Nichts nach außen ohne ausdrückliche Ansage.** `git commit`, `git push`, `gh pr create`, `gh pr merge`, `gh pr comment`, `gh issue create`/`comment`, `gh release create`, Tags, Branch-Löschung — jeder dieser Schritte braucht eine eigene Aufforderung des Users. Lokal arbeiten (Branch anlegen, editieren, bauen, testen) ist frei; sobald etwas das Repository verlässt oder in `main` landet, wird gefragt.
  - **„go", „mach das", „setz das um" heißt: implementieren.** Es heißt **nicht** committen, pushen, PR öffnen oder mergen. Wenn der User Commit/PR/Merge will, sagt er es (z. B. „pr und merge bitte") — und diese Freigabe gilt **nur für den einen Vorgang**, nicht für die nächste Aufgabe.
  - Nach getaner Arbeit den Stand im Arbeitsbaum liegen lassen und knapp berichten, was bereitliegt. Nicht vorgreifend committen, „damit nichts verlorengeht".
- **Branching:** Nicht-triviale Arbeit auf einem neuen Branch beginnen, **bevor** editiert wird; nachfragen nur, wenn der Branch-Name unklar ist. Triviale Einzeiler (z. B. `.gitignore`) bekommen **keinen** eigenen Branch/PR — in die laufende Arbeit einfalten.
- **PR-Budget: maximal 5 PRs gleichzeitig** für eigene Arbeits-Batches (größere Vorhaben in ≤5 PRs schneiden). Für **Dependabot gilt diese Zahl nicht**: `.github/dependabot.yml` bündelt Minor/Patch pro Ökosystem (`open-pull-requests-limit: 1` je Block → max. 5 Sammel-PRs), aber **jeder offene Major fällt aus der Gruppe und bekommt einen eigenen PR**. Majors bleiben bewusst ungruppiert, weil ein Bündel den Review verschlechtert.
- **Jede Änderung an `.github/dependabot.yml` löst sofort alle Blöcke neu aus** (unabhängig vom Montags-Zeitplan) und erzeugt binnen Minuten neue PRs. Config-Edits deshalb **bündeln**, nicht nacheinander mergen.
- **Scope:** Minimaler Root-Cause-Fix. Würde ein Fix deutlich mehr Dateien anfassen als das benannte Problem → stoppen und den geplanten Scope in 3 Bullets nennen, bevor editiert wird.
- **Keine Abwärtskompatibilität:** Keine Shims, Feature-Flags, optionale Defaults für sanfte Migration. Sauber durchziehen: `NOT NULL`, Required-Properties, alte Code-Pfade ersatzlos löschen. Alte DB → Migrations fahren, fertig.
- **PowerShell 5.1 / Windows:** Kein Inline-SQL durch PowerShell-Quoting — Query in eine `.sql`-Datei schreiben und per `psql -f` ausführen. Dateien als UTF-8 **ohne** BOM schreiben. Keine `sed`/Regex-Zeilen-Edits auf Source-Dateien (CRLF bricht sie) — Edit-Tool verwenden. Kein `$args`-Splatting; explizite benannte Parameter.
- **Code-Kommentare:** Sachlich und kurz, in einfachem Englisch. Sie sagen, **was** der Code tut und **warum** — nicht mehr. Keine Herleitung, keine Erzählung, keine Rückblende auf frühere Fehlversuche, keine Messwerte oder Beispielzahlen als Beleg, kein „X used to …, which meant …". Wer den Hintergrund braucht, findet ihn in Commit-Message, PR oder `docs/`. Ein bis drei Zeilen reichen fast immer; ein Kommentar, der länger ist als der Code darunter, ist meist eine Erzählung. Die vorhandenen langen Kommentare im Repo sind **kein** Vorbild.
- **Reporting:** Knapp berichten — was geändert, was verifiziert, was offen. Keine Per-File-Walkthroughs, kein Plan-Nacherzählen. Interaktive Rückfragen nur, wenn die Antwort wirklich blockiert.

## Datenbank

| Provider | `Database:Provider` | ConnectionString-Key |
|---|---|---|
| PostgreSQL (Default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`.
- **Aktuelle Baseline:** `20260915180058_InitialBaseline`. Datenbanken mit der alten Historie haben keinen Upgrade-Pfad dorthin; für diesen Stand eine neue Entwicklungsdatenbank verwenden. Bestehende Datenbanken niemals automatisch löschen oder deren Migration-History umschreiben.
- **Neue Migration:** `dotnet ef migrations add <Name> --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext`. **Pflicht-Postprocessing — zwei Schritte:**
  1. In der Migration (`<Name>.cs`): alle `type: "..."`-Annotations entfernen.
  2. In der Designer-Datei (`<Name>.Designer.cs`): `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` als letzte Zeile vor `#pragma warning restore 612, 618` in `BuildTargetModel` ergänzen. Der `ModelSnapshot` bekommt den Aufruf bewusst **nicht** (Diff-Basis, kein Migration-Target-Model).

  Beide Schritte sind durch `MigrationDriftTests` abgesichert — laufen lassen statt sich erinnern.
- Schema-Änderungen IMMER per EF-Migration. Kein DDL-Hotpatching.
- Credentials mit DPAPI verschlüsselt (`Credentials:DpapiScope`).
- **DB-TLS strikt (default):** `DatabaseTlsBootValidator` bricht den Boot ab, wenn die Connection den Server nicht verifiziert. Escape `Database:AllowInsecureTls=true` nur bei Loopback-Host **und** entweder Development-Env **oder** `Deployment:Mode=Desktop`.

Retention-Services im Scheduler: Execution (30d), AuditLog (365d), WorkflowVersions (50/Workflow), SupportEvents (90d), Notifications (90d), TriggerReceipts (7d) — opt-out via `Retention:*:Enabled: false`. IdempotencyKeys (24h, fixe TTL) läuft immer. Inventar aller Background-Services: `docs/claude-reference.md`.

### Datenbank-Verfügbarkeit (Laufzeit-Ausfall, ADR 0011)

Prozessweiter In-Memory-Breaker (`NodePilot.Data.Availability`): fällt die DB zur Laufzeit aus, antwortet `/api` sofort `503 DATABASE_UNAVAILABLE` statt zu hängen. Invarianten:

- **Einzelschreiber-Regel: nur die Sonde publiziert `Available`**, EF-Interceptors degradieren nur.
- Die Sonde nutzt `SELECT 1` auf eigener ungepoolter Verbindung — **nie `CanConnectAsync`**, ein hängender Server besteht das.
- Ein Command-Timeout öffnet nie direkt, er *armt* nur die Sonde (`Armed`); eine langsame Abfrage bleibt `DATABASE_TIMEOUT`.
- Hintergrunddienste parken via `WaitUntilServableAsync` (wirft nie; Gate **über** der Leader-Prüfung).
- `/healthz/ready` = schneller 503 fürs LB, `/healthz/database` = **immer 200** mit Status fürs SPA.
- Boot bleibt fail-closed (`Database:StartupWaitSeconds`); der Boot-Block wird bewusst **nicht** nachgeholt.
- `Database:AuthReadTimeoutSeconds` und die übrigen Availability-Budgets sind restart-pflichtige Boot-Config und bewusst **kein** `SettingsSchema`-Eintrag — der Connection-String gehört auf keine HTTP-Fläche.

Details: `docs/adr/0011-database-availability-breaker.md` + `docs/claude-reference.md`.

**Bekannte Falle (bewusst so):** `HostOptions.BackgroundServiceExceptionBehavior` bleibt auf `StopHost`, und die sieben Retention-Dienste haben ihren breiten Catch eine Ebene *unter* der host-fatalen Grenze — Code, der in deren `RunIterationAsync` außerhalb des inneren `try` landet, kann den Host töten.

## API Endpoints

Routen + Rollen-Gating stehen an den Controllern in `src/NodePilot.Api/Controllers/` (`[Route]`/`[Authorize]`) — dort nachsehen statt hier spiegeln. Rollen-Matrix unter `## Autorisierung`.

**Nicht getroffene `/api`-Pfade antworten `404 application/problem+json`** (`code: NOT_FOUND`), nicht mit dem SPA-Bundle — ein eigener `MapFallback("/api/{**rest}")` steht vor `MapFallbackToFile("index.html")`. Betrifft auch Routenparameter, die ihre Typ-Constraint verfehlen. Deep-Links außerhalb von `/api` gehen weiterhin an die SPA.

## Workflow-Kontrollfluss

| Endpoint | Semantik |
|---|---|
| `POST /execute` | Startet Lauf, asynchron. Body: `{"parameters": {}, "timeoutSeconds": N, "debug": bool}`. 202 + ExecutionId, Fortschritt via SignalR. |
| `POST /enable` / `/disable` | Kill-Switch. `enable` verlangt einen lock-freien Workflow (jeder Lock, auch der eigene → 423), validiert die **gespeicherte** Definition und füllt `PublishedByUserId`, falls unbesetzt — ein vorhandener wird nie überschrieben. Bereits aktiv = No-Op (`204`) **ohne** Stempel. `disable` ignoriert Locks. |
| `POST /cancel-all` | Cancelt alle `Running`- **und** `Pending`-Executions des Workflows. |
| `PUT /concurrency-limit` | Setzt `MaxConcurrentExecutions` (1..1000, `null` = unbegrenzt). `maxConcurrentExecutions` ist **Pflicht** (fehlend → 400, sonst würde `{}` das Limit still löschen), `0` wird abgelehnt. Kein Edit-Lock, kein Version-Bump, kein History-Snapshot. |
| `POST /executions/{id}/cancel\|retry\|resume` | Einzelner Lauf. Resume-Body: `{"stepId": "<node-id>", "mode": "continue"\|"stepOver"\|"stop", "overrides": {}}` — `stepId` ist **Pflicht**. |
| `POST /{id}/rollback` | Snapshottet wie `Update` die vorherige Definition in die Version-History. |

**Disable+cancel-all = Quarantäne.**

**Per-Workflow-Parallelität (SCOrch „max running instances"):** `Workflow.MaxConcurrentExecutions` begrenzt gleichzeitige Läufe *eines* Workflows über **alle** Aufrufer hinweg. Am Limit wird **eingereiht statt abgelehnt**. Ein Zähler für beide Wege: `IWorkflowConcurrencyGate`. Der Dispatch-Claim überspringt Workflows am Limit, sonst verhungern andere hinter deren Rückstau. Nicht versioniert, nicht im Update/Publish-Body. Details: `docs/claude-reference.md`.

## Edit-Lifecycle (SCOrch-style Edit-Lock)

Workflows haben einen per-User-Edit-Lock (`CheckedOutByUserId` + `CheckedOutAt`). Mutierende Endpoints liefern `423 Locked` wenn Caller nicht Lock-Owner. `Disable` ist **nicht** lock-gegated (Incident-Kill-Switch).

| Endpoint | Verhalten |
|---|---|
| `POST /lock` | Atomar `IsEnabled=false` + Lock-Fields setzen. 409 wenn schon gelockt. |
| `POST /unlock` | Lock-Fields auf null. `IsEnabled` bleibt unverändert. |
| `POST /publish` | Atomar: Save + `IsEnabled=true` + Unlock. Validiert den **übergebenen** Body vorher: schwaches Webhook-HMAC-Secret → `400 weak_webhook_hmac_secret`, für Quartz ungültige Cron-Expression → `400 invalid_cron_expression`. `enable` prüft dasselbe an der gespeicherten Definition. |
| `POST /force-unlock` | Admin-only. Bricht fremden Lock. |

UX-Flow und Button-State-Matrix: `docs/claude-reference.md`. Kurz: `canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId`.

## Activity-Typen

"Remote" = `targetMachineId`/WinRM. "Engine-local" = im API-Prozess. `(controlFlow)` = Kategorie `ControlFlow` im backend `ActivityCatalog` (Palette-Achse, unabhängig vom Scope).

- **Remote:** `fileOperation`, `folderOperation`, `textFileEdit`, `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `powerManagement`, `scheduledTask`, `fileHash`, `zipOperation`
- **Engine-local:** `restApi`, `sql`, `emailNotification`, `delay`, `xmlQuery`, `jsonQuery`, `log`, `generateText`, `llmQuery` + controlFlow: `junction`, `forEach`, `decision`, `startWorkflow`, `returnData`
- **Hybrid:** `runScript`, `waitForCondition`

Config-Keys & Output-Semantik pro Activity: `docs/claude-reference.md`.

**Retry pro Step:** `config.retry` mit `maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`. Nicht wiederholt werden dauerhafte Remote-Fehler (abgelehnter WinRM-Logon, per Policy geblockte HTTP-Session, nicht entschlüsselbares Credential) — sonst erzeugt ein Step mehrere Fehl-Logons und kann ein Konto sperren.

**Execution-Timeout:** `timeoutSeconds` im Execute-Body + per-Step `config.timeoutSeconds`.

**Prozess-Isolation (`runScript`, nur lokal):** `config.isolated: true` → eigener Prozess in einem Windows Job Object, opt-in Caps `memoryLimitMb`/`maxProcesses`; No-Op auf dem Remote/WinRM-Pfad. `ProcessSpawnCoordinator` serialisiert alle inheritable Spawns, dazu bounded stdout/stderr-Drain nach Prozess-Exit (`Engine:IsolatedDrainGraceSeconds`, default 5 s). Details: `docs/claude-reference.md`.

## Custom Activities (Plugin-System)

User-authored, PowerShell-backed Activities (UI: „Custom Nodes") — reine **runScript-Presets** (dieselbe Engine/Isolation/Marker-Capture/Redaction), keine zweite Script-Engine. Volle Doku: `docs/custom-activities.md`.

- **Dispatch:** `activityType = custom:<key>` → ein einziger Sentinel-registrierter `CustomActivityExecutor`; `__customDefinitionId` authoritativ + `__customKey` als Drift-Guard. Der Wrapper captured NUR die deklarierten Outputs (+ `exitCode`).
- **Governance:** Create/Edit/Delete = Admin+Operator **nur solange disabled**; Enable/Disable + Mutation enabled Defs = Admin-only. Latest-wins; jede Execution speichert `StepExecution.CustomActivity{Key,Version,Hash}`. Kein `secret`-Input-Typ — Secrets via `{{globals.X}}`/Credentials.
- **Architektur:** Geteilte Facts-Schicht `NodePilot.Core.Activities.CustomActivityType`/`CustomActivityValidation`, Frontend-Spiegel `lib/customActivities.ts`. `activityCatalog.generated.ts` + Parity-Test bleiben **unberührt**.

## Alerting (Notification-Rules)

User-definierte Regeln, die bei passenden Ereignissen über SMTP / Generic-Webhook + HMAC benachrichtigen. Opt-in **per Daten** (idle bis eine Regel existiert). Volle Doku: `docs/alerting.md`.

- **Zwei Arten:** Custom-Regeln (`Kind=Custom`, Execution-Events, Filter-AST = derselbe `ConditionEvaluator` wie Edge-Conditions) und System-Policies (`Kind=System`, ADR 0008 — 14 katalogisierte `ISystemAlertSource`s, ausgewertet vom `SystemAlertEvaluator`).
- **Zustellung ist at-least-once.** `NotificationDispatcher` (leader-gated, ~30 s) persistiert einen Pending-Attempt VOR jedem I/O; der eindeutige Ledger-Key `(rule, route, occurrence)` verhindert doppelte Attempts, aber ein Crash nach Empfang und vor gespeichertem `Sent` kann erneut zustellen — **Webhook-Empfänger deduplizieren über `EventKey`**.
- **Governance:** Read Admin/Op; alle Mutationen + Test-Fire Admin-only; neue Regeln entstehen disabled. Secrets in Responses redigiert. Frontend `/alerts`, `np alerting` + `np system-alert`, MCP-Tools für beides.

## Architektur-Konventionen

- **Neue Activity:** Klasse in `Engine/Activities/`, `IActivityExecutor` implementieren — Auto-Discovery via `AddNodePilotActivities()`, **keine** DI-Verdrahtung nötig. Pflichtteil derselben Änderung: die UI-Seite (Palette, `*Config`, handgepflegter Katalog-Spiegel — `src/nodepilot-ui/CLAUDE.md`) **und** ein Eintrag in `src/NodePilot.Core/Activities/Embedded/activity-config-reference.json`. Daraus werden AI-Prompt-Katalog *und* MCP-Config-Tools gespeist; `ActivityConfigReferenceTests` prüft, dass jeder dokumentierte Key vom Executor wirklich gelesen wird — ein erfundener Key erzeugt sonst Nodes, die korrekt aussehen und nichts tun.
- **Neuer API Controller:** In `Api/Controllers/`, DTOs in `Api/Dtos/`. **Immer parallel** CLI-Command *und* MCP-Tool anlegen — Mechanik in `src/NodePilot.Cli/CLAUDE.md` bzw. `src/NodePilot.Mcp/CLAUDE.md`.
- **Frontend:** Seiten/Nodes/i18n/State-Konventionen in `src/nodepilot-ui/CLAUDE.md`. **Farben immer über Design-Tokens/CSS-Variablen** — nie Tailwind-Farbliterale hardcoden (`text-gray-900`, `bg-white` brechen die Dark-Skins). Natives `<select>`: `option:hover` ist in Chromium nicht stylbar → Custom-Dropdown-Komponente verwenden.
- **Models/Interfaces:** Immer in `NodePilot.Core`
- **Doc-Sync:** Feature-Änderungen halten alle Doku-Flächen synchron — README, `docs/*.md`, `docs/testing/E2ETests.md` + `e2e/README.md` und die Doku-Website `src/nodepilot-docs-ui/content/` (eigener kuratierter Korpus, kein Render von `docs/`). **Zweisprachig:** jede Seite braucht `content/de/…` **und** `content/en/…` plus Titel-Eintrag in `src/i18n/locales/{de,en}.json`; Querverweise **ohne** Sprach-Präfix. Der AI-Wissenskorpus zieht bewusst **nur** `content/en/`. Die Doku-`index.html` darf **kein Inline-`<script>`** enthalten (CSP `script-src 'self'`, Guard: `document-head.test.ts`).

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

`data.controlPoints` überschreibt Auto-Routing. Fehlt es → bestehendes Routing greift. Implementierungsdetails: `docs/claude-reference.md`.

Layout-Styleguide für Workflow-JSONs: **zuerst** `docs/workflow-styleguide.md` lesen. Referenz-Beispiel: `scripts/test-master-all-activities.json`.

## Datenbus / Variable Resolution

- `{{varName.output}}` — Stdout
- `{{varName.error}}` — Stderr
- `{{varName.success}}` — Step-Erfolg (`"true"` / `"false"`)
- `{{varName.param.xxx}}` — OutputParameter
- `{{globals.NAME}}` — Globale Variable
- `{{manual.NAME}}` — Trigger-Input des Laufs (dieselben Keys liegen zusätzlich als `param.*` des Trigger-Nodes an). Deklarierte `manualTrigger`-Parameter werden beim Laufstart mit ihrem `default` geseedet, wenn der Aufrufer sie weglässt. Ein deklarierter Parameter **ohne** Default bleibt abwesend, und die Referenz scheitert.
- Kein `outputVariable` → Step-ID wird verwendet: `{{step-123.output}}`

**Ein veröffentlichter Wert hat genau einen Besitzer (SCOrch-Modell).** Die qualifizierte Form `{{aktivität.param.name}}` ist verbindlich und löst bei **jedem** Nachfahren auf. Den unqualifizierten Kurznamen (im `runScript` als `$name`) legt der Resolver nur an, wenn **genau eine** Aktivität auf dem Vorgängerpfad ihn veröffentlicht; bei zwei Publishern wird **nichts** gebunden statt einen Gewinner zu ziehen. Linter-Code `dup-published-param`; qualifiziert bleibt der Wert erreichbar. **Gemeldet wird nur, wenn mindestens ein Publisher den Namen selbst vergeben hat** — typ-abgeleitete Namen (`exitCode`, `registryOperation`-Outputs, `count`) kollidieren bauartbedingt und sind nicht umbenennbar. Herkunft: `WorkflowDataBusAnalyzer.AuthoredParameters` vs. `TypeDerivedParameters`.

**Contract-Garantie:** Drei Muster im `VariableResolver` — `GlobalsPattern`, `ManualPattern` und `StepPattern` mit genau vier Tails (`output`, `error`, `success`, `param.X`). Andere Tails bleiben Literal; unresolved → granulare Diagnostik je Namespace (StepRunner T-7.1). Das Globals-Muster gilt auch für `runScript`/Custom Activities, weil ein nicht existierendes Global nie legitimer Skripttext ist. Scheitert das **Laden** der Globals, endet ein Lauf, der Globals referenziert, vor dem ersten Step als `Failed`. **Ein neuer Namespace braucht ein eigenes Muster** — ein frei gewählter Tail kann von `StepPattern` prinzipiell nicht getroffen werden.

**Sichtbarkeits-Scope (Ahnen-only):** Ein Step sieht **ausschließlich** Ergebnisse seiner Graph-Vorgänger (`AncestorIndex` + `AncestorScopedResults`). Eine Referenz auf einen Knoten aus einem **parallelen Zweig** löst nie auf — auch nicht, wenn dieser Zweig zufällig schon fertig ist. **Ein Ahne ohne Ergebnis bleibt unauflösbar**; bei `junction`/waitAny und übersprungenen Knoten ist das korrekt so.

**Out-of-Scope-Gate gilt auch für `runScript`/Custom Activities.** Beide sind von der allgemeinen T-7.1-Prüfung ausgenommen (ein übriges `{{...}}` kann legitimer Skripttext sein) — **nicht** aber vom Cross-Branch-Fall. Maßgeblich ist die **Graph-Zugehörigkeit**, nicht ob der Knoten schon ein Ergebnis hat; sonst wird das Gate zum Rennen. Tippfehler/unbekannte Steps bleiben tolerant.

**Strukturierter Output:** `runScript` captured die Variablen, die das Skript **selbst zuweist**, als `param.*`. `$hostName = ...` → `{{step.param.hostName}}`. **Nicht** dabei: durchgereichte Upstream-Parameter und PowerShell-Automatiken/Preference-Variablen (Liste in `NodePilot.Core.Activities.PowerShellReservedVariables`). Umgesetzt über zwei geschachtelte Scopes im Wrapper: Injektion außen, User-Skript innen.

**RunScript Auto-Quoting:** `{{step.output}}` wird als Single-Quoted String eingesetzt. Im Script `$x = {{step.output}}` schreiben, NICHT `$x = '{{step.output}}'`.

**RunScript Erfolg (fehler-basiert):** Ein Step scheitert **nur** bei einem terminierenden PowerShell-Fehler. Ein `exit N` macht den Step **nicht** rot; opt-in `config.successExitCodes` macht non-zero Codes wieder zum Fehlschlag. Der Exit-Code liegt als `{{step.param.exitCode}}` an und meint das letzte native Kommando **dieses** Skripts — der Wrapper setzt `$LASTEXITCODE` und `$Error` vorher zurück, weil beide sonst im prozesslang offenen Runspace-Pool aus einem fremden Lauf überleben. **Engine-Asymmetrie:** ein script-eigenes `exit N` ist nur im Prozess/isoliert-Pfad sichtbar (Runspace kann `exit` nicht beobachten → `0`). Ein **Parse-Fehler** ist auf jeder Engine rot: das Fehlen des `###NODEPILOT_START###`-Markers werten die Prozess-Engines als „Skript lief nie". Gating in `RunScriptActivity`.

## Edge Conditions

- `stepId.success` / `stepId.failed` — Shortcut
- `null` / leer — Immer
- `disabled: true` — übersprungen; Target-Node wird dadurch **nicht** zum Root, und sind alle eingehenden Kanten disabled → `Skipped`
- `conditionExpression` — Typ `comparison` (==, !=, <, >, <=, >=, contains, startsWith, endsWith, matches, isEmpty, isNotEmpty, isTrue, isFalse), `group` (AND/OR), `not`. Operanden: `variable` oder `literal`.

**Conditions sind fail-closed (ADR 0015).** Save/Publish/Import lehnen eine unbrauchbare Condition mit `400 invalid-edge-condition` ab (`EdgeConditionValidator` in Core). Zur Laufzeit wirft `ConditionEvaluator` eine `ConditionEvaluationException` und der Scheduler bricht den Lauf mit Kantenbezug ab — die Kante wird weder geöffnet noch still übersprungen. Ein Variablen-Operand **ohne Wert** macht seinen Vergleich **unentscheidbar**: das erfüllt die Condition nie, auch nicht durch `not`, und in einer Gruppe entscheidet nur ein echtes `true`/`false`. Alerting-Filter (`source: event`) sind ausgenommen — ein fehlendes Event-Feld liest als leer, weil der Feldkatalog deklariert ist.

## Sub-Workflows & Contract

`startWorkflow` ruft jeden enabled Workflow auf (frischer DI-Scope), unabhängig vom Trigger-Typ; die übergebenen `parameters` landen als `manual.*` im Child-Run. `waitForCompletion: true` (default) → Parent blockiert, Child-`returnData` als `param.*` gespiegelt. **Max Call-Depth: 10** — gilt auch für `forEach`.

Contract-Derivation: `GET /{id}/contract` liefert Inputs aus `manualTrigger.parameters` + Outputs aus `returnData.data`-Keys + System-Outputs (`__executionId`, `__status`, `__workflowId`, `__workflowName`). By-name-Lookup (API + Engine + Trigger/Webhook): exact-case gewinnt, sonst case-insensitive; mehrdeutige Namen → 409 bzw. Step-Fehler (`WorkflowNameResolver`). Details: `docs/claude-reference.md`.

## Trigger

| Trigger | Backing |
|---|---|
| `scheduleTrigger` | Quartz cron |
| `fileWatcherTrigger` | FileSystemWatcher |
| `databaseTrigger` | Timer + SELECT-Polling |
| `eventLogTrigger` | EventLog.EntryWritten |
| `webhookTrigger` | HTTP `/api/webhooks/{name}/{path}` |
| `manualTrigger` | UI / API |

`TriggerOrchestrator` scannt alle 5 s. Trigger-Daten landen als `manual.*`-Variablen im Run + als `param.*` des Trigger-Nodes — **kein** `trigger.*`-Namespace. Key-Namen pro Trigger-Typ: `docs/claude-reference.md`.

**Config-Vertrag (eine Vokabel, zwei Laufzeiten):** Jeder Trigger-Node wird von zwei Pfaden gelesen — Node-Executor (`Engine/Triggers/`, Diagnose-Lauf) und Hintergrundquelle (`Scheduler/Sources/`, feuert real). Beide parsen über `Core/Triggers/`: Keys, Defaults, Validierung, Matching liegen dort **einmal**. Neuer Trigger-Key = Settings-Klasse + `activity-config-reference.json` + Designer-Feld; `TriggerContractParityTests` bricht sonst. `databaseTrigger` feuert bei **Sentinel-Änderung**, nicht pro Zeile. Details: `docs/claude-reference.md`.

**Kein Nachholen nach Neustart oder Failover.** Der durable Cursor dient der Deduplizierung und Diagnose, nicht dem Backfill: beim Start spult jede Quelle ihn ohne zu feuern vor und meldet das übersprungene Fenster (`nodepilot.scheduler.triggers.fires_skipped`). Der **laufende** Betrieb ist unberührt. Preis: Signale aus einem Stillstandsfenster werden nicht verarbeitet.

**Selbstheilung:** Jede `ITriggerSource` beantwortet `Health` — vertraglich ein **reiner In-Memory-Read**, weil ein blockierender Probe dort die Reconciliation aller Workflows lahmlegt. `unhealthy` → Quelle wird evictet und mit Exponential-Backoff (5 s→300 s) neu aufgebaut. Ein `FileSystemWatcher` lässt sich **nicht** in-place re-armen. Buffer-Overflow gilt bewusst **nicht** als Fault. Alertbar über `trigger-unhealthy`.

**webhookTrigger-Hardening:** `signatureMode` = `header` (default, `X-Webhook-Secret`) oder `nodepilot-hmac-v2` (HMAC-SHA256 über Freshness-Metadaten + Methode + Pfad + Query + Raw-Body; CSPRNG-Secret ≥32 Bytes, clusterweiter Replay-Guard/5-min-Fenster). **Legacy `hmac` (Body-only) wird abgelehnt** → Adapter nötig. `fieldMappings` extrahiert Body-Felder per JSONPath als `manual.*`.

## WorkflowEngine — Execution-Modell

- **Event-driven:** Queue + `inFlight`-Dict. Roots = **ausschließlich Trigger-Nodes**; ohne (aktiven) Trigger → 0 Roots → Execution `Failed`, ErrorMessage nennt den fehlenden Trigger. **Kein** `inDegree==0`-Fallback. Disabled Trigger nie Root. Orphan-/Nicht-Trigger-Activities ohne eingehende Edge laufen **nie** → `Skipped`. **Leerer** Workflow (0 Nodes) läuft mit 0 Steps durch (`Succeeded`). Node-Level `data.disabled: true` → `Skipped`, Downstream ohne andere Quellen auch. (ADR 0006)
- **Expliziter Fan-in:** Nur eine `junction` darf mehrere eingehende Edges haben; jede andere Activity hat maximal eine. Designer und SCOrch-Import fügen bei Bedarf eine `waitAll`-Junction ein, die Strukturvalidierung schützt Save/Publish/API. Junction-Conditions werden über alle relevanten Eingänge ausgewertet, nicht über die zuletzt abgeschlossene Edge. (ADR 0013)
- **Cancellation:** `_runningExecutions` Dict (Guid → CTS). **Per-Step-DI-Scope:** eigener Scope pro Step → scope-lokaler `DbContext`.
- **Startup-Reconciler:** `Running`/`Paused` und inkonsistente `Pending` ohne Dispatch Intent → `Cancelled`; `Pending` mit durablem Outbox-Intent bleibt erhalten und wird neu geleast. (ADR 0014)
- **Kein Step überlebt seine Execution:** Jede terminale Execution-Schreibung setzt anschließend alle noch `Running`/`Paused`-Steps auf `Cancelled` (`ExecutionStateLifecycle.CancelOrphanedStepsAsync`). Sonst bliebe ein Step für immer `Running` — sichtbar als endloser Spinner und als dauerhaft zu hohe „aktive Läufe"-Badge, weil beide nur `StepExecution.Status` lesen.
- **Step-Debugger:** `POST /execute` mit `debug: true` → Breakpoints, SignalR `StepPaused`, Resume via `POST /executions/{id}/resume`.

## Build & Test

Standard-Invocations (`dotnet build|test`, in `src/nodepilot-ui` die `package.json`-Scripts). Backend nutzt Central Package Management (`Directory.Packages.props`).

**Konventionen:**
- **Tests sind Pflicht.** Jeder relevante Code-Change braucht passenden Test-Code in derselben Änderung.
- Coverage-Gates: Backend Line >= 85 % / Branch >= 70 % — **erzwungen in `.github/workflows/ci.yml`, das ist die einzige autoritative Zahl** (Ratsche — nur anheben, nie senken). Frontend siehe `vitest.config.ts`. Messverfahren + Assembly-Filter: `docs/claude-reference.md`. Genuin untestbare Infrastruktur trägt `[ExcludeFromCodeCoverage]` **mit Begründungskommentar**; `coverage.runsettings` zieht das Attribut aus dem Nenner.
- Naming: `MethodName_Scenario_ExpectedResult`
- Remote-Layer (WinRM) IMMER gemockt.
- DB-Tests: SQLite in-memory.

### Testumfang pro Änderung

**Tests schreiben ≠ alle Tests ausführen.** Die Pflicht oben gilt unverändert für das *Schreiben*; lokal *ausgeführt* wird nur, was die Änderung betrifft. Die Voll-Suite ist gemessen unverhältnismäßig (6.597 Backend-Testfälle, 234 Vitest-Dateien, 77 E2E-Specs — die beiden Frontend-Zahlen hält `DocumentationCountsTests` an der Dateiliste fest, die Backend-Zahl bleibt ein Handmaß) und liefert lokal kein neues Signal: das Netz hängt an `ci.yml`, das auf **jedem PR und jedem Push auf main** läuft (Coverage-Gate + E2E eingeschlossen).

**Der Nightly ist kein verlässlicher zweiter Boden.** Er läuft als Windows-Task um 22:00 gegen den ausgecheckten Baum und wird verpasst, sobald die Maschine dann aus ist. Wer sich auf ihn beruft, prüft vorher `C:\temp\nodepilot-nightly\latest.md` auf sein Datum.

**Markdown-Ausnahme:** Ein PR, der **ausschließlich** `*.md` oder `docs/images/**` anfasst, überspringt Frontend, Desktop und E2E (`changes`-Job). **Backend und docs-ui laufen immer**, weil Markdown für sie eine Eingabe ist (`DocumentationCountsTests`, `SettingsSchemaDocumentationTests`, `MonitoringDeploymentSecurityTests`, Sprach-Parity-Guard). Pushes auf `main` laufen **immer** vollständig; jeder Fehlerpfad der Erkennung endet bei „alles ausführen".

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
| `KnownProgramLaunchers` / `lib/knownProgramLaunchers.ts` | `KnownProgramLaunchersFrontendSyncTests` | Engine.Tests |
| Neue EF-Migration / Designer-Postprocessing | `MigrationDriftTests` | Data.Tests |
| `*.csproj`-Referenzen / Dep-Graph | `DependencyDirectionTests` | Api.Tests |
| Neuer Audit-Code | `AuditActionsCatalogTests` | Api.Tests |
| API-DTO (+ CLI-Spiegel) | `ApiDtoParityTests` | Cli.Tests |
| Trigger-Config-Key | `TriggerContractParityTests` | Engine.Tests |
| `SettingsSchema.cs` / Admin-Settings-UI | `AdminSettingsFrontendSyncTests`, `SettingsSchemaDocumentationTests` | Api.Tests |
| AI-Prompt-Katalog | `PromptCatalogDriftTest` | Ai.Tests |
| Alerting-Katalog / System-Policies | `AlertingCatalogFrontendSyncTests`, `SystemAlertCatalogTests` | Engine.Tests |
| Workflow-Analyzer (`WorkflowAnalyzer`/`WorkflowDataBusAnalyzer` in Core — MCP **und** AI-Chat) | `WorkflowAnalyzerFrontendParityTests` | Engine.Tests |
| Template-Grammatik / Variable-Resolution | `TemplateGrammarParityTests` | Engine.Tests |
| Metrics-Dashboard-Katalog | `MetricsDashboardCatalogTests` | Api.Tests |
| Zahl-tragende Doku-Behauptung (MCP-Tool-Zahl, Activity-Typen, Skins), eine neue Vitest-/E2E-Datei **oder der Umfang dieser Datei** | `DocumentationCountsTests` | Mcp.Tests |
| `RequestSizeLimit` an `/import`/`/import-scorch` oder die Upload-Gates in `WorkflowsPage.tsx` | `ImportSizeLimitFrontendSyncTests` | Api.Tests |
| LLM-Profil-Defaults (`LlmProfileOptions`, `LlmProfileSettingsDto`, `SettingsSections.cs`, `IntegrationsSection.tsx`) | `LlmProfileDefaultsTests` | Api.Tests |
| `vite.config.ts`-Proxy / Dev-Ports | `AppSettingsHygieneTests` | Api.Tests |
| Browser-Demo (`demo/`), Hub-/Doku-/Auth-Naht, root-absolute URL-Literale in `src/` | `src/__tests__/demo/*` + `e2e-demo/demo-smoke.spec.ts` | nodepilot-ui |
| `index.css` / `designer-atelier.css` designer-light tokens | `designerLightParity.test.ts` | nodepilot-ui |
| Font-Tokens / Monaco-Stack | `fontTokens.test.ts` | nodepilot-ui |

**E2E (Playwright):** hermetische Specs in `src/nodepilot-ui/e2e/`, alle APIs gemockt (kein Backend/Postgres nötig). Konventionen: `src/nodepilot-ui/CLAUDE.md` + `src/nodepilot-ui/e2e/README.md`.

**Desktop-Shell:** `src/nodepilot-desktop` hat eine eigene vitest-Suite (node-Env) für die reine Logik — `config.ts`, `security.ts`, `skins.ts`. `npm run test:run`; eigener CI-Job `desktop`.

**Nightly:** Windows-Task `NodePilot Nightly Tests` (täglich 22:00) fährt via `scripts/nightly-tests.ps1` alle vier Suiten (je 1× Retry), Report nach `C:\temp\nodepilot-nightly\`. Das Skript killt vorm Rebuild nur `testhost`-Prozesse **aus diesem Checkout**. Zeit ändern: `scripts/register-nightly-task.ps1 -Time HH:mm`.

## Clients (`np` CLI + `nodepilot-mcp`)

Beide sind reine HTTP-Clients gegen die REST-API — **kein** eigener Backend-Pfad. Beide Installer liefern sie mit; `tools\np` landet idempotent in der Maschinen-`PATH`, der MCP-Server bewusst **nicht** (absoluter Pfad in `.mcp.json`). **Keine** `dotnet global tool`s — `PackAsTool` verträgt das geerbte `net10.0-windows`-TFM nicht (NETSDK1146, `docs/roadmap.md`-Sperrvermerk). Der MCP-Server ergänzt In-Proc-Analyse gegen `NodePilot.Core` (102 Tools, 3 Resources, stdio) und reused die DPAPI-Session der CLI.

**Jeder neue API-Endpoint braucht beide Clients.** Mechanik, Befehlsbereiche und Tool-Katalog: `src/NodePilot.Cli/CLAUDE.md`, `src/NodePilot.Mcp/CLAUDE.md`, `docs/mcp-server.md`.

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

**Ordner-Löschen hat zwei Sicherheitsgrenzen:** Ein leerer Ordner bleibt eine Folder-`Edit`-Mutation. `?recursive=true` entfernt auch Workflows und deren Execution-Historie und ist deshalb wie `DELETE /api/workflows/{id}` global Admin-only. Die Folder-Capabilities liefern dafür `canDelete` getrennt von `canEdit`; **die UI darf den rekursiven Delete nicht aus `canEdit` ableiten.**

**Der Global-Variablen-Ordnerbaum kennt dieselbe Mechanik, bleibt aber Admin-only** — es gibt dort kein Per-Ordner-RBAC, an dem sich lockern ließe. Frontend-seitig teilen sich beide Bäume die Löschmechanik (`hooks/useFolderBulkDelete.ts`, `components/common/FolderBulkBar.tsx`, `lib/folderSelection.ts`); die Baum-Komponenten selbst sind weiterhin Klone.

Initial-Admin: erster Login bei leerer DB (One-Shot-Token `admin-setup.token`).

## Security

- **Session:** absolute Lebensdauer **8h** (`Authentication:SessionAbsoluteLifetimeHours`; `AuthSessionIssuer`). Refresh verlängert die absolute Grenze **nicht**. `jti`-Revocation. Key aus `Jwt:Key` oder auto-generiertes `jwt-secret.key`.
- **Auth-Pfade:** Local-BCrypt (`Authentication:LocalLoginMode`, Produktionsdefault **`BreakGlassOnly`**) + LDAP + Windows-Negotiate + OIDC (release-gated, + SCIM-Controller). Alle konvergieren auf JWT-Cookie + CSRF-Token. Siehe `docs/ldap-windows-sso.md`.
- **External Trigger:** `X-Api-Key` gegen SHA-256-Hashes unter `ExternalTrigger:Keys:<id>`; jeder Eintrag hat eine GUID-only `AllowedWorkflowIds`-Liste. Die `Keys`-Map kommt **atomar** aus dem höchstprioren Provider (`Keys: {}` widerruft alle niedrigeren); Scope-Arrays ebenso (`[]` = deny-all). Der Workflow braucht zusätzlich einen aktiven `manualTrigger`. Legacy-`ApiKey` ist ohne eigene Liste inert.
- **Idempotency:** `POST /api/trigger/{name}` akzeptiert `Idempotency-Key`; Replay gilt nur innerhalb desselben Key-Principals und Workflows, domain-separiert per Integration-ID + Key-Fingerprint (die DB speichert nur den Digest). `Pending` + Reservation + Dispatch Intent entstehen in **einer** Transaktion und werden nach Failover weiter dispatched. Für bereits gestartete Executions bleibt die Reservation bestehen.
- **Rate-Limiting** (per-IP, Sliding-Window): login 50/Min, refresh 20/Min, webhook 60/Min, trigger 30/Min, ai-generate 20/Min, audit 60/Min, alerting-heavy 20/Min, backup 10/Min.
- **Output-Redaction:** `OutputRedactor` maskiert Secrets. Immer aktiv. Custom-Patterns via `Logging:Redaction:Patterns`.
- **Localhost-Bypass / Operator-Trust:** ohne Credentials läuft in-process unter der NodePilot-Service-Identität. `Operator` ist bewusst ein vertrauenswürdiger Automation-Author und darf solchen Workflow-Code publizieren/ausführen. Folder-RBAC ist keine Code-Sandbox. **Produkt-Feature, keinen Require-Target-Guard einziehen.**
- **Security-Headers (Non-Dev):** HSTS, CSP, X-Frame-Options=DENY, nosniff, Referrer-Policy.
- **SignalR-Auth:** httpOnly `np_auth`-Cookie wird beim WebSocket-Upgrade automatisch mitgeschickt (nur `/hubs/`); kein `?access_token=`-Querystring.
- **REST-API-Proxy:** `RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode`.

**Hardening-Flags** — vollständige Tabelle mit Defaults und Wirkung: `docs/claude-reference.md`. Zwei Feinheiten: `Webhook:RequireSecret` ist **default `true`** (fehlender Key liest als `true`), und `WaitForCondition:AllowedHosts` ist eine **eigene** Liste für die Probes `portOpen`/`httpOk`, bewusst getrennt von `RestApi:AllowedHosts` und **alleinige** Autorität für beide Probe-Typen.

## Admin-Settings Hot-Reload

Admin-Settings-Saves persistieren atomar nach `appsettings.runtime.json` (`reloadOnChange: true`). Pro Sektion trägt `SettingsSchema.cs` ein `IsHotReloadable`-Flag; nur `false`-Sektionen setzen den Restart-Marker (UI: emerald `HotReloadHint` vs. oranger `RestartBanner`). 13 Sektionen sind hot-reloadable, 9 restart-pflichtig; harter Kern (JWT, DB, Kestrel, Cluster/HA, `Remote:Provider`) bleibt boot-fixed.

**Consumer-Regel:** hot-reloadable Werte via `IOptionsMonitor<T>.CurrentValue` bzw. rohes `IConfiguration` pro Use/Pass lesen — **nie** `IOptions<T>.Value`-Snapshot.

**Dimensionierung:** `Performance:ManualTuning` (default **`false`**) entscheidet, ob `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` und `ExecutionDispatch:WorkerCount` aus erkannter CPU+RAM abgeleitet oder verbatim aus der Config genommen werden. Aus = hardware-adaptiv, die konfigurierten Zahlen bleiben inertes Preset. Restart-pflichtig. **`Engine:MaxConcurrentExecutions:*` ist ausgenommen** (Sicherheits-Cap, nicht Tuning). Details: `docs/performance-improvements.md` + `docs/claude-reference.md`.

## AuditLog

`IAuditWriter` injizieren, `await _audit.LogAsync(AuditActions.VerbNomen, "Resource", resourceId, detailsJson, ct)` **nach** `SaveChanges`. Schreibfehler darf normale Mutation nie abbrechen. Ausnahme: DB-Admin-Write-SQL läuft fail-closed — ohne vorab persistierten `DBADMIN_SQL_WRITE_ATTEMPTED`-Eintrag wird das SQL nicht ausgeführt. Passwörter/Secrets nie in Details.

Audit-Codes folgen dem Muster `VERB_NOMEN` und sind **zentral** in `NodePilot.Core.Audit.AuditActions` registriert — nie ein rohes String-Literal am Call-Site (Guard: `AuditActionsCatalogTests`). Pipeline: `IAuditStager` (Core) + `IAuditWriter` (Api, wrappt Stager); Archive gzip + SHA-256-Sidecar. Code-Übersicht: `docs/claude-reference.md`.

## KI-Features

Opt-in (`Llm:Enabled=false` default), OpenAI-kompatibler Endpunkt, Rate-Limit 20/min/IP. Volle Doku: `docs/ai-features.md` + `docs/claude-reference.md`.

- **`POST /api/ai/generate-script`** (Admin/Op, SSE-Streaming — tippt live in Monaco) + **`POST /api/ai/generate-workflow`** (Admin/Op, JSON).
- **`POST /api/ai/chat`** (alle Rollen, SSE) — Workflow-Assistent. Proposals nur Admin/Op, Merge per Node-ID aufs unredigierte Original (Secrets/Layout erhalten). **Secrets werden vor jedem LLM-Call redigiert** (`WorkflowSecretRedactor`). Tool-Calling opt-in am aktiven Profil.
- **Globaler AI-Chat / Wissens-Assistent** (`POST /api/ai/knowledge/ask`, SSE) — read-only Q&A in `/ai-chat`. Vier admin-toggelbare Quellen (Sektion `AiKnowledge`): **Docs**, **Operational** (RBAC-folder-gescoped), **Source-Code** (Admin/Op), **DB / text2sql** (**ausschließlich globaler Admin**, zentraler Executor-Guard über `ISqlKnowledgeReader`). **Folder-Grants erhöhen nie auf Raw-SQL.** Quellen sind nur sichtbar, wenn das aktive Profil `EnableToolCalling` gesetzt hat.
- **`llmQuery`-Activity:** Engine-lokal, Prompt→Text; per-Node-Overrides `baseUrl`/`model`/`apiKey`/`maxTokens`/`temperature`/`timeoutSeconds`/`jsonMode`, **gated durch `Llm:Enabled`**. Einziger BaseUrl-Validierungspunkt ist `LlmEndpointGuard`.
- **LLM-Profile:** `Llm:Profiles:<id>` ist ein **Objekt gekeyt nach unveränderlicher Id, kein Array** — nur so übersteht der Secret-Erhalt Rename/Reorder. `Llm:ActiveProfileId` wählt das eine aktive; **kein „nimm das erste"-Fallback** → 503 `LLM_NO_ACTIVE_PROFILE`. Ausgeliefert wird `"Profiles": {}` — ein Profil in der Basis-Config wäre über die UI nie löschbar. **Keine scoped `ILlmClient`-Registrierung**; Consumer nehmen `ILlmClientFactory`.
- **Zwei Wire-Dialekte, kein Config-Key:** `LlmEndpointGuard.ResolveEndpoint` leitet aus dem `BaseUrl`-Pfad ab, wer antwortet (`…/responses` → Responses-API, sonst Chat-Completions); endet der Pfad schon auf `/chat/completions`, wird **nichts** mehr angehängt. Quirk-Fallbacks sind Chat-Completions-only — **Ausnahme `temperature`** (`LlmTemperatureQuirk`, beide Dialekte).
- **Erreichbarkeit ≠ Antwortzeit:** `TimeoutSeconds` ist reines **Antwort**-Budget; der Verbindungsaufbau hat eigene Konstanten in `LlmConnectGuard`. **Die Ordnung `HandshakeTimeout` (30 s) > `ConnectPhaseTimeout` (15 s) ist tragend** (per Test gepinnt) — nur deshalb darf ein gefeuertes `ConnectTimeout` als TLS-Stufe gelesen werden.
- **LLM-Proxy:** `Llm:Proxy:Mode` = `Off` (default) | `System` | `Custom`. Sitzt bewusst **nicht** im `SocketsHttpHandler`, sondern in `LlmConfiguredProxy : IWebProxy` — nur deshalb bleibt die Sektion hot-reloadable.
- **Hardening:** SSRF-Block (Cloud-Metadata), Klartext-ApiKey-Warning, Prompt-Injection-Mitigation (Schema-only, User-reviewed Insert). Drift-Schutz: `PromptCatalogDriftTest.cs`. Audit: `AI_*`-Codes.

## Workflow Import/Export

`GET /{id}/export` / `GET /export` / `POST /import`. Envelope `nodepilot-workflow-export/v1`. Import erzeugt neue Einträge **immer disabled**; Aktivierung via `POST /{id}/enable`. Namenskollisionen → Suffix `" (Imported 2)"`. SCOrch-Import via `POST /import-scorch` übernimmt `<MaxParallelRequests>` originalgetreu als `MaxConcurrentExecutions`, inklusive `1`. Ziel-Folder via `?folderId=`; RBAC = Edit darauf. **Secrets werden hier redigiert** (`***`) — Teilen-Artefakt, kein DR.

## System-Configuration Backup (ADR 0001)

Getrennt vom Workflow-Export: portables Konfigurations-Backup (Workflows+Folders, Machines, Credentials, Globals, Users, Custom Activities, Alerting, Settings — **keine** Execution-History/Audit/Stats). Admin-only, Envelope `nodepilot-system-backup/v4` (`.npbackup`), Payload passphrasenbasiert verschlüsselt; unvollständige Exporte und Restores brechen fail-closed ab. **Kein vollständiges DR** — native DB-, ProgramData- und Key-Sicherung plus Restore-Drill bleiben erforderlich. UI `/backup`, CLI `np backup`. Details: `docs/claude-reference.md`.

## Production Deployment

Produktiv-Rollout über `deploy/`-Skripte — Claude führt sie **nur auf ausdrückliche Aufforderung** aus (Release-Artefakte über `deploy/Build-Artifact.ps1` sind der übliche Anlass; ein Rollout auf eine laufende Instanz braucht dieselbe eigene Freigabe). Vollständige Doku: `deploy/README.md`; Architektur (gMSA, Kestrel-HTTPS, Install-Dir-Split, Config-Keys, Stolperfallen): `docs/claude-reference.md`.

**Desktop-App (Electron, `deploy/desktop/`):** zweites Shipping-Ziel — offline Win-11-x64-Installer, alles als Boot-Start-Dienste. Posture `Deployment:Mode` (`Server`|`Desktop`, default `Server`): Desktop relaxiert **nur** loopback-DB-TLS + Kestrel-`ListenLocalhost`, der Rest bleibt Production-gehärtet. Volle Doku: `deploy/desktop/README.md`.
