# Runde 3 – Clients, Desktop und Client-Harness

Datum: 2026-10-09. Eigenständiger erneuter Quellenlauf auf dem in `round-3.md`
festgehaltenen Stand, anschließend minimale, gegengeprüfte Korrekturen. Kein bloßer
Diff-Review. Ausgangsinventar: 3166 Dateien, SHA-256
`9ec49063c7b0b522b5ade566c36b70d1160c9680954d3482cba1a2c49384032b`.

**Ergebnis: sechs neue Medium-Befunde in diesem Bereich, behoben. Runde 3 ist damit
keine Nullrunde; eine vollständige weitere Runde ist erforderlich.** Keine neue
bestätigte Critical/High-Sicherheitslücke im Clientbereich. Zusätzlich wurde der
Low-Diagnosebefund #65 mit dem Backend abgeglichen.

## Methode und Verträge

Erneut angewandt: `improve-codebase-architecture` und `audit-ai-slop-code`.
Grundlage waren Projekt-/Client-CLAUDE.md, CONTEXT, Trust-Modell sowie ADR 0005
(HTTP-Clients), 0006 (Trigger-Wurzeln), 0008/0009 und die einschlägigen Agent-/Editor-
Entscheidungen 0010/0016. Geprüft wurden insbesondere Zustandsbesitz, Lebensdauer,
Import/Export, asynchrone Identitätswechsel, versteckte Konfigurationsfelder und
entfernte Ressourcen. Sicherheitsbehauptungen wurden an Servergrenzen geprüft;
ausgeblendete UI-Schaltflächen gelten nicht als Zugriffskontrolle.

Die Matrix beschreibt tatsächlich verfolgte Modulgruppen und Einstiegspunkte.
Sie behauptet weder, jede Zeile jeder Testfixture gelesen zu haben, noch eine
vollständige manuelle Browser-/Windows-Abnahme. Generierte Kataloge, Übersetzungen,
Icons und reine Darstellungszeilen wurden über Verbraucher und Verträge beurteilt,
nicht als einzelne sicherheitskritische Implementierungen.

## Architektur und Funktion: Quellenmatrix

