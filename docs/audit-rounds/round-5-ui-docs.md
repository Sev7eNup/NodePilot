# Runde 5 – SPA, Designer und Doku-Website

Basis: `de71058`, Wiederaufnahme am 12. Oktober 2026. Beide Skills angewandt;
`CLAUDE.md`, UI-`CLAUDE.md`, `CONTEXT.md`, Threatmodell und ADR-Index gegengelesen.
**R5-Quellenprüfung abgeschlossen; drei bestätigte Medium-Findings behoben.
Die unabhängige Schlussrunde steht noch aus.**

## Fortsetzung des offenen Designer-Bereichs

### #134 – Schritttests warten nicht auf ihre Eingaben und behalten fremde Ergebnisse

**Medium, tägliche Nutzung.** Standardmodus des `StepTestPanel` lädt den Kontext
des letzten Laufs im Hintergrund, bietet aber sofort „Activity testen“ an.
`buildMocks` liefert ohne Antwort `undefined`; der Backendpfad übernimmt diese
Werte direkt und lädt den angekündigten Kontext nicht nach. Ein normaler Klick
kurz nach Auswahl des Schritts kann daher einen echten Test ohne die angezeigte
Testsemantik starten. Beispiel: Ein Skript erwartet den Servernamen aus dem
vorherigen Schritt und wird stattdessen mit leerer Eingabe getestet.

Das gleiche Panel blieb beim Wechsel von Workflow/Schritt montiert. Eine alte
POST-Antwort konnte deshalb beim neu gewählten Schritt als dessen Testergebnis
erscheinen. Die Run-/Edit-Lock-Prüfungen auf dem Server bleiben wirksam; das ist
keine belegte Sicherheitslücke oder Rechteausweitung.

Gegenprüfung: `WorkflowEditingController.TestStep` und `StepTester` reichen die
Mocks weiter; ein serverseitiges Nachladen findet nicht statt. Drei neue
Panel-Regressionen mit kontrolliert verzögerten Antworten schlugen vor der
Korrektur fehl. Keine künstlichen Sleeps oder echten Activity-Ausführungen.

Korrektur: Der Modul-Einstieg bindet den Panelzustand an Workflow und Activity Node.
Kontextmodi sperren Ausführung bis zur erfolgreich geladenen Antwort und während
eines Refreshs. Ladefehler sind im Standardmodus sichtbar und wiederholbar.
Empty-/Manual-Modus bleiben nutzbar, schema-only-Kontext ohne frühere Ausführung
bleibt zulässig. Diese Verantwortung bleibt lokal im Schritttest-Modul; die
Aufrufer müssen keinen zusätzlichen Lifecycle-Vertrag beachten.

Nachweis: `StepTestPanel.test.tsx` enthält nun 13 Fälle einschließlich verzögertem
Standardkontext, ausgewähltem Lauf, Retry nach Ladefehler und Identitätswechsel.
Zusammen mit `PropertiesPanel.test.tsx` bestehen 41 Tests.

### #137 – Konfigurationsfelder behalten Werte des vorher ausgewählten Agenten

**Medium, tägliche Nutzung.** `PropertiesPanel` verwendete dieselbe
Konfigurationskomponente für verschiedene Nodes desselben Activitytyps.
`Lines` und `JsonSchemaField` besitzen lokale Eingaben; ein normaler Wechsel
zwischen zwei AI-Agenten mit gleicher interner Agent-ID zeigte deshalb noch
Pfade/Schema des ersten. Die nächste Bearbeitung schrieb sie in den zweiten Node.
Ein Regressionstest über das echte Panel war rot. Der Konfigurationseinstieg
verwendet jetzt Workflow-/Node-ID als React-Key. 41/41 kombinierte Paneltests grün;
unabhängige Quellgegenprüfung durch API-Reviewer.

### #139 – Verbindungseditor und tatsächliche Bedingung widersprechen sich

**Medium, tägliche Nutzung.** `EditorRightPanel` hielt das `EdgePropertiesPanel`
beim Kantenwechsel montiert. Dessen Modus war nur einmal aus der anfänglichen
Kante abgeleitet. Bei simple → expression blieb das einfache Feld sichtbar;
`setCondition` ließ den unsichtbaren Ausdruck stehen, den
`ConditionEvaluator.EvaluateEdgeCondition` vorrangig auswertet.
Der Workflow-/Edge-Key behebt diese falsche Zuordnung an der vorhandenen
Panelgrenze. Echter Wechsel über `EditorRightPanel` erst rot, danach 29/29
Edge-/Berechtigungs-/ConditionBuilder-Tests grün. Keine Änderung des
Auswertungsvertrags. API-Reviewer hat den Quellpfad unabhängig bestätigt.

## Risikobasierte Quellenabdeckung

Inventar und Suche über SPA/Designer, Doku-Website und Demo; vertiefte Prüfung der
Benutzereingaben und Mutationen statt einer behaupteten Prüfung jeder Renderzeile:

- API-Client/CSRF, Auth-Generation, Cache-/Store-Löschung und Cross-Tab-Transport;
  Rollenrouten und serverseitige Capabilities als UI-Vertrag.
- Designer-Speichern/Autosave/Publish/Lock/Run, Nodes/Edges und Eigenschaften,
  Script-Editor, Schritttest, Agent-Konfiguration und Team-Draft-Übernahme.
- Ausführungshistorie, Output/Watch/Debug-Variablen, Live-/SignalR-Queryzuordnung,
  Dashboard-Zeitfenster und App-/Support-Log-Darstellung.
- Workflow-/Ordner-Bulkaktionen, Backup-Client, ETag-Einstellungsclient,
  Browserpersistenz und externe Observability-Links.
- Dokumentationsrouter und `DocMarkdown`: Markdown-Rendering ohne Raw-HTML-Plugin,
  Linkauflösung und statisch kontrollierte HTML-Sinks der Website. Demo-Isolation
  und Import-/Routeninventar gegengeprüft; keine neue Browser-Demo-End-to-End-Runde.

Zusätzlich gezielte Core-/DTO-Verträge: Workflow/Version/Capabilities, Ausführungs-
und Debugdaten, StepTest, Credential-/Settings-Secrets, AuthSession/OIDC-Ticket,
ResourceAuthorization und Subworkflow-Identität. Implementierungsprüfungen der
zugehörigen Serverpfade stehen im API- und Runtime-Report.

Verifikation: fokussierter ESLint für sechs geänderte TS/TSX-Dateien ohne Befund.
Abschließender TypeScript-App-Typecheck ebenfalls grün.
Logs: `.runlogs/audit-20261012-{step-test,agent-panel,edge-mode}-red.log`,
`panels-green`, `edge-mode-green`, `ui-lint`, `ui-types-final` mit gleichem Präfix.
Das ist Quell-/Regressionsevidenz, kein Live-CM1- oder UI-Browsernachweis.

## Zurückgestellt gemäß neuem Auftrag

Keine Änderungen für kosmetische Strukturfragen oder hypothetische Fälle wie
gesperrtes Browser-LocalStorage, beschädigte lokale Preferences oder nur beim
Schließen mitten im Drag verbleibende temporäre Listener. Solche Beobachtungen
werden nicht zu Medium hochgestuft, um zusätzliche Korrekturen zu rechtfertigen.
