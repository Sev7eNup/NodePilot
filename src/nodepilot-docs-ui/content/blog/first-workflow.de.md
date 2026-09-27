# Einen ersten Workflow mit NodePilot erstellen

Für den Einstieg genügt ein Workflow, der den Namen des NodePilot-Hosts ausliest. Das Beispiel besteht aus einem manuellen Trigger und einem PowerShell-Schritt. Voraussetzung ist eine laufende NodePilot-Instanz mit einem Admin- oder Operator-Zugang.

**1. Workflow anlegen**

Unter „Arbeitsbereich > Workflows“ einen neuen Workflow mit dem Namen „Host prüfen“ anlegen und anschließend „Edit“ auswählen. Im Designer werden die einzelnen Schritte platziert und miteinander verbunden.

**2. Trigger und Aktivität verbinden**

Einen „Manual Trigger“ und eine „Run Script“-Aktivität aus der Node-Bibliothek auf die Arbeitsfläche ziehen. Danach den Ausgang des Triggers mit dem Eingang der Aktivität verbinden. Der Trigger bestimmt den Start, die Aktivität führt das Skript aus.

![Zwei verbundene Schritte im NodePilot-Designer](images/designer-beispiel.png)

*Screenshot eines anderen Beispielworkflows mit „Greeting Probe“. Die Verbindung wird bei „Run Script“ auf dieselbe Weise angelegt.*

**3. PowerShell-Skript hinterlegen**

Die „Run Script“-Aktivität auswählen und folgendes Skript eintragen:

```powershell
$env:COMPUTERNAME
```

Als Ausgabevariable `hostInfo` festlegen. Das Feld „Maschine“ bleibt leer, damit das Skript lokal auf dem NodePilot-Host unter der Dienstidentität läuft. Eine separate Zielmaschine und zusätzliche Zugangsdaten sind dafür nicht erforderlich.

**4. Veröffentlichen und ausführen**

Den Workflow über „Publish“ veröffentlichen und mit „Run“ starten. Anschließend die Ausführung öffnen und die Ausgabe der „Run Script“-Aktivität prüfen. Sie enthält den Namen des Rechners, auf dem NodePilot läuft.

![Erfolgreich abgeschlossener Beispiellauf in NodePilot](images/ausfuehrung-beispiel.png)

*Erfolgreicher Lauf des Beispielworkflows „Greeting Probe“ mit markiertem Ausführungspfad und dem Status „Succeeded“.*

Sollte der eigene Lauf fehlschlagen, geben Status und Ausgabe des betroffenen Schritts Hinweise auf die Ursache. Nach dem erfolgreichen Test kann ein weiterer Schritt ergänzt werden, der das Ergebnis verarbeitet.
