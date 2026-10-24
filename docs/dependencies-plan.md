# Abhängigkeitsmenü — Feature- und Umsetzungsplan

Stand: 2026-10-23. Status: **Auf Nutzerfreigabe im Branch `feat/dependencies-menu` umgesetzt.**
Bedienung und Grenzen: [Abhängigkeiten](dependencies.md). Der Plan dokumentiert den vereinbarten Umfang.

Das visuelle Konzept (Desktop-Entwurf v1, Bildvergleiche) liegt lokal unter `design/dependencies/` und ist nicht Teil des Repos. Das Bild enthält ausschließlich Beispieldaten. Die fachlichen Regeln und Texte dieses Plans sind verbindlicher als generierter Bildtext.

## 1. Ziel und erste Version

Ein Administrator soll vor einer Änderung beantworten können: **Was verwendet dieses Objekt? Was verwendet es selbst? Wo genau ist die Beziehung hinterlegt?** Jede angezeigte Beziehung erhält eine verständliche Erklärung und eine überprüfbare Fundstelle.

Neuer Menüpunkt **Abhängigkeiten**, auf Nutzerwunsch Gruppe **Monitoring**, unmittelbar unter der Gruppenüberschrift. Route `/dependencies`. Die Seite ist eine lesende Analyse gespeicherter Konfiguration. Sie startet weder Workflows noch Verbindungsprüfungen und verändert keine Objekte. Der Typfilter für globale Variablen heißt **Globals**.

V1 umfasst Workflows, registrierte Maschinen, Zugangsdaten, globale Variablen und Custom Activities; direkte Beziehungen, mittelbare Verwendung, Suche, Übersicht, fokussierte Karte und Fundstellen. Betriebsstatus eines Workflows ist Zusatzinformation. Erreichbarkeit einer Maschine und aktuell laufende Ausführungen werden nicht zur Erklärung statischer Beziehungen herangezogen.

Die Ansicht verspricht **potenziell betroffene Automatisierungen**, keine Ausfallvorhersage: Bedingungen, Eingaben und Fehlerbehandlung entscheiden erst beim Ausführen, welche Schritte tatsächlich laufen.

## 2. Zentrale Nutzerwege

1. **Maschine ändern:** `FS-PROD-01` suchen → vier direkt verwendende Workflows sehen → zwei mittelbare Aufrufer aufklappen → genaue Schritte vor der Änderung prüfen.
2. **Zugangsdaten rotieren:** Zugangsdaten wählen → explizite Verwendungen und wirksam geerbte Maschinenzugangsdaten getrennt erkennen → betroffene Workflows öffnen. Ein Schritt mit eigenen Zugangsdaten darf nicht fälschlich als Nutzer der Maschinenvorgabe erscheinen.
3. **Unterworkflow umbauen:** Workflow wählen → „Wird verwendet von“ → direkte Aufrufer und weitere aufrufende Workflows samt Aufrufschritten sehen.
4. **Variable umbenennen:** Variable wählen → Verwendungen in Workflow-Konfiguration und Custom-Activity-Vorlagen samt Feld/Fundstelle sehen. Werte, insbesondere geheime Werte, sind für die Analyse nicht erforderlich.
5. **Unklare Referenz untersuchen:** Workflow wählen → „Verwendet“ → nicht zuordenbare oder dynamische Ziele sehen → zur Konfiguration des verursachenden Schritts springen.

Direkte Einstiege „Abhängigkeiten anzeigen“ kommen auf die bestehenden Objektflächen. Für Zugangsdaten liegen diese derzeit in den Einstellungen; ein eigener Zugangsdaten-Menüpunkt ist keine Voraussetzung.

## 3. Seitenaufbau und Interaktion

### Einstieg ohne Auswahl

