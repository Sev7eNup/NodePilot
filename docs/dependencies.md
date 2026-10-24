# Abhängigkeiten

Unter **Monitoring → Abhängigkeiten** lassen sich gespeicherte Beziehungen zwischen
Workflows, Maschinen, Zugangsdaten, globalen Variablen und Custom Activities untersuchen.
Die Objektseiten bieten außerdem den Einstieg **Abhängigkeiten anzeigen**.

1. Ein Objekt über Name, Ordner, Hostname oder ID suchen und auswählen. Die Liste filtert schon
   beim Tippen. Jede Zeile nennt, von wie vielen Objekten das Objekt direkt und mittelbar verwendet
   wird, und die Zahl seiner Probleme. **Verwendung** sortiert nach direkter Verwendung.
   **Mit Problemen** zeigt Objekte mit nicht zuordenbaren, mehrdeutigen oder nicht auswertbaren
   Verweisen. **Ohne erkannte Verwendung** zeigt Maschinen, Zugangsdaten, Globals und Custom
   Activities, die im sichtbaren Bereich niemand statisch referenziert. Workflows sind dort
   ausgenommen, weil sie über Trigger starten. Ein Eintrag ist kein Nachweis, dass Löschen gefahrlos ist.
2. **Wird verwendet von** zeigt Verbraucher und Aufrufer; **Verwendet** zeigt verwendete Ressourcen.
3. Ein Ergebnis öffnen: **Warum betroffen?** erklärt bis zu drei Wege. **Fundstellen** nennt
   den konfigurierten Schritt und bei Bedarf das technische Feld.
4. **Schritt im Designer öffnen** zentriert und markiert den gespeicherten Schritt. Der Einstieg
   fordert keinen Bearbeitungs-Lock an. Wurde der Schritt inzwischen entfernt, erscheint ein Hinweis.
   **Zurück zu Abhängigkeiten** und Browser-Zurück führen zum ursprünglichen Eintrag zurück:
   Suchbegriff, Typfilter, Richtung, Auswahl und Details bleiben erhalten. Scrollposition sowie
   Kartenausschnitt und eingeblendete Kartenobjekte werden für die laufende Sitzung gemerkt.
5. Die **Karte** zeigt beide Richtungen zugleich: links, wer das Objekt verwendet, rechts, was es
   verwendet. Zunächst erscheinen die direkten Beziehungen; **+N** an einem Objekt blendet die
   nächste Ebene über dieses Objekt ein, der Weg zur aktuellen Auswahl ist bereits geöffnet.
   Ein Klick auf ein Objekt der anderen Seite wechselt die Richtung der Übersicht. Maximal 40
   Kartenobjekte; beim Öffnen wird nur so weit verkleinert, dass die Beschriftungen lesbar bleiben.

Die Übersicht unterscheidet direkte Beziehungen, mittelbare Verwendung und mögliche Agent-Aufrufe.
Gezählt werden verschiedene Objekte, nicht die Anzahl der Schritte. Ein über mehrere Wege
erreichbares Objekt wird einmal gezählt. Agent-`workflow_run`-Freigaben bedeuten **kann aufrufen**;
sie sind kein Nachweis eines tatsächlichen Aufrufs. Deaktivierte Definitionen und Schritte bleiben
mit Kennzeichnung enthalten, weil die Ansicht den gespeicherten Stand beschreibt.

## Erkennungsumfang

- `startWorkflow` und `forEach`: statische Workflow-ID oder Name. Exakte Schreibweise gewinnt,
  sonst muss der Name ohne Beachtung der Groß-/Kleinschreibung eindeutig sein.
- Remote-Aktivitäten: registrierte Zielmaschine, explizite Zugangsdaten und wirksame
  Maschinenvorgabe. Ein explizites Credential ersetzt die Vorgabe; Agent-Service-Identität
  erzeugt keine Credential-Verwendung.
