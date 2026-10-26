# Kalender — Feature- und Umsetzungsplan

Stand: 2026-10-24. Status: **Planungsentwurf, noch nicht implementiert.**

Nutzerentscheidung: Die erste Version soll geplante Läufe zeigen, Wartungsfenster verwalten und **Workflow-Zeitpläne direkt im Kalender bearbeiten** können. Die übrigen Festlegungen dieses Dokuments sind Umsetzungsvorschläge. Der Auftrag umfasst die Planung; die Roadmap erhält dadurch noch keinen neuen R1-Eintrag.

## 1. Ziel

Ein Operator kann beantworten: **Was soll wann starten, welche Starts verhindert eine Wartungsregel, und wo könnten sich Arbeiten zeitlich überschneiden?** Er kann einen bestehenden Zeitplan anpassen, ohne dafür den Workflow-Designer öffnen zu müssen.

Drei zentrale Nutzerwege:

1. **Wartung vorbereiten:** kommende Woche öffnen → Ordner oder Maschine filtern → geplante Starts und geschätzte Enden prüfen → Wartungsfenster mit Wirkungsübersicht anlegen.
2. **Zeitplan ändern:** Termin auswählen → gesamte Serie bearbeiten → bisherige und neue nächste Termine vergleichen → Workflow veröffentlichen → aktualisierten Kalender sehen.
3. **Ausbleibenden Start erklären:** geplanten Termin auswählen → blockierende Regel bzw. bekannte Trigger-Störung erkennen → Wartungsfenster, Zeitplan oder Live-Ops öffnen.

Die Vorschau beschreibt den gespeicherten Konfigurationsstand. Tatsächliche Starts können durch Warteschlangen, ausgeschaltete Workflows, Trigger-Störungen, Ausfälle und spätere Änderungen davon abweichen. Die Oberfläche nennt sie deshalb **geplante Starts**, nicht zugesicherte Ausführungen.

## 2. Erste Version und Abgrenzung

| Bestandteil | Umfang in V1 |
|---|---|
| Ansichten | Woche als Standard, Tag und chronologische Liste; Woche beginnt montags. |
| Planungshorizont | Heute bis 90 Tage voraus; maximal 42 lokale Kalendertage pro Abfrage. |
| Termine | Jeder aktive `scheduleTrigger` eines aktivierten Workflows wird separat betrachtet. |
| Zeitpläne bearbeiten | Bestehende Trigger-Serie: Cron-Ausdruck, einfache Zeitmuster und Beschreibung; direkte Vorschau und bestehender Workflow-Veröffentlichungsablauf. |
| Wartungsfenster | Bestehende Einmal-, Wochen- und Cron-Fenster darstellen; Admins können sie direkt anlegen, ändern und löschen. |
| Filter | Workflow-Suche, Workflow-Ordner einschließlich Unterordnern, Maschine, blockierte Starts, mögliche Überschneidungen. |
| Laufdauer | Historisch geschätzter Abschluss, bei ausreichender Datenbasis; klar als Schätzung. |
| Hinweise | Start durch Wartungsregel blockiert; erwartete Laufzeit reicht in ein Fenster; mögliche parallele Verwendung derselben Maschine; mögliche Selbstüberschneidung. |
| Integration | Navigation, Objekt-Deep-Links, CLI, MCP, DE/EN, Browser-Demo, Dokumentation. |

Neue Workflow-Trigger hinzufügen oder entfernen und ihre Graph-Verbindungen ändern bleibt zunächst Aufgabe des Designers. Das direkte Ändern vorhandener Zeitpläne ist vollständiger V1-Umfang, kein späterer Platzhalter.

Einzelne Vorkommen verschieben oder auslassen benötigt ein eigenes Ausnahmemodell und gehört nicht zu V1. Ebenso später: Drag-and-drop, Monatsraster, Feiertagskalender, iCal/Outlook-Synchronisation, automatische Optimierung, Ressourcenreservierungen und historischer Soll-Ist-Abgleich. Manuelle, Webhook-, Datei-, Datenbank- und Eventlog-Auslöser erhalten keine erfundenen Zukunftstermine. Ein Polling-Intervall ist kein vorhersehbarer Workflow-Start.

## 3. Navigation und Oberfläche

Neuer Eintrag **Kalender** in **Arbeitsbereich**, unmittelbar nach Workflows, Route `/calendar`. Der bisherige Menüeintrag Wartungsfenster geht in diesem Bereich auf. Die REST-Routen der Wartungsfenster bleiben bestehen; interne UI- und Dokumentationslinks werden auf den neuen Bereich umgestellt. Kein zweites Wartungsfenster-Formular mit eigener Logik.