Titel „Abhängigkeiten“ in der bestehenden Topbar, Unterzeile „Verstehen, was zusammenhängt.“, breite Suche mit Typfilter. Darunter eine paginierte Objektliste mit Name, Typ und vorhandenem Ordnerpfad/Hostnamen zur Unterscheidung. Keine erfundenen Ordner für Maschinen; „Produktion / Dateiserver“ im Bild ist ein illustrativer Kontext, in der Umsetzung kommen dort vorhandene Tags und Hostname hin.

Suche nach Anzeigename, Workflow-Ordner, Hostname und Variablenname; IDs sind ein zusätzlicher Suchweg, nicht die Hauptbeschriftung. Leere Suche liefert die erste alphabetische Seite, keine neue flächendeckende Bestandsstatistik. Aktuellen Typfilter beibehalten; kein stilles Auswählen des ersten Treffers.

### Auswahl und Übersicht

- Kompakter Kopf mit Typicon, vollständigem Objektnamen und Kontext. Erläuterung in einem Satz; Beispiel: „4 Workflows verwenden diese Maschine direkt. 2 weitere rufen diese Workflows auf.“
- Datenbasis sichtbar: **Gespeicherter Stand · Nur sichtbare Objekte · Stand 14:32**. Zeit aus der Antwort, lokal formatiert.
- Richtungswahl: **Wird verwendet von** / **Verwendet**. Für Ressourcen standardmäßig die erste, für Workflows die zweite Richtung.
- Darstellungswahl: **Übersicht** / **Karte**. Übersicht ist der Standard. Beide teilen Auswahl, Richtung und Filter.
- Hauptbereich: gegliederte Zeilen mit Objektname, kurzer Beziehungserklärung, Fundstellenanzahl und Details-Aktion. Zuerst direkte, dann mittelbare Beziehungen; innerhalb jeder Gruppe stabil nach Name und ID sortieren.
- Rechte Detailspalte: **Warum betroffen?**, erklärender Satz, eine gut lesbare Beziehungskette, konkrete Fundstellen und **Schritt im Designer öffnen** beziehungsweise **Objekt öffnen**.

Eine Zeile repräsentiert ein Objekt, keine einzelne Verwendung. Mehrere Schritte ergeben beispielsweise „3 Fundstellen“ unter demselben Workflow. Ein Workflow, der direkt und mittelbar verwendet, steht einmal in der direkten Gruppe; zusätzliche Wege sind in den Details sichtbar. Summen zählen eindeutige Objekte, nicht Pfade oder Schritte.

Für andere Beziehungen passen sich Gruppentitel an: „Direkt verwendet“, „Über andere Workflows“, „Über Custom Activities“, „Als Standard hinterlegt“. Keine interne Fachsprache wie Inbound, Outbound, Blast Radius oder Transitivität in der Oberfläche.

### Fokussierte Karte

Die Karte öffnet immer um das ausgewählte Objekt. Links stehen Nutzer/Aufrufer, in der Mitte die Auswahl, rechts die verwendeten Ressourcen; die aktive Richtung bestimmt, welche Seite zunächst gefüllt ist. Pfeile zeigen konsistent **vom verwendenden Objekt zum verwendeten Objekt**, beschriftet mit „ruft auf“, „verwendet“, „nutzt Variable“ oder „kann aufrufen“.

Initial eine Beziehungsebene, maximal 40 Objektknoten. Weitere Gruppen haben einen expliziten „Weitere anzeigen“-Knoten. Stabile Spaltenanordnung statt physikalisch animierter Verteilung; vorhandenes React Flow und vorhandenes Layout-Werkzeug verwenden. Keine neue Graphbibliothek erforderlich. Klick auf eine Beziehung öffnet dieselbe Detailspalte wie die Übersicht. „Als Mittelpunkt öffnen“ ist eine separate Aktion; ein normaler Klick darf nicht plötzlich die ganze Karte umbauen.

