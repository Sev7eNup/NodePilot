# Ein Wartungsfenster ist eine Betriebsvereinbarung

Um 21:58 Uhr startet ein Auftrag, dessen letzter Lauf zwölf Minuten gedauert hat. Um 22:00 Uhr beginnt das Wartungsfenster. Beide Zeitangaben sind korrekt konfiguriert. Trotzdem ist offen, ob der Auftrag weiterlaufen darf.

Die Uhrzeit beantwortet diese Frage nicht. Sie braucht eine Vereinbarung zwischen dem Verantwortlichen der Anwendung und dem Betrieb der Automatisierung. Dabei muss zunächst eindeutig sein, was das Fenster bezeichnet: einen Zeitraum, in dem Änderungen erlaubt sind, oder eine Sperrzeit, in der bestimmte Automatisierungen ruhen sollen. Beides wird im Alltag als Wartungsfenster bezeichnet.

Für das folgende Beispiel bedeutet 22:00 bis 23:00 Uhr eine Sperrzeit, weil eine Datenbank gewartet wird. Der exportierende Auftrag darf sie währenddessen nicht verwenden. Die Zeiten und der Ablauf sind frei gewählt.

## Die Entscheidung fällt vor 22:00 Uhr

Soll um 22:00 Uhr keine aktive Datenbankverbindung des Exports mehr bestehen, genügt eine Startsperre ab diesem Zeitpunkt nicht. Bereits laufende Arbeit muss vorher abgeschlossen oder an einem geeigneten Punkt angehalten sein. Dazu braucht der Ablauf einen Vorlauf, der seine übliche Dauer und beobachtete Ausreißer berücksichtigt.

Eine starre Annahme von zwölf Minuten wäre bei stark schwankenden Datenmengen zu knapp. Für diesen Export könnte der Betrieb beispielsweise ab 21:40 Uhr keine neuen Läufe mehr zulassen und um 21:55 Uhr kontrollieren, ob noch eine Ausführung aktiv ist. Das sind vorgeschlagene Regeln für das Beispiel, keine allgemein geeigneten Standardwerte.

Wird ein Restlauf gefunden, muss ein benannter Verantwortlicher entscheiden können. Ein Export, der nur liest und seine Ausgabe zunächst in eine temporäre Datei schreibt, lässt sich möglicherweise anders behandeln als ein Auftrag, der bereits Buchungen vorgenommen hat. Das Verfahren zum Abbruch gehört deshalb zum Ablauf selbst.

## Ausgefallene Starts bleiben eine offene Aufgabe

Nach 23:00 Uhr ist die Sperre aufgehoben. Daraus folgt noch nicht, dass alle ausgelassenen Starts nachgeholt werden sollen.

Bei einem regelmäßigen Zustandsbericht genügt häufig eine aktuelle Erhebung. Zehn nachgeholte Berichte würden denselben gegenwärtigen Zustand mehrfach abfragen. Bei einem Export mit aufeinanderfolgenden Datenintervallen muss dagegen jedes noch fehlende Intervall berücksichtigt werden. Entscheidend ist die fachliche Bedeutung des Auftrags.

Für die Übergabe lässt sich die Vereinbarung so festhalten:

| Situation | Regel im Exportbeispiel |
|---|---|
| Neuer Start ab 21:40 Uhr | Zurückstellen und als ausstehend erfassen |
| Aktiver Lauf um 21:55 Uhr | Verantwortlichen informieren und Zustand prüfen |
| Datenbankwartung dauert länger | Sperre ausdrücklich verlängern |
| Datenbank ist wieder freigegeben | Verbindung und benötigte Funktion prüfen |
| Rückstand wird abgearbeitet | Fehlende Intervalle geordnet und mit begrenzter Parallelität verarbeiten |

Ein Ende nach Uhrzeit sollte keine Funktionsfreigabe ersetzen. Wenn die Wartung überzieht, würde ein automatischer Neustart um 23:00 Uhr sonst genau in die noch laufende Änderung fallen.

## Was zur dokumentierten Vereinbarung gehört

Eine brauchbare Regel benennt die betroffenen Ressourcen. Werden nur einzelne Workflows gesperrt, kann ein anderer Ablauf dieselbe Datenbank weiterhin erreichen. Die Zuordnung muss deshalb von der Anwendung ausgehen und anschließend auf ihre Verbraucher übertragen werden.

Für wiederkehrende Fenster gehören außerdem Zeitzone und Verhalten bei Zeitumstellungen in die Dokumentation. Besonders bei überregionalen Teams sollte eine Uhrzeit ohne Zeitzone nicht als ausreichende Angabe gelten. Auch die Stelle, an der eine Verlängerung oder Ausnahme vermerkt wird, muss vorab bekannt sein.

Die Vereinbarung kann unabhängig vom verwendeten Werkzeug formuliert werden. Wird sie mit NodePilot umgesetzt, ist anschließend für jeden Startweg zu prüfen, welche Starts tatsächlich blockiert werden und wie bereits laufende oder zurückgestellte Aufträge behandelt werden. Der Kalendereintrag allein liefert diesen Nachweis nicht.
