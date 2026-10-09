# Runde 4 – ergänzende Designer-Prüfung (auf Nutzerwunsch unterbrochen)

Explizite Übergabe durch Root nach Abschluss der API-/Core-Quellenrunde. R4-Basis:
`round-4-inventory.json`, SHA-256
`586be1aacd35e96be2906a90bb17a144def44e28778389473d959cf1db6a8e42`.
Startinventar: 140 Dateien, davon eine colocated Testdatei. Beide Skills angewandt;
Root-/UI-CLAUDE, CONTEXT, Threatmodell und relevante ADRs gelesen.

Am 2026-10-09 bat der Nutzer ausdrücklich um Ende des Arbeitsabschnitts und spätere
Fortsetzung. Die Designer-Runde ist **nicht vollständig**, #134 bleibt **offen**.
Keine Nullbefundaussage, kein Abschluss des Gesamtziels.

## Tatsächlich gelesene Quellen

| Module | Architekturprüfung | Sicherheit/Funktion |
| --- | --- | --- |
| EditorOverlays, EditorRightPanel, EditorSidebar vollständig | Kontrollierte Komponenten, Commands/Stores, Property-/Runseams | canWrite, Zielworkflow/-knoten, Subworkfloweinbettung und echte Page-Callbacks verfolgt |
| BulkEditPanel, QuickEditPopup, FindReplaceOverlay | Configpatch, typisierte Return-Map, Listenerbesitzer | #121 Medium: Return-Objekt bleibt Objekt. #122 Low: Bulk/Ersetzen/Tidy an Schreibrecht gebunden. #125 Low: kein verzögerter Listener nach Unmount |
| WorkflowInfoCard, MaintenanceWindowBadge, workflowTriggerMeta, EditorStatusBanners | Readmodel, Polllebenszyklus und gemeinsame Callrefs | Keine zusätzliche belegte C/H/M-Autorisierungsgrenze; Serverdurchsetzung verfolgt |
| BreadcrumbSegment, FolderContentsBrowser, FolderPathBreadcrumb, WorkflowBreadcrumbs, WorkflowBrowser, WorkflowQuickSwitcher, SubWorkflowPreviewModal vollständig | Navigation, Foldermodell, Queryownership und gekapselte Preview | #109 Namesprojektion auch für Kind-Links. #129 Medium: serverseitiges Paging/Suche, lazy Popovers, begrenzter Recents-Batch, Scope vor Count/Sort |
| EditorHeader, editorHeaderTypes, LifecycleControls, RunControls, ClassicEditorHeader, CompactEditorHeader, WorkflowNameField, EditorIdentity, StandardMoreMenu, ToolsMenu | Gemeinsame Lifecycle-/Runmatrix und kontrollierte Callbacks | Lock-/Rolegates, Bestätigung/Cancel; Tidy/Restore beachten canWrite. Übrige Header-Unterdateien offen |
| PropertiesPanel, activityConfigMap, StepTestPanel vollständig | Configpatch, Expertenmodus, vorhandene Context-/Test-API | #134 offen: Kontextbereitschaft und späte Testantwort bei Knotenwechsel |
| Activities: AiAgentConfig, DynamicActivityConfig, RunScriptConfig, ReturnDataConfig, StartWorkflowConfig, ForEachConfig, DecisionConfig, DelayConfig, JunctionConfig, LogConfig | Spezialisierte Panels, gemeinsame ConfigProps, Unterworkflowvertrag | Kein weiterer abschließend bestätigter C/H/M-Befund; lokale AiAgent-Draftzustände bei Knotenwechsel noch nicht abschließend getestet |
| Activities: EmailConfig, RestApiConfig, FileOperationConfig, FolderOperationConfig, fsOperationShared, SqlConfig, JsonQueryConfig, XmlQueryConfig, structuredQueryShared vollständig; WmiQueryConfig überwiegend | Gemeinsame File-/StructuredQuery-Seams haben echte Verbraucher; Providerwechsel entfernt widersprüchliche Felder | Objekt-/Textkonfiguration und Runtimegrenzen gegengelesen. UI-Validierung nicht als Servergarantie gewertet; WMI-Randfälle und übrige Activities offen |

## Nachweise bereits behobener Befunde

- #121: fünf Return-Data-Regressionen zuerst rot, anschließend grün; Objektwerte bleiben erhalten,
  ungültiges JSON/Nichtobjekte abgewiesen. #125: Listener-nach-Unmount zuerst rot.