Vollständige Namen durch mehrzeilige Knoten, reduzierte Zusatzdaten, wenig kreuzende Linien. Eine markierte Kette wird hervorgehoben, andere bleiben lesbar. Kein automatisches Wegzoomen, bis Namen unlesbar werden. Bei Platzmangel Gruppen einklappen und die Übersicht anbieten. Zoom- und Zentrierungsschaltflächen haben Textlabels/zugängliche Namen.

## 4. Erfasste Beziehungen und ihre Grenzen

| Quelle → Ziel | Erkennung und Darstellung |
|---|---|
| Workflow → Workflow | `startWorkflow.config.workflowNameOrId`, `forEach.config.childWorkflowNameOrId`; ID oder Name, mit Node-ID und Fundstelle. „Ruft auf“. |
| Workflow → Maschine | Target-Bindings der unterstützten Remote Activities und Agenten. Registrierte IDs sicher zuordnen; eindeutige statische Namen/Hostnamen entsprechend der tatsächlichen Laufzeitauflösung. |
| Workflow → Zugangsdaten | Explizite Bindings sowie wirksame Vererbung über Maschine bzw. Agentenkontext. Herkunft „direkt“ oder „über Maschinenvorgabe“ zeigen. |
| Maschine → Zugangsdaten | `DefaultCredentialId`: „Als Standard hinterlegt“. Das allein bedeutet nicht, dass jeder Workflow auf dieser Maschine diese Zugangsdaten verwendet. |
| Workflow → globale Variable | Erkannte `{{globals.NAME}}`-Ausdrücke in tatsächlich ausgewerteten Konfigurationsfeldern, Targets, Credentials und Bedingungen; Parserregeln an der Laufzeit ausrichten. Keine Volltextsuche über beliebige JSON-Felder. |
| Workflow → Custom Activity | `config.__customDefinitionId` ist maßgeblich; Typ/Key gegenprüfen. Fehlende ID nicht durch einen geratenen Key-Treffer ersetzen. |
| Custom Activity → globale Variable | Unterstützte Template-Ausdrücke im Skripttemplate und in Eingabe-Defaults. Fundstelle mit Vorlagen-/Parameterbezeichnung; daraus mittelbare Workflow-Verwendung ableiten. |
| Agent im Workflow → erlaubter Workflow | `workflow_run.workflowIds`: **„Kann aufrufen“**, gesonderte Gruppe möglicher Aufrufe. Eine Erlaubnis ist kein fest konfigurierter Aufruf und fließt nicht in die direkten Standardzähler ein. |

**Abdeckung explizit machen:** Skills und MCP-Server als eigene Ressourcen, externe API-/SQL-Endpunkte, beliebige Skriptzugriffe und aus Laufzeitdaten konstruierte Referenzen sind keine vollständig analysierten V1-Ressourcen. Bei entsprechenden Konfigurationen trägt der Bericht einen Abdeckungshinweis. Der Scope ist immer sichtbar; aus einem leeren Ergebnis folgt nie „sicher löschbar“.

**Status:** `resolved` → „Zugeordnet“, `dynamic` → „Wird beim Ausführen bestimmt“, `unresolved` → „Nicht zuordenbar“, `ambiguous` → „Nicht eindeutig“, `invalid` → „Konfiguration nicht auswertbar“. Aktiv/deaktiviert ist eine separate Dimension. Ein dynamisches Ziel kann trotzdem eine sicher erkennbare Beziehung zu einer globalen Variable besitzen; daraus wird keine sichere Maschinenbeziehung abgeleitet. Variablenwerte werden dazu nicht ausgewertet.

Bei Auflösung außerhalb des sichtbaren Bereichs bleibt die Meldung neutral: „Nicht zuordenbar“. Keine Behauptung „gelöscht“, solange fehlende Berechtigung dieselbe Ursache sein könnte. Mehrdeutigkeit nur mit zulässigen Informationen erläutern.