- Globale Variablen: `{{globals.NAME}}` in bekannten Konfigurationsfeldern, Kantenbedingungen,
  Custom-Activity-Vorlagen und deren Eingabestandardwerten. Überschriebene Standardwerte werden
  nicht als mittelbare Verwendung der aufrufenden Workflows gezählt.
- Dynamische Ziele, freie Hostnamen, lokale Ausführung, ungültige oder nicht zuordenbare
  Referenzen erscheinen als Hinweise. Sie erzeugen keine erfundenen Ressourcen.

Beliebige Skriptzugriffe, dynamisch zusammengesetzte Ziele, externe Systeme, Agent-Skills und
MCP-Server sind nicht vollständig als Ressourcen erfasst. Eine Beziehung sagt keinen Ausfall
voraus. Die Analyse ist auf 12 Ebenen und 20.000 untersuchte Beziehungen pro Anfrage begrenzt;
bei Erreichen der Grenze sind Zähler ausdrücklich Mindestwerte. Fundstellen gehören zu den
bis zu drei erklärenden Wegen, nicht zu einer unbegrenzten Aufzählung sämtlicher möglicher Pfade.

## Berechtigungen und Aktualisierung

Workflow-Ordnerrechte werden vor der Analyse angewendet. Namen, Zähler, Pfade und Fundstellen
enthalten ausschließlich sichtbare Objekte. Zugangsdaten und globale Variablen stehen wie auf
ihren Objektseiten nur Admin und Operator zur Verfügung. Es werden keine Secret-Werte,
Passwörter oder Skripttexte an den Client zurückgegeben.

**Aktualisieren** liest den aktuellen gespeicherten Stand. Cursor sind an Stand, Suchbereich
und Richtung gebunden; Änderungen während des Blätterns führen zu `409 DEPENDENCIES_CHANGED`.
Fundstellen mit einer abweichenden Analyse-Revision werden nicht mit dem alten Bericht vermischt.
Die Analyse benötigt keine Migration, Hintergrundindizierung oder zusätzliche Graphbibliothek.
Der Browser-Demo-Modus verwendet dieselbe Oberfläche mit Beziehungen aus seiner veränderlichen
In-Memory-Welt; er führt keine echten Remote-Zugriffe aus.

## API, CLI und MCP

Alle drei API-Endpunkte sind authentifiziert und lesend. `kind` ist `workflow`, `machine`,
`credential`, `variable` oder `customActivity`; `direction` ist `uses` oder `usedBy` (Default).
`limit` ist standardmäßig 50, maximal 100; Folgeseiten verwenden `nextCursor`.

| REST | CLI | MCP |
|---|---|---|
| `GET /api/dependencies/objects?q=&kind=&state=&sort=&cursor=&limit=` | `np dependencies list --query TEXT --kind machine --state unused --sort usage` | `list_dependency_objects` |
| `GET /api/dependencies/{kind}/{id}?direction=&cursor=&limit=` | `np dependencies show machine ID --direction usedBy` | `get_dependencies` |
| `GET /api/dependencies/{kind}/{id}/evidence?relatedKind=&relatedId=&direction=&cursor=&limit=` | `np dependencies evidence machine ID workflow WORKFLOW_ID` | `get_dependency_evidence` |

Die Objektliste liefert je Objekt `usedByDirect`, `usedByIndirect` und `problems`. `state` ist
`problems` oder `unused`, `sort` ist `name` (Default) oder `usage`. Die mittelbare Verwendung
wird nur für die ausgelieferte Seite berechnet; die Sortierung nach Verwendung nutzt die direkte Zahl.
CLI-Befehle unterstützen `--cursor`, `--limit` und die üblichen globalen Ausgabeoptionen.
MCP liefert denselben Bericht und dieselben RBAC-gefilterten Daten wie die API.

Entwurf und technische Entscheidungen: [Feature-Plan](dependencies-plan.md).
