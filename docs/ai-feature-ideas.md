# AI-Feature-Ideen

> **Status:** Ideen-Backlog für spätere Produktentscheidungen. Dieses Dokument ist keine
> verbindliche Umsetzungsspezifikation und legt weder Reihenfolge noch konkrete Interfaces fest.

NodePilot besitzt bereits Script- und Workflow-Generierung, den Workflow-Assistenten, den globalen
Wissens-Chat und die `llmQuery`-Activity. Der größte zusätzliche Nutzen entsteht deshalb durch
kontextuelle AI-Aktionen in bestehenden Arbeitsabläufen und nicht durch einen weiteren allgemeinen
Chat.

## Empfohlene erste Ausbaustufe

Die Ideen 1 und 2 bilden gemeinsam die empfohlene erste Ausbaustufe. Sie adressieren akute
Nutzerprobleme und können große Teile der vorhandenen Execution-Analyse, Secret-Redaktion,
Folder-RBAC sowie des sicheren Proposal-, Diff- und Apply-Flows wiederverwenden.

## 1. Execution Incident Copilot

**Priorität:** Sehr hoch — empfohlene erste Ausbaustufe

- **Nutzerproblem:** Bei einer fehlgeschlagenen Workflow Execution müssen Nutzer ErrorOutput,
  Activity-Verlauf und Workflow Definition heute selbst zusammenführen.
- **Vorgeschlagene Funktion:** Eine Aktion „Fehler mit KI analysieren“ erklärt die beobachteten
  Fehler, trennt Evidenz von vermuteten Ursachen und nennt konkrete nächste Prüfungen. Von dort kann
  der Nutzer den Workflow im Designer öffnen und eine Reparatur vorbereiten.
- **Geeignete Stelle:** Fehlgeschlagene Einträge in der Execution-Liste, der Operations-Ansicht und
  dem History-Tab des Designers.
- **Sicherheitsgrenzen:** Read-only Diagnose für Nutzer mit Folder-Leserecht; ausschließlich
  redigierte Execution- und Activity-Daten; keine automatische Änderung, kein Retry und kein
  Publish. Vermutungen müssen als solche gekennzeichnet und auf sichtbare Evidenz zurückgeführt
  werden.

## 2. KI-Reparatur für Lint und Publish

**Priorität:** Sehr hoch — empfohlene erste Ausbaustufe

- **Nutzerproblem:** Lint- und Pre-Publish-Findings zeigen ein Problem und dessen Ort, helfen aber
  nicht bei der konkreten Reparatur.
- **Vorgeschlagene Funktion:** „Mit KI beheben“ übergibt ausgewählte deterministische Findings an
  den Workflow-Assistenten. Dieser erzeugt einen strukturierten Änderungsvorschlag, den der Nutzer
  als Diff prüft und vollständig oder selektiv übernimmt.
- **Geeignete Stelle:** Lint-Panel und Pre-Publish-Checklist im Workflow-Designer.
- **Sicherheitsgrenzen:** Findings werden serverseitig bzw. deterministisch validiert und nicht
  allein dem Modell geglaubt. Übernehmen erfordert Admin-/Operator-Rechte und den eigenen
  Edit-Lock; Stale-Hash-Schutz, Secret-Merge und normaler Save-/Publish-Flow bleiben verbindlich.

## 3. Activity-Konfigurations-Copilot

**Priorität:** Hoch

- **Nutzerproblem:** Komplexe Activity-Konfigurationen wie Decision-Bäume, Cron-Ausdrücke, REST,
  SQL, JSONPath/XPath, Mappings und Retry-Regeln sind fehleranfällig und erfordern Detailwissen.
- **Vorgeschlagene Funktion:** „Mit KI konfigurieren“ übersetzt eine natürlichsprachliche Absicht
  in einen typisierten Konfigurationsentwurf und erlaubt die Übernahme einzelner Felder.
- **Geeignete Stelle:** Properties-Panel der jeweiligen Activity, beginnend mit den komplexesten
  Konfigurationen.
- **Sicherheitsgrenzen:** Der Entwurf muss durch die vorhandene deterministische Validierung und
  Vorschau laufen. Secret-Werte werden nie übertragen; die Activity wird nicht automatisch
  ausgeführt, und ein Step-Test bleibt eine separate bewusste Aktion.

## 4. Workflow Generator v2

**Priorität:** Hoch

- **Nutzerproblem:** Die bestehende Workflow-Generierung ist ein One-shot-Flow mit Statistik- und
  JSON-Vorschau; Unklarheiten lassen sich vor dem Erstellen nur durch einen neuen Versuch beheben.
- **Vorgeschlagene Funktion:** Ein geführter Entwurfsprozess stellt Rückfragen, zeigt eine echte
  Graph-Vorschau, führt Lint aus, markiert ungelöste Referenzen und erlaubt iterative
  Verfeinerungen.