**Maschinen-Sonderfälle:** Freie Hostnamen sind im heutigen `MachineResolver` zulässig; `localhost`/leeres Target können lokal ausführen. Diese Fälle sind keine pauschal fehlenden Maschinen. Zeigen als „Freier Hostname“ bzw. „Lokal auf dem NodePilot-Server“, ohne künstliches Managed-Machine-Objekt. Bei mehrdeutiger Namens-/Hostnamezuordnung keine willkürliche Maschine als gesichert darstellen. Diese bekannte Laufzeitgrenze mit Tests absichern.

**Gespeicherte Konfiguration:** Einbeziehen auch deaktivierte Workflows/Schritte, deren Zustand kenntlich machen. Nicht behaupten, dass ein konfigurierter Schritt erreicht wird. Nicht wirksame Bindings, etwa Target an einer lokal definierten Custom Activity, als Konfiguration kennzeichnen und nicht als sichere Remote-Verwendung zählen. Ungespeicherte Designer-Änderungen, frühere Workflow-Versionen und die Definition bereits laufender Ausführungen gehören nicht zu diesem Stand.

## 5. Lesbarkeit, Barrierefreiheit und Zustände

Bestehende NodePilot-Skins, IBM Plex Sans und Carbon-Icons weiterverwenden. Nach Nutzerfeedback zur Stilkonsistenz: gemeinsamer Topbar-Titel; Untertitel, Abschnitte und Objektnamen 14/20 px, ausgewähltes Objekt 16/24 px, Erklärung 14/20 px, Metadaten 12/16 px; Zeilen mindestens 64 px, Abstandsraster 4 px. Karten, Buttons und Segmente nutzen die vorhandenen `np-card`-, `np-btn`- und `np-segmented`-Stile. IBM Plex Mono nur für IDs/technische Feldpfade in aufgeklappten Details.

Blau zeigt Auswahl und Aktion. Amber signalisiert eine echte Unklarheit; Rot nur belegte ungültige Konfiguration. Typen über Icon **und** Text unterscheiden; Status nie nur über Farbe. Keine dekorativen Risikozahlen. Lange Namen umbrechen, wichtige Informationen nicht ausschließlich im Hover-Tooltip anbieten. Kontrastziel mindestens 4,5:1 für normale Texte, 3:1 für große Texte und relevante grafische Bedienelemente; bei der späteren Implementierung messen.

| Zustand | Verhalten |
|---|---|
| Lädt | Ruhige Skeleton-Zeilen; beim Aktualisieren bisherige Daten mit Ladehinweis behalten. |
| Keine Auswahl | Objektliste mit dem Hinweis „Objekt auswählen, um seine Beziehungen zu sehen.“ |
| Keine Treffer | „Keine passenden Objekte gefunden.“ + Filter zurücksetzen. |
| Keine Beziehungen | „Keine statisch erkannten Beziehungen im sichtbaren Bereich.“ Scope und Analysegrenzen bleiben sichtbar. |
| Dynamisch/teilweise ausgewertet | Eigenen Hinweis und Fundstelle zeigen; nie als null Abhängigkeiten kaschieren. |
| Fehler/DB nicht erreichbar | Bestehende Fehlerbehandlung verwenden, veralteten Stand sichtbar markieren, „Erneut versuchen“. Kein leeres Erfolgsergebnis. |
| Objekt inzwischen geändert | Nach Refetch neue Erklärung; geänderte/entfallene Auswahl sachlich melden. |
| Objekt nicht mehr zugänglich | Auswahl und geschützte Daten entfernen; neutrale Meldung. |
| Begrenzte Analyse | „Analyse begrenzt“ mit Grund/erreichter Tiefe; unvollständige Summen mit „mindestens“. |

