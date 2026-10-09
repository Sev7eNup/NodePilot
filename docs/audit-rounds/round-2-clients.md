# Runde 2 – Clients und Clientverträge

Stand: 2026-10-09, Arbeitsbaum auf `audit/architecture-security-2026-10-09`.
Diese Runde ist eine erneute Prüfung der unten aufgeführten Modulgruppen, einschließlich
unveränderter Produktionspfade. Sie ist kein bloßer Diff-Review und keine Behauptung, jede
Darstellungszeile oder jede Testfixture einzeln gelesen zu haben. Dateiinventare und Suchtreffer
dienten der Navigation; die aufgeführten Ergebnisse beruhen auf gelesenen Implementierungen
und verfolgten Aufruf-/Datenpfaden. Die abschließende projektweite nächste Runde steht noch aus.

## Methode und Vertrauensgrenzen

Erneut angewendet: `improve-codebase-architecture` und `audit-ai-slop-code`.
Grundlagen: `CONTEXT.md`, bereichsspezifische `CLAUDE.md`, `docs/threat-model.md`,
ADRs zu HTTP-only Clients, Workflow-Roots, Alerting und Shared-Folder-Autorisierung
(0005, 0006, 0008, 0009, 0010). Hostadministratoren und bewusst erstellte Automationsskripte
sind vertrauenswürdig; Viewer, fremde Antworten und veraltete Browseridentitäten nicht.

Architektur: Zustandsbesitz, Lifecycle, Speichern/Publizieren, DTO-Roundtrip,
gemeinsame HTTP-Schichten, Grenzen zwischen CLI/MCP und Engine, Duplikation mit
tatsächlicher Vertragsabweichung. Security: Autorisierungsdurchsetzung versus UI-Gates,
Auth-Wechsel über asynchrone Arbeit, Secret-Erhaltung, Ausgabe-/URL-Sinks, Electron-IPC,
privilegierte Service-/Prozesssteuerung und Herkunft authentifizierter SCOrch-Requests.

## Tatsächlich verfolgte Bereiche