| Modulgruppe | Tatsächlich verfolgte Einstiegspunkte und Zustands-/Vertragsprüfung | Ergebnis |
|---|---|---|
| UI-Routing und gemeinsame Clients | App, authStore/authBoundary, API client/paging, Benutzerwechsel, Query-Cache und confirmStore; direkte Fetch-Ausnahmen AI-SSE und resolveWorkflowRef | Gemeinsame Identitätsgrenze statt lokaler Ersatz-Authentifizierung; kein weiterer Medium-Befund |
| UI-Listen und Verwaltung | Workflows, Executions, Users, Machines, Globals, Maintenance, CustomActivities, Backup, DbViewer, Settings/Login/Mobile; Form öffnen → bearbeiten → Request und Dateiimport → Mutation | CustomActivity-Limits gingen verloren: C2. Sonstige geprüfte Pfade erhalten unbekannte Felder oder haben absichtlich vollständige DTOs |
| Workflow-Lebenszyklus | WorkflowEditorPage-Handler, useWorkflowPersistence/Lock/Execution, Publish-Generation, Laden/Wechseln, Sperre, Autosave, Run, Import, Löschen | R2-Publish-Bindung gegengeprüft; Layout-Snapshot überschrieb spätere Inhalte: C4 |
| Canvas und lokale Graphzustände | CanvasConnect, NodeOperations, Clipboard/History/Simulation, Annotations, Coverage/CriticalPath/DisplayedGraph, autoLayout, editorGraphHelpers, Sanitizer, configClone, groupReparenting, Find/Replace, Snippets, Diff und selektive Proposal-Übernahme | Positionsrestauration jetzt nach aktuellen IDs/Eltern; keine gelöschten Knoten wieder einsetzen. Diff-Anwendung hat Staleness-Gate und entfernt verwaiste Kanten |
| Designer-Konfiguration | PropertiesPanel, activityConfigMap, DynamicActivityConfig, shared Fields; Script/SQL/REST/LLM/Agent/ForEach/StartWorkflow, Dateisystem/ZIP/Hash/Text/JSON/XML, Service/Registry/Power/ScheduledTask/WMI, Delay/GenerateText/Log/Decision/Junction/ReturnData; Manual/Webhook/Schedule/FileWatcher/Database/EventLog-Trigger | Patch-Seam erhält übrige Konfiguration. Skript-Run las eine alte Closure: C6. Defaults und Auswahlwechsel gegen die aufrufende Merge-Seam geprüft |
| Ausführung und Debugging | ExecutionPanel, LiveExecutionPanel, StepTestPanel, PausedVariablesInspector, useSignalR und Feeds, useAgentRuns; Join/Rejoin, Snapshots, Resume-Request, Unmount/Abort | R2-Reconnect/Rejoin-Korrekturen erneut geprüft; Resume läuft über die API, Anzeigezustand ist kein Autorisierungsnachweis |
| AI-UI | AI API/SSE, AiChatStores, KnowledgeChat, AiWorkflowChatPanel/ProposalCard, WorkflowGenerationDialog, AiPromptDialog, ScriptEditorDialog/useAiScriptStream, Markdown, chatExport | Proposal-Hash und selektive Anwendung, Zustimmung zum Skriptkontext, Abbruch und Blob-Lebensdauer geprüft; C6 betrifft beide tatsächlichen Run-Callers |
| Agent-Projektionen | agentRunTrace, agentRunSummary, agentTeamProjection und Komponenten-Verbraucher | Ereignisse werden als Darstellung projiziert; keine zusätzlichen ausführbaren Workflow-Knoten, keine Autorisierung durch Modelltext |
| Betriebs-/Monitoring-UI | Dashboard, Metrics, Operations, AuditLog, SupportLog; SupportEventsTable, Quarantine/Cancel/Retry, observability/operations/diagnostics APIs, OTel | Operationen rufen geschützte APIs; ausgewählter Lauf wird auf sichtbare Workflow-Menge begrenzt. Telemetrie ist admin-konfigurierter Export, kein Browser-Secret-Speicher |
| Alerting und Systemeinstellungen | AlertingRuleEditor, SystemPolicyEditor, SystemAlertsSection, DeliveriesModal; Authentication, Security, Retention, Performance, Integrations (SMTP/LLM/Proxy), LoggingTelemetry, Agents, AiKnowledge, DbAdmin, SystemInfo/Restart/TestProbe, EtagConflict/SecretField | R2-Policy-Roundtrip, Secret unverändert/ändern/löschen, ETag-Override und Server-DTO-Payload verfolgt; kein weiterer Medium-Befund |
| UI-Hilfsverträge | workflowLint/prePublishChecks/templateValidation, upstream-/output-Variablenverbraucher, jsonPathBuilder, variableDragDrop, navigation, Monaco-Initialisierung | Low #65: Groß-/Kleinschreibung doppelter Ausgabe-Aliase an Runtime/Backend angeglichen; keine neue Runtime-Abstraktion |
| CLI Host/Transport | Program und CommandRegistration, BaseCommand, SessionResolver/Context, ApiClientFactory, vollständige API-Client-Dateien; Core.Clients TokenStore/Config/Coordinator/Refresh/ResponseReader/PinnedCertificateHandlerFactory | HTTP-only-Abhängigkeit beibehalten. Trigger-Anonymclient ignorierte TLS-Optionen: C3 |
| CLI Workflow/Ausführung | WorkflowRead/Lifecycle/ImportExport/Run/Trigger/StepTest/ContractCoverage, WorkflowResolver, RunParameterParser, ExecCommands/ExecWatcher | Sperre/Publish, Status-Fallback, Parameter, Fehlercodes und Datei-/stdin-Pfade verfolgt; keine Wiederholung eines Workflows beim Reconnect |
| CLI Administration/Lesen | Auth, Agent, Backup, Db, Settings, Secrets, Operations, Users/Credentials/Machines/Globals/Folder/SharedFolder/Maintenance/Alerts/SystemAlert/Config/Audit/Stats/Observability/Health/Cron; OutputWriter/Renderers/YamlEmitter | Globals-Export → Upsert zerstörte Secrets: C5. Ausgabeformate sind keine Roundtrip-Schnittstelle außer explizitem JSON-Export |
| MCP | Program/Tool-Registrierung, vollständige API-Clients, WorkflowEdit/Read, CanvasAssistant, Discovery, DbAdmin, Telemetry, SystemAlert/Alerting, Agent, Execution, Destructive/SupportingData, Resources; MappingPatcher, Redactor, ErrorMapper, Payload-Shaping | Stdio-Host und explizites Destructive-Opt-in, HTTP-Autorisierung sowie gekürzte/redigierte Ausgabe verfolgt. Kein Engine-Inprozess-Bypass |
| Switcher | Konfigurationsladen/Probe/CLI-Locator, ViewModel, privilegierte Service-/Prozess-/SCOrch-Pfade, ServiceModels/Options, Bestätigungsdialog | Servicewechsel bleibt absichtliche lokale Adminfunktion; Same-Origin-Pagination und Fehler-/Abbruchpfade erneut betrachtet |
| Electron Desktop | main, security, setup-preload/config/http/backendRestart/setupFlow, Setup-HTML/Skins | Fenster-/Navigation-/IPC-Besitz, Loopback-TLS/Pin, Download/Shell-URL und Setup→SPA-Übergang erneut geprüft |
| Docs-UI | App/DocPage/Markdown/Search, kuratierter Content-Loader, Routing/Legacy-Redirects, SEO/Head/Prerender, Blog/Media/Video, i18n, Sidebar/TopBar/Toc/Language/Theme; Experience Controller/Markup/Missions/Graph/Topology | Vertrauenswürdiger Repo-Inhalt vs URL-/Suchparameter getrennt; kein öffentliches Schreib-Backend. Keine künstliche Konsolidierung statischer Produktinhalte |
| LoadHarness | Program, LoadTestOptions, Seeder, WorkflowTemplates, ExecutionScenarioFactory, SignalRObserver und NodePilotApiClient; README-Vertrag und Engine-Vertragstests | Echte PagedResponse hieß `total`, der Harness erwartete `totalCount`: C1. Kein Lastlauf gegen reale API |