- **Geeignete Stelle:** Bestehender Dialog „KI generieren“ auf der Workflow-Übersicht.
- **Sicherheitsgrenzen:** Der erzeugte Workflow bleibt deaktiviert. Folder-Schreibrechte werden vor
  dem Erstellen geprüft; Secret-Werte sind ausgeschlossen, und Lint-Fehler müssen vor Publish
  behoben werden.

## 5. Reliability- und Optimierungs-Review

**Priorität:** Mittel bis hoch

- **Nutzerproblem:** Coverage, Critical Path, Laufzeiten, Retries und Failure-Heatmap sind einzeln
  sichtbar, müssen für eine Optimierung aber manuell zusammengeführt werden.
- **Vorgeschlagene Funktion:** „Workflow optimieren“ identifiziert belegte Engpässe und schlägt
  Änderungen an Parallelisierung, Timeouts, Retry-Verhalten und Fehlerpfaden vor.
- **Geeignete Stelle:** Preset im Workflow-Assistenten sowie kontextuelle Aktion an auffälligen
  Workflows im Dashboard.
- **Sicherheitsgrenzen:** Jede Empfehlung zeigt Datenfenster, Stichprobengröße und zugrunde liegende
  Telemetrie. Kleine Stichproben werden kenntlich gemacht; Änderungen nutzen nur den vorhandenen
  Proposal-Flow und werden nie automatisch publiziert.

## 6. Custom-Activity-Copilot

**Priorität:** Mittel

- **Nutzerproblem:** Das Erstellen einer Custom Activity erfordert gleichzeitig PowerShell-,
  Contract- und NodePilot-Kenntnisse.
- **Vorgeschlagene Funktion:** Aus einer Beschreibung entsteht ein Draft mit Name, Key,
  Inputs/Outputs, PowerShell-Template, Timeout, Success Codes und vorgeschlagenen Testszenarien.
- **Geeignete Stelle:** Erstellungsdialog der Custom Activities.
- **Sicherheitsgrenzen:** Neue Definitionen bleiben deaktivierte Drafts. Nur Admins können sie
  aktivieren; Secrets werden als Referenzen statt als Werte verwendet, und weder Test noch
  Aktivierung werden automatisch gestartet.

## 7. AI-Testfall- und Mock-Generator

**Priorität:** Mittel

- **Nutzerproblem:** Aussagekräftige positive, negative und Grenzfall-Inputs für einen Workflow
  oder eine einzelne Activity zu erstellen ist zeitaufwendig.
- **Vorgeschlagene Funktion:** Aus Workflow Contract und Activity-Konfiguration entstehen
  Testfallentwürfe mit Inputs, Mock-Werten, erwarteten Pfaden und erwarteten Outputs. Ein Entwurf
  kann in Step-Test oder manuellen Execute-Dialog übernommen werden.
- **Geeignete Stelle:** Step-Test-Panel, manueller Execute-Dialog und optional ein Test-Preset im
  Workflow-Assistenten.
- **Sicherheitsgrenzen:** Generieren und Ausführen bleiben strikt getrennt, weil Activities reale
  Nebenwirkungen haben können. Historische Werte werden nur redigiert verwendet; destructive
  Activities erhalten eine deutliche Warnung und werden nie automatisch gestartet.

## 8. Kontextueller Operator-Copilot und Schichtbriefing

**Priorität:** Mittel

- **Nutzerproblem:** Der globale Wissens-Chat kann Betriebsfragen beantworten, kennt beim Wechsel
  aus einer Fachansicht aber nicht automatisch die gerade ausgewählten Workflows, Executions,
  Maschinen oder Alerts.
- **Vorgeschlagene Funktion:** „Diese Ansicht fragen“ öffnet einen kontextuellen Assistenten.
  Zusätzlich fasst ein on-demand Schichtbriefing neue Fehlschläge, gemeinsame Zielsysteme,
  gefährdete Schedules und aktuelle Alerts mit Links zur Evidenz zusammen.
- **Geeignete Stelle:** Dashboard, Operations, Machines und Alerts.
- **Sicherheitsgrenzen:** Read-only, Folder-RBAC-gescoped und nur mit explizit ausgewählten IDs und
  Filtern statt vollständigen Seitendumps. Korrelationen werden nicht als bewiesene Ursachen
  dargestellt; Mutationen bleiben außerhalb dieses Features.

## Gemeinsame Leitplanken

- AI bleibt opt-in und standardmäßig deaktiviert.
- Vorhandene Rollen-, Folder-RBAC-, Rate-Limit-, SSRF-, Audit- und Secret-Redaktionsregeln gelten
  unverändert.
- Deterministische Analyse und Validierung liefern Fakten; das Modell erklärt, priorisiert und
  formuliert Vorschläge.
- Schreibende Wirkung erfolgt ausschließlich über sichtbare, bestätigte Vorschläge und die
  bestehenden Edit-Lock-, Save- und Publish-Abläufe.
- Prompt- und Antwortinhalte werden nicht ungeprüft in Audit-Logs oder andere langlebige
  Speicher übernommen.