Drei Bereiche unter derselben Seite:

- **Kalender:** Tag, Woche und Liste mit denselben Filtern und derselben Auswahl.
- **Zeitpläne:** durchsuchbare Serienliste. Zeigt auch deaktivierte Workflows, deaktivierte Trigger, ungültige Regeln und Serien ohne weiteres Vorkommen. Damit bleiben diese bearbeitbar, selbst wenn sie keinen Kalendertermin erzeugen.
- **Wartungsfenster:** bisherige Verwaltung in gemeinsam genutzten Komponenten, nur für Admin/Operator sichtbar.

Kalenderkopf: Heute, vorheriger/nächster Zeitraum, Zeitraum, Ansicht und sichtbare **Anzeige-Zeitzone**. Darunter Filter und eine kompakte Erklärung der Darstellung. Im Seitenfuß beziehungsweise Kopf stehen Berechnungszeit und Vollständigkeit der Vorschau.

Terminkarte: Workflow-Name, Trigger-Bezeichnung bei mehreren Zeitplänen, geplante Startzeit, gegebenenfalls „Ende ca. …“ und ein erklärender Status. Ein Klick öffnet rechts die Details; auf kleinen Bildschirmen ein zugängliches Panel. Aktionen: **Zeitplan bearbeiten**, **Workflow öffnen**, **Abhängigkeiten anzeigen** und gegebenenfalls **Wartungsfenster öffnen**. Ein Klick allein fordert keinen Edit-Lock an.

Wartungsfenster liegen als eigene Zeitbänder hinter beziehungsweise neben den Terminen. Jedes Band zeigt seine Reichweite: alle Workflows, Ordner oder ausgewählte Workflows. Ein ordnerbezogenes Fenster darf die gesamte Ansicht nicht wie eine globale Sperre aussehen lassen. Bei einem Maschinenfilter wird deutlich: Das Fenster adressiert Workflows, keine physische Maschine.

Schraffierung und Text unterscheiden Wartungsregeln von Terminbalken. Rot kennzeichnet einen gemäß aktuellen Regeln blockierten Start; Amber eine mögliche Überschneidung. Ein gewöhnlich geplanter Termin bekommt keine grüne Erfolgsaussage. Status sind auch ohne Farbwahrnehmung verständlich.

Unter 768 px ist die Liste der Standard. Alle Angaben und Aktionen sind über die Liste und Tastatur erreichbar. Lange Workflow-Namen bleiben lesbar; Termine mit gleichem Start stehen geordnet nebeneinander oder in einer aufklappbaren Gruppe. Bestehende Design-Tokens, IBM Plex Sans und Carbon-Icons verwenden.

URL-Zustand enthält Ansicht, Datum, Filter und ausgewählte Objekt-IDs, etwa `/calendar?view=week&date=2026-10-26&folderId=…`. Entwürfe und komplette Definitionen gehören nicht in die URL. In der laufenden Woche werden bereits vergangene Zeiten abgedimmt; es werden keine historischen Soll-Termine aus der heutigen Definition zurückgerechnet. Für tatsächliche vergangene Läufe führt ein Link zu Ausführungen.

## 4. Zeitpläne direkt bearbeiten

Ein Kalendertermin bezeichnet ein Vorkommen einer Serie. Der Editor benennt ausdrücklich: **„Zeitplan bearbeiten — gilt für alle zukünftigen Termine dieser Serie.“**