Ab etwa 1280 px Viewport Übersicht plus 340–400 px Detailspalte, bei breiter geöffneter Sidebar nach verfügbarer Inhaltsbreite entscheiden. Darunter Details unter der gewählten Zeile bzw. als vorhandenes zugängliches Panel; keine dritte schmale Spalte. Unter 768 px einspaltige Übersicht, Suchfilter umbrechen, mindestens 44 px große Touchziele. Die Karte ist eine optionale Zusatzansicht, jede ihrer Aussagen muss auch ohne Grafik erreichbar sein.

Tastatur: Suchfeld, Typfilter, Richtung, Ansicht, Zeilenaktionen, Fundstellen; sichtbarer Fokus, Escape schließt Details und gibt den Fokus zurück. Ergebnisanzahl nach Suche höflich ankündigen. Bewegung auf kurze Übergänge beschränken, `prefers-reduced-motion` beachten. DE und EN werden gemeinsam geliefert.

## 6. Fachliche Berechnung und Berechtigungen

Knotenidentität ist `(kind, id)`, niemals Anzeigename. Eine Beziehung enthält Quelle, Ziel oder Auflösungsstatus, Beziehungstyp und mindestens eine Fundstelle mit Workflow-ID, Node-ID, Feldpfad und Quellrevision. Generierte Erklärungen entstehen aus lokalisierten Satzvorlagen; kein LLM erforderlich. Ungefilterte Konfigurationswerte und Skripte gehören nicht in diese Antwort.

Die Analyse arbeitet auf einem Request-Snapshot. Definition-lokale Fundstellen werden pro Quellrevision gecacht; Namen, Maschinenvorgaben, Aktivierungszustände, Custom-Activity-Versionen und Berechtigungen zum aktuellen Bericht auflösen. **Keinen fertig aufgelösten globalen Graphen ohne Revision und Berechtigungsscope cachen.**

Mittelbare Verwendung per zyklensicherer Traversierung. Nur fachlich zulässige Pfadkombinationen: Ein blindes `Workflow → Maschine → DefaultCredential` wäre bei einem Credential-Override falsch. Wirksame Credential-Verwendung wird pro Schritt/Agent ermittelt und mit dem Vererbungsweg belegt. Ebenso erlaubte Agent-Aufrufe getrennt von festen Aufrufen behandeln. Kürzesten erklärenden Pfad anzeigen, alternative Pfade aufklappbar; Startobjekt in Zyklen nicht als eigener Verbraucher zählen.

V1-Budgets als zu validierende Vorgaben: höchstens 12 Beziehungsebenen und 20.000 besuchte Beziehungen pro Bericht, höchstens 3 erklärende Pfade pro Treffer. Kürzung von Belegpfaden verändert keine bereits vollständig berechnete Objektanzahl. Abbruch der eigentlichen Traversierung bedeutet dagegen `complete=false` und explizit unvollständige Zähler.

Alle Rollen dürfen die Seite öffnen, ausschließlich mit den bisherigen Leserechten: Viewer sehen beispielsweise keine Zugangsdaten-/Globalvariablenobjekte, wenn die bestehenden Endpunkte diese verbieten. Workflow-Folder-RBAC gilt vor Ausgabe, Aggregation und Pfadanzeige. Auch Suchtreffer, Trefferzahlen, Fehlermeldungen, Pfade und Exporte/Clients dürfen keine fremden Objekte verraten. Keine „3 versteckte Workflows“-Platzhalter.

Namensauflösung muss die wirkliche Runtime-Zielwahl berücksichtigen und anschließend Sichtbarkeit anwenden: Ein unsichtbarer gleichnamiger Workflow darf nicht dazu führen, dass die UI den falschen sichtbaren Treffer als sichere Zielwahl behauptet. Eine intern nicht offenlegbare Auflösung wird neutral nicht zuordenbar. Mittelbare sichtbare Ergebnisse werden nur über vollständig sichtbare Wege erklärt. Allgemeiner Scope-Hinweis für alle Nutzer, keine datenabhängigen Hinweise auf versteckte Objekte.