| Modulgruppe | Gelesene Entry Points und verfolgte Pfade | Architektur / Security-Ergebnis |
| --- | --- | --- |
| SPA Einstieg und Transport | `App.tsx`, `api/client`, `api/adminSettings`, `security/authBoundary`, `sensitiveBrowserState`, `authStore` einschließlich Cross-Tab-Reprobe, `confirmStore` | Auth-Epoch remountet geschützte Inhalte; gemeinsame Requests prüfen Boundary und CSRF. Keine neue Umgehung in diesem Kern. |
| Workflow-Liste und Editor | `WorkflowsPage`, `WorkflowEditorPage`, `useWorkflowPersistence`, `useWorkflowLock`, `useWorkflowClipboard`, `useWorkflowHistory`, `useNodeOperations`, `useCanvasConnect`, `useWorkflowContract`, `useWorkflowExecution` | Speichern/Publizieren über verzögerte Arbeit sowie Dirty-/History-Besitz verfolgt. Zwei Medium-Vertragsfehler behoben (C2, C3). |
| Live-Ausführungen | `useSignalR`, `signalrReducer`, `useLiveOpsFeed`, `useAgentRuns`, `signalrConnect`, `hubConnection`, `operationsStore`; `ExecutionsPage`, `OperationsPage`, `MobileWorkflowView` | Snapshot/Event-Reihenfolge, Reconnect, terminale Zustände, Timer/Hydration, Cancel/Quarantine und Rollen geprüft. Verlorener terminaler Status behoben (C4). |
| Infrastruktur und Rollen | `MachinesPage`, `GlobalVariablesPage`, `UsersPage`, `SettingsPage`, `MaintenanceWindowsPage`, `CustomActivitiesPage`; Folder Trees, Permissions Modal, Bulk-Delete und Run Dialog | Form-/Mutationseinstiege, Read-modify-write, Secret-Sentinel, UTC/Recurrence, Importgrenzen, Capability-Gates und Rekursivlöschung verfolgt. API bleibt Autorität. |
| Backup, DB und Beobachtung | `BackupPage`, `DbViewerPage`/`QueryPane`, `AuditLogPage`, `SupportLogPage`/`SupportLogViewerSection`, `DashboardPage`, `MetricsPage` | Preview/Restore-Auswahl, Write-Confirmation, Query-History-Owner, Paging/Filter, begrenzte Loganzeige, Downloads, Polling und sichere React-Textausgabe geprüft. |
| Systemkonfiguration | `SystemSettingsPage`; Integrations, Authentication, Security, Agents, AiKnowledge, Performance, Retention, LoggingTelemetry, DbAdmin, SystemInfo; `SectionFormHelpers`, `SecretField`, `EtagConflictDialog` | Payload-Aufbau, ETag 412, Env-Sperren, Secret keep/change/clear, profilgebundene Schlüssel, Limits/Read-Grants/Package-Import geprüft. Systempolicy-Roundtrip behoben (C5); Skill-Import-Boundary C7 behoben. |
| Alerting | `AlertingPage`, `AlertingRuleEditor`, `SystemPolicyEditor`, `SystemAlertsSection`, `api/alerting`, `api/systemAlerting`; CLI/MCP und `SystemAlertingController` als Gegenvertrag | Versteckte, aber unterstützte Vorkommensschwellen und Route-Filter wurden beim Umbenennen überschrieben: C5. |
| AI UI | `AiChatPage`, `AiWorkflowChatPanel`, ProposalCard/MessageBubble, KnowledgeChat, `aiChatStore`, `knowledgeChatSessionStore`, `useAiScriptStream`, `api/ai`, Markdown-Renderer | Prompt/Stream-Abbruch, Thread-/Workflowbesitz, Proposal-Hash und explizites Anwenden, keine direkte Veröffentlichung, Source-Zugriff und sichere Markdown-Sinks verfolgt. |
| Graph und reine Clientlogik | DynamicActivityConfig, PropertiesPanel, `configClone`, `useDisplayedGraph`, `groupReparenting`, `collapsedGraphView`, `autoLayout`, `workflowSimulation`, `workflowLint`, `prePublishChecks`, `workflowDiff`, `findReplace`, `variablePreview`, Katalog-/Parameter-/Port-Helfer | Projektion versus gespeicherte Definition, unveränderliche Konfigurationsänderung, Trigger-Reachability, Fan-in, stabile IDs, Referenz-/Preview-Verarbeitung geprüft. UI-Lint ersetzt keine Servervalidierung. Kein kosmetischer Refactor als Medium eingestuft. |
| CLI | Program/CommandRegistration, BaseCommand, SessionResolver/ApiClientFactory, Auth/Config; alle Command-Familien Workflow/Exec, Machine/Credential/Globals/SharedFolder, Maintenance/User/Agent/Secrets, Alerting/SystemAlert, Settings/Db/Backup, Audit/Stats/Observability/Operations; OutputWriter/YamlEmitter/Renderers und API-DTOs | HTTP-only bleibt erhalten. Pfad-/Querybildung, Read-modify-write, Secret-Sentinel, nichtinteraktive Bestätigung, Abbruch/Exitcodes und Pagingverträge verfolgt. Disconnect-Hänger behoben (C1). |
| MCP | Program/config/client factory; Discovery, WorkflowRead/Edit, Execution, SupportingData, Alerting/SystemAlerting, Agent, DbAdmin, Telemetry, CanvasAssistant, DestructiveTools; Resources, DefinitionRedactor, WorkflowDefinitionPatcher, PayloadShaping, ApiErrorMapper | Tool-Registrierung/Destructive-Gate, Benutzer-API-Identität, DTOs, redigierte Definitionsausgabe, gekappte Ausgabe und SQL-readonly Pfad geprüft. Kein neuer bestätigter C/H/M-Befund. |
| Electron | main, security, setupFlow, setup-preload, setup.html, http, config, backendRestart, skins und Package-/Testgrenzen | Navigation/Window-open, Sandbox/Preload-Trennung, Bootstrap-IPC-Sender, Tokenbesitz im Main, Loopback-Zertifikatspinning, Downloadherkunft/Save-As und eingeschränkter Service-Restart geprüft. Kein neuer C/H/M-Befund. |
| Switcher | ConfigurationLoader/Validator/Probe, CliLocator, WindowsServiceControlGateway, ServiceDiscovery, ProcessPresenceProbe, EnvironmentEvaluator, SwitchCoordinator, AllowListReader, ActivityLogger, NodePilotWorkflowReconciler, ScorchRunbookReconciler; App/MainViewModel/Login/MainWindow/AsyncCommand/UserInteraction/ThemeService | Fail-closed Vorprüfungen, Allowlist, SCM-PID-Abgleich, Stop/Start-Reihenfolge, CLI-ArgumentList und Password-stdin, Paginglimits und Same-Origin-Guard verfolgt. Kein neuer C/H/M-Befund nach Runde-1-Fix. |
| Docs/Public Site | Docs App/DocPage/DocMarkdown/SearchModal/content/docsBase/siteContext; Site Router/SEO/Main/Experience-Controller; assemble-site, prerender-plugin, site-origin, demo-preview-plugin, pages-redirect und Buildkonfiguration | Buildzeit-Inhalte versus Benutzer-URL, Suchdaten, Markdown/HTML-Sinks, feste Redirect-Origin und begrenztes Assembly-Ziel geprüft. Kein neuer C/H/M-Befund. |
| Zusätzlicher Load-Harness | sämtliche Produktionsdateien unter `tests/NodePilot.LoadTests`: Program, Optionen, Seeder, API-Client, Templates, ScenarioFactory, SignalRObserver, README/Stack-Konfiguration; Core-Analyzer und Create/Publish/Execute-Verträge | Dokumentierter Runner war durch mehrere veraltete Verträge nicht ausführbar: C6. Keine echte Last ausgelöst. |

