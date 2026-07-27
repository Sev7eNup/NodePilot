# Eigenschaften, Modi und Tastenkürzel

Das Eigenschaften-Panel enthält die Einstellungen des ausgewählten Nodes oder der ausgewählten Verbindung. Änderungen sind nur mit Schreibrecht und aktivem Bearbeitungs-Lock möglich.

## Eigenschaften bearbeiten

Nach Auswahl eines Nodes zeigt das Panel die für diesen Activity-Typ verfügbaren Felder. Häufig verwendete Einstellungen sind:

- **Bezeichnung:** sichtbarer Name des Schritts
- **Ausgabevariable:** Name für den Zugriff auf das Ergebnis in nachfolgenden Schritten
- **Beschreibung:** optionale Erläuterung zum Schritt
- **Zielmaschine und Zugangsdaten:** Ausführungsziel für Remote-Activities
- **Timeout:** maximale Laufzeit des Schritts
- **Deaktiviert:** überspringt den Schritt bei der Ausführung
- **Breakpoint:** pausiert einen Debug-Lauf vor diesem Schritt; nur im Expertenmodus

Pflichtfelder sind im Panel gekennzeichnet. Die verfügbaren Felder unterscheiden sich je nach Activity. Eine vollständige Übersicht enthält die [Activity-Referenz](../activities-reference).

Nach Auswahl einer Verbindung können unter anderem Bezeichnung, Bedingung und Darstellung geändert werden. Weitere Informationen enthält [Edge-Bedingungen](../concepts/edge-conditions).

## Standard- und Expertenmodus

Der **Standardmodus** enthält die Funktionen zum Erstellen, Konfigurieren, Testen und Veröffentlichen von Workflows.

Der **Expertenmodus** ergänzt Funktionen für umfangreiche Workflows:

- Breakpoints und Debug-Läufe
- Simulation und Versionsvergleich
- Suchen und Ersetzen
- Gruppierung und genaue Positionierung von Nodes
- zusätzliche Ansichts- und Darstellungsoptionen
- JSON-Export und erweiterte Navigation

Der Modus kann im Designer umgeschaltet werden. Bestehende Workflow-Daten werden dadurch nicht verändert.

## Variablen einsetzen

Variablen übertragen Werte zwischen Triggern und Activities. Die Eingabe von `{{` öffnet die Variablenauswahl.

| Eingabe | Funktion |
|---|---|
| `↑` / `↓` | Eintrag auswählen |
| `Enter` / `Tab` | Auswahl übernehmen |
| `Esc` | Auswahl schließen |

Verfügbare Werte stammen aus:

- Ausgaben vorheriger Schritte, zum Beispiel `{{script.output}}`
- globalen Variablen, zum Beispiel `{{globals.API_URL}}`
- Eingaben eines manuellen Triggers, zum Beispiel `{{manual.customerId}}`

Ein Feld kann einen festen Wert, eine Variable oder eine Kombination aus beidem enthalten. Welche Werte an einer Stelle verfügbar sind, beschreibt [Datenbus und Variablen](../concepts/data-bus).

## Trigger konfigurieren

Auch Trigger werden über das Eigenschaften-Panel eingerichtet.

| Trigger | Wichtige Einstellungen |
|---|---|
| Manueller Trigger | Titel, Beschreibung und Eingabeparameter |
| Zeitplan | Cron-Ausdruck oder Vorlage und Beschreibung |
| Webhook | HTTP-Methode, Pfad und optionales Secret |
| Dateiüberwachung | Verzeichnis, Dateifilter, Ereignistyp und Unterverzeichnisse |
| Datenbank | Verbindung, Prüfintervall und Abfrage |
| Windows-Ereignisprotokoll | Protokoll, Ereignistyp, Quelle, Ereignis-ID und Suchzeitraum |

## Prüfen und ausführen

Für die Prüfung eines Workflows stehen mehrere Ebenen zur Verfügung:

- **Step Test:** führt nur den ausgewählten Schritt mit Testdaten aus.
- **Test Run:** führt den Workflow als Test aus. Parameter eines manuellen Triggers werden vor dem Start abgefragt.
- **Debug Run:** führt den Workflow mit Breakpoints aus; nur im Expertenmodus.
- **Simulation:** zeigt den möglichen Ablauf, ohne Activities auszuführen; nur im Expertenmodus.
- **Lint:** zeigt fehlende Pflichtangaben, nicht erreichbare Nodes und weitere Probleme.

Fehler aus der Lint-Prüfung verhindern die Veröffentlichung. Warnungen müssen vor der Veröffentlichung bestätigt werden.

Ungespeicherte Änderungen werden vor einem Test- oder Debug-Lauf gespeichert. Ein laufender Test kann abgebrochen werden.

## Lauf überwachen

Das Ausführungs-Panel enthält:

- **Live:** aktueller Status und Ausgaben der laufenden Schritte
- **History:** vergangene Ausführungen
- **Output:** verfügbare Trigger-, Variablen- und Schrittdaten
- **Watch:** beobachtete Ausdrücke; nur im Expertenmodus

Bei einem Breakpoint pausiert der Debug-Lauf vor dem betroffenen Schritt. Danach stehen folgende Aktionen zur Verfügung:

- **Continue:** Ausführung bis zum nächsten Breakpoint fortsetzen
- **Step Over:** genau einen Schritt ausführen und erneut pausieren
- **Stop:** Ausführung beenden

## Speichern, veröffentlichen und exportieren

Änderungen werden nach einer kurzen Bearbeitungspause automatisch als Entwurf gespeichert. `Ctrl+S` speichert den Entwurf sofort.