1. Detailansicht lädt die aktuelle Workflow-Version und den Trigger anhand seiner stabilen Node-ID. Anzeigen ist lesend.
2. **Bearbeitung starten** erklärt die vorhandene Produktsemantik und fordert den Workflow-Lock an: Der gesamte Workflow wird deaktiviert, einschließlich anderer Trigger. Bereits laufende Ausführungen werden nicht gestoppt. Während der Bearbeitung verpasste Zeitplan-Termine werden anschließend nicht automatisch nachgeholt.
3. Nach erfolgreichem Lock den frischen Serverstand als Entwurfsbasis übernehmen. Bei fremdem Lock bleibt die Ansicht lesend. Einen bereits bestehenden eigenen Lock aus dem Designer nicht still übernehmen; stattdessen zum bestehenden Bearbeitungskontext führen. Der Server-Lock ist benutzerbezogen, kein garantierter Tab-Lock.
4. Eingabe über **Intervall**, **täglich**, **wöchentlich** oder **Cron**. Einfache Formen erzeugen Quartz-Cron; komplexe bestehende Ausdrücke öffnen unverändert im Cron-Modus. Kein verlustbehaftetes Zurückübersetzen in ein einfaches Formular. Der Eingabekontext nennt die Scheduler-Zeitzone.
5. Vorschau zeigt die nächsten fünf bisherigen und neuen Starts sowie Änderungen an Wartungsblockaden und möglichen Überschneidungen im sichtbaren Zeitraum. Beide Varianten werden ausdrücklich **„bei aktiviertem Workflow“** verglichen: Der durch den eigenen Lock deaktivierte Workflow darf den Vergleich nicht leeren. Der Live-Kalender markiert ihn währenddessen weiterhin als in Bearbeitung; Auswahl und Entwurf bleiben sichtbar. Veraltete oder fehlgeschlagene Vorschauen werden nicht als aktuelle Ergebnisse ausgegeben.
6. **Speichern** hält den Workflow deaktiviert und in Bearbeitung. **Speichern & veröffentlichen** verwendet den vorhandenen Publish-Endpunkt, aktiviert den gesamten Workflow und gibt den Lock frei. Insbesondere bei ursprünglich deaktivierten Workflows wird diese Wirkung ausdrücklich benannt.
7. Erfolgreiche Mutation invalidiert Kalender, Serienliste, Workflow-Ansichten und Live-Ops/Dashboard-Daten. Fehler lassen den Entwurf erhalten. Ein Versionskonflikt verlangt Neuladen und erneute Prüfung; kein automatisches Überschreiben.

Der Kalender ändert ausschließlich die eigenen Felder des ausgewählten `scheduleTrigger`. Node-ID, Kanten, Positionen, Annotationen, sonstige Konfiguration und andere Schritte werden aus der aktuellen Definition unverändert übernommen. Die vollständige Definition durchläuft dieselben Prüfungen wie im Designer. Fehler außerhalb des Triggers werden mit einem Link zur betroffenen Stelle gezeigt.

**Abbrechen ist fachlich relevant:** Ungespeicherte Änderungen können verworfen und der Lock freigegeben werden. Wie heute bleibt der Workflow danach deaktiviert. Der Dialog zeigt diesen Zustand und bietet eine bewusste Aktion zum Aktivieren des aktuell gespeicherten Stands an, soweit berechtigt und inzwischen lock-frei. Ohne zwischenzeitliches Speichern entspricht dies dem bisherigen Stand. Kein stilles Wiederaktivieren und kein „Abgebrochen“-Toast, der die Deaktivierung verschweigt. Bereits ausdrücklich gespeicherte Änderungen sind durch Abbrechen nicht zurückgenommen.

Bei unklarem Netzwerkergebnis nach Speichern/Publish zuerst Workflow-Version und Lock-Zustand nachladen. Keine automatische erneute Veröffentlichung. Entwürfe dürfen bei Hintergrund-Refetch nicht ersetzt werden. Die bestehende Versionsprüfung bleibt auch bei mehreren Tabs desselben Benutzers maßgeblich; noch ungespeicherte fremde Tab-Entwürfe kann sie nicht erkennen.

## 5. Wartungsfenster und Wirkungsübersicht

**Wartungsfenster anlegen** öffnet die bestehende Form, optional mit ausgewähltem Zeitraum. Administratoren können das Fenster unmittelbar im Kalender speichern. Der Formularkontext zeigt die Zeitzone des Fensters; bei abweichender Anzeige-Zeitzone erscheinen beide Zeiten vor dem Speichern.

Die lesende Vorschau vor dem Speichern gilt für den abgefragten Zeitraum. Beim Ändern wird die betreffende gespeicherte Regel im Vorschau-Snapshot ersetzt; beim Anlegen ergänzt und bei einer Löschvorschau entfernt. Alle anderen Regeln bleiben wirksam. Die Vorschau nennt:

- Welche sichtbaren geplanten Starts neu blockiert bzw. wieder zugelassen würden.
- Welche geschätzten Laufzeiten in das Fenster hineinreichen könnten.
- Ob die Auswertung vollständig ist und welche Teile nicht berechnet wurden.

Vorschau und Speichern sind getrennt. Zwischenzeitliche Änderungen können das Ergebnis ändern; nach dem Speichern neu berechnen. Die Vorschau ist keine Reservierung und hält keinen Scheduler an.

Verbindliche Semantik aus dem bestehenden Evaluator:

- **Blackout:** blockiert neue Starts innerhalb des Fensters.
- **AllowOnly:** ein betroffener Workflow darf nur innerhalb mindestens eines noch wirksamen Erlaubnisfensters starten. Eine aktive Blackout-Regel gewinnt.
- Ein abgelaufenes einmaliges oder erschöpftes Cron-Erlaubnisfenster wirkt entsprechend der bestehenden `IsLive`-Regel nicht unbegrenzt weiter.
- Intervalle sind am Anfang inklusive und am Ende exklusive: `[Start, Ende)`.
- Ordnerziele schließen Unterordner ein; deaktivierte Fenster sind unwirksam.
- Ein blockierter geplanter Start bleibt als **„Würde übersprungen“** sichtbar. Er wird nicht auf das Fensterende verschoben. Aktuell gibt es kein automatisches Nachholen.
- Läufe vor Fensterbeginn dürfen weiterlaufen. Ebenso folgt aus einer Fensterregel keine neue Sperre für interne Wiederholungen, Resume oder Unterworkflow-Aufrufe.

Eine Änderung an einer wiederkehrenden Wartungsregel betrifft in V1 ebenfalls die Serie. Ein einzelnes Vorkommen bekommt keine scheinbar funktionierende Bearbeitungsoption.

## 6. Zeitberechnung und Datenbasis

Die Berechnung erfolgt im Backend mit der vorhandenen Quartz-Version. Der Browser formatiert Zeitpunkte und stellt sie dar; er entscheidet nicht über gültige Cron-Termine.

| Quelle | Fachliche Verwendung |
|---|---|
| `WorkflowDefinitionDocument.TriggerDescriptors` | Aktive Trigger samt Node-ID und Konfiguration; mehrere Trigger desselben Workflows nicht zusammenfassen. |
| Gespeicherte Workflow-Definition | Zusätzlich deaktivierte Trigger für die Serienverwaltung lesen; sie fehlen absichtlich in `TriggerDescriptors`. |
| `ScheduleTriggerSource` | Maßgeblich für Cron-Syntax, Server-Zeitzone, Mindestintervall und Verhalten bei verpassten Starts. |
| `TriggersController.CalculateFires` / `useSchedulePreview` | Bereits vorhandene serverseitige Vorschau weiterverwenden bzw. auf gemeinsame Berechnung umstellen. |
| `MaintenanceWindowEvaluator` | Maßgebliche Blockaderegeln, Ordnerreichweite und Wiederholungsauswertung. |
| `WorkflowExecution` | Begrenzte Stichprobe historischer abgeschlossener Läufe für Dauerabschätzungen. |
| Dependencies | Statisch erkannte Maschinenverwendung und erklärbare Fundstellen, unter denselben Sichtbarkeitsregeln. |

**Drei Zeitzonen unterscheiden:** Workflow-Cron läuft heute in `TimeZoneInfo.Local` des Servers. Wartungsfenster besitzen `TimeZoneId`. Die Anzeige verwendet standardmäßig die Browser-Zeitzone, umschaltbar auf Scheduler-Zeitzone oder UTC. Eine andere Anzeige-Zeitzone ändert keinen Zeitplan. V1 führt keine frei wählbare Zeitzone je Workflow-Trigger ein.

API-Zeitpunkte sind UTC mit expliziter Zeitzoneninformation; die Antwort liefert zusätzlich die auflösbaren Quellzeitzonen. Beim Berechnen einer Woche zuerst die lokalen Kalendergrenzen bestimmen und dann nach UTC umrechnen. Ein Tag bei Zeitumstellung kann 23 oder 25 Stunden haben. Doppelte lokale Uhrzeiten mit UTC-Offset unterscheiden; Eintragsidentität ist `(WorkflowId, NodeId, StartUtc)`, nie eine formatierte Uhrzeit.

In HA darf die Vorschau nicht versehentlich die lokale Zeitzone eines anders konfigurierten Followers als Scheduler-Zeitzone ausgeben. Die Berechnung muss den Stand der zuständigen Scheduler-Instanz verwenden oder die Vorschau als nicht verlässlich verfügbar kennzeichnen. Die konkrete Bereitstellung dieses Metadatums ist Teil des ersten technischen Arbeitspakets.

Wartungsbänder werden aus derselben fachlichen Zeitlogik wie `Evaluate` abgeleitet. Dafür die bisher privaten Berechnungen in gemeinsam verwendbare reine Hilfen extrahieren. Keine zweite unabhängige Wiederholungsimplementierung im Kalender. Vor allem Zeitumstellung, über Mitternacht laufende Wochenfenster und überlappende Cron-Vorkommen müssen gegen das tatsächliche Evaluator-Verhalten geprüft werden; Kommentare allein sind keine ausreichende Spezifikation. Bei Abweichungen zuerst den bestehenden Vertrag klären, keine beiläufige Änderung der Produktionsregeln.

