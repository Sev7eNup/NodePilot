# Runde 6 – unabhängige UI-/Docs-/Designer-Gegenprüfung

2026-10-12, Branch `audit/practical-impact-2026-10-12`. Frische Rotation vom Client-/Betriebsscope
auf den aktuellen UI-Stand nach den R5-Korrekturen. Skills `improve-codebase-architecture` und
`audit-ai-slop-code` angewandt; CONTEXT, UI-CLAUDE und bestehende Auth-/Workflow-/Trigger-/
Settingsverträge zugrunde gelegt. Nutzerscope: Critical/High und Medium mit belegter Alltagseinwirkung.

## Tatsächliche risikobasierte Abdeckung

| Familie | Quellpfade und geprüfter Vertrag |
| --- | --- |
| Einstieg, Identität, globale Zustände | App-Routen, ProtectedRoute-/AdminOnly-Komposition, api/client und adminSettings, authBoundary sowie Store-Registrierungen: Cookie/CSRF, alte Ergebnisse vor Cache/Download verwerfen, Identitätswechsel leert Zustand. Serverseitige Authorize-/ResourceAuthorization-Seams bei Workflow und Custom Activities gegengeprüft; UI-Rollen sind keine Sicherheitsautorität. |
| Workflowliste, Import/Export und Ordner | WorkflowsPage Request-/Mutationseams, API-Paging, Importschleife und SCOrch-Dateilesen: Authgeneration vor und nach Dateilesen, Folderziel serverseitig, Page-/Sort-/Filterkeys, begrenzte Auswahl und Präfixinvalidierung. |
| Designer-Speichern und Ausführen | useWorkflowPersistence, useWorkflowExecution, WorkflowEditorPage-Aufrufer und workflowDefinitionSanitizer: immutable Savesnapshots, Revisionsfolge, Publishzuordnung, Savefehler stoppt Run, Runtimeprojektion bleibt aus Persistenz. |
| Designer-Knoten, Kanten, Eigenschaften und Overlays | useCanvasConnect, EditorRightPanel, PropertiesPanel, activityConfigMap und QuickEditPopup: deaktivierte Kantenhälften, ausgewählte Kante, canWrite-Durchleitung, Objekt-Roundtrip von returnData und Listenercleanup. StepTestPanel: Workflow/Step-Key, aktuelle ConfigOverride, Context-Laden/Fehler verhindert verfrühten Test. |
| Liveausführung, Historie, Dashboard, Mobile und Operations | useLiveOpsFeed, SignalR-/Store-Registrierungsseams, useWorkflowExecution und MobileWorkflowView; Executions-/Operations-/Dashboard-Requestkeys und Navigation: Feedgeneration/Dispose, serverseitige Such-/Statusfilter, terminale Historie und laufende Workflowansicht unterschieden. |
| Maschinen, Benutzer, Globals und Credentials | Seiten-/Mutationfamilien und Typen: gemeinsame API, Cacheinvalidierung, Seiten-/Pickervertrag; GlobalVariables-Save erhält unangetastetes Secret über null. Kein Klartext aus dem Maskierungswert rekonstruiert. |
| Custom Activities, Alerting und Maintenance | CustomActivitiesPage Form/Saveseam erhält Limits, Input-/Outputschema und CAS-Token. SystemPolicyEditor übernimmt Scope, Occurrencebudget und Routenbedingungen; Namenspicker verwenden vollständigen Namensvertrag. |
| Adminsettings, Backup, DB, Audit und Metrics | SectionFormHelpers, adminSettings, BackupTab/RestoreTab, TableGrid, AuditLogPage und observability: eigener ETag-/Draftbesitzer, Preview-/Restoreparameter, Adminserverseam, filtergebundene Cursorfolge, erlaubte Linkprotokolle. Keine Behauptung aller Feldrenderer gelesen zu haben. |
| AI/Agents und Streaming | useAiScriptStream-/API-/Store-Seams, Agentauthoring-Komposition: aktuelle Scriptweitergabe nur mit Zustimmung, gecappte Metadaten, Abbruch-/Identitätsbesitz und gemeinsame API. Prompt-/Modellinhalte begründen keine zusätzliche Berechtigung. |
| Docs und Website | DocMarkdown, DocPage, content, router und HTML-/Link-Sinks: Build-time-Quellen statt ungeprüfter Laufzeit-HTML-Injektion, sprach-/pfadgebundene Navigation und ToC, bekannte Slugs, Markdown ohne raw-HTML-Plugin. Generierte Medien und fremde image-manifest.json unberührt. |
| Demo und Verträge | demo/net/install, Produkt-/Demo-Entrypointvertrag sowie tatsächliche Requestformen; Core PagedResponse und WorkflowContractDefinition gegen UI-Consumer: Total/Items, nullable Default, Inputs/Outputs. Demo antwortet auf unbekannte Mutationen mit 501 und bleibt browserlokal. |

## Ergebnis und Gegenargumente

Kein neuer konkret belegter Critical/High oder praktischer Medium in dieser Gegenprüfung.
Die vorhandenen tiefen Module api/client, authBoundary, useSectionEditor und
useWorkflowPersistence bestehen den Deletion-Test: Entfernen würde Identitäts-, ETag- oder
Revisionswissen auf zahlreiche Aufrufer verteilen. Keine neue Abstraktion oder kosmetische
Zusammenführung gerechtfertigt.

Nicht hochgestuft: reine lokale Anzeige-/Formatierungsrandfälle, absichtliche Adminwerkzeuge,
auf Buildquellen begrenzte Website-HTML-Sinks, Fehlermeldungen bei tatsächlich nicht erreichbarem
Backend und theoretische Mehrfachnavigation während eines bereits laufenden Dialogs ohne
belegte gewöhnliche Nutzungsauswirkung. Insbesondere ist ein UI-canWrite-Defekt alleine kein
Nachweis einer serverseitigen RBAC-Umgehung.

Keine neuen Tests/Builds gestartet, da kein neuer Fix entstanden ist. Diese Runde ist eine
unabhängige risikobasierte Quellenprüfung über alle Familien, keine vollständige Neu-Lektüre
jeder JSX-Zeile, Sprachdatei, Testfixture oder Medienressource. Keine Browser-/Livebackend-
Abnahme behauptet. Ein separater finaler Integrationsdurchgang folgt nach dem letzten Backendfix.