Zuordnung der zugehörigen Tests: bestehende CLI/MCP-Command- und DTO-Paritytests,
Desktop-Security/Setup/Downloadtests, Switcher-Service-/Reconciliertests, UI-Hook-/Page-/Auth-
und Formtests wurden als Gegenbelege herangezogen. Reine DTO-Feldparität beweist keine
Workflow-Lifecycle-Kompatibilität; genau deshalb verwendet C6 den tatsächlichen Analyzer.

## Bestätigte Befunde und verständliche Folgen

Alle folgenden Befunde sind Medium, unabhängig gegengeprüft und minimal korrigiert,
einschließlich des unabhängig gegengeprüften C7. Keine Critical/High in diesem Bereich bestätigt.

| ID | Ort / Defekt | Alltagsszenario und Korrektur |
| --- | --- | --- |
| C1 | `ExecWatcher.cs`: nach erfolgreichem SignalR-Start kein Fallback bei späterem Closed | `np workflow run --wait` hängt nach einem kurzen Netzwerkausfall, obwohl der Job fertig ist. Closed aktiviert den bestehenden Status-Pollingpfad; der Workflow wird nicht erneut gestartet. |
| C2 | `useWorkflowPersistence`: Publish bestimmt das Ziel erst nach fremder Save-Wartezeit | Benutzer publiziert A und öffnet während langsamen Speicherns B; vorher konnte B veröffentlicht werden. Publish bindet Workflow-ID und Generation vor dem Warten und verwirft veraltete Absichten, auch A→B→A und Unmount. |
| C3 | `useCanvasConnect`: direkte Node-/Edge-Änderungen ohne Dirty-Markierung | Knoten per Kanten-Plus oder Quick-Connect eingefügt, weg navigiert, Änderung verloren. Beide Pfade markieren jetzt dirty und nutzen unverändert bestehendes Autosave/History. |
| C4 | `useSignalR`: active-only Snapshot kann verlorene Terminal-Events nicht ersetzen | Laptop verliert WLAN, Lauf endet, nach Rückkehr bleibt Running sichtbar. Fehlende zuvor aktive IDs werden begrenzt parallel einzeln gelesen; bestätigte Terminalzustände und Schritte werden nachgezogen. Abwesenheit allein beendet keinen Lauf; TTL bleibt erhalten. |
| C5 | `SystemPolicyEditor`: versteckte `minOccurrences`, `occurrenceWindowMinutes`, `conditionExpressionJson` durch Defaults ersetzt | Per CLI gegen Alarmflattern konfigurierte Regel wird in UI nur umbenannt und sendet danach jeden Einzelvorfall bzw. ungefilterte Route. Bestehende Werte bleiben pro Policy/Route erhalten; neue Regeln behalten bisherige Defaults. |
| C6 | Load-Harness: fehlende Trigger, unerlaubtes Fan-in, nie veröffentlichte Workflows, veraltete Array-Annahme | Dokumentierter Smoke-Lastlauf scheitert schon beim Seeden/Ausführen oder beim Abschluss-Gate. Templates besitzen gültige Manual-Roots/Junctions; Seeder publiziert Kinder vor Eltern; Running-Zahl stammt aus gefiltertem PagedResponse.totalCount. README erlaubt nur den expliziten lokalen Health-Host. |
| C7 | `AgentsSection`: Paketdatei wird asynchron gelesen, anschließend ohne ursprüngliche Auth-Generation importiert | Nach Logout/Login während des Lesens wird die alte Importabsicht unter dem neuen Administrator gesendet. Auth-Generation wird jetzt vor dem Lesen erfasst und danach geprüft; Boundary-Wechsel und Unmount verhindern den alten POST. |

## Bewusst ausgeschlossene Kandidaten

- **Loadtest-Ziel nicht als GUID registriert:** kein Defekt. `MachineResolver` unterstützt
  ausdrücklich Ad-hoc-Hostnamen. `loadtest-target` führt über Noop; eine Umstellung auf localhost
  würde reale lokale PowerShell statt Noop auswählen und wäre eine Funktions-/Sicherheitsänderung.