Pro Antwort einen konsistenten Maintenance-Snapshot erfassen, einschließlich Generation/Zeitstempel, und für alle Vorkommen verwenden. Ein Refresh mitten in einer Berechnung darf nicht zwei Regelstände mischen. Auswertungsfehler ergeben einen sichtbaren Zustand **„Nicht bewertbar“**; die Fail-open-Runtime-Regel ist kein Beweis für ein konfliktfreies Kalenderergebnis.

## 7. Laufdauer und Überschneidungen

Vorgeschlagene erste Schätzung: die letzten höchstens 20 erfolgreichen, abgeschlossenen Hauptausführungen eines Workflows innerhalb von 30 Tagen, mindestens fünf Stichproben. Als Planungsspanne das 90. Perzentil von `CompletedAt - StartedAt` verwenden. Quelle, Stichprobenzahl und Zeitraum sind in den Details sichtbar. Keine Ausgabe wie „endet sicher um …“.

`StartedAt` ist heute schon bei Annahme vorhanden; die Spanne kann Queue-Zeit enthalten. Außerdem besitzen Ausführungen keine direkte gespeicherte Workflow-Versionszuordnung. Deshalb ausdrücklich **historische Gesamtdauer** ausweisen, keine reine Maschinenbelegung und keine Garantie für die aktuelle Version. Läufe verschiedener Eingaben können stark streuen. Unter fünf Stichproben bleibt es bei einem Startmarker mit **„Dauer unbekannt“**.

| Hinweis | Aussage und Grundlage |
|---|---|
| Start blockiert | Der bestehende Maintenance-Evaluator blockiert dieses Vorkommen im aktuellen Snapshot. |
| Lauf reicht möglicherweise in Wartung | Start ist erlaubt, aber das geschätzte Ende liegt im betroffenen Fenster. Keine Behauptung, der Lauf werde abgebrochen. |
| Mögliche Selbstüberschneidung | Die geschätzte Dauer überlappt das nächste Vorkommen desselben Workflows; vorhandenes Parallelitätslimit als Kontext nennen. |
| Mögliche Maschinenüberschneidung | Zwei erlaubte geplante Läufe überschneiden sich geschätzt und besitzen dieselbe statisch zugeordnete Maschine. |

Maschinenhinweise verwenden in V1 direkte, wirksame und statisch aufgelöste Verwendungen aktiver Schritte. Dynamische Ziele, beliebige Skriptzugriffe, Agentenentscheidungen und indirekte Unterworkflow-Ziele liefern keine vollständige Maschinenbelegung. Keine Variablenwerte oder Secrets auflösen, um eine Zuordnung zu erraten. Das gesamte Workflow-Intervall ist nur eine konservative Näherung; der Zeitpunkt eines einzelnen Remote-Schritts wird nicht vorgetäuscht.

Jeder Überschneidungshinweis führt zu den beiden Terminen und den erkannten Maschinen-Fundstellen. Geplante Starts, die bereits durch Wartung blockiert würden, erzeugen keine zusätzliche Ausführungsüberschneidung. Fehlende Dauer oder unvollständige Zielauflösung führen zu **„Nicht vollständig prüfbar“**, nicht zu „Keine Konflikte“. Die Funktion reserviert keine Ressourcen und verändert die Ausführungsreihenfolge nicht.

## 8. Rechte und Sichtbarkeit

- Admin, Operator und Viewer dürfen den Kalender für ihre lesbaren Workflows öffnen. Ordner-RBAC vor Terminberechnung, Aggregation, Statistik und Ausgabe anwenden.
- Workflow-Zeitpläne ändern verlangt dieselben Edit-Rechte wie der Designer sowie Lock und Versionsprüfung. Viewer erhalten keine Änderungsaktionen.
- Vollständige Wartungsfenster bleiben gemäß heutiger API auf Admin/Operator beschränkt; ändern darf nur Admin. Viewer sehen beim eigenen sichtbaren Termin nur einen neutralen Hinweis auf eine Startregel, keine Fenster-Namen, IDs oder Zielauflistungen.
- Wartungsziel-Namen und betroffene Workflow-Listen nur aus zulässigen Metadaten zusammensetzen. Ein breites Wartungsfenster gewährt keine zusätzlichen Workflow-Leserechte.
- Maschinenfilter, Vorschauen, Serienlisten und Überschneidungen dürfen weder Namen noch Anzahlen fremder Workflows verraten. Kein Hinweis „weitere versteckte Konflikte“.
- Scope-Hinweis immer gleich: **„Vorschau für sichtbare Workflows und statisch erkannte Ziele.“**
- Bei Rechteentzug bzw. Sitzungswechsel geschützte Cache-Daten und offene Details verwerfen; nicht als veraltete Vorschau weiter anzeigen.