## 7. Technischer Ansatz im vorhandenen Repository

| Vorhandene Grundlage | Geplante Verwendung / notwendige Ergänzung |
|---|---|
| `Core/WorkflowDefinitions/WorkflowDefinitionDocument.cs` | Gemeinsames Parsing, Node-IDs und Activity-Typen; kein zweiter vollständiger Workflow-Parser. |
| `Core/WorkflowDefinitions/WorkflowDependency.cs` | Bekannte Resource-Slots einschließlich Agent-Bindings; für die Analyse um reine, nicht mutierende Fundstellenbeschreibung erweitern/ergänzen. Export-DTO und Ersetzungs-Callbacks nicht zum UI-Vertrag machen. |
| `Core/WorkflowDefinitions/WorkflowDefinitionFacts.cs` | Definition-lokale Fakten um Referenzfundstellen und Analysezustand erweitern. Heute fehlen u. a. Feld-/Node-Belege, Variablen und Fehlersignal. |
| `Api/Services/WorkflowDefinitionFactsCache.cs` | Bestehenden revisionsgebundenen Cache nutzen; fehlende/ungültige Definitionen für diese Seite nicht mit erfolgreich leeren Fakten gleichsetzen. |
| `Core/Operations/WorkflowCallGraphBuilder.cs` | Aufrufarten als Grundlage, aber Aggregation erst nach Belegextraktion. Bestehender Builder verliert Node-Fundstellen. |
| `Data/WorkflowNameResolver.cs` | Semantik übernehmen: exakte Schreibweise gewinnt, danach eindeutiger Vergleich ohne Beachtung der Großschreibung. Der aktuelle CallGraphBuilder macht nur den zweiten Vergleich; diese Abweichung vor Wiederverwendung durch gemeinsame reine Auflösungsregeln beheben. |
| `Engine/Execution/MachineResolver.cs`, `Activities/ActivityTargetResolver.cs`, `Agents/AgentTarget.cs` | Maßgeblich für Target-/Credential-Semantik. Statische Analyse benötigt Metadaten, keine Verbindungsaufnahme, DNS-Prüfung oder Secret-Entschlüsselung. |
| `Engine/Activities/CustomActivityExecutor.cs` | Maßgeblich für ID/Key, lokale/remote Ausführung und unterstützte Global-Templates inklusive Defaults. |
| `ui/src/lib/variableUsageScan.ts` | Kein geeigneter Global-Scanner: filtert `globals` absichtlich heraus und analysiert Datenfluss zwischen Steps. Nicht als Ressourcenanalyse zweckentfremden. |
| `ui/src/lib/navigation.ts`, `App.tsx` | Navigation, Breadcrumb und lazy Page-Route ergänzen. |

Geplante Modulgrenzen: reine Referenzextraktion/Beziehungstypen/Traversal in `NodePilot.Core`; auf Metadaten begrenzte Auflösung in `NodePilot.Data` soweit DB-Abfragen nötig sind; nutzerbezogene Orchestrierung, Sichtbarkeit und DTO-Projektion in `NodePilot.Api`. Bestehende Projektrichtung beibehalten. Einfache Klassen statt Interface pro Klasse. Keine Graphdatenbank und zunächst keine neue persistente Index-Tabelle.

Frontend: `DependenciesPage`, `DependencySearch`, `DependencyOverview`, `DependencyDetails`, `DependencyMap`; Datenzugriffe unter `src/api/dependencies.ts`, Serverzustand in React Query. Auswahl, Richtung, Darstellung und Filter in URL-Parametern, zum Beispiel `/dependencies?kind=machine&id=…&direction=usedBy&view=overview`; keine Objektinhalte oder Geheimnisse in URL/localStorage.