## Sicherheitsgegenprüfung

* Browser-Sessionwechsel: gemeinsame AuthBoundary und zusätzliche Guards vor
  asynchronen Datei-/Bestätigungsgrenzen geprüft, einschließlich des R2-Agent-Skill-
  Imports. Wiederverwendete Requests dürfen nicht unter einer Nachfolgesession mutieren.
* Admin-/Viewer-Grenzen: CustomActivitiesController, GlobalsController/Store,
  WorkflowsController (inklusive Names und 500er-Limit), Executions-Paging,
  AgentsController (Run-/Event-Ordnerrechte), BackupController und DbAdminController
  wurden als konkrete Gegenstücke verfolgt. DbAdmin prüft Tabellen/Spalten und
  Benutzerinvarianten serverseitig; die vollständige Backend-Prüfung liegt beim API-Lauf.
* Dateisystem/Netzwerk: Electron privilegiert nur eigene Setup-IPC-Aufrufer; CLI/MCP
  speichern Sitzungstokens über Core.Clients und rufen HTTP auf. Switcher besitzt
  bewusst lokale Adminrechte; fremde SCOrch-Pagination bleibt vor Handler-Aufruf gesperrt.
* Fremde Texte: AI-/Log-/Event-Anzeige, Markdown und Proposal-Übernahme sind keine
  automatische Codeausführung. Das tatsächlich gewollte Ausführen von Admin-Skripten
  ist gemäß Trust-Modell keine neue Sicherheitslücke. C6 korrigiert allerdings, welcher
  explizit angeforderte Text ausgeführt wird.

## Bestätigte neue Befunde und minimale Fixes

| ID | Severity / Kategorie | Evidenz und Alltagsszenario | Gegenargument und Fix-Seam | Regression |
|---|---|---|---|---|
| C1 / #50 | Medium, Vertragsdrift | LoadTests/NodePilotApiClient.CountRunningExecutionsAsync suchte `totalCount`; Core.PagedResponse liefert `total`. Ein sonst erfolgreicher Benchmark brach bei der Abschlusszählung ab. | Kein Serverfehler: Test muss echten Core-Typ serialisieren. Nur Feldvertrag korrigiert. | 1 erwarteter Fehler, danach LoadHarnessContractTests 8/8 |
| C2 / #51 | Medium, destruktiver Roundtrip | CustomActivitiesPage FormState/openEdit/save verlor memoryLimitMb/maxProcesses; Controller ersetzt die Eingabe. Eine reine Umbenennung entfernte Limits. | Felder fehlen absichtlich in der UI, müssen trotzdem erhalten bleiben; keine neue UI nötig. | Zwei reale Page-Fälle rot, CustomActivity-Tests 19/19 grün |
| C3 / #52 | Medium, Transportfunktion | WorkflowTriggerCommand nutzte CreateAnonymous ohne session.Tls. Ein gültiger selbst signierter Desktop-Endpunkt funktionierte bei anderen Befehlen, nicht beim Trigger. | Kein generelles Zertifikats-Abschalten; bestehende TLS-Seam verwenden. | Vier echte lokale HTTPS-Fälle Profil/Flag × richtiger/falscher Pin; 2 rot/2 grün vor Fix, danach volle CLI 597/597 |
| C4 / #58 | Medium, Inhaltsverlust | RestoreLayout setzte den kompletten alten Node-Snapshot ein. Bearbeitung, Duplikat und Löschen nach AutoLayout wurden rückgängig gemacht und konnten autosaven. | AutoLayout verändert Positionen; Wiederherstellung darf keine Inhalte/Membership besitzen. Aktuelle IDs/Eltern abgleichen. | Tatsächliche Page tidy→edit/duplicate/delete→restore→save rot, danach Editor + CustomActivity 127/127 |
| C5 / #60 | Medium, Secret-Verlust | GlobalsExport exportierte die API-Maske `***`; Import --upsert schrieb sie als echtes Passwort. | Secrets werden absichtlich nicht exportiert. Null bedeutet bei bestehendem Secret bewahren, legacy*** ebenso; neue Secrets/Plain→Secret ohne echten Wert scheitern. | Echter CLI-Datei-Roundtrip und 7 weitere Fälle: 7 rot/1 grün, danach volle CLI 605/605 |
| C6 / #67 | Medium, falsche Ausführung | ScriptEditorDialog setzte React-Konfiguration und rief sofort eine alte onRun-Closure auf. Der sichtbare neue Text konnte beim Test durch die alte Version ersetzt sein. | Expliziter aktueller Scriptparameter in beiden vorhandenen Callers reicht; keine zusätzliche Zustandsarchitektur. | Zwei tatsächliche Page/Dialog-Fälle rot, danach vier betroffene Testdateien 168/168 |