**Veröffentlichen** übernimmt den aktuellen Entwurf als ausführbare Version. Abhängig vom Workflow-Status kann dieselbe Aktion den Workflow aktivieren oder deaktivieren. Frühere Versionen bleiben über den Versionsverlauf verfügbar.

Der Workflow kann als JSON exportiert werden. Eine PNG-Datei bildet die aktuelle Canvas-Ansicht ab.

## Tastenkürzel

`Ctrl` entspricht unter macOS `Cmd`. Die integrierte Kurzübersicht öffnet sich mit `?`.

### Standardmodus

| Tastenkürzel | Funktion |
|---|---|
| `?` | Kurzübersicht ein- oder ausblenden |
| `Esc` | geöffnetes Fenster oder Overlay schließen |
| `Home` | gesamten Workflow in die Ansicht einpassen |
| `F11` | Designer-Vollbild ein- oder ausschalten |
| `Ctrl+P` | Workflow-Schnellwechsel öffnen |
| `Ctrl+Shift+P` | Befehlspalette öffnen |
| `Ctrl+F` | Nodes suchen |
| `Ctrl+S` | Entwurf speichern |
| `Ctrl+Shift+S` | veröffentlichen, aktivieren oder deaktivieren |
| `Ctrl+E` | Bearbeitungs-Lock anfordern |
| `Ctrl+U` | Bearbeitungs-Lock freigeben |
| `Ctrl+Enter` | Testlauf starten |
| `Ctrl+Shift+X` | laufende Ausführung abbrechen |
| `Ctrl+Z` | Änderung rückgängig machen |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Änderung wiederholen |
| `Ctrl+A` | alle Elemente auswählen |
| `Ctrl+C` | Auswahl kopieren |
| `Ctrl+V` | kopierte Elemente einfügen |
| `Ctrl+D` | Auswahl duplizieren |
| `Delete` / `Backspace` | Auswahl löschen |
| `Ctrl+Shift+T` | Workflow automatisch anordnen |
| `Ctrl+Shift+L` | Lint-Panel ein- oder ausblenden |
| `Ctrl+Alt+P` | Canvas als PNG exportieren |

### Expertenmodus

Die folgenden Kürzel ergänzen die Kürzel des Standardmodus.

| Tastenkürzel | Funktion |
|---|---|
| `Ctrl+Shift+Enter` | Debug-Lauf starten |
| `Ctrl+Shift+U` | fremden Bearbeitungs-Lock aufheben; Administratorrecht erforderlich |
| `Ctrl+G` | ausgewählte Nodes gruppieren |
| `Ctrl+H` | Suchen und Ersetzen öffnen |
| `Tab` / `Shift+Tab` | nächsten oder vorherigen verbundenen Node auswählen |
| `Pfeiltasten` | ausgewählte Nodes um 10 Pixel verschieben |
| `Shift+Pfeiltasten` | ausgewählte Nodes um 1 Pixel verschieben |
| `Ctrl+Shift+E` | Auswahl in die Ansicht einpassen |
| `Ctrl+Shift+O` | ursprüngliche Anordnung wiederherstellen |
| `Ctrl+Shift+D` | Vergleich mit einer Version öffnen |
| `Ctrl+Shift+R` | Simulation starten oder zurücksetzen |
| `Ctrl+Alt+X` | Activity-Filter zurücksetzen |
| `A` | Edge-Animation ein- oder ausschalten |
| `R` | Edge-Verlauf wechseln |
| `M` | Nodes nach Maschine einfärben |
| `H` | Fehler-Heatmap ein- oder ausschalten |
| `C` | kritischen Pfad ein- oder ausblenden |
| `G` | Ausrichtung am Raster ein- oder ausschalten |
| `D` | ausgewählten Node aktivieren oder deaktivieren |
| `B` | Breakpoint am ausgewählten Node ein- oder ausschalten |
| `Ctrl+Shift+N` | Node-Darstellung wechseln |
| `Ctrl+]` / `Ctrl+[` | Edge-Breite erhöhen oder verringern |
| `Ctrl+Shift+>` / `Ctrl+Shift+<` | Node-Größe erhöhen oder verringern |
| `Ctrl+Alt+.` / `Ctrl+Alt+,` | Schriftgröße der Bezeichnung erhöhen oder verringern |
| `Ctrl+Shift+J` | Workflow als JSON exportieren |
| `Ctrl+Shift+1` | Workflows öffnen |
| `Ctrl+Shift+2` | Ausführungen öffnen |
| `Ctrl+Shift+3` | Maschinen öffnen |
| `Ctrl+Shift+4` | globale Variablen öffnen |
| `Ctrl+Shift+5` | Audit öffnen |

### Script-Editor

Diese Kürzel gelten im geöffneten Script-Editor.

| Tastenkürzel | Funktion |
|---|---|
| `Ctrl+S` | aktuellen Inhalt übernehmen |
| `Ctrl+F` | Text suchen |
| `Ctrl+H` | Text suchen und ersetzen |
| `Ctrl+G` | zu einer Zeile wechseln |
| `Ctrl+/` | Zeile kommentieren oder Kommentar entfernen |
| `Ctrl+Space` | Autovervollständigung öffnen |
| `Esc` | Script-Editor schließen |

### Canvas-Gesten

| Geste | Funktion |
|---|---|
| Ziehen auf einer freien Fläche mit linker Maustaste | Auswahlrahmen aufziehen |
| Ziehen mit mittlerer oder rechter Maustaste | Canvas verschieben |
| `Shift` + Klick | Element zur Auswahl hinzufügen oder daraus entfernen |
