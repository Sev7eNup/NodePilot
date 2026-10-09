# Runde 4 – SPA, Browser-Demo und Dokumentationsoberflächen

Status: **in Arbeit, kein Abschlussurteil**. Startinventar und Zuständigkeiten:
[Runde 4](round-4.md). Root prüft diese Module nach dem Runde-3-Clientprüfer frisch.

## Quellenabdeckung

| Familie | Tatsächlicher Stand |
|---|---|
| SPA Einstieg, Auth-Transport und sensible Browserdaten | `App`, `main`, `api/client`, `csrf`, `authBoundary`, `sensitiveBrowserState`, `authStore` vollständig gelesen; Cache-/Health-/Streaming-Consumer folgen |
| Weitere API-Adapter, Stores und Hooks | Alle 15 API-Adapter, 15 Stores und sämtliche Hooks einschließlich SignalR-Typen/Reducer vollständig gelesen |
| Alle Seiten und fachlichen Dialoge | offen |
| Designer, Konfiguration, Graphbearbeitung und Ausführungsprojektion | Hooks vollständig; `LabeledEdge` und `edgeEditingContext` vollständig; übrige Komponenten und Editorseite offen |
| Gemeinsame UI-Bausteine, Bibliotheken, Themen, Typen und Übersetzungsverträge | offen |
| Browser-Demo mit eigenem Transport, Datenmodell, Simulation und Touren | offen |
| Docs-UI, Website, Routing, Inhaltseinbindung und Buildskripte | offen |
| Zugehörige Tests, Buildkonfiguration und End-to-End-Verträge | offen |

Vorläufige Zählung der nicht-testbezogenen TS/JS-Module: SPA `src` 380, Demo 38,
Docs/Website `src` 38 und Docs-Buildskripte 11. Inventarisierung und Suchtreffer allein
gelten nicht als Quellenprüfung. Übersetzungen, Medien und generierte Daten werden
über ihre besitzenden Module und ihre Verträge beurteilt.

## Laufende Gegenprüfung

- Auth-Nachrichten zwischen Tabs enthalten keine Tokens. Empfänger vertrauen erst
  dem serverseitigen Identitätsprobe-Ergebnis; Generationen entwerten alte Antworten.
  Das Entfernen der gemeinsamen Auth-Seam würde dieselben Cache-/Cookie-/Tab-Invarianten
  auf mehrere Aufrufer verteilen. UI-Rollengates werden nicht als Serverautorisierung gewertet.
- Die REST-/Download-Seam verwirft verspätete Inhalte nach einer Identitätsänderung.
  SSE-Consumer und Query-/Health-Consumer sind noch gesondert zu prüfen.
- Root prüfte außerhalb des eigenen Quellenscopes den neuen Login-/Refresh-Kandidaten
  #77 unabhängig gegen Issuer und TokenValidity. Die konkrete Passwortreset-Race wird
  im API/Core-Scope reproduziert; Runde 4 ist damit noch keine bestätigte Nullrunde.
- Der CLI-Hub-Pin-Kandidat #80 wurde eingeschränkt: ein gültiges CA-Zertifikat gewinnt
  laut vorhandenem gemeinsamen TLS-Modul absichtlich vor dem optionalen Pin. Dieser
  Fall ist kein neuer Sicherheitsbypass. Die fehlende Pin-/TLS-Konfiguration des Hub-
  Transports wird als funktionale Abweichung im Client-Scope geprüft.

## Teststand

Zusätzliche API-Projektsuite gegen den R3-Stand: **2850 bestanden, 2 übersprungen,
1 fehlgeschlagen**. Der verbleibende Test erzeugt absichtlich zwei aktive identische
Custom-Activity-Schlüssel und kollidiert jetzt mit der korrigierten #76-Invariante.
Die Fixture wird fachlich aktualisiert; dies wird nicht als grüner Lauf dargestellt.
Die vollständige Engine-Projektsuite ergab **3100 bestanden, 10 übersprungen,
3 fehlgeschlagen**. Die drei alten Service-Skript-Textassertionen wurden an die
korrigierte PowerShell-Interpolation angepasst und durch Ausführung des tatsächlich
erzeugten Fehlerzweigs ergänzt: **194/194 Fokusprüfungen grün**. Die verstümmelte
Fehleranzeige des ersten Laufs stammte aus der Assertion-Formatierung, nicht aus
einem zusätzlichen Produktionsfehler.

### Bestätigt und korrigiert: L82 – veralteter Operations-Livestatus

