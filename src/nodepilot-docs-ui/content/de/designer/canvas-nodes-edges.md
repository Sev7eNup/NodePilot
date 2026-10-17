# Canvas, Nodes & Edges

Die Canvas ist die Arbeitsfläche des Workflow-Designers. Nodes stellen Trigger und Activities dar. Edges verbinden die Nodes und bestimmen den Ausführungspfad.

Für Änderungen muss der Workflow mit **Edit** gesperrt sein.

## Canvas bedienen

| Aktion | Bedienung |
|---|---|
| Ausschnitt verschieben | Mittlere oder rechte Maustaste gedrückt halten und ziehen |
| Zoomen | Mausrad |
| Gesamten Workflow anzeigen | `Home` |
| Vollbild ein- oder ausschalten | `F11` |
| Auswahl verschieben | Nodes mit der Maus ziehen |

Die MiniMap zeigt die aktuelle Position im Workflow. **Auto-Layout** ordnet die Nodes automatisch an.

## Node hinzufügen

1. Gewünschten Trigger oder Activity-Typ in der Node Library auswählen.
2. Eintrag auf die Canvas ziehen.
3. Node auswählen.
4. Einstellungen im Properties-Panel eintragen.

Die wichtigsten Node-Typen:

| Node-Typ | Zweck |
|---|---|
| **Trigger** | Startet den Workflow |
| **Activity** | Führt einen Arbeitsschritt aus |
| **Group** | Gruppiert Nodes nur visuell |
| **Sticky Note** | Fügt eine Notiz hinzu und wird nicht ausgeführt |

## Nodes bearbeiten

- Node auswählen, um die Einstellungen zu öffnen.
- Mehrere Nodes mit `Ctrl` oder `Shift` auswählen.
- `Ctrl+D` dupliziert die Auswahl.
- `Delete` oder `Backspace` löscht die Auswahl.
- `Ctrl+C` legt die ausgewählten Nodes samt der Edges zwischen ihnen als Workflow-JSON in die Zwischenablage, etwa zum Ansehen in VS Code. Bei einer ausgewählten Gruppe kommen ihre Nodes mit. Konfigurationswerte, auch Inline-Secrets, sind enthalten.
- `Ctrl+V` fügt solches JSON ein, auch aus einem anderen Workflow oder einem Texteditor. Die Nodes bekommen neue IDs, und Bedingungen und Verweise zwischen ihnen werden mitgezogen. Eine Kopie aus dem Designer landet versetzt neben dem Original, fremdes JSON in der Mitte der sichtbaren Fläche. Ungültiges JSON wird gemeldet.
- Bei mehreren ausgewählten Nodes können gemeinsame Werte wie Machine, Timeout oder Aktivstatus zusammen geändert werden.

## Nodes verbinden

1. Vom Ausgangsport eines Nodes ziehen.
2. Verbindung am Eingangsport des Ziel-Nodes ablegen.
3. Edge auswählen, um Beschriftung oder Bedingung festzulegen.

Ein Port reagiert schon etwas neben seinem sichtbaren Punkt, und eine gezogene Verbindung rastet in der Nähe eines Ports ein, auch bei kleinem Zoom.

Die Pfeilrichtung zeigt die Ausführungsrichtung. Eine Edge kann immer, nur bei Erfolg, nur bei Fehler oder anhand einer eigenen Bedingung ausgeführt werden. Details enthält [Edge-Bedingungen](../concepts/edge-conditions). Eine Edge, die immer läuft, trägt keine Beschriftung — auf der Leinwand steht nur eine Bedingung oder ein selbst vergebenes Label.

Über das Plus-Symbol einer Edge kann eine neue Activity zwischen zwei vorhandenen Nodes eingefügt werden.

Eine bestehende Edge lässt sich auf ein anderes Ziel umhängen, ohne sie neu anzulegen: entweder die Edge auswählen und ihr Zielende direkt auf den neuen Node ziehen, oder — auf großen Graphen bequemer — per Rechtsklick auf die Edge **Ziel lösen** wählen und anschließend den neuen Ziel-Node anklicken. Die Vorschau-Linie zeigt dabei laufend, an welchem der vier Verbindungspunkte sie landen wird: maßgeblich ist der Punkt, der dem Klick am nächsten liegt. Das gilt auch für den Node, an dem die Edge bereits hängt — ihn erneut anzuklicken verschiebt nur den Anschlusspunkt. Beschriftung, Bedingung und der Deaktiviert-Zustand ziehen mit um; Esc oder ein Klick auf die leere Fläche bricht ab.

## Status während einer Ausführung

| Status | Darstellung |
|---|---|
| Läuft | animierte Hervorhebung |
| Erfolgreich | grün |
| Fehlgeschlagen | rot |
| Übersprungen | grau und gestrichelt |
| Pausiert | orange |

Deaktivierte Nodes und Edges werden abgeschwächt dargestellt und bei der Ausführung nicht verwendet.

## Ansicht anpassen

Über die Ansichtsoptionen stehen bei Bedarf folgende Hilfen zur Verfügung:

- Raster und Snap-to-Grid
- unterschiedliche Node-Darstellungen und Größen
- automatische Layout-Richtungen
- Machine-Farben
- Fehler- und Ausführungsabdeckung
- kritischer Pfad
- Datenfluss auf Edges

Diese Optionen ändern nicht die Ausführungslogik. Auto-Layout verschiebt jedoch die gespeicherten Node-Positionen.

## Wichtige Tastenkürzel

| Tastenkürzel | Aktion |
|---|---|
| `Ctrl+Z` | Rückgängig |
| `Ctrl+Y` | Wiederholen |
| `Ctrl+A` | Alle Nodes auswählen |
| `Ctrl+C` / `Ctrl+V` | Kopieren / Einfügen |
| `Ctrl+D` | Duplizieren |
| `Delete` | Löschen |
| `Home` | Workflow vollständig anzeigen |

Weitere Einstellungen und Testfunktionen stehen unter [Properties, Modi & Shortcuts](./properties-modes).
