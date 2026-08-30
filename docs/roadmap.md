# NodePilot Roadmap

**Führendes Dokument für „was wird gebaut".** Was hier nicht steht, ist kein Vorhaben — auch dann
nicht, wenn es irgendwo als Idee, Plan oder Follow-up auftaucht.

Stand: **2026-08-22**. Konsolidiert am 2026-08-03 aus dem damaligen `docs/backlog.md`, `docs/ai-feature-ideas.md`,
`docs/db-schema-audit-followups.md`, den „Noch offen"-Abschnitten in `docs/performance-improvements.md`
und `docs/alerting.md`, den offenen GitHub-Issues sowie der Session-Memory. Der Status der R1-Posten
ist am 2026-08-22 gegen `main` verifiziert — was unten als erledigt markiert ist, liegt im Code.

## Spielregeln

- **R1** — gesetzt. Wird gebaut, Reihenfolge unten ist die empfohlene.
- **R2** — steht auf der Roadmap, startet aber erst bei einer **benannten Auslösebedingung**.
  Kein Datum, kein „irgendwann" — jeder R2-Posten trägt seinen Trigger.
- **E** — offene Entscheidung. Kann nicht gebaut werden, bevor sie getroffen ist.
- **Anhang** — bewusst *nicht* auf der Roadmap, inkl. Begründung. Der Anhang ist ein Sperrvermerk:
  Er verhindert, dass verworfene oder gemessen widerlegte Ideen erneut aufschlagen.

Ein Posten wandert von R2 nach R1, wenn sein Trigger eintritt — nicht, weil er alt geworden ist.

---

## R1 — Gesetzt

28 Posten in acht Wellen. Die Wellenreihenfolge ist bewusst: erst wird ehrlich, was gerade
unehrlich ist, dann wird gebaut.

### Welle 1 — Sicherheit

Detail zu den Findings liegt bewusst **nicht** in diesem öffentlichen Repo, sondern im
Audit-Protokoll vom 2026-07-26 (Session-Memory `security-audit-2026-07-26-open-items`).
Hier steht nur, *was* geändert wird.

| # | Posten | Inhalt |
|---|---|---|
| 1 | ~~**Lokale Skript-Ausführung härten**~~ **Entschieden (2026-08-15)** | `Operator` ist bewusst ein vertrauenswürdiger Automation-Author. Lokale Activities dürfen unter der NodePilot-Service-Identität laufen; Folder-RBAC ist keine Code-Sandbox. README und CLAUDE.md dokumentieren diese Trust Boundary. Ein `Engine:RequireTargetMachineForScripts`-Flag ist daher nicht vorgesehen. |
| 2 | ~~**SSRF-Guard: Adressabdeckung vervollständigen**~~ **Erledigt (2026-08-18)** | Behoben und als M-34 in [`security-findings.md`](security-findings.md) registriert, mit Regressionstest in beide Richtungen. |
| 3 | **Passwortpolicy für Break-Glass-Konten** | Die Mindestlänge für explizit als Break-Glass markierte Konten anheben und eine Komplexitätsregel ergänzen. |
| 4 | **`restApi` bekommt ein First-Class-Credential-Feld** | Ein `credentialId`-Feld analog zu den Remote-Activities, damit Auth-Daten denselben Weg nehmen wie überall sonst statt über frei befüllbare Header. Backend + Designer-Konfiguration. |
| 5 | **Audit-Nachlauf: die verbleibenden Dimensionen** | Der Audit vom 2026-07-26 hat nicht alle Bereiche erreicht. **Teilweise erledigt (2026-08-09):** ein Nachlauf-Audit über `deploy/`, IDOR/Folder-Scoping, Alerting und die anonymen Endpunkte hat fünf Befunde geliefert, die alle behoben und in [`security-findings.md`](security-findings.md) registriert sind — H-18, M-31, M-32, M-33, L-17. Der Rest der Fläche steht noch aus; der Umfang wird im Audit-Protokoll geführt, nicht hier. |

### Welle 2 — Hygiene

Ein Tag Arbeit. Danach lügt keine Quelle mehr — das ist Voraussetzung dafür, dass diese Roadmap trägt.