Der Operations-Store überdauerte Identitätswechsel; verspätete Hub-Callbacks konnten
ihn erneut füllen. Die Seiten filtern weiterhin nach erlaubten Workflow-IDs, deshalb
wird dies als Low-Zustandsfehler und nicht als belegter fremder Datenzugriff bewertet.
Die bestehende Auth-Boundary leert jetzt auch diesen Store. Live-Callbacks und Timer
prüfen ihre Generation und Lebensdauer; die Timerreferenz wird beim Abbau zurückgesetzt.
Unabhängige Quellengegenprüfung durch den API/Core-Prüfer bestätigt die Einstufung.
**4 rot → 49/49 grün** in vier Operations-/Feed-/Seiten-/Demo-Testdateien.

### Beitrag zu M81 – vollständige Schlüsselrotation

SecuritySection zeigt die drei zusätzlichen Speicherfamilien einschließlich aller
übersprungenen Einträge. Eine gemeinsame Kategorienabbildung speist Details und
Gesamtzähler; DE/EN-Texte nennen die vollständige Reichweite und die Bedeutung von
Teilergebnissen. **2 rot → 10/10 grün**; TypeScript und fokussierter ESLint grün.
Backend und CLI werden von den jeweiligen Prüfern abgedeckt.

### M84 – Einfügen in einen deaktivierten Zweig

`useCanvasConnect.insertOnEdge` aktivierte die erste Hälfte einer zuvor deaktivierten
Kante. Der echte Hook mit der gemeinsamen Ablaufprojektion reproduziert den zusätzlich
erreichbaren Skriptschritt. Unabhängige Gegenprüfung gegen
`WorkflowDefinitionDocument.ActiveEdges` bestätigt die Wirkung auch im Engine-Graphen.
Die minimale Korrektur erhält `disabled` auf beiden Hälften; die bisherige Platzierung
von Label/Bedingung bleibt erhalten. **1 rot → 12/12 grün** im Hook-Test.

### L88 – doppelte API-Anbindung der Unterworkflow-Vorschau

`resolveWorkflowRef` begründete eine eigene Fetch-/Auth-Implementierung mit einem längst
überholten fehlenden HTTP-Status im API-Client. Die Vorschau verwendet nun `api.get` und
behandelt nur den fachlichen 404-Sonderfall selbst. Die strukturierte 503-Diagnose ging
vorher verloren (**1 rot**); 404 und Schutz vor verspäteten Auth-Antworten bleiben erhalten.
Unabhängige Gegenprüfung bestätigt die Konsolidierung. Zusammen mit Demo-Backend,
Simulation und M84: **91/91 grün in vier Dateien**.

### L89 – Einfügeschaltfläche in schreibgeschützter Ansicht

Der Plusbutton einer Kante ignorierte das vorhandene `canWrite`, obwohl andere
Kantenaktionen es berücksichtigten. Er konnte lokal Änderungen auslösen, die der Server
beim Speichern zurückweist. Dies ist ein Low-Bedienungsfehler, keine Umgehung der
Serverautorisierung. Die Schaltfläche wird nur mit Schreibrecht angeboten.
**1 rot → 21/21 grün** einschließlich positiver Editorfunktion und Memoisierung.

## Lesefortschritt gemeinsamer Bibliotheken

Vollständig gelesen: `hubConnection`, `signalrConnect`, `workflowSimulation`,
`bulkOperations`, `edgeLabels`, `edgePorts`, `edgeDetach`, `editorGraphHelpers`,
`groupReparenting`, `collapsedGraphView`, `rbac`, `customActivities`, `outputParameters`,
`workflowDefinitionSanitizer`, `configClone`, `resolveWorkflowRef`, `prePublishChecks`,
`variablePreview`, `templateValidation`, `findReplace`, `variableUsageScan`, `navigation`,
`queryErrorToast`, `chatMessages`, `chatExport`, `jsonPathBuilder`, `eventFields`.
Zusätzlich vollständig gelesen: `upstreamVariables`, `agentTeamProjection`, `agentRunTrace`,
`agentRunSummary`, `workflowLint`, `opsTimeline`, `cronPreview`, `workflowDiff`,
`activityConfigFacts`, `editorCommandPalette`, `folderSelection`, `junctionFanIn`,
`lastSimilarNode`, `variableDragDrop`, `docsLink`, `format`, `knownProgramLaunchers`,
`breadcrumbs`, `groupLabel`, `summarizeExpression`, `uuid`, `shallowEqual`.
Alle hier nicht genannten Bibliotheken bleiben offen.

### M90 – Zeitplan-Vorschau mit der tatsächlichen Serverberechnung