## 9. Technischer Zuschnitt und API

Kein zweiter Scheduler, keine persistierten Kalendertermine und voraussichtlich keine Datenbankmigration für V1. Termine werden aus vorhandener Konfiguration berechnet. Neue Nutzerdaten entstehen nur durch die bestehenden Workflow- und Wartungsfenster-Mutationen.

- **Core:** kleine Kalender-Datentypen bzw. definition-lokale Trigger-Fakten, falls mehrfach gebraucht; keine neue Quartz-Abhängigkeit.
- **Data:** gemeinsame Wartungszeitberechnung bei der vorhandenen Quartz-Integration; begrenzte Historienabfragen.
- **Api:** `CalendarController`, `CalendarQueryService`, autorisierte Projektion, Cron-Vorschau, Dauerabschätzung und Überschneidungsauswertung. Gemeinsame reine Cron-Hilfen dürfen wegen der bestehenden Projektrichtung nicht aus Api vom Scheduler importiert werden; geeignete gemeinsame Implementierung im schon referenzierten Data-Projekt.
- **Frontend:** `CalendarPage`, `CalendarWeekView`, `CalendarAgenda`, `CalendarEventDetails`, `ScheduleSeriesList`, `ScheduleEditorDialog`, gemeinsame Wartungsfenster-Form; Datenzugriff unter `src/api/calendar.ts`, React Query für Serverzustand.
- Gemeinsame Formular-/Vorschau- und Workflow-Persistenzlogik aus dem Designer verwenden oder gezielt entkoppeln. Kein Import der kompletten Designer-Page in den Kalender.

Vorgeschlagener API-Vertrag:

| Route | Zweck |
|---|---|
| `GET /api/calendar/range?fromUtc=&toUtc=&folderId=&workflowId=&machineId=` | Termine, sichtbare Wartungsbänder, Schätzungen, Hinweise und Metadaten für einen begrenzten Bereich. |
| `GET /api/calendar/schedules?q=&folderId=&workflowId=&cursor=&limit=` | Paginierte Serienliste einschließlich deaktivierter, fehlerhafter und erschöpfter Zeitpläne; stabile Node-ID und aktuelle Workflow-Version. |
| `POST /api/calendar/preview` | Seiteneffektfreie Vorschau einer Zeitplanänderung oder Wartungsfensteränderung gegenüber dem aktuellen Stand. Begrenzter Zeitraum; bei Workflow-Änderung `workflowId`, `nodeId`, `expectedVersion` und ausschließlich editierbare Trigger-Felder. |

Die Preview-Varianten erhalten explizite, validierte DTOs statt beliebiger JSON-Konfiguration. Vorhersage eines Workflow-Entwurfs verlangt Edit-Rechte, einer Wartungsänderung Admin. Vorschau erzeugt weder Lock noch Version noch Scheduler-Job. Die vorhandene `/api/triggers/schedule/next-fires`-Route bleibt der gemeinsame einfache Cron-Preview-Weg.

Für Schreiben vorhandene Endpunkte verwenden: Workflow `lock`, `PUT`, `publish`, `unlock`, gegebenenfalls `enable`; Wartungsfenster-CRUD. Der vollständige Workflow-Payload wird aus der beim Lock erhaltenen aktuellen Definition mit einer gezielten Änderung des Triggers erzeugt. `expectedVersion`, bestehende Validierung, Audit und Versionshistorie bleiben verbindlich.

Bereichsantwort enthält `generatedAtUtc`, `range`, `schedulerTimeZone`, `maintenanceSnapshotRevision`, `complete`, verständlich abbildbare `limitations`, Serien-/Vorkommensidentitäten, Quellversionen und Berechtigungen. Konfiguration, Aktivierung, Wartungsurteil und bekannte Trigger-Störung sind getrennte Merkmale, kein mehrdeutiger Einheitsstatus.

`TriggerHealthRegistry` kann bekannte lokale Störungen beisteuern. Ein leerer Registry-Eintrag ist kein Nachweis einer aktiven Registrierung. Auf einem HA-Follower ohne maßgebliche Zustandsdaten steht der Registrierungszustand auf unbekannt. Keine rohe Ausnahme mit sensiblen Konfigurationswerten an die Oberfläche geben.

