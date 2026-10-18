# Workflow-Automatisierung: vom Runbook zum ausführbaren Ablauf

Viele Betriebsabläufe im Windows-Umfeld beginnen als Runbook, also als dokumentierte Arbeitsanweisung im Wiki: Wenn der Druckdienst hängt, prüfe dies, starte jenes neu, eskaliere sonst. Workflow-Automatisierung macht daraus einen Ablauf, den eine Maschine ausführt, ohne dabei die Sorgfalt der Anweisung zu verlieren. In System Center Orchestrator hieß diese ausführbare Form ebenfalls Runbook, in NodePilot heißt sie Workflow.

Dieser Beitrag beschreibt, woran sich ein automatisierbares Runbook erkennen lässt, in welchen Stufen die Umstellung sinnvoll ist und welche Rolle Low-Code-Werkzeuge und KI dabei spielen. Das Beispiel am Ende ist konstruiert und beschreibt keinen tatsächlichen Kundeneinsatz.

## Was ein Runbook automatisierbar macht

Eine Arbeitsanweisung, die ein erfahrener Kollege zuverlässig ausführt, ist noch kein Workflow, den eine Maschine ausführen kann. Der Unterschied liegt in den Stellen, an denen ein Mensch stillschweigend entscheidet. Vor der Automatisierung sollte jede dieser Stellen ausdrücklich beantwortet sein:

| Eigenschaft | Frage an das Runbook | Beispiel |
|---|---|---|
| Auslöser | Wann beginnt der Ablauf? | Ereignis 7031 im Systemprotokoll, täglicher Zeitplan |
| Eingaben | Welche Werte braucht er, und woher kommen sie? | Rechnername, Dienstname |
| Schritte | Was genau wird in welcher Reihenfolge getan? | Status lesen, Dienst neu starten |
| Entscheidungen | Woran wird gemessen, welcher Weg folgt? | Status ist `Running` oder nicht |
| Fehlerpfad | Was passiert, wenn ein Schritt scheitert? | Benachrichtigung statt erneutem Versuch |
| Nachweis | Woran ist der Erfolg später erkennbar? | Protokoll pro Schritt mit Ausgabe |
| Verantwortung | Wer reagiert, wenn der Workflow aufgibt? | Bereitschaft der Serverbetreuung |

Lässt sich eine Zeile nicht beantworten, ist das kein Grund gegen die Automatisierung. Es zeigt aber, wo die Anweisung selbst noch unvollständig ist. Diese Lücke würde ein Skript nicht schließen, sondern nur verstecken.

## In Stufen umstellen statt alles auf einmal

Zwischen Handarbeit und vollautomatischem Workflow liegen mehrere Stufen, und nicht jedes Runbook muss die letzte erreichen:

1. **Dokumentiert:** Die Anweisung ist vollständig und wird von Hand ausgeführt.
2. **Halbautomatisch:** Ein Skript erledigt die Schritte, ein Mensch startet es und bewertet das Ergebnis.
3. **Ausgelöst:** Ein Zeitplan oder ein Ereignis startet den Workflow, das Ergebnis wird weiterhin geprüft.
4. **Mit Entscheidung und Fehlerpfad:** Der Workflow reagiert selbst auf die erwartbaren Ergebnisse und meldet, was er nicht lösen kann.

Jede Stufe liefert Erfahrung für die nächste. Ein Ablauf, der auf Stufe 2 dreimal anders reagiert hat als erwartet, gehört noch nicht auf Stufe 4. Manche Aufgaben bleiben bewusst auf Stufe 1 oder 2, etwa weil sie selten vorkommen oder jedes Mal eine Abwägung verlangen.

## Low-Code heißt nicht ohne Code

Low-Code-Werkzeuge stellen einen Workflow grafisch dar: Schritte als Knoten, Übergänge als Verbindungen, Bedingungen an den Verbindungen. Der Vorteil liegt weniger im Tempo beim Erstellen als in der Prüfbarkeit. Reihenfolge, Verzweigungen und Fehlerpfade sind auf einen Blick sichtbar, auch für Kollegen, die nicht jedes Skript lesen.