Die Browserbibliothek deutete numerische Wochentage anders als Quartz, verwarf die
Jahresbegrenzung und verwendete die Browserzeitzone. Die beiden produktiven Consumer
verwenden nun gemeinsam den bestehenden Serverendpunkt und dessen Query-Cache.
Eingaben werden entprellt, alte Vorschauen bei einer Änderung sofort ausgeblendet.
Die getrennte Offline-Demo unterstützt ausdrücklich nur einen Teil der Quartz-Syntax;
sie übersetzt einfache Wochentage und lehnt Jahresbeschränkungen/komplexe Wochentage ab,
statt eine scheinbar gültige Vorschau zu erfinden. Sie nutzt denselben HTTP-Vertrag.

**3 ursprüngliche UI-Regressionen rot → 142/142 Fokusfälle grün**; zusätzliche
Cache-/Eingabe-/Demo-Vertragsprüfungen **63/63 grün**. Backend: **25/25 grün**, einschließlich
Jahresende, numerischem Sonntag und Zeitumstellung in Berlin. TypeScript für Produktion
und Demo sowie fokussierter ESLint grün. Die vollständige UI-Suite ist jetzt mit
**3430/3430 in 249 Dateien grün** (`.codex-round4-ui-full.log`). Dies ersetzt keine neue
projektweite Prüfrunde.

### Aktualisierte gemeinsame Tests

Der API-Nachlauf nach der korrigierten Custom-Activity-Fixture und den damaligen R4-Fixes
ist mit **2878 bestanden, 2 externe Datenbankfälle übersprungen, 0 Fehlern** abgeschlossen.
Der Engine-Nachlauf hatte **3102 bestanden, 10 externe Fälle übersprungen, 1 Fehler**:
eine zeitabhängige WaitCondition-Testannahme. Die gezielte Stabilisierung steuert die
Deadline über eine interne Uhr und prüft exakt zwei Versuche; der kombinierte
Trigger-/Alarm-/WaitCondition-Fokus ist mit **119/119 grün**. Der neue vollständige
Engine-Nachlauf steht noch aus.

## Weitere R4-Ergebnisse und Lesefortschritt

- L103: fehlender Output-Alias der Try/Catch-Vorlage, unabhängig gegen den Engine-Resolver bestätigt. Einfüge-Regression rot → 40 Vorlagen-/Hook-Fälle grün; korrigierter Template-Lint-Vertrag eingeschlossen.
- L106: doppelte Abschnittsnamenzuordnung ließ AI-Knowledge im Breadcrumb fehlen. Gemeinsame Metadaten in `navigation.ts`, echte Tab-/Breadcrumb-Consumer erhalten. Zusammen mit L103 49 Fokusfälle grün.
- M109: reine Namens-Consumer nutzten die auf 500 Einträge begrenzte Workflow-Übersicht. Vier Consumer verwenden nun den vorhandenen `/workflows/names`-Vertrag und den bestehenden Cache-Präfix. Wartungsauswahl-Regression rot → 151 Fälle grün; unabhängige Gegenprüfung angefragt.
- Vollständiger Engine-Nachlauf nach #94/#96/Uhr-Stabilisierung: **3109 bestanden, 10 externe Providerfälle übersprungen, 0 Fehler**, `.codex-round4-engine-full-final.log`. Spätere Engine-Fixes sind darin noch nicht enthalten.
- Alle handgeschriebenen `src/lib`-Implementierungen jetzt frisch gelesen, einschließlich `autoLayout`, `workflowSnippets`, `monacoSetup`, `activityTypes`, `cssColor`, `appIcon`, `chartTheme`, `codeMirrorTheme`, `CurlyBracesIcon`, `activityIcons`, `triggerBadgeMeta`, `statusTokens`. Generierter Aktivitätskatalog wird über Generator-/Syncverträge bewertet; Typdeklarationen bleiben gesondert.
- Seiten vollständig: `DbViewerPage`, `LoginPage`, `SettingsPage`, `SystemSettingsPage`, `BackupPage`, `AlertingPage`. `AuditLogPage` bis Zeile 455, `MaintenanceWindowsPage` bis 305. Übrige Seiten und Komponenten weiterhin offen.
## Fortschritt nach weiteren Seiten- und Dokumentationsprüfungen