CLI: `np calendar list`, `np calendar schedules`, `np calendar preview`; entsprechende MCP-Tools auf denselben API-Verträgen. Neue Routen mit `EndpointClientCoverageTests` und DTO-Parität absichern. Datum-/Zeitzonen-/Bereichsfehler müssen in beiden Clients ebenso eindeutig sein wie in der UI.

## 10. Datenmenge, Aktualisierung und Fehlerzustände

Ein minutenweiser Zeitplan produziert bereits 10.080 Vorkommen pro Woche. Daher wird die Wochenansicht nicht einfach mit allen Einträgen befüllt.

Vorgeschlagene, im Lasttest zu prüfende Budgets: maximal 5.000 einzeln zurückgegebene Termine, 200 pro Serie und 100.000 Cron-Iterationsschritte pro Anfrage. Grenzen vor großen Allokationen und innerhalb jeder Schleife prüfen, Cancellation beachten. Die Serienliste bleibt unabhängig davon vollständig paginierbar.

Dichte Serien erscheinen als eigene Zusammenfassung mit Regel, nächsten Terminen und **„Zeitraum eingrenzen“**. Eine Zusammenfassung ist keine durchgehende Belegung. Alle aufgrund von Budgets nicht einzeln berechneten Serien werden benannt, soweit sichtbar; bei großen Mengen über die Serienliste. Überschneidungen und Zähler sind dann ausdrücklich unvollständig. Keine stillen `Take(5000)`-Ergebnisse und keine exakten Summen aus einer Stichprobe. Der Zeitraum muss eingegrenzt werden können, bis Details vollständig vorliegen.

Wenn kein weiterer Lauf existiert, gehört die Serie in den Zustand „Kein zukünftiger Termin“, nicht in einen Fehlerzustand. Ungültige Cron-Konfiguration, unbekannte Zeitzone und fehlende Daten sind gesonderte Zustände. Netzwerk-/DB-Ausfälle ergeben keinen leeren erfolgreichen Kalender. Bisherige Daten dürfen mit deutlich sichtbarem Zeitstempel als veraltet stehen bleiben, außer bei Rechteentzug.

Definition-lokale Fakten revisionsgebunden cachen; fertige Antworten nur unter vollständigem Benutzer-/Rechte-/Filter-/Zeit-/Revisionsschlüssel oder zunächst gar nicht. Keine Datenbankabfrage pro Vorkommen. Dauerstatistik gebündelt und serverseitig begrenzt lesen; keine vollständigen Ausgaben oder Parameter laden.

Aktualisieren bei Rückkehr in den Tab und relevanten Workflow-/Wartungsänderungen; zusätzlich höchstens alle 60 Sekunden im sichtbaren Tab. Neue Ausführungen verändern nur die historische Dauerbasis, nicht ständig das Layout. Offene Editor-Entwürfe bleiben bei Refetch erhalten.

## 11. Umsetzung in fünf prüfbaren Paketen

| Paket | Ergebnis | Wesentliche Prüfung |
|---|---|---|
| 1. Berechnungsgrundlage | Gemeinsame Cron-/Wartungslogik, konsistenter Snapshot, Scheduler-Zeitzone einschließlich HA, begrenzte Range- und Serien-API samt CLI/MCP. | Gleiche Vorkommen und Wartungsurteile wie die bestehenden Runtime-Regeln; Grenzen, Rechte und Sommerzeit. |
| 2. Kalenderoberfläche | Visueller Entwurf anhand vorhandener Skins; Woche, Tag, Liste, Filter, Details, Serienliste und Navigation. | Lange Namen, dichte Zeitpläne, Tastatur, schmale Breiten, UTC/Ortszeit, leere und unvollständige Daten. |
| 3. Bearbeitung | Direkter Serieneditor, vorher/nachher-Vorschau, gemeinsame Wartungsform und Wirkungsvorschau; vollständiger Lock-/Save-/Publish-Ablauf. | Speichern erhält restliche Definition; Konflikte, Abbruch, Netzwerkausfall, laufende Ausführungen und ursprünglich deaktivierte Workflows. |
| 4. Planungshinweise | Historische Dauerabschätzung, Wartungsüberlappung, Selbstüberschneidung, statische Maschinenhinweise und Deep-Links zu Dependencies. | Unbekannte Dauer/Ziele bleiben unbekannt; blockierte Starts erzeugen keine Scheinkollisionen; keine fremden Ressourcen. |
| 5. Durchgängige Lieferung | DE/EN, Demo mit realen Änderungen im In-Memory-Modell, API-/Client-Parität, Dokumentation und gezielte Integrations-/UI-Prüfungen. | Bedienung vom Kalender bis zur geänderten Serie und zurück; kritische Fälle in allen vorhandenen Skins. |