Fundstellen-Deep-Link zum Designer gezielt ergänzen, falls nicht vorhanden: Workflow öffnen, Node nach dem Laden selektieren und zentrieren, ohne Edit-Lock anzufordern. Bei abweichender Revision den aktuellen Stand und gegebenenfalls „Schritt nicht mehr vorhanden“ anzeigen. Vorhandenen Schutz ungespeicherter Änderungen erhalten.

### Vorgeschlagene API und Client-Parität

| Endpoint | Vertrag |
|---|---|
| `GET /api/dependencies/objects?q=&kind=&cursor=&limit=50` | Autorisierte Objektmetadaten, maximal 100 je Seite, stabiler Cursor. Keine Definitions-JSONs oder Secret-Werte. |
| `GET /api/dependencies/{kind}/{id}?direction=uses\|usedBy&cursor=&limit=50` | Auswahl, Scope, Datenstand, eindeutige direkte/mittelbare Zähler, paginierte Beziehungen, begrenzter Kartenausschnitt und Abdeckung/Truncation. |
| `GET /api/dependencies/{kind}/{id}/evidence?relatedKind=&relatedId=&cursor=&limit=50` | Fundstellen und erklärende Pfade zum ausgewählten sichtbaren Treffer; gleiche Berechtigungsprüfung. |

Bericht mit `generatedAt`, `analysisRevision`, `complete`, `limitations[]`, `counts`, `items[]`, `map`, `nextCursor`; `analysisRevision` nur aus sichtbaren Quellrevisionen und relevanten sichtbaren Metadaten ableiten. Cursor an Scope, Parameter und Revision binden. Ändert sich die Revision zwischen Seiten, neu laden statt Bestände still vermischen. Query-Sortierung/Paging deterministisch; keine exakten Komplettzähler behaupten, wenn das Berechnungsbudget erreicht ist.

Gemeinsame Core-Verträge und HTTP-only Clients: CLI `np dependencies list/show/evidence`, dazu drei lesende MCP-Tools für Objektsuche, Bericht und Fundstellen. Endpunktparität ist laut Root-CLAUDE Pflicht und Bestandteil von V1. Auth- und Sitzungswechsel leeren alle zugehörigen Client-Caches über die bestehende Grenze.

Datenfrische: beim Seitenaufruf, Objektwechsel, explizitem Aktualisieren und nach einschlägigen Mutationen aktualisieren; vorhandene Änderungsereignisse verwenden, zusätzlich auf dieser Seite bei Fensterfokus refetchen. Kein Live-Versprechen aus bloßem Zeitstempel. Langfristige Prozess-Caches enthalten ausschließlich revisionsgebundene Fakten.

## 8. Umsetzung in fünf prüfbaren Schritten

1. **Erste durchgängige Strecke:** Maschine auswählen → direkte Workflow-Verwendungen → Fundstelle → Designer. Reine Extraktion, sichtbarer API-Bericht, Client-Parität und Übersicht gemeinsam implementieren; Diagramm zunächst ohne Funktion nicht anbieten.
2. **Ressourcen vervollständigen:** Workflows/Unterworkflows, Zugangsdaten einschließlich korrekter Vererbung, Variablen und Custom Activities. Alle definierten Auflösungszustände, Namen, Agenten-Sonderfälle und Berechtigungsgrenzen absichern.
3. **Mittelbare Verwendung:** zulässige Pfade, Zyklen, Deduplizierung, Summen, Abdeckungsgrenzen, Detailketten und revisionsgebundene Pagination.
4. **Karte und Bedienung:** fokussiertes Layout auf denselben Daten, geteilte Auswahl, Deep-Links von bestehenden Seiten, Tastatur, responsive Layouts und alle vorhandenen Skins.
5. **Abnahme:** Demo-Backend mit konsistenten Beispieldaten, DE/EN-Doku, fokussierte Tests und visuelle Prüfung gegen den ausgewählten Entwurf. Keine zweite nachgebaute Demo-Seite.