- M109 unabhängig gegen API-RBAC und sämtliche vier ursprünglichen Consumer bestätigt; ein weiterer reiner Namensconsumer in `WorkflowBreadcrumbs` wird im Designer-Scope überprüft.
- M111 Audit-Paging: drei RED-Fälle (alter Filter, Doppelklick, neuer Kopf-Cursor) → 23/23 grün. Ein gemeinsamer InfiniteQuery besitzt die komplette Cursorfolge; TypeScript und fokussierter ESLint grün.
- L112 Docs-ToC-Sprachwechsel: `lang` ist jetzt Teil der DOM-Neuextraktion. 221 Dokumentationstests und TypeScript grün, unabhängig vom Website-Reviewer bestätigt. Keine gezielte Browserprüfung behauptet.
- L119 Nulltrefferfilter, L123 Dashboard-Liveverlinkung und L124 Custom-Activity-Warnungsrevision: jeweils reproduziert, gemeinsam 90/90 Seitentests grün. #120 Suchfelder und #126 Maschinen-Sammeltest bleiben in Gegenprüfung bzw. Umsetzung.
- Frisch vollständig gelesene weitere Seiten: `AuditLogPage`, `MaintenanceWindowsPage`, `MetricsPage`, `MobileWorkflowView`, `SupportLogPage`, `AiChatPage`, `ExecutionsPage`, `DashboardPage`, `CustomActivitiesPage`, `GlobalVariablesPage`, `MachinesPage`.
- Docs: sämtliche React-Komponenten, `src/lib`, Spracheinrichtung und öffentliche Theme-/Legacy-Skripte frisch gelesen. Website, Buildskripte und Daten-/Assetverträge anschließend zusätzlich vollständig an den Client-/Delivery-Reviewer übertragen. Designer separat an den API-/Core-Reviewer, übrige SPA-Komponenten nach Websiteabschluss an den Client-/Delivery-Reviewer übertragen.
- Root verbleiben `UsersPage`, `WorkflowsPage`, `WorkflowEditorPage` vollständig und `OperationsPage` ab Zeile 301. Runde 4 bleibt offen; wegen ihrer neuen Medium-Funde folgt danach zwingend eine frische Runde 5.

## Aktualisierung nach vollständiger Seitenlektüre

`UsersPage`, `OperationsPage` und `WorkflowEditorPage` sind nun vollständig frisch gelesen. `WorkflowsPage` wurde zusätzlich vollständig vom Architektur-Reviewer übernommen und mit #129 auf echte serverseitige Seiten umgestellt. Damit sind sämtliche SPA-Seiten in dieser Runde gelesen; Designer und übrige Komponenten bleiben bei den benannten Reviewern bis zu deren Abschluss offen.

- L120: Benutzername und ID-Teile vor serverseitiger Seitenauswahl; 2 RED → 79/79 API/RBAC, kombinierter UI-Fokus 42/42 grün. SQL-Übersetzung gegengeprüft anhand [SQL Server](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/functions) und [Npgsql-Quellcode](https://github.com/npgsql/efcore.pg/blob/main/src/EFCore.PG/Query/ExpressionTranslators/Internal/NpgsqlObjectToStringTranslator.cs); kein Live-Providerlauf.
- M126: Maschinen-Sammeltest begrenzt auf vier Verbindungen; 12-parallel-RED → höchstens 4, alle Ziele trotz Einzelfehler verarbeitet. WinRM-Pool unabhängig gegengeprüft.
- L127: Fünf duplizierte Spaltenresize-Lebenszyklen durch gemeinsamen Hook ersetzt. Unmount-RED → 74 Seitentests grün; zusätzliche Mindestbreiten-/Listenerprüfungen.
- L131: Gruppierte Knoten und Kanten nutzen beim Suchsprung vorhandene absolute Koordinaten. Gegenprüfung unabhängig bestätigt. Gemeinsam mit L127: 129/129 Editor-/Koordinaten-/Hooktests grün.
- M129 API/Pager unabhängig gegengeprüft: Zugriffsscope vor Filter/Zählung/Sortierung, begrenzte Projektion, erhaltene Statistiken, klare seitenlokale Auswahl. Designer-/Demo-Anbindung noch im Abschluss.
- Vollständiger Engine-Nachlauf nach allen R4-Datei-/PowerShell-Korrekturen: 3137 bestanden, 10 bekannte externe Providerfälle übersprungen, 0 Fehler. Vollständiger API-Nachlauf läuft; neue Section-Editor-Abstraktion erfordert Anpassung des bestehenden statischen Settings-Synctests.

Runde 4 ist weiterhin keine Nullfundrunde. Eine anschließende frische Gesamtprüfung bleibt erforderlich.
## Zwischenstand auf ausdrücklichen Pausenwunsch

Die bereits begonnenen Korrekturen sind abgeschlossen. Der vollständige SPA-Nachlauf
ist mit **3476/3476 in 252 Dateien grün**; der vollständige API-Nachlauf mit **2918
bestanden, zwei externen Provider-Skips und null Fehlern**. Details und verbleibende
Prüfflächen stehen im [Pausenstand](pause-2026-10-09.md). Insbesondere wurde keine
neue Nullfundrunde gestartet. #133 und der vorläufige Kandidat #134 sind nicht behoben.