Das sind Arbeitspakete desselben V1-Umfangs. Paket 3 darf nicht als bloßer Link zum Designer abgeschlossen werden. Eine neue Kalenderbibliothek ist für den Plan keine Voraussetzung; zunächst vorhandene React-/Virtualisierungsbausteine nutzen. Sollte die visuelle Erprobung eine neue Abhängigkeit rechtfertigen, wird diese gemäß Repo-Regel separat entschieden.

## 12. Konkrete Abnahmekriterien

1. Ein Workflow mit zwei aktiven Zeitplan-Triggern zeigt beide Serien und ihre einzelnen Vorkommen mit richtiger Node-ID.
2. Ein Wochenfenster Freitag 22:00 bis Samstag 02:00 erscheint korrekt über Mitternacht. Anfang inklusive, Ende exklusive; globale und geerbte Ordnerregeln verhalten sich wie `Evaluate`.
3. Blackout und AllowOnly überlagern sich entsprechend der vorhandenen Priorität; erschöpfte bzw. abgelaufene Regeln sperren nicht versehentlich unbegrenzt.
4. Tests mit expliziter Zeitzone decken Europe/Berlin am 29.03.2026 und 25.10.2026 sowie abweichende Browser-/Server-/Fenster-Zeitzonen ab. Tatsächliche Quartz-Ergebnisse sind maßgeblich; keine doppelte Ereignis-ID bei gleicher lokaler Uhrzeit.
5. Änderung „werktags 06:00“ auf „werktags 07:30“ direkt im Kalender: korrekte Vorschau, Lock, Versionsprüfung, Veröffentlichung und anschließend aktualisierte Serien. Alle anderen Workflow-Felder bleiben erhalten.
6. Fremder Lock, eigener bestehender Designer-Lock, Versionskonflikt, fehlerhafter Cron, zu kurzes Mindestintervall und zwischenzeitlich gelöschter Trigger führen zu einem nachvollziehbaren Zustand ohne stilles Überschreiben.
7. Abbruch nach Lock lässt den Deaktivierungszustand sichtbar. Abbruch nach ausdrücklichem Speichern behauptet keine Rücknahme gespeicherter Daten. Unsicherer Publish-Ausgang wird durch Nachladen geklärt.
8. Wartung 22:00–23:00: Start 22:30 als blockiert; Start 21:55 mit geschätztem Ende 22:15 als möglicher Überhang. Keine automatische Verschiebung auf 23:00 und kein Stopp des vorher gestarteten Laufs.
9. Fehlende Historie und dynamische Maschinenziele erzeugen keine erfundene Dauer oder Entwarnung. Gleiche Maschine allein ist noch kein bewiesener Ressourcenkonflikt.
10. Ein Viewer sieht nur berechtigte Workflows, keine Wartungsdetails und keine Schreibaktionen. Ein Operator kann berechtigte Zeitpläne ändern, aber keine Wartungsfenster mutieren. Fremde Namen und Anzahlen fehlen auch in Hinweisen und Preview-Differenzen.
11. Lastfall mit 500 minutenweisen Serien bleibt innerhalb der Rechen-/Antwortbudgets, zeigt Verdichtung und Teilabdeckung ausdrücklich und erlaubt Eingrenzen auf eine vollständige Detailansicht. Reale Laufzeit messen und dokumentieren; vorab keine unbelegte Performance-Zusage.
12. Browser-Demo und E2E prüfen echte Kalender-Mutationen und ihre sichtbaren Folgen. API-Client-Abdeckung, DTO-Parität, Typecheck, relevante Backend-/Frontend-Tests und Build laufen erfolgreich; keine Wiederholung unbeteiligter Tests ohne Anlass.

## 13. Wichtigste offene technische Punkte

Der Produktumfang ist mit der obigen Nutzerentscheidung planbar. Vor der Implementierung sind drei Punkte durch Code und einen kleinen technischen Durchstich zu klären:

- Bereitstellung der maßgeblichen Scheduler-Zeitzone auf HA-Followern, ohne eine neue unbelegte Clusterannahme einzuführen.
- Exakte Wiederverwendung der Maintenance-Zeitlogik für Intervalle und Vorschauen, besonders bei Zeitumstellung. Bestehende Runtime-Semantik dabei erhalten bzw. Abweichungen separat entscheiden.
- Begrenzte, providerübergreifende Dauerstichprobe und lesbare Verdichtung bei vielen häufigen Serien.

Diese Punkte sind Teil von Paket 1 beziehungsweise 4; sie verschieben die zugesagte direkte Zeitplanbearbeitung nicht aus V1.