- **Markdown-Bild als Secret-Exfiltration:** im ausgelieferten API-Host verhindert `img-src 'self' data:`
  externe Bildrequests; kein bestätigter Exploit. Kein raw-HTML-Plug-in im Chatrenderer.
- **CLI/MCP-Duplizierung der DTOs:** bewusst unabhängige HTTP-Clients mit gemeinsamen
  Transportbausteinen und Paritytests. Eine Engine-Abhängigkeit zur vermeintlichen Konsolidierung
  widerspräche dem dokumentierten Vertrag.
- **Große Editor-/Settings-Dateien, kopierte Folder-Ansichten oder kleine Formwidgets:**
  Lesbarkeits-/Wartungskosten, aber ohne bestätigte Medium-Auswirkung kein erzwungener Umbau.
  SharedFolder hat absichtlich andere Capability-Regeln als globale Admin-Folder.
- **Switcher-Konfiguration/PATH durch denselben Hostbenutzer manipulierbar:** kein neuer
  privilegienübergreifender Angriffsweg unter dem dokumentierten Host-Vertrauensmodell.
- **SCOrch-Redirect leakt automatisch Windows-Auth:** nicht behauptet. Der frühere Befund betrifft
  explizite neue Requests aus fremden NextLinks, die der Same-Origin-Guard verhindert.
- **Desktop ConnectionString-Split mit frei quoted Passwort:** eigener Standard-Provisioner
  erzeugt sichere Base64-Zeichen; ohne belegten unterstützten Fremdpfad kein Medium erhoben.

## Ausgeführte Verifikation dieser Runde

- CLI-Disconnect: zuvor Timeout; danach komplette CLI-Release-Suite **593/593**, kein Skip.
- Publish: neuer Verhaltenstest zuvor falscher `/wf-2/publish`; drei Guard-Szenarien grün.
- Canvas-Connect: beide Autosave-Szenarien zuvor `isDirty=false`; danach grün.
- Live-Reconnect: zuvor Succeeded→Running hängen geblieben; danach Succeeded/Failed/Cancelled
  sowie weiterhin Running trotz Abwesenheit aus der Liste grün.
- Systempolicy: Red-Test zeigte exakt 5→1, 15→0, Route-Filter→null; danach grün.
- Gemeinsamer UI-Fokus: **206/206** in sieben Dateien (SignalR, Reducer, Persistence, Canvas,
  WorkflowEditorPage, SystemAlertsSection, AgentSkillImportBoundary); `tsc -b` erfolgreich.
- LoadHarnessContractTests: **8/8 rot**, danach **8/8 grün**; sechs echte Analyzer-Graphs,
  Pagingcount und Publish-Reihenfolge. LoadTests Release-Build: **0 Fehler/0 Warnungen**.
- C7 zuerst **1 erwarteter Fehler, 1 normaler Import bestanden**, danach **2/2 grün** im gemeinsamen Fokus; erneutes `tsc -b` erfolgreich.

Runde-1-Zahlen (Desktop 123, MCP 206, Switcher 102 usw.) werden hier ausdrücklich nicht als
erneute Runde-2-Testläufe ausgegeben. Keine realen SCOrch-Zugriffe, Windows-Servicewechsel,
Installation/DB-Restores, externen Nachrichten oder Lastläufe durchgeführt.

## Unabhängiger Desktop-Rollback-Gegencheck

Zusätzlich zum eigenen Scope `deploy/desktop/Update-Desktop.ps1` und
`deploy/Test-DesktopUpdate.ps1` gegengelesen: Rollback stoppt zuerst vollständig, startet
nur PostgreSQL, prüft Readiness begrenzt, restauriert mit `--exit-on-error --single-transaction`,
prüft Exitcode, setzt PGPASSWORD zurück und startet API erst nach Erfolg. Harness extrahiert
echte AST-Blöcke und prüft fünf Pfade offline; Root meldete PS5.1 grün. Kein neuer C/H/M im
Gegencheck. PowerShell 7 und realer Installer-Rollback lokal nicht verifiziert.

## Grenzen und nächster Abschluss