Im Windows-Betrieb kommt ein reiner No-Code-Ansatz trotzdem schnell an Grenzen. Eingebaute Bausteine decken die häufigen Aufgaben ab, etwa Dienste, Dateien, Registry oder geplante Aufgaben. Für alles Spezielle bleibt PowerShell das passende Werkzeug. Ein gutes Low-Code-Werkzeug verbindet beides: Der Workflow ist grafisch, der einzelne Schritt darf ein Skript sein, das wie jeder andere Code geprüft und versioniert wird.

## Wo KI hilft und wo nicht

Sprachmodelle können aus einer Beschreibung einen ersten Skriptentwurf oder einen Workflow-Entwurf erzeugen und eine Fehlermeldung in verständliche Sprache übersetzen. Bei der Umstellung eines Runbooks spart das vor allem am Anfang Zeit, wenn aus einer Wiki-Anweisung ein erstes Gerüst entstehen soll.

Ein solcher Entwurf ersetzt aber weder die Prüfung noch die Abnahme. Das Modell kennt weder die eigene Umgebung noch die Gründe hinter einer Ausnahme in der Anweisung. Es kann außerdem plausibel klingende Parameter erfinden. Deshalb gehört jeder Entwurf durch dieselbe Prüfung wie ein selbst geschriebenes Skript, bevor er gegen produktive Systeme läuft.

Ebenso wichtig ist, was an das Modell gesendet wird. Zugangsdaten und andere Geheimnisse gehören nicht in einen Prompt. Ein Werkzeug sollte das technisch verhindern, statt sich auf die Sorgfalt der Benutzer zu verlassen.

## Ein Beispiel: der hängende Dienst

Das Runbook im Wiki lautet: „Meldet das Systemprotokoll, dass der Dienst `AppWorker` unerwartet beendet wurde, Status prüfen und neu starten. Läuft er danach nicht, die Bereitschaft informieren.“ Übertragen auf einen Workflow ergeben sich fünf Schritte:

1. **Auslöser:** ein Eintrag im Systemprotokoll mit der Ereignis-ID 7031 für diesen Dienst.
2. **Status lesen:** den aktuellen Zustand des Dienstes auf dem betroffenen Server abfragen.
3. **Entscheidung:** Läuft der Dienst wieder von selbst, endet der Workflow mit einem Protokolleintrag.
4. **Neustart und erneute Prüfung:** Andernfalls wird der Dienst gestartet und sein Status ein zweites Mal gelesen.
5. **Fehlerpfad:** Läuft er danach immer noch nicht, geht eine E-Mail mit dem Protokoll an die Bereitschaft.

Bemerkenswert ist, was das Runbook nicht gesagt hat: wie oft der Neustart pro Stunde versucht werden darf. Ohne diese Grenze würde ein dauerhaft defekter Dienst eine Schleife aus Absturz und Neustart erzeugen. Die Antwort gehört zuerst in die Anweisung und danach in den Workflow.

## Was am Ende jedes Laufs feststehen sollte

Ein automatisierter Workflow ist erst dann besser als die Handarbeit, wenn sich jeder Lauf nachvollziehen lässt: was ihn ausgelöst hat, mit welchen Eingaben er gelaufen ist, welches Ergebnis jeder Schritt hatte und wo er gegebenenfalls aufgegeben hat. Fehlt diese Spur, wird die Fehlersuche schwieriger als zuvor.

In NodePilot entsteht ein solcher Workflow im grafischen Designer im Browser. Die Schritte sind eingebaute Aktivitäten oder PowerShell und werden agentenlos über WinRM auf den Zielsystemen ausgeführt. Zu jedem Lauf hält NodePilot das Ergebnis und die Ausgabe jedes Schritts fest. Die KI-Funktionen sind optional und standardmäßig ausgeschaltet. Im Workflow-Assistenten werden Geheimnisse vor jedem Aufruf des Sprachmodells geschwärzt. Wer bereits Runbooks in System Center Orchestrator betreibt, kann sie als Workflows importieren und das Ergebnis prüfen, wie im Beitrag [SCOrch-Runbooks importieren und das Ergebnis prüfen](blog/scorch-import/) beschrieben.