Diese Schritte sind Implementierungsabschnitte, keine automatisch zu eröffnenden Issues/PRs. Die erste Version wird erst als fertig bezeichnet, wenn alle fünf Abschnitte und die folgenden Kriterien erfüllt sind.

## 9. Abnahmekriterien und gezielte Prüfungen

- Ein Nutzer kann beim Beispielserver die vier direkten und zwei weiteren Workflows unterscheiden und von jeder Beziehung zu einer belegenden Fundstelle gelangen.
- In einer kurzen Bedienprobe finden Nutzer ohne Erklärung innerhalb von 30 Sekunden einen indirekten Aufrufer und den ursächlichen Schritt; technische Graphbegriffe sind nicht nötig.
- Ein Workflow mit fünf Verwendungsstellen zählt einmal; direkte plus mittelbare Verwendung desselben Workflows erzeugt keine doppelte Gesamtsumme.
- Credential-Override, Maschinenvorgabe und Agent-Vererbung liefern jeweils fachlich richtige Ergebnisse; bloß zugelassene Agent-Aufrufe stehen separat.
- IDs, exakte Namen, Groß-/Kleinschreibung, mehrdeutige Namen, freie Hostnamen, localhost, dynamische Referenzen, deaktivierte Schritte und fehlerhafte Definitionen haben reproduzierbare Tests.
- Global-Templates in bekannten verschachtelten Konfigurationen und Custom-Activity-Defaults werden gefunden; Kommentare/Anmerkungen außerhalb auswertbarer Felder werden nicht zu Abhängigkeiten.
- Zyklus, mehrfach erreichbares Ziel, lange Kette, mehrere Pfade und Analysebudget hängen nicht und verfälschen keine Vollständigkeitsangabe.
- Negative RBAC-Tests prüfen Suche, Objektbericht, Fundstellen, Zähler, Pfade und zuvor von Admin gefüllte Caches. Keine Klartextsecrets in API, Client, Logs oder Fehlern.
- Umbenennen, Löschen, Ändern eines Maschinen-Credentials, Custom-Activity-Änderung und Rechteentzug aktualisieren den Bericht ohne erneutes Parsen unveränderter Workflow-Definitionen.
- Gleiche Erklärung in Übersicht, Karte, CLI und MCP; fehlende Berechtigung wird an allen Einstiegen geprüft.
- Visuelle Abnahme in Hell/Dunkel und vorhandenen Skins, bei 1440/1280/768/390 px, mit langen deutschen Namen und 200 % Zoom. Kein abgeschnittener wesentlicher Text; alle Beziehungen ohne Maus zugänglich.
- Messziel auf dokumentierter Referenzmaschine: bei 1.000 Workflows / 20.000 erkannten Beziehungen warmer Bericht p95 unter 500 ms, kalter Aufbau unter 3 s, UI bleibt während Suche/Wechsel bedienbar. Ziele werden gemessen, nicht als bestehende Leistung beworben. Erst bei belegter Überschreitung einen persistenten Index erwägen.

Testumfang: fokussierte Core-/API-Tests für Semantik und Zugriff, Frontend-Interaktionstests, hermetische Playwright-Nutzerwege, Demo-Smoke und `EndpointClientCoverageTests`. Im späteren Build zwei bis drei begrenzte Screenshot-Vergleiche gegen das dann freigegebene Bild. Für den aktuellen Dokument-/Bildentwurf genügen Link-, Diff- und Sichtprüfung.

## 10. Bewusst spätere Erweiterungen

Vergleich zweier Versionsstände, konkrete Änderungssimulation, Einbindung in Löschdialoge, beobachtete Beziehungen aus Ausführungshistorie sowie Skills/MCP-Server/externe Verbindungen als eigene Objekttypen. Sie benötigen eigene Semantik und gehören nicht stillschweigend in V1. Keine pauschale Sicherheitsfreigabe für Löschen aus einer statischen Abhängigkeitsansicht ableiten.