| # | Posten | Inhalt |
|---|---|---|
| 6 | ~~**Stale Branches löschen**~~ **Erledigt (verifiziert 2026-08-22)** | Die sieben Vor-Migrations-Branches, die inhaltlich *hinter* `main` lagen, existieren nicht mehr; `git branch --merged main` ist leer. Was lokal verbleibt, sind unmerged Arbeitszweige, keine Altlasten. |
| 7 | **Status-Drift in der Session-Memory korrigieren** | Rund ein Dutzend Memory-Einträge behaupteten „ungemergt/geparkt" für Arbeit, die längst in `main` ist. Erledigt im Zuge dieser Roadmap-Erstellung — der Posten bleibt als Erinnerung stehen, dass Memory-Status regelmäßig gegen den Code zu prüfen ist. |
| 8 | ~~**`docs/backlog.md` aufgelöst**~~ **Erledigt (2026-07-27, PR #81)** | Enthielt Ideen mit ✅-SHIPPED-Markierung neben echten Vorhaben. Inhalt ist in diese Datei überführt, die Datei ist entfernt, README zeigt hierher. |
| 9 | ~~**Offene Issues abräumen**~~ **Erledigt (2026-08-09)** | [#76](https://github.com/Sev7eNup/NodePilot/issues/76) (Row-Serialization über `DbAdminSecretColumns`), [#77](https://github.com/Sev7eNup/NodePilot/issues/77) (Regressionstest für den Heatmap-Sortierfix) und [#79](https://github.com/Sev7eNup/NodePilot/issues/79) (`premiumCanvas`-Beschreibung mit Canvas-Hintergrund) sind geschlossen; der Issue-Tracker ist leer. |
| 10 | ~~**Dependabot einrichten**~~ **Erledigt (2026-08-07, PR #126)** | `.github/dependabot.yml` für vier Ökosysteme (nuget + 3× npm), Minor/Patch je Ökosystem gruppiert, Majors bewusst einzeln — Semantik und PR-Budget stehen in CLAUDE.md. Ohne das lief der Versions-Drift auf, daher kam der 10.0.0/10.0.5/10.0.10-Split. |
| 11 | ~~**Doc-Sync AI-Chat / Knowledge**~~ **Erledigt (2026-08-22)** | Aus dem AI-Chat-PR bewusst ausgeklammert, inzwischen auf allen vier Flächen nachgezogen: CLAUDE.md (Endpunkt + Wissensquellen), `docs/ai-features.md`, docs-ui `content/{de,en}/ai-features.md` und — als letztes Stück — das README-Highlight für den globalen AI-Chat. |

### Welle 3 — Eigene Konventionen einhalten

Keine Features, sondern Verstöße gegen Regeln, die im Repo bereits verbindlich stehen.

| # | Posten | Inhalt |
|---|---|---|
| 12 | **`np alerting enable\|disable` + MCP-Tools** | CLAUDE.md: *„Jeder neue API-Endpoint braucht beide Clients."* Die Endpunkte `POST /api/alerting/rules/{id}/enable\|disable` existieren. Die CLI erreicht den Zustand heute nur indirekt über `np alerting update --enabled\|--disabled`; dedizierte `enable\|disable`-Befehle und das MCP-Tool fehlen. |
| 13 | **`np`-Befehlsgruppe für Custom Activities** | Gleiche Regel, gleiche Lücke. Der Controller ist da, die CLI-Gruppe fehlt. |
| 14 | **Settings-UI: Alerting-Schwellwerte + vollständige Retention** | (a) `Alerting:LongRunningSeconds` / `QueuedLongSeconds` haben keine Schema-Section — der Code kommentiert wörtlich *„Operator lowers … in the Settings UI"*, die es nicht gibt. Neue Section `Alerting`, hot-reloadable. (b) `RetentionOptions` hat fünf Sweeper, `RetentionSettingsDto` nur drei — Notifications und SupportEvents sind per UI nicht tunbar, ebenso die AuditLog-Verify-Parameter. (c) **Priorität gestiegen:** die Live-Ops-Timeline liest `LongRunningSeconds` jetzt als Overdue-Schwellwert (`OpsSnapshotMeta`) — ein Operator *sieht* die Wirkung des Werts, kann ihn aber ohne `appsettings.json` nicht ändern. Dabei den dritten, hart kodierten 30-min-Wert für `DashboardStats.LongRunningCount` mit vereinheitlichen. |
| 15 | **Config-Panes internationalisieren** | Restposten (Stand 2026-08-22): acht Komponenten unter `components/designer/properties/` tragen noch hart verdrahtete deutsche UI-Texte — `WmiQueryConfig`, `StartWorkflowConfig`, `ZipOperationConfig`, `StartProgramConfig`, `PowerManagementConfig`, `WaitForConditionConfig`, `panelChrome`, `VariablePreviewTooltip` —, obwohl das Produkt DE/EN umschaltet. Der Großteil der Panes läuft inzwischen über i18n. |

**Muster für Punkt 14:** `SettingsSchema.cs` → DTO unter `Api/Dtos/Settings/` → Adapter in
`SettingsSections.cs` → Frontend-Section unter `pages/settings/` + Tab in `SystemSettingsPage.tsx`
→ i18n DE+EN → Tests. `AdminSettingsFrontendSyncTests` erzwingt die Parität.

### Welle 4 — Produktnutzen

| # | Posten | Inhalt |
|---|---|---|
| 16 | **Alert → Workflow (Auto-Remediation)** | Dritter Route-Target-Typ „Workflow" im `NotificationDispatcher`, kein neuer `ITriggerSource`. Wegen At-least-once-Delivery braucht die Route einen idempotenten Dispatch-Key (siehe [ADR 0011](adr/0011-database-availability-breaker.md)). |
| 17 | **Starter-Presets + „Beispiele importieren"** | Verifizierter Befund: NodePilot hat **keinen** Seeding-, Template- oder Galerie-Mechanismus. Einziger Ingest-Weg ist `POST /api/workflows/import`. Die `scripts/*.json` nutzen durchgängig `targetMachineId: "localhost"` und sind damit umgebungsunabhängig direkt seedbar. Zehn Presets mit aufsteigender Lernkurve sind entworfen (siehe unten). Liefermechanismus: **Button auf der Workflows-Seite**, kein First-Run-Seeding — die DB soll sich nicht ungefragt füllen. |
| 18 | **Mail-Trigger (IMAP/EWS/Graph)** | Der SCOrch-Klassiker „Monitor Email" und die direkte Gegenrichtung zu `emailNotification`. Filter auf Absender/Betreff/Anhang, Anhänge als `{{manual.attachmentPath}}`. Größte Erwartungshaltung aus dem Ex-SCOrch-Umfeld. |
| 19 | **ChatOps-Rezept dokumentieren** | Teams/Slack-Auslöser laufen heute schon über `webhookTrigger` + `fieldMappings`. Ein dokumentiertes Rezept deckt ~80 % des Bedarfs ohne eine Zeile Code. Ein Nachmittag. |
| 20 | **Alerting-Regel-Vorlagen (Katalog)** | Detailplan: [alerting-rule-templates-plan.md](alerting-rule-templates-plan.md). Dieselbe Idee wie Posten 17, für Alerting: 30 kuratierte Custom-Regeln liegen heute nur in der Dev-DB, `scripts/seed-custom-alert-rules.ps1` kennt davon 17, ist nicht idempotent und verdrahtet eine private Mail-Adresse. Künftig ein **eingebetteter Katalog** in `NodePilot.Core` (Muster `ActivityConfigReference`) + Dialog auf `/alerts` — **kein** Boot-Seeding, Bestandsinstallationen bleiben unberührt. Vorlagen sind zweisprachig (DE/EN, angelegt in der UI-Sprache), kommen **ohne Route** und immer deaktiviert; der Empfänger entsteht beim Aktivieren. Vier Vorarbeiten, die alle schon verifiziert sind: (a) neue Spalte `NotificationRule.SourceTemplateId` als Herkunfts-ID — namensbasierte Erkennung überlebt weder Umbenennen noch Sprachwechsel; `SystemPresetId` wird **nicht** überladen (dokumentiert System-only). (b) Routenlose Entwürfe zulassen, exakt wie die System-Policies es schon tun (`SystemAlertingController` 292-295 + Enable-Gate 135-136) — dazu gehören die drei Folgestellen: `PreviewRule` ruft `TryBuildDraft` mit hartem `isEnabled: true`, und `results.All(…)` ist über der leeren Routenliste `true`, wodurch `test-fire` heute **Erfolg meldet, ohne etwas gesendet zu haben**. (c) Client-Validierung nachziehen: `AlertingRuleEditor.formValid` verlangt Routen unbedingt, und `toggleMutation` hat kein `onError` — ein Enable-400 verpufft spurlos. (d) Batch-Vertrag validate-then-mutate: Sprache, unbekannte und doppelte Ids → 400 **vor** jeder Mutation; danach bewusst Teilergebnis nur für die konkurrierende Namenskollision, mit `Detached` je Fehlschlag, weil `NotificationRuleStore.CreateAsync` pro Regel speichert und ein `DbUpdateException` sonst den Tracker für den Rest des Batches vergiftet. Guard-Test mit Positiv- **und** Negativ-Fixture je Vorlage sowie einer Allowlist-Prüfung der Filterfelder gegen `NotificationContext.ToFieldMap()` — ein getipptes `folderpath` ergäbe sonst eine Regel, die korrekt aussieht und nie greift. Ersetzt `scripts/seed-custom-alert-rules.ps1` (wird gelöscht). Der eigentliche Aufwand ist nicht das Gerüst, sondern die 30 Regelkörper samt englischer Texte — die bekommen einen eigenen PR ohne Controller-Rauschen, damit der Review die Frage stellen kann, die zählt. Fünf PRs. |

**Die zehn Presets (Posten 17), aufsteigende Lernkurve:**

1. *Hello NodePilot* — `manualTrigger`→`log`→`runScript`→`returnData` (Databus, Trigger-only-Roots)
2. *Disk Space Check* — `scheduleTrigger`→`runScript`→`decision`→`junction` (`param.*`-Capture, Edge-Conditions)
3. *Log-Rotation & Archiv* — `folderOperation`→`forEach`→`fileHash`→`zipOperation`→`fileOperation`
4. *API Health Check mit Alarm* — `restApi`(+retry)→`jsonQuery`→`decision`→`emailNotification`
5. *Webhook-Empfänger* — `webhookTrigger`(fieldMappings)→`decision`→`runScript`
6. *Dienst-Selbstheilung* — `serviceManagement`→`decision`→start→`waitForCondition`→`emailNotification`
7. *Sub-Workflow-Paar* (zwei Workflows) — `startWorkflow` + Child-`returnData` → Contract-Derivation
8. *Fehlerbehandlung & Kompensation* — `.failed`-Edges, `retry`, `exit N` vs. `successExitCodes`
9. *Inventar-Report* — parallele Zweige→`junction`→`textFileEdit`→`emailNotification` (Ahnen-Scope)
10. *Ordner-Watcher* — `fileWatcherTrigger`→`fileHash`→`decision`→zip/move (Idempotenz per Hash)

Abdeckung: 20 von 33 Katalog-Typen + alle fünf ControlFlow-Nodes. Bewusst draußen (invasiv oder
umgebungsabhängig, gehören in die Test-Suite unter `scripts/test-suite/`, siehe
`docs/workflow-tests.md`): `registryOperation`, `powerManagement`,
`scheduledTask`, `startProgram`, `xmlQuery`, `generateText`, `sql`, `eventLogTrigger`. Optional und
gated: AI-Log-Triage (`llmQuery`, braucht `Llm:Enabled`), DB-Wächter (`databaseTrigger`).
Leitplanken: `localhost` statt GUID, Sticky-Notes als Inline-Doku, externe Trigger kommen disabled
an und brauchen *Publish* (nicht nur Enable), sonst `missing_effective_principal`.

### Welle 5 — KI-Ausbaustufe 1

Die drei Posten gehören zusammen und sollten als Block gebaut werden. Sie halten sich an die
gemeinsamen Leitplanken in [`ai-feature-ideas.md`](ai-feature-ideas.md).

| # | Posten | Inhalt |
|---|---|---|
| 21 | **Execution Incident Copilot** | „Fehler mit KI analysieren" an fehlgeschlagenen Executions (Liste, Operations-Ansicht, History-Tab). Erklärt beobachtete Fehler, trennt Evidenz von Vermutung, nennt nächste Prüfungen. Read-only, Folder-Leserecht, ausschließlich redigierte Daten, kein Retry, kein Publish. |
| 22 | **KI-Reparatur für Lint und Publish** | „Mit KI beheben" übergibt ausgewählte deterministische Findings an den Workflow-Assistenten; Ergebnis ist ein Diff, der vollständig oder selektiv übernommen wird. Übernehmen erfordert Admin/Operator **und** den eigenen Edit-Lock; Stale-Hash-Schutz, Secret-Merge und Save-/Publish-Flow bleiben verbindlich. |
| 23 | **AI-Chat Run-Card** | Tool `propose_workflow_run` führt **nichts** aus — es löst nur auf: Workflow via `WorkflowNameResolver` (exact-case → case-insensitive → 409 bei Mehrdeutigkeit, nicht die LLM raten lassen), Parameter gegen `GET /{id}/contract`, Zielmaschinen. Der Chat rendert eine Karte mit „Ausführen"-Button; erst der **User-Klick** ruft `POST /execute` — der User ist der Principal, nicht „die KI". Fehlende Pflichtparameter → Rückfrage statt erfundenem Default. **Die Bestätigung ist eine harte Sicherheitsgrenze:** Der Chat liest Doku, DB und Execution-Logs — jeder dieser Kanäle wäre sonst ein Prompt-Injection-Vektor. Zusätzlich Toggle `AiKnowledge:AllowExecution` (default false), Admin/Operator-Gating, eigener Audit-Code. |

### Welle 6 — Engine- und Designer-Nutzen

| # | Posten | Inhalt |
|---|---|---|
| 24 | **Rerun-from-Step (Hot Resume)** | „Rerun from here" an einem fehlgeschlagenen Step: frische Execution, vorbelegt mit den Outputs aller Upstream-Steps des Originallaufs. Spart das Neu-Ausführen der ersten 46 von 50 Steps. Drei gekoppelte Änderungen, jede mit Risiko — Detail unten. Aufwand ~1,5–2 Tage mit ordentlicher Testabdeckung. |
| 25 | **Workflow-Tags / Labels** | Farbkodierte Tags am `Workflow` (`prod`, `qa`, `incident-response`); Dashboard, Quick-Switcher und Executions-Liste filtern danach, Pills überall neben dem Namen. ~1 Tag (Migration + UI). Bestes Aufwand/Wirkung-Verhältnis im Designer-Bestand. |

**Detail zu Posten 23** — die drei gekoppelten Änderungen:

1. **Engine-Signatur** — `WorkflowEngine.ExecuteAsync` braucht ein optionales
   `Dictionary<string, ActivityResult>? prefilledResults`. Die Scheduling-Schleife muss `results`
   und `completed` damit vorbelegen, damit der Scheduler sie überspringt und beim ersten
   ungecachten Nachfahren einsetzt. Hot Path — braucht sorgfältige Tests für die
   `waitAny`/`waitAll`/`junction`-Fälle.
2. **Schema-Migration** — `StepExecution` persistiert `OutputParameters` **nicht** (nur `Output`
   und `ErrorOutput` als Text). Ohne eine nullable `OutputParametersJson`-Spalte kann ein Rerun
   `{{step.param.hostName}}`-Referenzen des Originallaufs nicht wiederherstellen — sie würden zu
   Leerstrings auflösen und Downstream-Logik still brechen.
3. **API + UI** — `POST /api/executions/{id}/rerun-from/{stepId}`: Originallauf laden,
   `prefilledResults` bauen (per BFS auf dem Graphen `stepId` und dessen Downstream ausschließen),
   `ExecuteAsync` rufen. UI: Kontextmenü-Eintrag in der History-Step-Liste.

Dateien: `WorkflowEngine.cs` (Signatur + Scheduler-Init), `Core/Models/StepExecution.cs`,
`Data/Migrations/`, `Api/Controllers/ExecutionsController.cs`,
`components/designer/ExecutionPanel.tsx` (HistoryRow-Kontextmenü).

### Welle 7 — Guardrail

| # | Posten | Inhalt |
|---|---|---|
| 26 | **CI-Matrix: Migrationen gegen echte Container fahren** | Top-Empfehlung des DB-Schema-Audits. Fresh-Install **und** Upgrade-Pfad gegen echte PostgreSQL- und SQL-Server-Container **ausführen**, nicht nur `GenerateScript`. Die vorhandenen `MigrationDriftTests` generieren nur Skripte und fangen daher keine reinen Laufzeitfehler — z. B. ein ungeschütztes `DropTable` auf einer divergierten DB. Schließt eine ganze Fehlerklasse. |

### Welle 8 — Auslieferung

| # | Posten | Inhalt |
|---|---|---|
| 27 | ~~**GUI-Setup für die Server-Installation**~~ **Erledigt (2026-08-03, PR #108)** | Gebaut und unter `deploy/server/` ausgeliefert: `NodePilotServer.iss` + `Build-ServerInstaller.ps1`, Adapter `Invoke-NodePilotSetup.ps1` + `SetupContract.ps1`, `Preflight.ps1`, Tests `Test-SetupAdapter.ps1` + `Test-DeploymentTemplates.ps1`. Der folgende Entwurf bleibt als Sperrvermerk stehen, weil seine Entscheidungen weiter gelten. — `NodePilot-Server-Setup-<version>.exe` (Inno Setup 6) als zweiter Weg zur selben Installation; der ZIP-Weg bleibt unverändert die Referenz. Der Pascal-Layer bleibt dünn — Seiten und Payload, keine Installationslogik: der Wizard schreibt eine ACL-geschützte Antwortdatei und ruft über `Invoke-NodePilotSetup.ps1` das unveränderte `Install-NodePilot.ps1`. Nötig, weil `-PostgresPassword` ein `[SecureString]` ist und über `powershell.exe -File` gar nicht übergeben werden kann; `/SILENT /ANSWERFILE=` deckt damit zugleich SCCM/GPO ab. Größter Gewinn ist nicht die Oberfläche, sondern der Wegfall der Vertrauenszeremonie: ein Asset statt fünf, kein manueller Thumbprint-Abgleich, kein `LocalMachine\Root`-Eingriff von Hand. Readiness-Seite über die seiteneffektfreie `Preflight.ps1` (Posten aus derselben Welle), zwei Opt-in-Aktionen (SQL-Login+DB nur mit ausreichenden Rechten, sonst nur DDL-Ausgabe; Labor-Zertifikat mit Warnung), ASP.NET-Core-Runtime als offizieller Microsoft-Installer im Payload (SHA512 gegen Release-Metadaten **und** eingecheckten Pin, Authenticode auf Microsoft, Standalone-Runtime statt Hosting Bundle). Re-Run erkennt die Installation über `HKLM\SOFTWARE\NodePilot\Server` und fährt Update-Semantik; `/FULLREINSTALL` erzwingt Neuaufsetzen inklusive neuem External-Trigger-API-Key (bestätigungspflichtig). **Die Deinstallation entfernt die Datenbank nicht und bekommt dafür auch keine Option** — der Installer legt sie nicht an, sie ist separat bereitgestellt und wird im Cluster von beiden Knoten geteilt. Einzige Frage ist das Datenverzeichnis, Default überall „behalten". Testfläche: `Test-SetupAdapter.ps1` (Verhalten) plus mutationsgeprüfte Verträge in `Test-DeploymentTemplates.ps1`; der Pascal-Anteil bleibt bewusst minimal und ist samt manueller Smoke-Matrix als Testlücke in `deploy/server/README.md` dokumentiert. |
| 28 | ~~**Schlüsselfertige Erstinstallation (ohne Token-Eingabe)**~~ **Erledigt (2026-08-05, PR #114)** | Gebaut: `ProvisioningSeeder` (`Api/Security`), `NodePilot:BootstrapAdminUsername` im Token-Einlösepfad, Answer-File-Gruppen `bootstrap`/`seed` in `deploy/SetupContract.ps1`. Der folgende Entwurf bleibt als Sperrvermerk stehen, weil seine Entscheidungen weiter gelten. — Ein unbeaufsichtigter Rollout endete bisher mit einer Instanz, die niemand benutzen kann: das Setup-Token müsste ein Mensch in die Anmeldemaske tippen. Zwei sich ausschließende Wege, ohne zusätzlichen Schalter — welcher greift, entscheidet allein, ob die Instanz danach Benutzer hat. **(a) Answer-File-Gruppe `bootstrap`:** das Setup löst das Token selbst ein und legt die erzeugten Zugangsdaten ACL-geschützt ab (SYSTEM + Administratoren, ACL vor Inhalt). Kennwort **pro Maschine zufällig**, kein `adminPassword`-Schlüssel — ein fester Wert wäre über die ganze Flotte gleich und würde gescannt statt geraten, auf einem Produkt, das PowerShell auf allen verwalteten Maschinen ausführt. `NodePilot:BootstrapAdminUsername` wird mitgesetzt, damit ein abgefangenes Token nur genau dieses Konto anlegen kann. **(b) Answer-File-Gruppe `seed`:** `ProvisioningSeeder` spielt beim ersten Start ein `.npbackup` ein — Benutzer, Workflows, Maschinen, Credentials und Settings —, **bevor** irgendetwas die Benutzertabelle liest, sodass gar kein Token entsteht. Möglich, weil die Admin-Sperre am Controller sitzt und nicht am `BackupRestoreService`; der Fall „leere Zieldatenbank" ist dort bereits behandelt und verlangt einen Break-Glass-Admin im eingespielten Satz — genau die Bedingung, an der später das Einschalten von LDAP/SSO hängt. Zwei Regeln machen den Seed dauerhaft konfigurierbar: er füllt **nur** eine leere Instanz (nie Migration), und er **fail-closed** — falsche Passphrase oder kaputte Datei lassen den Dienst nicht starten, statt eine scheinbar provisionierte, in Wahrheit leere Instanz zu hinterlassen. Die Passphrase geht in den `Environment`-Wert des Dienstschlüssels, nie in die JSON. **Feste Default-Zugangsdaten wurden erwogen und verworfen.** Im Lab in fünf Silent-Szenarien verifiziert. |

---

## R2 — Auf der Roadmap, trigger-gated

Jeder Posten trägt seine Auslösebedingung. Ohne Trigger wird nicht gestartet.

### Daten & Performance

| Posten | Auslöser |
|---|---|
| **F3 — SupportEvents-Freitextsuche** (Full-Scan auf ungeindextem `Message`/`UserName`) | Diagnose-Query p95 > ~500 ms **oder** SupportEvents > ~5 Mio. Zeilen im Retention-Fenster, **und** die Suche wird real genutzt. Messquery + providerspezifischer Fix: [`db-schema-audit-followups.md`](db-schema-audit-followups.md). |
| **F7 — vermutlich ungenutzte AuditLog-Composites** | `idx_scan = 0` über ≥30 Tage echten Traffic für einen konkreten Composite. |
| **Per-Endpoint-Lese-Budgets** | Eine langsame, aber lebende Datenbank lässt interaktive Reads noch bis zu 120 s warten; Ausfall und Retry-Verstärkung sind bereits durch [ADR 0011](adr/0011-database-availability-breaker.md) abgedeckt. |
| **F9 — GUID-PK-Fragmentierung** (nur SQL Server) | Nur auf einem SQL-Server-Deployment mit gemessener Fragmentierung > ~30 % trotz regelmäßigem Rebuild. Auf dem Default-PostgreSQL irrelevant. |
| Vier kleine Backend-Perf-Items (`ExecuteDeleteAsync` in der Execution-Retention, geteilte `JsonSerializerOptions`, `LoggerMessage.DefineScope` im StepRunner, inkrementeller `HubRevocationSweeper`) | Alle Größe S–M und sauber, aber ohne gemessenen Schmerz. Guter Füllblock, wenn Kapazität übrig ist. |
| Route-Level `React.lazy` + `manualChunks` + Material-Symbols-Font-Subset (Main-Bundle 1,79 MB → ~700 KB) | Sobald NodePilot außerhalb des LAN erreichbar gemacht wird oder Initial-Load über VPN gemeldet wird. |
| SignalR-Batch-Coalescing (N Step-Events pro Execution zu einem Delta verdichten) | Wenn das Concurrency-Plateau über die aktuellen ~600 in-flight Steps hinaus gehoben werden soll. Der einzige verbliebene Weg dorthin. |
| Backend-Mini-Benchmark (`BenchmarkDotNet`) | Wenn Perf-Claims belegt werden müssen. Nicht präventiv. |

**Voraussetzung für F3/F7/F9 gemeinsam:** `pg_stat_statements` bzw. Query Store aktivieren und
≥30 Tage repräsentativen Produktions-Traffic sammeln. Ohne echte Statistik ist jede dieser
Änderungen Spekulation.

### Trigger & Integration

| Posten | Auslöser |
|---|---|
| Execution-Event-Trigger (Workflow A endet → Workflow B, ohne dass A B kennt) | Erster konkreter Verkettungs-Bedarf. Braucht Zyklen-/Tiefen-Guard analog `startWorkflow` (maxCallDepth 10). |
| `databaseTrigger` von Poll auf Push (Postgres `LISTEN/NOTIFY`, SQL Server Change Tracking) | Wenn Polling-Latenz oder -Last real stört. Aufwertung eines bestehenden Typs, kein neuer Trigger. |
| WMI-Event-Subscription (`__InstanceCreationEvent` per WQL über WinRM, echtes Push) | Kundenbedarf. Der anspruchsvollste Trigger, aber mit dem größten „kann sonst keiner"-Faktor. |
| Schwellwert-/Performance-Trigger („CPU > 90 % für 10 min", mit Hysterese) | Kundenbedarf **und** vorherige Abgrenzung gegen die ADR-0008-System-Policies — die decken konzeptionell Ähnliches ab. |
| AD/LDAP-Change-Trigger (DirSync/`uSNChanged`) · Message-Queue (RabbitMQ/Service Bus/Kafka) · SNMP-Trap-Receiver | Jeweils erster konkreter Kundenbedarf. |

**Pflicht-Gotchas für jeden neuen Trigger** (gelten unabhängig vom Typ):

- Erweiterungspunkt ist `src/NodePilot.Scheduler/ITriggerSource.cs`. Mechanisch billig — teuer sind
  die Nebenflächen: `ActivityCatalog.cs` + Frontend-Spiegel `activityCatalog.generated.ts`
  (Parity-Test!), Config-Komponente unter `properties/triggers/`, i18n DE/EN, Docs, Tests.
- Trigger-Sources dürfen nicht in den Root-DI-Container lecken (siehe Commit `34f7874b`).
- Im HA-Passive-Knoten darf nichts doppelt feuern → externe Trigger leader-gaten.
- Trigger-Daten landen als `manual.*` (+ `param.*` des Trigger-Nodes) — **kein** `trigger.*`-Namespace.
- Trigger-Läufe brauchen `Workflow.PublishedByUserId` → der Workflow muss *published* sein, nicht nur enabled.
- **`Health` beantworten.** Der Orchestrator wertet es sequenziell für *jeden* Trigger im 5-s-Pass
  aus → **reiner In-Memory-Read**, kein I/O, kein Lock. Wer echtes I/O braucht, probt auf eigenem
  Timer und cached das Urteil. Konstant `Healthy` ist erlaubt, aber nur mit Kommentar, der die
  Lücke benennt (Muster: `EventLogTriggerSource`). Ohne das bleibt eine Quelle, die im Betrieb
  stirbt, für immer registriert und feuert nie wieder.

| Trigger-gated Posten | Auslöser |
|---|---|
| Echte Health für `eventLogTrigger` (eigene Probe-Schleife statt konstant `Healthy`) | Erster gemeldeter Fall einer stillen toten `eventLogTrigger`-Subscription. `EventLog` hat keinen Fault-Kanal; die einzige Probe wäre RPC an den EventLog-Dienst. |
| Lese-API für Laufzeit-Trigger-State (`GET /api/triggers/status` + CLI + MCP) | Operator will Live-Zustand inspizieren, ohne auf einen Alert zu warten. Die System-Policy `trigger-unhealthy` deckt „sag mir Bescheid" bereits ab; das hier wäre „lass mich stöbern". |

### Engine & Datenbus

| Posten | Auslöser |
|---|---|
| **`{{run.*}}` — Metadaten des laufenden Workflows im Template** (`run.workflowName`, `run.executionId`, ggf. `run.workflowId`/`run.startedAt`) | Ein produktiv gehender SCOrch-Import, dessen Runbooks `Policy.Name`/`Policy.PID` benutzen — **oder** der erste eigene Bedarf, Lauf und Workflow in Text zu benennen (Ticket-Betreff, Log-Zeile, Korrelations-ID). Am Referenz-Export gemessen: **13 der 46 Warnungen** sind genau diese beiden Felder. |

Heute gibt es dafür **nichts**: `VariableResolver` kennt `globals.`, `manual.` und `StepPattern`
(vier Tails). `__executionId`/`__workflowName` existieren nur als Ausgaben eines **Child**-Laufs,
den `startWorkflow` gestartet hat — von innen kommt ein Workflow nicht an seine eigenen Daten.

**Gotcha, falls der Posten startet:** ein neuer Namespace braucht ein **eigenes Regex-Muster** und
wird von `StepPattern` prinzipiell nicht mitgetroffen — genau daran ist `manual.NAME` bis 1.2.7
still gescheitert (Platzhalter blieb stehen, der Step meldete Erfolg). Dazu Seeding in der Engine,
`TemplateGrammarParityTests`, Designer-Variablenpicker, MCP-/AI-Analyzer und Doku.

### KI

| Posten | Auslöser |
|---|---|
| Activity-Konfigurations-Copilot (Idee 3) | Nach Abschluss von Welle 5. Beginnend mit den komplexesten Configs (Decision-Bäume, Cron, REST, SQL, JSONPath/XPath, Retry). |
| Workflow Generator v2 — geführter Entwurf mit Rückfragen, Graph-Vorschau, Lint (Idee 4) | Wenn der One-shot-Flow als Engpass gemeldet wird. Heute funktioniert er. |
| Reliability-/Optimierungs-Review (5) · Custom-Activity-Copilot (6) · Testfall- und Mock-Generator (7) · Operator-Copilot und Schichtbriefing (8) | Alle setzen auf 1–4 auf. Nach Welle 5 neu bewerten. |
| Sechs weitere Knowledge-Tools im globalen Chat (Globals, Maintenance-Windows, Custom Activities, Alerting-Regeln, Credentials, Users) | Wurden bewusst zurückgestellt. Wiederaufnahme, wenn im Chat konkret danach gefragt wird. `read_settings` und die DB-/text2sql-Quelle sind bereits gebaut. |

Beschreibung, Nutzerproblem und Sicherheitsgrenzen je Idee: [`ai-feature-ideas.md`](ai-feature-ideas.md).

### Alerting

| Posten | Auslöser |
|---|---|
| PagerDuty- / Opsgenie-Sinks | Erster Kundenbedarf. Die Enum-Werte existieren bereits, es fehlen die Sinks — Validierung lehnt Kanäle ohne registrierten Sink heute korrekt ab. |
| „Resolved"-Recovery-Notifications für Signal-Events | Wenn silent recovery als Mangel gemeldet wird. |
| Escalation-Policies · per-Rule Dedup-Key-Templates | Enterprise-Bedarf. |
| `sourceKey` auch für Execution-Events befüllen | Konsistenz-Posten, mitnehmen wenn ohnehin am Dispatcher gearbeitet wird. |

### Designer

| Posten | Auslöser |
|---|---|
| Group/Subgraph collapsible — N Kind-Nodes zu einem Platzhalter falten, per Doppelklick zurück | Workflows regelmäßig > 50 Nodes. ~2 Tage (React-Flow-interne Node- und Edge-Behandlung für den Collapsed-Zustand). |
| JSONPath-Picker im Output-Tab — Klick auf einen Wert → `{{step.param.hosts[0].name}}` mit Copy-Button | Wenn JSON-Pfade als Fehlerquelle auffallen. ~1,5 Tage. |
| Machine-Online-Indikator pro Node (grün/rot, 5-min-Cache) | Zusammen mit einem WinRM-Reachability-Poller bauen, nicht davor. ~1 Tag. |
| Side-by-Side Workflow-Diff (zwei Canvas, geänderte Nodes hervorgehoben) | Wenn der bestehende Text-Diff bei workflow-förmigen Änderungen als unbrauchbar gemeldet wird. ~2 Tage. |
| Restliste aus dem Designer-Redesign: Skin-Block-Dedup in `index.css`, vollständige ModalShell-v2-Konsolidierung der acht Overlays, Checkbox→Switch-Sweep über die restlichen ~13 Config-Dateien, Palette-Drag-Ghost | Als *ein* Sammelposten. Kosmetik mit e2e-Risiko — nur angehen, wenn ohnehin am Designer gearbeitet wird. |

### Enterprise

| Posten | Auslöser |
|---|---|
| RBAC Stufe B — `WorkflowPermissions`-Tabelle für Per-Resource-Sharing | Konkreter Kundenwunsch. Stufe A (Folder-RBAC inkl. Group-SID-Integration) ist seit Mai 2026 in `main`. |
| Active/Active-Cluster | Nur bei **Last**-Skalierungsbedarf. Für Verfügbarkeit reicht Active/Passive (RTO 40–60 s). |
| HashiCorp Vault Transit / KMIP / Cloud-KMS · HSM-gestützter AES-Key · Per-Row-Key-ID | `ISecretProtector` ist die vorbereitete Naht — ein neuer Provider ist eine Klasse plus DI-Zeile. Kein Vorbau nötig, jederzeit nachrüstbar. Details: [`secrets-providers.md`](secrets-providers.md). |
| Windows-SSO: Restlücken bis zum Wegfall des „Preview"-Labels | **Kerberos ist am 2026-08-02 gegen echtes AD verifiziert** (Server 2025, gMSA, domain-joined Client; 22 PASS — echtes `HTTP/<host>`-Ticket, transitive `tokenGroups`, LDAP↔Windows dieselbe UserId, JIT-Race, Gruppen-Gate). Harness + Evidenz: `scripts/ad-sso-labtest/`. **Offen bleiben:** HAProxy-Pfad, Multi-DC-Konsens (Lab hat 1 DC), NTLM-Negativtest (braucht einen SPN-freien DNS-Alias — der Lab-Versuch war unschlüssig), Session-Revocation bei Gruppenentzug, OIDC/SCIM und HA-Restart. Das Label fällt erst, wenn diese Punkte abgeräumt sind. |

### Qualität & Betrieb

| Posten | Auslöser |
|---|---|
| Backend-Line-Coverage 89 % → 90 % | Die Ratsche steht in `ci.yml` auf 85 % Line / 70 % Branch. Anheben, wenn ohnehin breit getestet wird — kein Selbstzweck. |
| `IWorkflowDefinitionMutator` — ein Mutations-Pfad für Workflow-Definitionen | Aus dem Audit 2026-08-15 (P1). Version, `UpdatedAt`, berechnete Metadaten, History und Trigger-Sync liegen heute mehrfach nebeneinander in Create/Publish/Duplicate/Restore. Auslöser: der nächste Bug, der nur einen dieser Pfade trifft. |
| Custom-Activity-Invarianten DB-seitig erzwingen | Aus dem Audit 2026-08-15 (P2). Heute nur App-Level-`ConcurrencyToken` in `CustomActivityDefinitionStore`; DB-seitig fehlen `IsConcurrencyToken()` und Live-Key-Eindeutigkeit. Braucht EF-Migration + Parallel-Test mit zwei DbContexts. Auslöser: erste beobachtete Race in der Praxis. |
| Credential-Rotation an den WinRM-Pool koppeln | Aus dem Audit 2026-08-15 (P2). Pool-Key um Credential-Fingerprint erweitern, Idle-Sessions bei Update invalidieren, Semantik für ausgeliehene Sessions dokumentieren. Auslöser: erste Rotation, nach der eine alte Identität weiterlief. |
| N+1 bei Notifications und Workflow-Statistiken bündeln | Aus dem Audit 2026-08-15 (P2). Ziel-Größenordnung laut Betreiber: ~100 parallele Läufe. Auslöser: gemessene Latenz, nicht Verdacht — vorher `docs/performance-improvements.md` gegenlesen. |
| Reale Integrationsgrenzen (Container-Migrationen, Browser↔echte API, Electron-Smoke) | Aus dem Audit 2026-08-15 (P2). Alle bestehenden Suiten sind hermetisch gemockt. Größter Einzelposten der Liste; sinnvoll nur stückweise. |
| `WorkflowEditorPage` in Hooks zerlegen + Gzip-Budgets in CI | Aus dem Audit 2026-08-15 (P2). Die Seite ist zuletzt weiter gewachsen. Monaco und ELK bleiben erlaubte Lazy-Ausnahmen. Auslöser: wenn der Initial-Chunk spürbar wird. |
| xUnit1051 abbauen, produktive Nullable-Warnungen als Fehler | Aus dem Audit 2026-08-15 (P3). Bewusst schrittweise — als Big-Bang bricht es die halbe Suite auf einmal. |
| `WorkflowEditorPage.test.tsx` splitten (1558 Zeilen / 89 Tests) | Wenn die Frontend-Suite in CI zum Zeitfaktor wird. Die Flakiness selbst ist gefixt. |
| E2E-Spec für `/metrics/:section` | Einzige Seite ohne Playwright-Spec. Mock-Smoke-testbar. |
| `/operations` (Live-Ops) über den Snapshot-Happy-Path hinaus | RBAC-Cross-Checks, Drilldown und Health-Rail sind unasserted; SignalR-Live bleibt untestbar (404-Stub). |
| MCP Streamable-HTTP-Transport | Bewusst als Erweiterungspunkt dokumentiert, nicht gebaut. Erster Bedarf nach nicht-stdio-Anbindung. |
| Least-Privilege-DB-Login statt lexikalischer Read-Only-Prüfung | Deployment-Thema, kein Code-Thema. Die autoritative Lösung für den DbAdmin-Read-Pfad auf SQL Server/SQLite. |

---

## E — Offene Entscheidungen

Diese fünf Punkte können nicht gebaut werden, solange die Entscheidung aussteht. Sie sind
bewusst keine R2-Posten: Es fehlt nicht der Auslöser, es fehlt der Beschluss.

| Frage | Kontext |
|---|---|
| **SSH/Linux-Cross-Platform: neu aufsetzen oder streichen?** | Die frühere Implementierung ist **nicht mehr im Repo** — `RemoteProtocol` findet sich nirgends in `NodePilot.Core`, Branch und PR sind mit der Public-Repo-Migration verloren gegangen. Damals: ~11 000 Zeilen über 107 Dateien, Backend platform-aware (Bash-Adapter für runScript/File/Folder/Service/StartProgram), SSH.NET-Stack mit Host-Key-TOFU/Strict, gegen Debian-bookworm per Docker verifiziert. Die bekannte Lücke war ausschließlich UI-seitig: `RunScriptConfig.tsx` blieb PowerShell-zentriert. Als „pausiert" weiterzuführen ist die einzige Option, die eindeutig falsch ist. |
| **`aiAgent`-Activity: bauen?** | Ein iterativer read-only Diagnose-Agent als *ein* Workflow-Step. Ein Agent-Loop im Graphen ist strukturell unmöglich — ein Agent ist `plan → act → observe → repeat` mit unbekannter Iterationszahl, die WorkflowEngine ist ein DAG-Scheduler, in dem jeder Node maximal einmal pro Lauf läuft. Der Plan kapselt die Schleife deshalb *innerhalb* eines Steps. Echte Produktentscheidung, nicht nebenbei. |
| **Multi-DC: Quorum oder All-DC-Konsens?** | Heute strikter All-DC-Konsens, **kein** Failover: Ein DC down ⇒ externe Logins 503. Das ist per Unit-Test festgeschrieben und inzwischen ehrlich dokumentiert (früher stand fälschlich „Failover" da). Ob das so bleibt, ist ein Produktentscheid — kein Bug. |
| **Fremde MCP-Server im AI-Chat nutzbar machen?** | Admin konfiguriert externe MCP-Server, der Chat darf deren read-only Tools aufrufen. Das ist der eigentliche Nutzen eines MCP-**Clients** im Backend — fremde Tools anbinden. Die Gegenrichtung (den Chat über `nodepilot-mcp` auf die *eigenen* Tools fahren) ist geprüft und verworfen: `nodepilot-mcp` authentifiziert sich mit *einer* DPAPI-Session, der Chat läuft pro Request unter dem `ClaimsPrincipal` des Aufrufers (Folder-RBAC, SQL nur für globale Admins) — ein Umleiten würde alle Tool-Calls unter eine Dienst-Identität legen; dazu Loopback-HTTP je Call und ~90 Schreib-Tools, die der Chat bewusst nicht hat. Die geteilte Analyse-Logik liegt stattdessen in `NodePilot.Core` (`WorkflowAnalyzer`, `WorkflowDataBusAnalyzer`), von beiden Flächen konsumiert. Offen für den Client-Fall: Trust-Modell gegen Prompt-Injection aus fremden Tool-Ergebnissen, RBAC-Gate, Prozess-Lifecycle. |

---

## Anhang — Bewusst nicht auf der Roadmap

Dieser Abschnitt ist ein **Sperrvermerk**. Er existiert, damit diese Ideen nicht alle paar Monate
erneut aufschlagen und neu analysiert werden.

### Gemessen widerlegt (Performance)

Alle folgenden Hebel wurden in echten 500-Parallel-Stress-Tests durchgemessen und sind **aktiv
schädlich**. Belege, Zahlen und Begründungen: [`performance-improvements.md`](performance-improvements.md).

| Idee | Messergebnis |
|---|---|
| Concurrency-Caps hochziehen (`MaxConcurrentSteps` 600→1500, Runspace 768→1500) | **42 % schlechter.** Das System saturiert *downstream* der Engine — CIM-Provider serialisieren, die OS-Process-Spawn-Rate wird vom CSRSS gedrosselt. |
| Concurrency-Caps reduzieren (600→300) | **9 % schlechter.** 500 Workflows × 1 Step brauchen mindestens 500 Slots. Die Defaults liegen in beide Richtungen nahe am Optimum. |
| `ChildSemaphore` von 128 auf 600 heben | **16 % schlechter.** Verschiebt den Stau nur eine Schicht weiter. |
| DB-Pool aufbohren (800→1500) | **Null Effekt.** Nur ~85 von 480 Pool-Slots sind unter Last belegt. |
| Eager RunspacePool-Pre-Warm + `MinRunspaces` 768 | **28 % Regression.** OS-Thread-Kosten dominieren über Runspace-Init-Kosten; der Pool wächst unter Last ohnehin organisch. |
| `VACUUM ANALYZE` auf den Hot-Tabellen | **Null Effekt.** Autovacuum hält `n_dead_tup` bereits nahe 0. |
| Batch-Writes für `StepExecution`-Persists über einen 50-ms-Channel | Rechnerisch attraktiv, aber bricht die Live-Step-Anzeige (UI sieht Steps nicht in der DB, bevor der Batch flusht) und verkompliziert die Cancellation-Pfade. Architektur-Risiko zu hoch. |

**Regel daraus:** Wenn der nächste „lass uns Pre-Warm bauen / die Caps hochziehen"-Vorschlag kommt,
erst mit dem dokumentierten 500-Parallel-Test verifizieren, dann committen. Das bauchgesteuerte
„mehr Slots muss schneller sein" ist hier konkret falsch.

### Verworfene Produktentscheidungen

| Idee | Grund |
|---|---|
| Native Teams- / Slack-Sinks | Vom Product Owner gestrichen. Der Bedarf wird über `webhookTrigger` + `fieldMappings` und generische Webhooks gedeckt (siehe R1-Posten 19). |
| Multi-Tenancy (RBAC Stufe C) | 2026-05-15 explizit gestrichen — kein Konzern-Kundenbedarf in Sicht, der 6–8 Personentage rechtfertigt. |
| OIDC/Entra-ID als Roadmap-Posten | Wurde inzwischen gebaut (release-gated, inkl. SCIM-Controller) — der alte „gestrichen"-Vermerk ist überholt. |
| Composite- / Sub-Workflow-Nodes bei Custom Activities | Bei v1 bewusst verworfen: Custom Activities bleiben reine `runScript`-Presets, keine zweite Script-Engine. |
| `secret`-Input-Typ für Custom Activities | Gestrichen — Secrets laufen über `{{globals.X}}` und Credentials. |
| R2 „nur `manualTrigger` ist als Child aufrufbar" | Zugunsten von Modell C zurückgenommen: `startWorkflow` ruft **jeden** enabled Workflow, unabhängig vom Trigger-Typ. Bekannter, bewusst akzeptierter Edge-Case: Ein Child mit mehreren Triggern feuert beim Call alle als Roots. |
| Multi-tone Node-Icons | 2026-07-08 verworfen — flache Silhouette + Glyph bleibt. |
| `dotnet pack` für CLI/MCP (global tools) | Die Tools hängen transitiv an `net10.0-windows`; `PackAsTool` verbietet ein Platform-TFM (NETSDK1146). Echte Tool-Pakete bräuchten Multi-Targeting der ganzen Kette. **2026-08-02 nachgemessen: `dotnet pack` scheitert für beide Projekte reproduzierbar.** Die Doku hatte bis dahin `dotnet tool install -g` als Installationsweg angegeben — also einen Weg, den es nie gab; sie zeigt jetzt auf `dotnet publish` + `PATH`. Die toten `PackAsTool`/`ToolCommandName`/`PackageId`-Properties sind aus beiden `.csproj` entfernt, damit die Behauptung nicht erneut entsteht. |

### Refactoring ohne Anlass

| Idee | Grund |
|---|---|
| `WorkflowEngine` aufspalten (~1100 LOC) | Gut getestet, hohes Risiko bei der Aufspaltung, kein Schmerzpunkt. Ein detaillierter 8-PR-Schnittplan wurde vorgelegt und als technisch sauber bewertet — aber ohne Auslöser nicht durchgezogen. **Wiederaufnahme nur**, wenn ein neues Feature (Dynamic Sub-DAGs, Saga-Patterns, Engine-Plugins) am Monolithen scheitert. |
| Controller ausdünnen (`WorkflowsController` ~970 LOC, `ExecutionsController` ~660 LOC) | Controller dürfen dick sein, solange die Logik testbar ist — sie ist es. **Wiederaufnahme nur**, wenn neue Endpunkte ohne Service-Schicht hässlich werden. |
| `WorkflowEditorPage` Overlays-Host (B5) | 10 Overlays mit gemischter Inline-Logik hätten 30+ Props oder einen Konvertierungs-Layer gebraucht — keine Klarheitsverbesserung. **Wiederaufnahme**, wenn die Page über ~1700 LOC wächst. |

### Extern blockiert

Nicht unser Problem, aber dokumentiert, damit es nicht erneut versucht wird:

- **Microsoft.OpenApi 3.x** — Swashbuckle 10.2.3 verlangt die 2.x-Oberfläche (transitiver Floor 2.7.5).
  Pin auf 2.9.0 bleibt nötig. Erst nach einem Swashbuckle-Major nachziehen.
- **TypeScript 7** — `typescript-eslint` bricht beim Laden hart ab; Support erst ab TS ≥ 7.1 geplant.
  Side-by-side TS 6+7 oder das Lint-Gate opfern wurden beide abgelehnt.
- **Spectre.Console 0.57.2** — `Spectre.Console.Cli` endet stabil bei 0.55.0 und pinnt den Core auf
  die eigene Version. Familie bewusst geschlossen auf 0.55.0.

### Kein Produkt-Scope

- **Codex-Plan-Review-Hook** — Entwickler-Tooling (`PreToolUse`-Hook auf `ExitPlanMode`), gehört auf
  eine Tooling-Liste, nicht auf die Produkt-Roadmap.
- **OS-Tuning** (`MaxShellsPerUser`, CSRSS-Desktop-Heap) — per Host zu setzen, nicht im Repo regelbar.
- **Workflow-Definitions-Rewrites** (z. B. `Get-CimInstance Win32_*` durch gecachte Aufrufe ersetzen) —
  wäre die einzige Stellschraube mit zweistelligem Perf-Gewinn im Demo-Workload, ist aber
  workflow-spezifisch und außerhalb des Engine-Scopes. Eingangsthema für künftige Designer-Lints.

---

## Verwandte Dokumente

| Dokument | Rolle |
|---|---|
| [`db-schema-audit-followups.md`](db-schema-audit-followups.md) | Messqueries, Schwellenwerte und Wiederaufnahmebedingungen für F3/F7/F9. Das Format dieses Dokuments ist die Vorlage für alle R2-Posten. |
| [`ai-feature-ideas.md`](ai-feature-ideas.md) | Nutzerproblem, Funktionsbeschreibung und Sicherheitsgrenzen je KI-Idee. Priorisierung liegt hier in der Roadmap. |
| [`performance-improvements.md`](performance-improvements.md) | Umgesetzte Optimierungen, Tuning-Erkenntnisse und die Messprotokolle hinter dem Sperrvermerk oben. |
| [`security-findings.md`](security-findings.md) | Register **behobener** Findings mit Fix und Test. Kein Backlog. |
| [`alerting-rule-templates-plan.md`](alerting-rule-templates-plan.md) | Detailplan zu Posten 20: Katalog-Schema, Batch-Vertrag, routenlose Entwürfe, PR-Schnitt. Stand der Entscheidung, nicht des Codes. |
| [`alerting.md`](alerting.md) · [`custom-activities.md`](custom-activities.md) · [`enterprise-features.md`](enterprise-features.md) | Fachliche Tiefe zu den jeweiligen Bereichen. |
