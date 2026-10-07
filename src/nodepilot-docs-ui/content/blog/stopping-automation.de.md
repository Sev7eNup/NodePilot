# Automatisierung braucht einen Ausschalter

Der Auftrag wurde abgebrochen. Auf dem Zielserver läuft der bereits gestartete Installer trotzdem weiter.

Dieser Zustand ist möglich, sobald die Ablaufsteuerung Arbeit an einen anderen Prozess oder ein entferntes System übergeben hat. Ob ein Abbruch auch dort ankommt und welche Wirkung er hat, hängt von der jeweiligen Implementierung ab. Für den Betrieb ist deshalb entscheidend, welche Zusage ein Ausschalter tatsächlich erfüllt.

| Eingriff | Beabsichtigte Wirkung | Was zusätzlich geklärt werden muss |
|---|---|---|
| Neue Starts sperren | Keine weiteren Aufträge zulassen | Bereits laufende und wartende Arbeit |
| Einen Lauf abbrechen | Die weitere Ausführung dieses Laufs beenden | Verhalten des aktuellen Schritts und externer Prozesse |
| Eine Änderung zurücknehmen | Einen definierten vorherigen Zustand herstellen | Bereits entstandene Folgen und neue Änderungen |

Die Trennung lässt sich an einer Softwareverteilung durchspielen. Das folgende Szenario ist eine Planungsübung: Ein fehlerhaftes Paket wurde für zwanzig Testserver freigegeben. Vier Installationen laufen, weitere Aufträge warten. Nach dem ersten auffälligen Ergebnis entscheidet der Betrieb, die Verteilung anzuhalten.

**Zuerst den weiteren Start verhindern.** Der Zeitplan allein ist möglicherweise nicht der einzige Auslöser. Auch manuelle Aufrufe, Webhooks oder übergeordnete Workflows können dieselbe Arbeit starten. Die Sperre muss die betroffenen Startwege erfassen. Anschließend wird geprüft, ob ein bereits angenommener Auftrag noch aus einer Warteschlange in die Ausführung wechseln kann.

**Danach die aktiven Ausführungen zuordnen.** Für jeden der vier Server muss feststehen, welcher Schritt erreicht wurde. Eine noch nicht begonnene Installation lässt sich anders behandeln als ein bereits laufender Installer. Ein Abbruchsignal an die Steuerung ist zunächst ein Signal. Die Bestätigung, dass ein Zielprozess beendet ist, braucht eine eigene Beobachtung.

**Den erreichten Zustand sichern.** Wo sich der Abschluss nicht zweifelsfrei feststellen lässt, bleibt der Zustand offen. Die Softwareversion und der Zustand der Anwendung werden auf dem Ziel geprüft. Ein erneuter Start vor dieser Bestandsaufnahme könnte dieselbe Installation doppelt auslösen oder eine bereits laufende Reparatur stören.

**Die Rücknahme gesondert entscheiden.** Ein Paket zu deinstallieren ist nicht automatisch die Umkehr seiner Installation. Hat die neue Version Daten verändert, muss eine alte Version diese Daten weiterhin verwenden können. Eventuell ist eine Wiederherstellung erforderlich, deren Auswirkungen wiederum geprüft werden müssen. Microsoft beschreibt solche fachlich bestimmten Gegenmaßnahmen im Muster der [Compensating Transaction](https://learn.microsoft.com/en-us/azure/architecture/patterns/compensating-transaction).

Für die Bereitschaft sollte dieser Ablauf vor dem ersten Ernstfall erprobt sein. Dazu genügt eine begrenzte Testverteilung, bei der die Ausführung an bekannten Stellen angehalten wird. Das Prüfprotokoll hält fest, wie lange die Unterbrechung dauert und welche Prozesse danach noch aktiv sind. So entsteht eine belegte Aussage über die Grenze des Abbruchs.

Auch der Weg zurück in den Betrieb braucht einen Verantwortlichen. Eine aufgehobene Sperre kann wartende Arbeit wieder freigeben. Vorher muss deshalb entschieden sein, welche Aufträge weiterhin gültig sind und welche durch die korrigierte Bereitstellung ersetzt werden. Allein die Behebung des ursprünglichen Paketfehlers beantwortet das nicht.

NodePilot unterscheidet zwischen dem Deaktivieren eines Workflows und dem Abbrechen seiner laufenden Ausführungen. Die Dokumentation beschreibt die Kombination aus `disable` und `cancel-all` zur Unterbindung weiterer Ausführung. Bereits ausgelöste Änderungen auf Zielsystemen brauchen trotzdem die beschriebene Zustandsprüfung und gegebenenfalls eigene Gegenmaßnahmen. [NodePilot: Workflow-Steuerung](https://www.nodepilot.run/docs/en/api/workflow-control/).