Diese Runde belegt Quellenprüfung und die genannten lokalen Tests, keine vollständige
End-to-End-Abnahme auf installierten Desktop-/Servermaschinen. Der experimentelle Computer-Use-
Katalog wird wegen geänderter UI-Verträge mit konkreten Szenarien ergänzt; dessen Coverage-Hash
bescheinigt nur überprüfte Abdeckung, keinen tatsächlich durchgeführten UI-Lauf.
Alle acht bestätigten Client-/Harness-/Proxy-Befunde dieser Runde sind behoben und lokal gegengeprüft.
Der Clientanteil von Runde 2 ist abgeschlossen. Ein fehlerfreier fokussierter Gegencheck ersetzt
keine erneute vollständige Projektrunde. Der Computer-Use-Katalog wurde für DES-01, DES-05,
RES-01, OPS-05 und BROWSER-01 konkret erweitert und sein Coverage-Vermerk nach Quellenreview
aktualisiert. Vorbestehende Drift wurde ebenfalls ergänzt: aiAgent, aiAgentTeam und Agents-Settings
besitzen nun konkrete sichere Fälle. `validate_catalog` akzeptiert **167 Fälle** und die sechs
Offline-Katalogtests sind grün. Das belegt keine ausgeführte Computer-Use-Abnahme.

Zusätzlicher Lint-Gegencheck der geänderten Produktionsdateien: neue Ref-Aktualisierung in
`useSignalR` auf Layout-Effect umgestellt; danach **0 ESLint-Fehler**, zwei bestehende Cleanup-
Warnungen. SignalR nach dieser Anpassung erneut **40/40** grün.

## Zusätzliche begrenzte LLM-Probe (Low)

Auf Gegenprüfung des Root-Reviews `SettingsTestProbe.TestLlmAsync` gehärtet: Der manuell durch
einen Administrator ausgelöste Verbindungstest puffert erfolgreiche `/models`-Antworten nicht
mehr und liest bei Fehlern höchstens 201 Zeichen zur 200-Zeichen-Vorschau mit Auslassung.
Ein verknüpftes Deadline-Token deckt auch den Body nach `ResponseHeadersRead` ab; Encoding-
Header und Ressourcenfreigabe bleiben berücksichtigt. Kein weiterer Medium-Befund daraus
konstruiert. Drei Offline-Tests mit nicht serialisierbarem HttpContent, einem nie endenden
Body und einem bis Cancellation blockierenden Body zuerst **3/3 rot**, danach zusammen mit
allen Probe-Tests und den Root-Auth-Override-Tests **32/32 grün**, kein Skip.

Separat gegengeprüft und beim Root-Review geführt: Security-Array-Tails in geschichteter
LDAP/OIDC- und Agent-Grant-Konfiguration. Die clientseitige Rolle/Secret-Erhaltung ersetzt
keinen atomaren serverseitigen Konfigurationsleser; Implementierung und Bewertung dieses
bereichsübergreifenden Autorisierungsbefunds gehören zum Root-Bericht.

## C8 – entfernte Proxy-Ausnahmen bleiben wirksam (Medium, vor Runde 3 ergänzt)

Aus dem Konfigurationslisten-Gegencheck ergab sich derselbe Fehler für
`RestApi:Proxy:BypassList` und `Llm:Proxy:BypassList`: Eine Basisliste `[keep, removed]`
und eine höher priorisierte vollständige Liste `[keep]` oder `[]` lassen beim normalen
ConfigurationBinder alte Indizes übrig. Im Alltag entfernt der Administrator einen
Direktzugriff, um das Ziel wieder durch den Unternehmensproxy zu führen; der Prozess
umgeht diesen weiterhin. Dies ist eine Verletzung der Routingkonfiguration, keine behauptete
Umgehung sämtlicher SSRF-Sperren.

Korrektur: REST-Options und der Fallback von `RestApiHttpClientProvider` verwenden den
bereits vom Root eingeführten atomaren Listenleser. `LlmProxyOptionsPostConfigure` wendet
dieselbe Regel am API-Kompositionspunkt auf die vom AI-Paket gelieferten Optionen an;
Program registriert ihn direkt nach `AddNodePilotAi`. AI bekommt keine Engine-Abhängigkeit,
Core keine Konfigurationsabhängigkeit und es entstehen keine neuen Paketabhängigkeiten.
Die vom Root angepasste DTO-Liste und die tatsächlichen Handler haben damit dieselbe Semantik.

Offline-Verifikation: **6/6 REST-Fälle rot** (Handler, Options-Policy und Fallback, jeweils
verkürzt und leer) und **2/2 LLM-Fälle rot**; nach Korrektur **30/30 Engine-Proxytests** sowie
**37/37 API-Tests** (LLM-Override, Bootvalidator, SettingsProbe) grün, keine Skips.
Die LLM-Tests verwenden echte geschichtete JSON-Provider und prüfen neben dem Start auch
mehrfaches Reload am bestehenden `LlmConfiguredProxy`. Keine Netzwerkverbindung geöffnet.