- #122: echter RightPanel-Einstieg mit zwei selektierten Knoten/canWrite=false zuerst rot.
  Lesendes Suchen bleibt, Ersetzen/Bulkmutation gesperrt. canWrite ist erforderlich, ohne
  permissiven Default. Erster gemeinsamer Designer-Fokus 69/69 grün.
- #109: Kindworkflow außerhalb des 500er Fensters zunächst nicht auflösbar, danach grün;
  neun Breadcrumb-Tests.
- #129: elf API-Fälle gegen zunächst cap-gebundene Endpunktimplementierung rot, dann 86/86
  Workflow-/RBAC-/Capabilities-Fälle grün. Weitere 17/17 Pagingfälle einschließlich echter
  PostgreSQL-/SQL-Server-Providerübersetzung aller neun Sortierungen in beiden Richtungen
  ohne DB-Verbindung. SQLite prüft echte Sortierung, Nullstatistik, Last-20-Fenster, Scope,
  spätere Seiten, Ordner/Suche vor Paging und maximal zehn IDs. Zwei Designer-Pagingfälle
  und ein QuickSwitcher-Fall zunächst rot; final 52/52 Browser/Breadcrumb/Switcher/FindReplace
  grün. Architekturagent: WorkflowsPage 93/93 und Demo 62/62 grün. TSC und fokussierter
  ESLint abschließend ohne Fehler/Warnung.

Teilweise überlappende Fokuszahlen werden nicht addiert. Keine echte PostgreSQL-/SQL-Server-
Verbindung und kein eigener Browser-/Computer-Use-Nachweis behauptet.

## Offen: #134 Schritt-Test-Kontext und Ergebniszuordnung (vorläufiger Medium-Kandidat)

StepTestPanel lädt Kontext asynchron (`step-test-context`, workflowId/stepId/pickedRunId),
sperrt Run jedoch weder während des Abrufs noch bei fehlendem Kontext. buildMocks liefert
dann undefined. Standardmodus setzt lastRun voraus und versteckt die Kontextvorschau.
WorkflowEditingController.TestStep reicht Mockwerte direkt weiter; Engine/StepTester baut
aus `mockVariables ?? []` seine Variablen. Kein Serverpfad lädt den angekündigten Kontext nach.

Daneben übernimmt eine späte POST-Antwort ungebunden setTestResult. PropertiesPanel rendert
denselben Paneltyp ohne workflowId/stepId-key; nach Knotenwechsel kann das alte Ergebnis beim
anderen Schritt erscheinen. Alltag: Ein Autor führt einen echten isolierten Schritt-Test mit
unerwarteten Eingaben aus oder hält die alte Antwort für die Prüfung des neuen Schritts.

Gegenargumente: API prüft Run/Edit-Lock, StepTester revalidiert den Autorisierungssnapshot.
**Kein belegter RBAC-Bypass.** Fehlender Kontext führt bei vielen Konfigurationen lediglich
zum sichtbaren Fehler. Relevant bleibt die falsche Testsemantik/-zuordnung bei ausführbaren
Aktivitäten. Root zur Gegenprüfung vorgelegt; wegen Pause ausstehend. Nicht als unabhängig
bestätigter Befund gezählt; Umsetzung auf Nutzerwunsch ausgesetzt.

Schmaler vorgeschlagener Seam: Panelidentität an workflowId/stepId binden; Kontextmodi bis
zur verwertbaren Antwort sperren, Fehler/Refresh anzeigen; alte Antworten nach Wechsel nicht
übernehmen. Deferred-GET-/POST-Regressionsfälle mit echtem Panel/PropertiesPanel erforderlich.
**Nicht implementiert oder getestet.**

## Offener Restscope

Keine abgeschlossene frische Prüfung aller übrigen Header-Unterdateien, Properties/shared/
panelChrome/Picker/Autocomplete, übrigen Activities/Triggerpanels, ActivityNode/AgentTeamNode/
Group/StickyNote, Edges, Library, Execution/Live/Timeline/Agent/Debug, ScriptEditorDialog und
weiterer Overlays/Dialoge. Frühere Runden/andere Prüfer ersetzen diesen eigenen R4-Nachweis
nicht. Nicht jede Testfixture zeilenweise gelesen. Vor einer Nullrunde müssen Restscope und
#134 fortgesetzt und anschließend eine weitere vollständige Projektrunde durchgeführt werden.
