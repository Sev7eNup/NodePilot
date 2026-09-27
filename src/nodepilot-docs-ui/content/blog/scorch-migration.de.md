# Was von einem SCOrch-Runbook beim Umzug erhalten bleiben muss

Auf dem Papier besteht das Runbook aus vier Aktivitäten: Server ermitteln, Dienst abfragen, bei Bedarf neu starten, Ergebnis melden. Der Nachbau scheint überschaubar. Erst der Blick auf die Daten zeigt, dass „Server ermitteln“ mehrere Ergebnisse veröffentlicht und die nachfolgenden Schritte für jeden Treffer ausgeführt werden. Wer daraus eine einzige Liste macht, hat den Ablauf bereits verändert.

Das folgende Migrationsbeispiel ist konstruiert. Es beschreibt eine Prüfung, die vor einer produktiven Übernahme stattfinden sollte, und keinen tatsächlich durchgeführten Kundeneinsatz.

## Der Vertrag zwischen den Aktivitäten

Im Ausgangsbeispiel liefert die Suche jeweils Rechnername und Dienstname. Die Dienstabfrage ergänzt den Zustand. Ein bedingter Link führt nur dann zum Neustart, wenn der Dienst angehalten ist. Ein weiterer Pfad protokolliert Abfragefehler, ohne daraus einen Neustartauftrag abzuleiten.

SCOrch stellt mit Published Data Informationen für nachfolgende Aktivitäten bereit. Welche Bedingungen auf einem Link möglich sind, hängt auch vom Datentyp der veröffentlichten Werte ab. Deshalb reicht es bei der Migration nicht aus, die sichtbaren Beschriftungen zu vergleichen. [Microsoft: Ablaufsteuerung von Runbook-Aktivitäten](https://learn.microsoft.com/en-us/system-center/orchestrator/control-runbook-activities).

Vor der Übertragung lässt sich der fachliche Vertrag knapp festhalten:

| Übergabe | Erwartung im Beispiel | Prüfung nach dem Umzug |
|---|---|---|
| Suchergebnis | Ein Datensatz je ausgewähltem Server und Dienst | Anzahl und Zuordnung bleiben erhalten |
| Dienstzustand | Eindeutiger Wert für laufend oder angehalten | Bedingung verwendet den tatsächlichen Zielwert |
| Abfragefehler | Eigener Fehlerpfad | Fehlende Ausgabe löst keinen Neustart aus |
| Abschlussmeldung | Ergebnis je Ziel mit erfolgter Aktion | Kein pauschaler Erfolg bei Teilergebnissen |

Besondere Aufmerksamkeit verdient die Kardinalität. Kein Treffer, ein Treffer und mehrere Treffer sind drei unterschiedliche Eingabesituationen. Zusätzlich kann ein Runbook Daten zusammenfassen, bevor es sie weiterreicht. Diese Zusammenfassung muss bewusst erhalten oder durch eine ausdrücklich freigegebene Änderung ersetzt werden.

## Den Import an Gegenbeispielen messen

Ein erfolgreicher Probelauf mit einem erreichbaren Server prüft nur den einfachsten Zweig. Für das Beispiel braucht es außerdem einen bereits laufenden Dienst, einen angehaltenen Dienst und eine verweigerte Abfrage. Die Erwartung steht vor dem Test fest: Ein Zugriffsfehler darf keinen Neustart auslösen, und ein bereits laufender Dienst soll unverändert bleiben.

Die ursprünglichen Ausgaben helfen dabei, Annahmen zu ersetzen. Im SCOrch Runbook Tester lassen sich die veröffentlichten Daten der Aktivitäten untersuchen. Ein geeigneter Test erfolgt gegen freigegebene Testziele, da die ausgeführten Aktivitäten Änderungen auslösen können. [Microsoft: Runbooks erstellen und testen](https://learn.microsoft.com/en-us/system-center/orchestrator/design-and-build-runbooks).

Für jeden Testfall werden anschließend Eingabe, gewählter Pfad und beobachtetes Ergebnis gegenübergestellt. Eine andere interne Struktur ist zulässig, solange die vereinbarten Eigenschaften erhalten bleiben. Verändert sich dagegen die Zahl der Neustarts oder die Behandlung eines Fehlers, benötigt diese Abweichung eine fachliche Entscheidung.

## Was im Diagramm leicht fehlt

Zum Runbook gehören auch seine Ausführungsbedingungen. Ein Zeitplan, die bisherige Dienstidentität und der Zugriff auf eine Freigabe stehen möglicherweise nicht neben der Aktivität, entscheiden aber über deren Verhalten. Ebenso kann die Betriebsdokumentation eine manuelle Prüfung verlangen, bevor nach einem Teilfehler erneut gestartet wird.

Diese Nacharbeit gehört in die Übergabe. Bleibt sie nur im Wissen eines einzelnen Administrators, wirkt der neue Workflow vollständiger, als er tatsächlich ist. Für die Freigabe sollten deshalb offene Punkte einen Verantwortlichen haben und die bisherigen Startmechanismen während der Umstellung kontrolliert werden. Zwei gleichzeitig aktive Umgebungen könnten denselben Auftrag doppelt ausführen.

NodePilot kann unterstützte SCOrch-Aktivitäten und ihre Verbindungen importieren. Nicht unterstützte Stellen und Einschränkungen müssen anschließend anhand des Berichts bearbeitet werden. Der Import spart Übertragungsarbeit. Die beschriebenen Vergleichsfälle liefern die Grundlage für die Freigabe. [NodePilot: SCOrch-Import](https://github.com/Sev7eNup/NodePilot#coming-from-system-center-orchestrator).
