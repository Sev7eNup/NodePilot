# NodePilot

Moderner, schlanker Ersatz fuer Microsoft System Center Orchestrator. Agentless Workflow-Orchestrierung fuer Windows-Umgebungen via WinRM.

**Diese Datei ist der Index, nicht die Doku.** Sie enthält Verhaltensregeln, Nachschlage-Tabellen und Invarianten — die Tiefe liegt in `docs/`.

**Pflege:** Ein Feature-PR fügt hier höchstens eine Zeile hinzu. Wer einen Absatz schreiben will, schreibt ihn in `docs/claude-reference.md` und verlinkt ihn von hier. Keine Fehler-Rückblenden („vorher war es so…"), keine Messwerte als Beleg — dieselbe Regel wie für Code-Kommentare. Steht eine Erklärung schon in `docs/`, steht hier nur der Zeiger. Umfang ist per `DocumentationCountsTests` gedeckelt.

## Contributor- & Attribution-Policy

NodePilot ist ein Single-Contributor-Projekt. KI darf beim Entwickeln helfen (diese Datei, `.claude/`, `.agents/` bleiben in Nutzung) — aber für ALLE Commits ab v1.0.0 gilt:

- **Autor & Committer sind immer** `Sev7eNup <79143581+Sev7eNup@users.noreply.github.com>`. Der GitHub-Contributor-Graph zeigt ausschließlich `sev7enup`.
- **Co-Author-Trailer sind erlaubt** (Claude, Codex, Dependabot). Sie stehen immer *hinter* dem Autor: `Sev7eNup` bleibt Hauptautor und damit das erste Avatar am Commit. Ein Trailer darf den Autor nie ersetzen.
- **Natürliche, menschliche Sprache.** Alle Texte und Beschreibungen — ebenso PR-Titel, PR-Beschreibungen und Commit-Nachrichten — sind natürlich und menschlich formuliert. KI-Floskeln und aufgeblähte Formulierungen sind zu vermeiden, Aussagen bleiben konkret und verständlich.
- **Sprache auf GitHub ist Englisch.** Commit-Messages, PR-Titel/-Beschreibungen, Issues, Issue-Kommentare, Review-Kommentare und Branch-Namen werden auf Englisch verfasst — unabhängig davon, in welcher Sprache der Chat geführt wird. (Repo-interne Doku und Code-Kommentare bleiben davon unberührt: dort gilt weiter die vorhandene Sprache der jeweiligen Datei.)

## Agent skills

- **Issue tracker:** GitHub Issues für `Sev7eNup/NodePilot`, siehe `docs/agents/issue-tracker.md`
- **Triage labels:** Fünf-Label-Vokabular, siehe `docs/agents/triage-labels.md`
- **Domain docs:** Single-context repo → root `CONTEXT.md` + `docs/adr/`, siehe `docs/agents/domain.md`

## Doku-Landkarte

- `docs/roadmap.md` — **führendes Dokument für „was wird gebaut"** (R1 gesetzt, R2 trigger-gated, E offen, Sperrvermerk-Anhang). Was dort nicht steht, ist kein Vorhaben.
- `docs/claude-reference.md` — **Overflow-Referenz dieser Datei**; die Sektionen hier zeigen auf ihre Tiefe dort
- `docs/alerting.md` — Notification-Rules + System-Policies (ADR 0008), Dispatcher, Sinks, Ledger
- `docs/custom-activities.md` — Custom Activities (Plugin-System)
- `docs/mcp-server.md` — MCP-Server inkl. Tool-Katalog + `.mcp.json`-Beispiel
- `docs/ai-features.md` — KI-Features: Config-Keys, Modell-Empfehlungen
- `docs/deployment-guide.md` — (EN) Artefakt verifizieren, selbst bauen, Troubleshooting — **nicht** der Installationsweg, der steht **einmal** auf der Doku-Website (`content/{de,en}/deployment/production.md`)
- `docs/av-exclusions.md` — Antiviren-Ausschlüsse (Server + Desktop) als Übergabedokument für eine AV-Abteilung
- `docs/workflow-styleguide.md` — Layout-Styleguide für Workflow-JSONs (**vor jedem Workflow-Gen lesen**)
- `docs/workflow-tests.md` — Test-Suite unter `scripts/test-suite/`: 46 generierte Workflows gegen die laufende Engine, `suite-manifest.json` als Abdeckungsquelle, Guard-Test `TestSuiteCoverageTests`
- `docs/enterprise-features.md` — HA, Secret-Provider, LDAP/SSO, SIEM, Folder-RBAC
- `docs/ai-feature-ideas.md` — Beschreibungstiefe zu den KI-Ideen, **keine Spezifikation**. Priorisierung und Status stehen in `docs/roadmap.md`.
- `src/nodepilot-ui/e2e/README.md` — E2E-Coverage-Map + Spec-Konventionen
- `src/nodepilot-ui/demo/` — **Browser-Demo** der SPA für GitHub Pages (`/demo/`), dieselbe App gegen ein In-Memory-Backend; Regeln in `src/nodepilot-ui/CLAUDE.md`
- `src/nodepilot-docs-ui/src/site/` — **Projekt-Website** (DE/EN) an der Pages-Wurzel, Doku darunter unter `/docs/`; **nie** Teil von `dist/`. Build, Routen, Vorschau: `src/nodepilot-docs-ui/README.md`. Impressum/Datenschutz liefert der User.

## Tech-Stack

- **Backend:** ASP.NET Core Web API, .NET 10, Windows-only (`net10.0-windows`)
- **Datenbank:** PostgreSQL (default) / SQL Server (`Database:Provider` = `postgres` | `sqlserver`). SQLite nur als Test-In-Memory-Backend.
- **Remote Execution:** PowerShell SDK / WinRM, agentless. `Remote:Provider`: `winrm` (default) | `noop` (`noop` braucht `Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1`, sonst Boot-Abbruch). Engine-local In-Proc-Pool (WinPS-Kompatibilität bewusst aus): `docs/performance-improvements.md`
- **Real-time:** SignalR (`/hubs/execution`)
- **Logging:** Serilog. Format via `Logging:Format`: `text`|`cmtrace`|`json`|`ecs-json` (ECS 1.x für SIEM, siehe `docs/siem-logging.md`). Support-Log: File + DB-Projektion
- **MCP-Server (opt-in):** `nodepilot-mcp` (stdio) — AI-Agent steuert/editiert Workflows über 102 Tools, HTTP-only gegen die REST-API
- **Enterprise (opt-in):** Active/Passive HA (`Cluster:Enabled`), pluggable Secret-Provider (`Secrets:Provider` = `Dpapi`|`AesGcm`), LDAP/Windows-SSO, ECS-JSON-SIEM, Folder-RBAC

## Solution-Struktur

Projekt-Layout unter `src/` + `tests/` — nicht hier gespiegelt, direkt nachsehen. Bindend ist die Abhaengigkeitsrichtung:

**Dep-Graph:** `Api -> Ai, Engine, Scheduler, Data, Remote, Core, Telemetry` | `Engine -> Ai, Data, Remote, Core, Telemetry` | `Scheduler -> Engine, Data, Core` (Application-Tier: konsumiert Engine-Notifications/-Conditions/-Security) | `Ai -> Core` (LLM-Stack, sitzt unter Engine, damit Api+Engine ihn teilen) | `Data -> Core` | `Remote -> Core` | `Telemetry -> Core` | `Cli -> Core` (HTTP-only) | `Mcp -> Core` (HTTP-only, MCP-Server) | `Switcher -> ∅` (lokale Windows-SCM-WPF-App). Maschinell erzwungen durch `DependencyDirectionTests` (Api.Tests/Architecture) — Graph-Änderung heißt: csproj + diese Zeile + der Test ändern sich gemeinsam.

## Projekt starten

```powershell
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -l 'C:\NodePilot-Postgres\data\postgres.log' -w   # Postgres dieser Maschine — immer zuerst
cd src\NodePilot.Api; dotnet run          # Backend, Port 5000 (launchSettings.json = Ziel des Vite-Proxys)
cd src\nodepilot-ui; npm run dev          # Frontend, Port 5173
cd src\nodepilot-docs-ui; npm run dev     # Doku-Website, Port 5174 — nur wenn /docs im Dev erreichbar sein soll
```

**Erster Login braucht das Setup-Token** aus `src\NodePilot.Api\admin-setup.token` (die Login-Maske zeigt das Feld beim ersten Versuch). Rolle/DB anlegen, Connection-String: `CONTRIBUTING.md` § Local setup.

**`/docs` im Dev:** Vite proxyt `/docs` auf 5174; läuft der Doku-Dev-Server nicht, führt der Doku-Button ins Leere — erwartet, kein Defekt. Der Doku-Dev-Server läuft selbst unter `/docs/` (`--base=/docs/` im dev-Skript, **weil Vite 8 das `base` aus der Config im Dev ignoriert**); ohne das landet man in der App statt in der Doku.

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

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`. Bestehende Datenbanken niemals automatisch löschen oder deren Migration-History umschreiben (Baseline und Upgrade-Pfad: `CONTRIBUTING.md`).
- **Neue Migration:** `dotnet ef migrations add <Name> --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext`. **Pflicht-Postprocessing — zwei Schritte:**
  1. In der Migration (`<Name>.cs`): alle `type: "..."`-Annotations entfernen.
  2. In der Designer-Datei (`<Name>.Designer.cs`): `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` als letzte Zeile vor `#pragma warning restore 612, 618` in `BuildTargetModel` ergänzen. Der `ModelSnapshot` bekommt den Aufruf bewusst **nicht** (Diff-Basis, kein Migration-Target-Model).

  Beide Schritte sind durch `MigrationDriftTests` abgesichert — laufen lassen statt sich erinnern.
- Schema-Änderungen IMMER per EF-Migration. Kein DDL-Hotpatching.
- Credentials mit DPAPI verschlüsselt (`Credentials:DpapiScope`). DB-TLS ist strikt (`DatabaseTlsBootValidator`); Escape-Bedingungen, Retention-Dienste und alle Background-Services: `docs/claude-reference.md`.

### Datenbank-Verfügbarkeit (Laufzeit-Ausfall, ADR 0011)

Prozessweiter In-Memory-Breaker (`NodePilot.Data.Availability`): fällt die DB zur Laufzeit aus, antwortet `/api` sofort `503 DATABASE_UNAVAILABLE` statt zu hängen. Invarianten und Health-Endpunkte: `docs/adr/0011-database-availability-breaker.md` + `docs/claude-reference.md`. Zwei Invarianten stehen hier, weil man sie mit nur dieser Datei sonst „repariert":

- **Nur die Sonde publiziert `Available`** (`SELECT 1` auf eigener ungepoolter Verbindung, nie `CanConnectAsync`); EF-Interceptors degradieren nur, ein Command-Timeout armt nur die Sonde. Hintergrunddienste parken via `WaitUntilServableAsync`.
- `Database:AuthReadTimeoutSeconds` und die übrigen Availability-Budgets sind restart-pflichtige Boot-Config und bewusst **kein** `SettingsSchema`-Eintrag — der Connection-String gehört auf keine HTTP-Fläche.

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

**Per-Workflow-Parallelität:** `Workflow.MaxConcurrentExecutions` begrenzt gleichzeitige Läufe *eines* Workflows über **alle** Aufrufer hinweg; am Limit wird **eingereiht statt abgelehnt** (ein Zähler für beide Wege: `IWorkflowConcurrencyGate`), und der Dispatch-Claim überspringt Workflows am Limit. Nicht versioniert, nicht im Update/Publish-Body. Details: `docs/claude-reference.md` § Per-Workflow-Parallelität.

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

Config-Keys & Output-Semantik pro Activity sowie Prozess-Isolation (`config.isolated: true`, nur lokal, No-Op auf dem WinRM-Pfad): `docs/claude-reference.md`.

- **Retry pro Step:** `config.retry` (`maxAttempts`, `backoff`, `initialDelayMs`, `maxDelayMs`). Dauerhafte Remote-Fehler (abgelehnter WinRM-Logon, geblockte HTTP-Session, nicht entschlüsselbares Credential) werden **nicht** wiederholt — sonst sperrt ein Step ein Konto.
- **Execution-Timeout:** `timeoutSeconds` im Execute-Body + per-Step `config.timeoutSeconds`.

## Custom Activities (Plugin-System)

User-authored, PowerShell-backed Activities (UI „Custom Nodes") als reine **runScript-Presets** — dieselbe Engine, Isolation, Marker-Capture und Redaction, keine zweite Script-Engine. `activityType = custom:<key>` → ein Sentinel-registrierter `CustomActivityExecutor` (`__customDefinitionId` authoritativ, `__customKey` Drift-Guard); captured werden nur die deklarierten Outputs (+ `exitCode`). Governance: Mutation nur solange disabled (Admin+Operator), ab enabled Admin-only; kein `secret`-Input-Typ. Geteilte Facts-Schicht `NodePilot.Core.Activities.CustomActivityType`, Frontend-Spiegel `lib/customActivities.ts`; `activityCatalog.generated.ts` bleibt **unberührt**. Volle Doku: `docs/custom-activities.md`.

## Alerting (Notification-Rules)

Custom-Regeln (`Kind=Custom`, Execution-Events, Filter-AST = derselbe `ConditionEvaluator` wie Edge-Conditions) und System-Policies (`Kind=System`, ADR 0008, `ISystemAlertSource`-Katalog), zugestellt über SMTP / Webhook + HMAC; idle bis eine Regel existiert. **Zustellung ist at-least-once:** der `NotificationDispatcher` persistiert den Attempt vor jedem I/O, ein Crash danach kann erneut zustellen — **Webhook-Empfänger deduplizieren über `EventKey`.** Alle Mutationen + Test-Fire Admin-only, neue Regeln entstehen disabled. Volle Doku: `docs/alerting.md`.

## Architektur-Konventionen

- **Neue Activity:** Klasse in `Engine/Activities/`, `IActivityExecutor` implementieren — Auto-Discovery via `AddNodePilotActivities()`, keine DI-Verdrahtung. Pflichtteil derselben Änderung: die UI-Seite (`src/nodepilot-ui/CLAUDE.md`) **und** ein Eintrag in `src/NodePilot.Core/Activities/Embedded/activity-config-reference.json`, aus dem AI-Prompt-Katalog und MCP-Config-Tools gespeist werden. Guard-Tests: Tabelle unter *Build & Test*.
- **Neuer API Controller:** In `Api/Controllers/`, DTOs in `Api/Dtos/`; braucht CLI-Command **und** MCP-Tool (siehe *Clients*).
- **Frontend:** Seiten, Nodes, i18n, State und Design-Tokens: `src/nodepilot-ui/CLAUDE.md`.
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

**Ein veröffentlichter Wert hat genau einen Besitzer (SCOrch-Modell).** `{{aktivität.param.name}}` ist verbindlich und löst bei **jedem** Nachfahren auf; den unqualifizierten Kurznamen (`$name` im `runScript`) bindet der Resolver nur bei **genau einem** Publisher auf dem Vorgängerpfad, bei zweien wird **nichts** gebunden. Linter-Code `dup-published-param`, gemeldet nur für selbst vergebene Namen (`WorkflowDataBusAnalyzer.AuthoredParameters` vs. `TypeDerivedParameters`). Details: `docs/workflow-designer-features.md`.

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

**Conditions sind fail-closed (ADR 0015):** Save/Publish/Import lehnen eine unbrauchbare Condition mit `400 invalid-edge-condition` ab (`EdgeConditionValidator` in Core); zur Laufzeit bricht der Scheduler den Lauf mit Kantenbezug ab, statt die Kante still zu überspringen. Ein Variablen-Operand **ohne Wert** ist unentscheidbar und erfüllt die Condition nie, auch nicht durch `not`. Alerting-Filter (`source: event`) lesen fehlende Felder als leer. Details: `docs/adr/0015-fail-closed-edge-conditions.md`.

## Sub-Workflows & Contract

`startWorkflow` ruft jeden enabled Workflow auf (frischer DI-Scope), unabhängig vom Trigger-Typ; `parameters` landen als `manual.*` im Child-Run, bei `waitForCompletion: true` (default) blockiert der Parent und spiegelt das Child-`returnData` als `param.*`. **Max Call-Depth: 10** — gilt auch für `forEach`. `GET /{id}/contract` leitet Inputs/Outputs ab; By-name-Lookup (API, Engine, Trigger/Webhook): exact-case gewinnt, sonst case-insensitive, mehrdeutig → 409 bzw. Step-Fehler (`WorkflowNameResolver`). Details: `docs/claude-reference.md` § Contract-Derivation.

## Trigger

| Trigger | Backing |
|---|---|
| `scheduleTrigger` | Quartz cron |
| `fileWatcherTrigger` | FileSystemWatcher |
| `databaseTrigger` | Timer + SELECT-Polling |
| `eventLogTrigger` | EventLog.EntryWritten |
| `webhookTrigger` | HTTP `/api/webhooks/{name}/{path}` |
| `manualTrigger` | UI / API |

`TriggerOrchestrator` scannt alle 5 s. Trigger-Daten landen als `manual.*`-Variablen im Run + als `param.*` des Trigger-Nodes — **kein** `trigger.*`-Namespace. Key-Namen, Config-Vertrag, Liveness und Zustellung: `docs/claude-reference.md` §§ Trigger. Invarianten:

- **Ein Trigger-Key lebt einmal in `Core/Triggers/`** und wird von Node-Executor (`Engine/Triggers/`) und Hintergrundquelle (`Scheduler/Sources/`) gelesen. Neuer Key = Settings-Klasse + `activity-config-reference.json` + Designer-Feld, sonst bricht `TriggerContractParityTests`. `databaseTrigger` feuert bei **Sentinel-Änderung**, nicht pro Zeile.
- **Kein Nachholen nach Neustart oder Failover:** der durable Cursor dedupliziert, er backfillt nicht — Signale aus einem Stillstandsfenster werden verworfen (`nodepilot.scheduler.triggers.fires_skipped`).
- `ITriggerSource.Health` ist ein **reiner In-Memory-Read**; `unhealthy` → Quelle wird evictet und mit Backoff neu aufgebaut, ein `FileSystemWatcher` lässt sich nicht in-place re-armen. Buffer-Overflow ist bewusst **kein** Fault.
- **webhookTrigger:** `signatureMode` = `header` (default) oder `nodepilot-hmac-v2`; **Legacy `hmac` (Body-only) wird abgelehnt.** `fieldMappings` extrahiert Body-Felder per JSONPath als `manual.*`.

## WorkflowEngine — Execution-Modell

- **Event-driven:** Queue + `inFlight`-Dict. Roots = **ausschließlich Trigger-Nodes** (ADR 0006): ohne aktiven Trigger 0 Roots → Execution `Failed`, **kein** `inDegree==0`-Fallback. Activities ohne eingehende Edge und Nodes mit `data.disabled: true` → `Skipped`, Downstream ohne andere Quellen auch. Leerer Workflow läuft mit 0 Steps durch (`Succeeded`).
- **Expliziter Fan-in (ADR 0013):** Nur eine `junction` darf mehrere eingehende Edges haben; Designer und SCOrch-Import fügen bei Bedarf eine `waitAll`-Junction ein, die Strukturvalidierung schützt Save/Publish/API.
- **Cancellation:** `_runningExecutions` Dict (Guid → CTS). **Per-Step-DI-Scope:** eigener Scope pro Step → scope-lokaler `DbContext`.
- **Startup-Reconciler (ADR 0014):** `Running`/`Paused` und `Pending` ohne Dispatch Intent → `Cancelled`; `Pending` mit durablem Outbox-Intent wird neu geleast.
- **Kein Step überlebt seine Execution:** Jede terminale Execution-Schreibung setzt anschließend alle noch `Running`/`Paused`-Steps auf `Cancelled` (`ExecutionStateLifecycle.CancelOrphanedStepsAsync`). Sonst bliebe ein Step für immer `Running` — sichtbar als endloser Spinner und als dauerhaft zu hohe „aktive Läufe"-Badge, weil beide nur `StepExecution.Status` lesen.
- **Step-Debugger:** `POST /execute` mit `debug: true` → Breakpoints, SignalR `StepPaused`, Resume via `POST /executions/{id}/resume`.

## Build & Test

Standard-Invocations (`dotnet build|test`, in `src/nodepilot-ui` die `package.json`-Scripts). Backend nutzt Central Package Management (`Directory.Packages.props`).

**Konventionen:** Tests sind Pflicht — jeder relevante Code-Change bringt passenden Test-Code in derselben Änderung. Naming `MethodName_Scenario_ExpectedResult`; Remote-Layer (WinRM) IMMER gemockt; DB-Tests SQLite in-memory. Coverage-Gate Backend Line >= 85 % / Branch >= 70 %, **erzwungen in `.github/workflows/ci.yml`, das ist die einzige autoritative Zahl** (Ratsche — nur anheben, nie senken); Frontend siehe `vitest.config.ts`. Messverfahren, Assembly-Filter und die `[ExcludeFromCodeCoverage]`-Regel: `docs/claude-reference.md` § Coverage-Messung.

### Testumfang pro Änderung

**Tests schreiben ≠ alle Tests ausführen.** Die Pflicht oben gilt unverändert für das *Schreiben*; lokal *ausgeführt* wird nur, was die Änderung betrifft. Die Voll-Suite ist gemessen unverhältnismäßig (6.597 Backend-Testfälle, 235 Vitest-Dateien, 77 E2E-Specs — die beiden Frontend-Zahlen hält `DocumentationCountsTests` an der Dateiliste fest, die Backend-Zahl bleibt ein Handmaß) und liefert lokal kein neues Signal: das Netz hängt an `ci.yml`, das auf **jedem PR und jedem Push auf main** läuft (Coverage-Gate + E2E eingeschlossen).

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

Parity-/Drift-Tests erzwingen Konsistenz zwischen weit auseinanderliegenden Dateien und liegen über sechs Testprojekte verteilt. Deshalb: **eine Auslöser-Fläche angefasst → genau diesen Test fahren**, statt sicherheitshalber alles.

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
| Neues Testprojekt in `NodePilot.slnx` / `coverage.runsettings` | `TestRunSettingsTests` | Api.Tests |
| Browser-Demo (`demo/`), Hub-/Doku-/Auth-Naht, root-absolute URL-Literale in `src/` | `src/__tests__/demo/*` + `e2e-demo/demo-smoke.spec.ts` | nodepilot-ui |
| `index.css` / `designer-atelier.css` designer-light tokens | `designerLightParity.test.ts` | nodepilot-ui |
| Font-Tokens / Monaco-Stack | `fontTokens.test.ts` | nodepilot-ui |

**E2E (Playwright):** hermetische Specs in `src/nodepilot-ui/e2e/`, alle APIs gemockt — Konventionen in `src/nodepilot-ui/CLAUDE.md` + `src/nodepilot-ui/e2e/README.md`. **Desktop-Shell:** eigene vitest-Suite und CI-Job `desktop`, siehe `src/nodepilot-desktop/README.md`. **Nightly:** `scripts/nightly-tests.ps1` (Windows-Task, 22:00, Report nach `C:\temp\nodepilot-nightly\`); Zeit ändern per `scripts/register-nightly-task.ps1 -Time HH:mm`.

## Clients (`np` CLI + `nodepilot-mcp`)

Beide sind reine HTTP-Clients gegen die REST-API — **kein** eigener Backend-Pfad; der MCP-Server ergänzt In-Proc-Analyse gegen `NodePilot.Core` (102 Tools, 3 Resources, stdio). Packaging, Anmeldewege, geteilte Client-Infrastruktur und Tool-Katalog: `src/NodePilot.Cli/CLAUDE.md`, `src/NodePilot.Mcp/CLAUDE.md`, `docs/mcp-server.md`.

**Jeder neue API-Endpoint braucht beide Clients** (Guard: `EndpointClientCoverageTests`).

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

**Der Global-Variablen-Ordnerbaum kennt dieselbe Mechanik, bleibt aber Admin-only** — dort gibt es kein Per-Ordner-RBAC, an dem sich lockern ließe.

## Security

- **Session:** JWT-Cookie + CSRF-Token, absolute Lebensdauer **8h** (`Authentication:SessionAbsoluteLifetimeHours`; Refresh verlängert sie **nicht**), `jti`-Revocation. Auth-Pfade (Local-BCrypt mit Produktionsdefault `LocalLoginMode=BreakGlassOnly`, LDAP, Windows-Negotiate, OIDC + SCIM): `docs/ldap-windows-sso.md`; Details `docs/claude-reference.md` § Security.
- **External Trigger:** `X-Api-Key` gegen SHA-256-Hashes unter `ExternalTrigger:Keys:<id>`; jeder Eintrag hat eine GUID-only `AllowedWorkflowIds`-Liste. Die `Keys`-Map kommt **atomar** aus dem höchstprioren Provider (`Keys: {}` widerruft alle niedrigeren); Scope-Arrays ebenso (`[]` = deny-all). Der Workflow braucht zusätzlich einen aktiven `manualTrigger`. Legacy-`ApiKey` ist ohne eigene Liste inert.
- **Idempotency:** `POST /api/trigger/{name}` akzeptiert `Idempotency-Key`, Replay nur innerhalb desselben Key-Principals und Workflows; `Pending` + Reservation + Dispatch Intent entstehen in **einer** Transaktion und überleben Failover.
- **Rate-Limiting** (per-IP, Sliding-Window): login 50/Min, refresh 20/Min, webhook 60/Min, trigger 30/Min, ai-generate 20/Min, audit 60/Min, alerting-heavy 20/Min, backup 10/Min.
- **Output-Redaction:** `OutputRedactor` maskiert Secrets. Immer aktiv. Custom-Patterns via `Logging:Redaction:Patterns`.
- **Localhost-Bypass / Operator-Trust:** ohne Credentials läuft in-process unter der NodePilot-Service-Identität. `Operator` ist bewusst ein vertrauenswürdiger Automation-Author und darf solchen Workflow-Code publizieren/ausführen. Folder-RBAC ist keine Code-Sandbox. **Produkt-Feature, keinen Require-Target-Guard einziehen.**
- **Security-Headers (Non-Dev):** HSTS, CSP, X-Frame-Options=DENY, nosniff, Referrer-Policy. **SignalR-Auth** über das httpOnly `np_auth`-Cookie beim WebSocket-Upgrade (nur `/hubs/`); kein `?access_token=`-Querystring.
- **REST-API-Proxy:** `RestApi:Proxy:Enabled` (default `false`). Per-Step-Override via `proxyMode`.

**Hardening-Flags** — Tabelle mit Defaults und Wirkung: `docs/claude-reference.md`. Zwei Feinheiten: `Webhook:RequireSecret` ist **default `true`** (fehlender Key liest als `true`), und `WaitForCondition:AllowedHosts` ist eine **eigene** Liste für die Probes `portOpen`/`httpOk`, bewusst getrennt von `RestApi:AllowedHosts` und **alleinige** Autorität für beide Probe-Typen.

## Admin-Settings Hot-Reload

Admin-Settings-Saves persistieren atomar nach `appsettings.runtime.json` (`reloadOnChange: true`). Pro Sektion trägt `SettingsSchema.cs` ein `IsHotReloadable`-Flag; nur `false`-Sektionen setzen den Restart-Marker. 13 Sektionen sind hot-reloadable, 9 restart-pflichtig; harter Kern (JWT, DB, Kestrel, Cluster/HA, `Remote:Provider`) bleibt boot-fixed. Matrix: `docs/claude-reference.md` § Hot-Reload-Matrix.

**Consumer-Regel:** hot-reloadable Werte via `IOptionsMonitor<T>.CurrentValue` bzw. rohes `IConfiguration` pro Use/Pass lesen — **nie** `IOptions<T>.Value`-Snapshot.

**Dimensionierung:** `Performance:ManualTuning` (default **`false`**) entscheidet, ob Runspace-, Step-, Threading- und Dispatch-Worker-Zahlen aus erkannter CPU+RAM abgeleitet oder verbatim aus der Config genommen werden; restart-pflichtig. **`Engine:MaxConcurrentExecutions:*` ist ausgenommen** (Sicherheits-Cap, nicht Tuning). Details: `docs/performance-improvements.md`.

## AuditLog

`IAuditWriter` injizieren, `await _audit.LogAsync(AuditActions.VerbNomen, "Resource", resourceId, detailsJson, ct)` **nach** `SaveChanges`. Schreibfehler darf normale Mutation nie abbrechen. Ausnahme: DB-Admin-Write-SQL läuft fail-closed — ohne vorab persistierten `DBADMIN_SQL_WRITE_ATTEMPTED`-Eintrag wird das SQL nicht ausgeführt. Passwörter/Secrets nie in Details. Codes folgen `VERB_NOMEN` und sind **zentral** in `NodePilot.Core.Audit.AuditActions` registriert — nie ein rohes String-Literal am Call-Site (Guard: `AuditActionsCatalogTests`). Code-Übersicht und Pipeline: `docs/claude-reference.md` § Audit-Codes.

## KI-Features

Opt-in (`Llm:Enabled=false` default), OpenAI-kompatibler Endpunkt, Rate-Limit 20/min/IP. Volle Doku: `docs/ai-features.md` + `docs/claude-reference.md` § KI-Features.

- `POST /api/ai/generate-script` (Admin/Op, SSE-Streaming) + `POST /api/ai/generate-workflow` (Admin/Op, JSON).
- `POST /api/ai/chat` (alle Rollen, SSE) — Workflow-Assistent; Proposals nur Admin/Op, Merge per Node-ID aufs unredigierte Original. **Secrets werden vor jedem LLM-Call redigiert** (`WorkflowSecretRedactor`).
- `POST /api/ai/knowledge/ask` (SSE) — globaler Wissens-Assistent in `/ai-chat`, vier admin-toggelbare Quellen (Sektion `AiKnowledge`). **DB / text2sql ausschließlich globaler Admin** (zentraler Guard über `ISqlKnowledgeReader`); Folder-Grants erhöhen nie auf Raw-SQL.
- `llmQuery`-Activity: Engine-lokal, per-Node-Overrides, gated durch `Llm:Enabled`; einziger BaseUrl-Validierungspunkt ist `LlmEndpointGuard`.

**Profile:** `Llm:Profiles:<id>` ist ein Objekt gekeyt nach unveränderlicher Id, kein Array; `Llm:ActiveProfileId` wählt das aktive — **kein „nimm das erste"-Fallback** (503 `LLM_NO_ACTIVE_PROFILE`). Keine scoped `ILlmClient`-Registrierung; Consumer nehmen `ILlmClientFactory`. Wire-Dialekte, Timeouts, Proxy und Hardening: `docs/ai-features.md`.

## Workflow Import/Export

`GET /{id}/export` / `GET /export` / `POST /import`, Envelope `nodepilot-workflow-export/v1`. Import erzeugt neue Einträge **immer disabled**; Ziel-Folder via `?folderId=`, RBAC = Edit darauf. **Secrets werden redigiert** (`***`) — Teilen-Artefakt, kein DR. SCOrch-Import (`POST /import-scorch`): `docs/claude-reference.md` § SCOrch-Import.

## System-Configuration Backup (ADR 0001)

Getrennt vom Workflow-Export: portables, passphrasenverschlüsseltes Konfigurations-Backup (`.npbackup`, Envelope `nodepilot-system-backup/v4`), Admin-only, fail-closed bei unvollständigem Export oder Restore; **keine** Execution-History und **kein vollständiges DR**. UI `/backup`, CLI `np backup`. Details: `docs/adr/0001-system-configuration-backup-restore.md` + `docs/claude-reference.md`.

## Production Deployment

Rollout über `deploy/`-Skripte (Freigabe-Regel unter *Projekt starten*). Doku: `deploy/README.md`; Architektur, Config-Keys und Stolperfallen: `docs/claude-reference.md` § Production Deployment. **Desktop-App** (Electron, `deploy/desktop/`, `Deployment:Mode=Desktop`): relaxiert **nur** loopback-DB-TLS + Kestrel-`ListenLocalhost`, der Rest bleibt Production-gehärtet — `deploy/desktop/README.md`.