Alle sechs wurden vor Produktionsänderung unabhängig durch den Root-Agenten
gegengeprüft. Die verständlichen Nutzerfolgen stehen zusätzlich in `fix-list-de.md`.

## Ausgeschlossene und niedrigere Kandidaten

* Große WorkflowEditor-/PropertiesPanel-Dateien: viele unterschiedliche, tatsächlich
  genutzte Lebenszyklen. Lösch-/Zusammenlegungstest entfernt Funktionalität; Dateigröße
  allein belegt kein Medium und rechtfertigt keinen umfassenden Refactor.
* CLI/MCP-DTO-Ähnlichkeit: absichtliche HTTP-Paketgrenze nach ADR 0005; gemeinsame
  Engine-Abhängigkeit würde die Grenze verschlechtern. Konkrete Drift wird mit echten
  Verträgen getestet (C1), nicht durch pauschales Zusammenziehen aller DTOs.
* AuditLog-Filter während Load-more, Darstellungsreste bei Replay und lokale
  Resize-/Editor-Lebensdauer: keine belegte weitere serverseitige Mutation oder
  Berechtigungsüberschreitung. Darstellungs-/Robustheitskandidaten unter Medium.
* Einzelne CLI-Markup-Felder und YAML-Skalarformatierung: Ausgabe-Robustheit bei
  ungewöhnlichen Daten, kein Shell-Interpreter und kein importierbarer Sicherheitsvertrag.
* LoadHarness Seed.ReuseExisting/Ramp.StartRps und best-effort SignalR-Beobachtung:
  optionale Benchmark-Knöpfe/Beobachtung, keine belegte erneute Kernpfadblockade;
  ohne realen Lastlauf keine Leistungsbehauptung.
* OTel-Browserziel ist admin-konfiguriert; keine belegte nichtprivilegierte Umleitung.
* Low #65 doppelte Ausgabe-Aliase: UI zunächst 1 rot/75 grün bei `DISK`/`disk`, dann
  mit Backend/Runtime auf Vergleich ohne Groß-/Kleinschreibung abgeglichen.

## Validierung und Grenzen

Frische Runde-3-Läufe: Docs-UI **221/221**, Desktop **123/123**, CLI zuletzt
**605/605**, LoadHarness-Vertrag **8/8**, UI-Fokus zuletzt **168/168**.
`npm audit` meldete in allen drei JS-Projekten **0** bekannte Schwachstellen.
TypeScript und ESLint der geänderten UI-Dateien bestanden. Vollständige UI zuletzt
**3416/3416 in 247 Dateien**, anschließend SignalR/Lint **116/116** mit den zusätzlich
präzisierten Identitäts-/Zeitstempelassertionen.

Der erste vollständige UI-Lauf ergab 3415/3416: ein vorhandener Isolationstest prüfte
den sortierten ersten Lauf (`liveExecution`) statt seine beabsichtigte Execution-ID.
Auch der fokussierte Lauf reproduzierte das. Die asynchrone Schritte-Hydration darf
einen zweiten Lauf vor dem Event-Batch anlegen; dessen Auswahlposition ist kein
Isolationsvertrag. Ausschließlich der Test wurde präzisiert: Typ/Startzeit/fehlender
Abschluss von exec-1 bleiben unverändert, exec-OTHER enthält separat seinen Abschluss.
Die produktive Status-Reconciliation für tatsächlich beendete Läufe blieb unverändert
und ist durch die vorhandenen drei Terminalstatus-Reconnect-Fälle mitgeprüft.

Nicht durchgeführt: echte Windows-Service-/UAC-/SCOrch-Umschaltung, Installation,
Produktions-API, Lasttest oder Live-Computer-Use-Abnahme. Der lokale HTTPS-CLI-Test
benutzt nur seinen eigenen Kestrel-Testserver. MCP/Switcher wurden in R3 erneut in
Quellen geprüft; ihre früheren Tests werden hier nicht als frische R3-Läufe ausgegeben.
Root prüft CI/Deployment/Support-Skripte separat und aktualisiert den experimentellen
CU-Katalog; dessen Hash-Prüfung ersetzt keine Live-Abnahme.
